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

using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using Helloworld;

// Parse command line args
var segmentName = args.Length > 0 ? args[0] : "interop_test_shm";
var name = args.Length > 1 ? args[1] : "DotNet";

Console.WriteLine("===========================================");
Console.WriteLine(".NET Interop Client - Shared Memory");
Console.WriteLine("===========================================");
Console.WriteLine($"Segment: {segmentName}");
Console.WriteLine($"Name: {name}");
Console.WriteLine();

try
{
    // Use ShmControlHandler for grpc-go-shmem compatibility
    // This uses the control segment protocol (_ctl suffix)
    using var handler = new ShmControlHandler(segmentName);
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = handler
    });

    var client = new Greeter.GreeterClient(channel);

    Console.WriteLine($"Calling SayHello(\"{name}\")...");
    
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    var reply = await client.SayHelloAsync(new HelloRequest { Name = name }, cancellationToken: cts.Token);

    Console.WriteLine($"Response: {reply.Message}");
    Console.WriteLine("SUCCESS");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    Console.Error.WriteLine(ex.ToString());
    return 1;
}
