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

using Grpc.Core;
using Grpc.Net.SharedMemory;
using Server.Services;

const string SegmentName = "error_shm_example";

Console.WriteLine("Error Handling - Shared Memory Server");
Console.WriteLine("=====================================");
Console.WriteLine($"Segment name: {SegmentName}");

// Create the greeter service with validation
var greeterService = new GreeterService();

// Create the shared memory listener
using var listener = new ShmConnectionListener(SegmentName, ringCapacity: 1024 * 1024, maxStreams: 100);
Console.WriteLine("Server listening on shared memory segment: " + SegmentName);
Console.WriteLine("Press Ctrl+C to stop the server.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await foreach (var serverStream in listener.AcceptStreamsAsync(cts.Token))
    {
        var method = serverStream.RequestHeaders?.Method;
        if (method == null) continue;

        try
        {
            Console.WriteLine($"Received request for method: {method}");

            // Read request message
            byte[]? requestData = null;
            await foreach (var msg in serverStream.ReceiveMessagesAsync(cts.Token))
            {
                requestData = msg;
                break;
            }

            // Handle the method with validation
            var response = await greeterService.HandleMethodAsync(
                serverStream,
                method,
                requestData ?? Array.Empty<byte>());

            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendMessageAsync(response);
            await serverStream.SendTrailersAsync(StatusCode.OK);

            Console.WriteLine("Response sent successfully.");
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"RPC error: {ex.Status.StatusCode} - {ex.Status.Detail}");
            await serverStream.SendTrailersAsync(ex.Status.StatusCode, ex.Status.Detail);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            await serverStream.SendTrailersAsync(StatusCode.Internal, ex.Message);
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server shutting down...");
}

Console.WriteLine("Server stopped.");
