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
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

const string SegmentName = "keepalive_shm";

Console.WriteLine("Keepalive Example - Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

// Create a shared memory channel. The ShmHttpHandler manages transport-level
// keepalive automatically as part of the shared memory connection lifecycle.
using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new ShmHttpHandler(SegmentName),
    DisposeHttpClient = true
});

var client = new Echo.Echo.EchoClient(channel);

// Perform a unary echo request
Console.WriteLine("Performing unary request...");
var reply = await client.UnaryEchoAsync(new EchoRequest { Message = "keepalive demo" });
Console.WriteLine($"RPC response: {reply.Message}");

// Wait to observe keepalive behavior.
// In a production scenario, the underlying shared memory transport maintains
// the connection and handles keepalive pings transparently.
Console.WriteLine();
Console.WriteLine("Waiting to observe keepalive behavior...");
Console.WriteLine("(The shared memory transport maintains connection health automatically)");
Console.WriteLine();

for (int i = 0; i < 5; i++)
{
    await Task.Delay(TimeSpan.FromSeconds(5));
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Still connected... ({(i + 1) * 5}s elapsed)");

    // Periodically send a request to demonstrate the connection is alive
    if (i == 2)
    {
        var midReply = await client.UnaryEchoAsync(new EchoRequest { Message = "keepalive check" });
        Console.WriteLine($"  Mid-wait RPC response: {midReply.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("Keepalive example completed!");
