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

using System.Text;
using Grpc.Core;
using Grpc.Net.SharedMemory;
using Greet;
using Google.Protobuf;

Console.WriteLine("==========================================");
Console.WriteLine(".NET Greeter Server - Shared Memory Transport");
Console.WriteLine("==========================================");
Console.WriteLine();

// Get segment name from args or use default
var segmentName = args.Length > 0 ? args[0] : "interop_greeter";

Console.WriteLine($"Listening on segment: {segmentName}");
Console.WriteLine();
Console.WriteLine("To test with Go client:");
Console.WriteLine("  cd ../go/client");
Console.WriteLine($"  go run client.go -segment {segmentName}");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop the server.");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Create the shared memory listener with grpc-go-shmem compatible control segment
using var listener = new ShmControlListener(segmentName, ringCapacity: 1024 * 1024, maxStreams: 100);

try
{
    Console.WriteLine("Waiting for connections...");
    
    await foreach (var connection in listener.AcceptConnectionsAsync(cts.Token))
    {
        Console.WriteLine($"New connection accepted: {connection.Name}");
        
        // Handle streams from this connection
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var stream in connection.AcceptStreamsAsync(cts.Token))
                {
                    try
                    {
                        // Wait for request headers
                        var headers = await stream.ReceiveRequestHeadersAsync(cts.Token);

                        if (headers != null)
                        {
                            Console.WriteLine($"Received request: {headers.Method}");

                            // Read the request message using async enumerable
                            byte[]? requestBytes = null;
                            await foreach (var msg in stream.ReceiveMessagesAsync(cts.Token))
                            {
                                requestBytes = msg;
                                break; // Unary: only expect one message
                            }

                            if (requestBytes == null)
                            {
                                Console.WriteLine("  No request message received");
                                await stream.SendTrailersAsync(StatusCode.InvalidArgument, "No message");
                                continue;
                            }

                            var request = HelloRequest.Parser.ParseFrom(requestBytes);
                            Console.WriteLine($"  Name: {request.Name}");

                            // Create response
                            var reply = new HelloReply
                            {
                                Message = $"Hello {request.Name} from .NET server!"
                            };

                            // Send response
                            await stream.SendResponseHeadersAsync();
                            await stream.SendMessageAsync(reply.ToByteArray());
                            await stream.SendTrailersAsync(StatusCode.OK);

                            Console.WriteLine($"  Response sent: {reply.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Stream error: {ex.Message}");
                        await stream.SendTrailersAsync(StatusCode.Internal, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        });
    }
}
catch (OperationCanceledException)
{
    // Normal shutdown
}

Console.WriteLine("Server stopped.");
