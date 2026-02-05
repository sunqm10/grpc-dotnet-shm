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

using Helloworld;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

Console.WriteLine("==========================================");
Console.WriteLine(".NET Greeter Client - Shared Memory Transport");
Console.WriteLine("==========================================");
Console.WriteLine();

// Get segment name from args or use default
var segmentName = args.Length > 0 ? args[0] : "interop_greeter";
var name = args.Length > 1 ? args[1] : ".NET Client";

Console.WriteLine($"Connecting to segment: {segmentName}");

try
{
    // Create a channel using the grpc-go-shmem compatible control handler
    using var handler = new ShmControlHandler(segmentName, TimeSpan.FromSeconds(30));
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = handler
    });

    var client = new Greeter.GreeterClient(channel);

    // Make the RPC call with timeout
    Console.WriteLine($"Sending: SayHello(name=\"{name}\")");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    var reply = await client.SayHelloAsync(new HelloRequest { Name = name }, cancellationToken: cts.Token);

    Console.WriteLine();
    Console.WriteLine($"Response: {reply.Message}");
    Console.WriteLine();
    Console.WriteLine("Interop test PASSED!");

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine($"Full exception: {ex}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first.");

    return 1;
}
