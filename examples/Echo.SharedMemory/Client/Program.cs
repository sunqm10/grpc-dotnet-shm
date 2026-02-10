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

using Echo;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

Console.WriteLine("Echo Client - Shared Memory Transport");
Console.WriteLine("=====================================");
Console.WriteLine();

const string SegmentName = "echo_shm_example";

Console.WriteLine($"Connecting to shared memory segment: {SegmentName}");
Console.WriteLine("(Make sure the server is running first!)");
Console.WriteLine();

try
{
    // Create channel using shared memory handler
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = new ShmHttpHandler(SegmentName),
        DisposeHttpClient = true
    });

    var client = new Echo.Echo.EchoClient(channel);

    // Demo 1: Unary echo
    Console.WriteLine("=== Unary Echo ===");
    var reply = await client.UnaryEchoAsync(new EchoRequest { Message = "Hello, shared memory!" });
    Console.WriteLine($"Response: {reply.Message}");

    Console.WriteLine();

    // Demo 2: Bidirectional streaming echo
    Console.WriteLine("=== Bidirectional Streaming Echo ===");

    using var call = client.BidirectionalStreamingEcho();

    // Start receiving in background
    var receiveTask = Task.Run(async () =>
    {
        await foreach (var response in call.ResponseStream.ReadAllAsync())
        {
            Console.WriteLine($"  Received: {response.Message}");
        }
    });

    // Send messages
    var messages = new[] { "First", "Second", "Third" };
    foreach (var message in messages)
    {
        Console.WriteLine($"  Sending: {message}");
        await call.RequestStream.WriteAsync(new EchoRequest { Message = message });
        await Task.Delay(200);
    }

    await call.RequestStream.CompleteAsync();
    await receiveTask;

    Console.WriteLine();
    Console.WriteLine("Echo example completed successfully!");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Echo.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
