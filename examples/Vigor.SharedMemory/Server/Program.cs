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
using Server.Services;

const string SegmentName = "vigor_shm_example";

Console.WriteLine("Health Check - Shared Memory Server");
Console.WriteLine("====================================");
Console.WriteLine($"Segment name: {SegmentName}");

// Create the health service
var healthService = new HealthService();

// Create the shared memory listener
using var listener = new ShmConnectionListener(SegmentName, ringCapacity: 1024 * 1024, maxStreams: 100);
Console.WriteLine("Server listening on shared memory segment: " + SegmentName);
Console.WriteLine("Press Ctrl+C to stop the server.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Start health status updater
_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(5000, cts.Token);
        
        // Randomly change health status
        var isHealthy = Random.Shared.Next() % 5 != 0;
        healthService.SetStatus("", isHealthy 
            ? HealthCheckResponse.Types.ServingStatus.Serving 
            : HealthCheckResponse.Types.ServingStatus.NotServing);
    }
});

try
{
    await foreach (var serverStream in listener.AcceptStreamsAsync(cts.Token))
    {
        var method = serverStream.RequestHeaders?.Method;
        if (method == null) continue;

        try
        {
            Console.WriteLine($"Received request for method: {method}");

            if (method == "/grpc.health.v1.Health/Check")
            {
                // Read request (unary)
                await foreach (var _ in serverStream.ReceiveMessagesAsync(cts.Token))
                    break;

                await serverStream.SendResponseHeadersAsync();
                var response = healthService.Check();
                await serverStream.SendMessageAsync(response.ToByteArray());
                await serverStream.SendTrailersAsync(StatusCode.OK);
            }
            else if (method == "/grpc.health.v1.Health/Watch")
            {
                await serverStream.SendResponseHeadersAsync();
                await healthService.WatchAsync(serverStream, cts.Token);
            }
            else
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, $"Method {method} is not implemented"));
            }
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"RPC error: {ex.Status.StatusCode} - {ex.Status.Detail}");
            await serverStream.SendTrailersAsync(ex.Status.StatusCode, ex.Status.Detail);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            await serverStream.SendTrailersAsync(StatusCode.Internal, ex.Message);
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server shutting down...");
}

Console.WriteLine("Server stopped.");
