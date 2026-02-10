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

using Client;
using Echo;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using Microsoft.Extensions.Logging;

const string SegmentName = "interceptor_shm";

Console.WriteLine("Interceptor Example - Shared Memory Client");
Console.WriteLine("===========================================");
Console.WriteLine();

Console.WriteLine($"Connecting to shared memory segment: {SegmentName}");
Console.WriteLine("(Make sure the server is running first!)");
Console.WriteLine();

try
{
    var loggerFactory = LoggerFactory.Create(b => b.AddConsole());

    // Create channel using shared memory HTTP handler (Kestrel-based dialer)
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = new ShmHttpHandler(SegmentName),
        DisposeHttpClient = true,
        LoggerFactory = loggerFactory
    });

    // Add client-side interceptor
    var invoker = channel.Intercept(new ClientLoggerInterceptor(loggerFactory));
    var client = new Echo.Echo.EchoClient(invoker);

    // Unary call
    await UnaryCallExample(client);

    // Bidirectional streaming call
    await BidirectionalCallExample(client);

    Console.WriteLine();
    Console.WriteLine("Interceptor example completed!");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Interceptor.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine("Press any key to exit...");
Console.ReadKey();

static async Task UnaryCallExample(Echo.Echo.EchoClient client)
{
    var reply = await client.UnaryEchoAsync(new EchoRequest { Message = "hello world" });
    Console.WriteLine($"UnaryEcho: {reply.Message}");
}

static async Task BidirectionalCallExample(Echo.Echo.EchoClient client)
{
    using var call = client.BidirectionalStreamingEcho();
    var readTask = Task.Run(async () =>
    {
        await foreach (var message in call.ResponseStream.ReadAllAsync())
        {
            Console.WriteLine($"BidiStreaming Echo: {message.Message}");
        }
    });

    for (var i = 1; i <= 5; i++)
    {
        await call.RequestStream.WriteAsync(new EchoRequest { Message = $"Request {i}" });
    }

    await call.RequestStream.CompleteAsync();
    await readTask;
}
