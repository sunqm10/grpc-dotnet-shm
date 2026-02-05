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

const string SegmentName = "error_shm_example";

Console.WriteLine("Error Handling - Shared Memory Server");
Console.WriteLine("=====================================");
Console.WriteLine($"Segment name: {SegmentName}");

// Create the greeter service with validation
var greeterService = new GreeterService();

// Create the shared memory listener using ShmControlListener for grpc-go-shmem compatibility
using var listener = new ShmControlListener(SegmentName, ringCapacity: 1024 * 1024, maxStreams: 100);
Console.WriteLine("Server listening on shared memory segment: " + SegmentName);
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
                            Console.WriteLine($"Received request for method: {method}");

                            // Read the request message
                            byte[]? requestBytes = null;
                            await foreach (var msg in stream.ReceiveMessagesAsync(cts.Token))
                            {
                                requestBytes = msg;
                                break;
                            }

                            // Send response headers
                            await stream.SendResponseHeadersAsync();

                            // Handle the method with validation
                            var response = await greeterService.HandleMethodAsync(
                                stream,
                                method,
                                requestBytes ?? Array.Empty<byte>());

                            await stream.SendMessageAsync(response);
                            await stream.SendTrailersAsync(StatusCode.OK);

                            Console.WriteLine("Response sent successfully.");
                        }
                    }
                    catch (RpcException ex)
                    {
                        Console.WriteLine($"RPC error: {ex.Status.StatusCode} - {ex.Status.Detail}");
                        await stream.SendTrailersAsync(ex.Status.StatusCode, ex.Status.Detail);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.Message}");
                        await stream.SendTrailersAsync(StatusCode.Internal, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server shutting down...");
}

Console.WriteLine("Server stopped.");
