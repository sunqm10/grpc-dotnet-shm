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

using System.Globalization;
using Echo;
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
    HttpHandler = new ShmHttpHandler(SegmentName),
    DisposeHttpClient = true
});

var client = new Echo.Echo.EchoClient(channel);

// ============================================================
// Unary Call with Metadata
// ============================================================
Console.WriteLine("=== Unary Call with Metadata ===");

// Create request metadata
var requestHeaders = new Metadata
{
    { "timestamp", DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture) },
    { "client-id", "shm-client-1" }
};

Console.WriteLine("Sending request with metadata:");
foreach (var entry in requestHeaders)
{
    Console.WriteLine($"  {entry.Key} = {entry.Value}");
}

// Make unary call with metadata
using var call = client.UnaryEchoAsync(
    new EchoRequest { Message = Message },
    headers: requestHeaders);

// Read response headers
var responseHeaders = await call.ResponseHeadersAsync;
Console.WriteLine("\nReceived response headers:");
foreach (var entry in responseHeaders)
{
    if (!entry.Key.StartsWith(":"))
    {
        Console.WriteLine($"  {entry.Key} = {entry.Value}");
    }
}

// Read response message
var response = await call.ResponseAsync;
Console.WriteLine($"\nReceived response: {response.Message}");

// Read trailers
var trailers = call.GetTrailers();
Console.WriteLine("\nReceived trailers:");
foreach (var entry in trailers)
{
    Console.WriteLine($"  {entry.Key} = {entry.Value}");
}

Console.WriteLine();
Console.WriteLine("All metadata tests completed!");
