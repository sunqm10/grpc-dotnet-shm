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
using System.Globalization;
using Grpc.Core;
using Grpc.Net.SharedMemory;
using Server;

const string SegmentName = "metadata_shm";
const string TimestampFormat = "MMM dd HH:mm:ss.fffffff";

Console.WriteLine("Metadata Example - Shared Memory Server");
Console.WriteLine($"Listening on shm://{SegmentName}");
Console.WriteLine();

var echoService = new EchoService();
await using var server = new ShmGrpcServer(SegmentName, ringCapacity: 1024 * 1024, maxStreams: 100);
server.MapUnary<EchoRequest, EchoResponse>("/echo.Echo/UnaryEcho", UnaryEchoWithMetadataAsync);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server stopped.");
}

async Task<EchoResponse> UnaryEchoWithMetadataAsync(EchoRequest request, ServerCallContext context)
{
    if (context.RequestHeaders.Count > 0)
    {
        Console.WriteLine("Received request metadata:");
        foreach (var entry in context.RequestHeaders)
        {
            var value = entry.IsBinary
                ? Convert.ToBase64String(entry.ValueBytes)
                : entry.Value;
            if (value != null)
            {
                Console.WriteLine($"  {entry.Key} = {value}");
            }
        }
    }

    await context.WriteResponseHeadersAsync(new Metadata
    {
        { "timestamp", DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture) },
        { "server-location", "shared-memory" }
    });

    var response = await echoService.UnaryEcho(request, context);
    context.ResponseTrailers.Add("trailer-timestamp", DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));

    Console.WriteLine($"Message: \"{request.Message}\", sending echo");
    Console.WriteLine();

    return response;
}
