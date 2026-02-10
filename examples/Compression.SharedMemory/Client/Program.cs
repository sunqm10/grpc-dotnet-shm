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

const string SegmentName = "compression_example_shm";

Console.WriteLine("=== Compression SharedMemory Client ===");
Console.WriteLine($"Segment: {SegmentName}");
Console.WriteLine();

Console.WriteLine($"Connecting to shared memory segment: {SegmentName}");
Console.WriteLine("(Make sure the server is running first!)");
Console.WriteLine();

try
{
    // Create channel using shared memory HTTP handler (Kestrel-based dialer)
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = new ShmHttpHandler(SegmentName),
        DisposeHttpClient = true
    });

    var client = new Echo.Echo.EchoClient(channel);

    // 'grpc-internal-encoding-request' is a special metadata value that tells
    // the client to compress the request.
    // This metadata is only used in the client and is not sent as a header to the server.
    var metadata = new Metadata();
    metadata.Add("grpc-internal-encoding-request", "gzip");

    // Create a message with enough data to benefit from compression
    var message = "Hello with compression! " + new string('A', 500);
    Console.WriteLine($"Request message: {message.Length} chars");

    var reply = await client.UnaryEchoAsync(
        new EchoRequest { Message = message },
        headers: metadata);

    Console.WriteLine($"Response: {reply.Message.Substring(0, Math.Min(80, reply.Message.Length))}...");
    Console.WriteLine($"Response length: {reply.Message.Length} chars");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Compression.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Compression example completed!");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
