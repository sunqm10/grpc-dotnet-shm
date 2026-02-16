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
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

const string SegmentName = "metadata_shm";
const string TimestampFormat = "MMM dd HH:mm:ss.fffffff";
const string Message = "this is examples/metadata";

Console.WriteLine("Metadata Example - Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new ShmControlHandler(SegmentName),
    DisposeHttpClient = true
});

var client = new Echo.Echo.EchoClient(channel);
Console.WriteLine("Connected to server");
Console.WriteLine();

// ============================================================
// Unary Call with Metadata
// ============================================================
Console.WriteLine("=== Unary Call with Metadata ===");
await UnaryCallWithMetadata(client, Message);
Console.WriteLine();

Console.WriteLine("All metadata tests completed!");

async Task UnaryCallWithMetadata(Echo.Echo.EchoClient echoClient, string message)
{
    try
    {
        // Create metadata with timestamp
        var requestMetadata = new Metadata
        {
            { "timestamp", DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture) },
            { "client-id", "shm-client-1" }
        };

        Console.WriteLine("Sending request with metadata:");
        foreach (var entry in requestMetadata)
        {
            Console.WriteLine($"  {entry.Key} = {entry.Value}");
        }

        var call = echoClient.UnaryEchoAsync(new EchoRequest { Message = message }, headers: requestMetadata);

        var responseHeaders = await call.ResponseHeadersAsync;
        Console.WriteLine("\nReceived response headers:");
        foreach (var entry in responseHeaders)
        {
            var value = entry.IsBinary
                ? Convert.ToBase64String(entry.ValueBytes)
                : entry.Value;
            if (value != null)
            {
                Console.WriteLine($"  {entry.Key} = {value}");
            }
        }

        var response = await call.ResponseAsync;
        Console.WriteLine($"\nReceived response: {response.Message}");

        var trailers = call.GetTrailers();
        Console.WriteLine("\nReceived trailers:");
        foreach (var entry in trailers)
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
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}
