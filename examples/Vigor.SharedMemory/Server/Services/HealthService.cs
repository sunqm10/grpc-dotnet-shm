#region Copyright notice and license

// Copyright 2025 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using Google.Protobuf;
using Grpc.Core;
using Grpc.Health.V1;
using Grpc.Net.SharedMemory;

namespace Server.Services;

/// <summary>
/// Health check service that provides the gRPC health checking protocol over shared memory.
/// </summary>
public class HealthService
{
    private readonly Dictionary<string, HealthCheckResponse.Types.ServingStatus> _statuses = new()
    {
        [""] = HealthCheckResponse.Types.ServingStatus.Serving
    };

    private event Action<string, HealthCheckResponse.Types.ServingStatus>? StatusChanged;

    /// <summary>
    /// Sets the health status for a service.
    /// </summary>
    public void SetStatus(string service, HealthCheckResponse.Types.ServingStatus status)
    {
        _statuses[service] = status;
        StatusChanged?.Invoke(service, status);
        Console.WriteLine($"Health status changed: '{service}' is {status}");
    }

    /// <summary>
    /// Checks the current health status of a service.
    /// </summary>
    public HealthCheckResponse Check(string service = "")
    {
        if (_statuses.TryGetValue(service, out var status))
        {
            return new HealthCheckResponse { Status = status };
        }

        throw new RpcException(new Status(StatusCode.NotFound, $"Service {service} not found"));
    }

    /// <summary>
    /// Watches health status changes and streams updates to the client.
    /// </summary>
    public async Task WatchAsync(ShmGrpcStream stream, CancellationToken cancellationToken)
    {
        var service = "";
        var lastStatus = HealthCheckResponse.Types.ServingStatus.Unknown;

        var statusChanged = new TaskCompletionSource<HealthCheckResponse.Types.ServingStatus>();

        void OnStatusChanged(string svc, HealthCheckResponse.Types.ServingStatus status)
        {
            if (svc == service)
            {
                statusChanged.TrySetResult(status);
            }
        }

        StatusChanged += OnStatusChanged;

        try
        {
            // Send initial status
            var currentStatus = _statuses.GetValueOrDefault(service, HealthCheckResponse.Types.ServingStatus.ServiceUnknown);
            var response = new HealthCheckResponse { Status = currentStatus };
            await stream.SendMessageAsync(response.ToByteArray());
            lastStatus = currentStatus;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait for status change or timeout
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    var delayTask = Task.Delay(Timeout.Infinite, linkedCts.Token);
                    var completedTask = await Task.WhenAny(statusChanged.Task, delayTask);

                    if (completedTask == statusChanged.Task)
                    {
                        var newStatus = await statusChanged.Task;
                        if (newStatus != lastStatus)
                        {
                            response = new HealthCheckResponse { Status = newStatus };
                            await stream.SendMessageAsync(response.ToByteArray());
                            lastStatus = newStatus;
                        }
                        statusChanged = new TaskCompletionSource<HealthCheckResponse.Types.ServingStatus>();
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // Timeout, continue watching
                }
            }

            await stream.SendTrailersAsync(StatusCode.OK);
        }
        finally
        {
            StatusChanged -= OnStatusChanged;
        }
    }
}
