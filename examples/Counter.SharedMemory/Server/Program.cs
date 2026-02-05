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

using Grpc.Core;
using Grpc.Net.SharedMemory;
using Server.Services;

const string SegmentName = "counter_shm_example";

Console.WriteLine("Counter Server - Shared Memory Transport");
Console.WriteLine("=========================================");
Console.WriteLine();
Console.WriteLine($"Starting shared memory server on segment: {SegmentName}");

var counter = new IncrementingCounter();
var counterService = new CounterService(counter);

// Create the shared memory listener using ShmControlListener for grpc-go-shmem compatibility
using var listener = new ShmControlListener(SegmentName, ringCapacity: 1024 * 1024, maxStreams: 100);
Console.WriteLine($"Server listening on shared memory segment: {SegmentName}");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop the server.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await foreach (var connection in listener.AcceptConnectionsAsync(cts.Token))
    {
        Console.WriteLine($"New connection accepted: {connection.Name}");

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var stream in connection.AcceptStreamsAsync(cts.Token))
                {
                    try
                    {
                        var headers = stream.RequestHeaders;
                        if (headers?.Method is { } method)
                        {
                            Console.WriteLine($"Received request: {method}");

                            await stream.SendResponseHeadersAsync();

                            // Route to appropriate handler based on method
                            var response = await counterService.HandleMethodAsync(
                                stream,
                                method,
                                cts.Token);

                            if (response != null)
                            {
                                await stream.SendMessageAsync(response);
                            }
                            await stream.SendTrailersAsync(StatusCode.OK);

                            Console.WriteLine($"  Current count: {counter.Count}");
                        }
                    }
                    catch (RpcException ex)
                    {
                        await stream.SendTrailersAsync(ex.StatusCode, ex.Status.Detail);
                        Console.WriteLine($"  RPC Error: {ex.Status.Detail}");
                    }
                    catch (Exception ex)
                    {
                        await stream.SendTrailersAsync(StatusCode.Internal, ex.Message);
                        Console.WriteLine($"  Error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nServer shutting down...");
}

Console.WriteLine($"Final count: {counter.Count}");
Console.WriteLine("Server stopped.");
