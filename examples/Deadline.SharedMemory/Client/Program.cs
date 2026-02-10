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

const string SegmentName = "deadline_shm";

Console.WriteLine("Deadline Example - Shared Memory Client");
Console.WriteLine("========================================");
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

    // Test cases matching the Go example:
    // 1. A successful request
    await UnaryCall(client, 1, "world", StatusCode.OK);

    // 2. Exceeds deadline (message contains "delay")
    await UnaryCall(client, 2, "delay", StatusCode.DeadlineExceeded);

    // 3. A successful request with propagated deadline
    await UnaryCall(client, 3, "[propagate me]world", StatusCode.OK);

    // 4. Exceeds propagated deadline
    await UnaryCall(client, 4, "[propagate me][propagate me]world", StatusCode.DeadlineExceeded);

    Console.WriteLine();
    Console.WriteLine("All deadline tests completed!");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Deadline.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

static async Task UnaryCall(Echo.Echo.EchoClient client, int requestId, string message, StatusCode expectedCode)
{
    var deadline = DateTime.UtcNow.AddSeconds(1); // 1 second deadline

    try
    {
        var reply = await client.UnaryEchoAsync(
            new EchoRequest { Message = message },
            deadline: deadline);

        Console.WriteLine($"request {requestId}: wanted = {expectedCode}, got = {StatusCode.OK}");

        if (expectedCode != StatusCode.OK)
        {
            Console.WriteLine($"  WARNING: Expected {expectedCode} but got OK");
        }
    }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
    {
        Console.WriteLine($"request {requestId}: wanted = {expectedCode}, got = {StatusCode.DeadlineExceeded}");
    }
    catch (RpcException ex)
    {
        Console.WriteLine($"request {requestId}: error - {ex.Status.StatusCode}: {ex.Status.Detail}");
    }
}
