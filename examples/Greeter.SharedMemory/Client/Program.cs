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

using Greet;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

Console.WriteLine("Greeter Client - Shared Memory Transport");
Console.WriteLine("=========================================");
Console.WriteLine();

// The segment name must match what the server creates
const string SegmentName = "greeter_shm_example";

Console.WriteLine($"Connecting to shared memory segment: {SegmentName}");
Console.WriteLine("(Make sure the server is running first!)");
Console.WriteLine();

try
{
    // Create a channel using the shared memory handler
    // The address is a placeholder - actual transport is via shared memory
    using var handler = new ShmHandler(SegmentName);
    using var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
    {
        HttpHandler = handler
    });

    var client = new Greeter.GreeterClient(channel);

    // Make a unary call
    Console.Write("Enter your name: ");
    var name = Console.ReadLine() ?? "World";

    Console.WriteLine($"Sending request to SayHello(\"{name}\")...");

    var reply = await client.SayHelloAsync(new HelloRequest { Name = name });

    Console.WriteLine();
    Console.WriteLine($"Response received: {reply.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Greeter.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
