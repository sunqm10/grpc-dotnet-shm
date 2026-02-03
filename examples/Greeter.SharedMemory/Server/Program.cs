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

using System.Text;
using Grpc.Core;
using Grpc.Net.SharedMemory;
using Server.Services;

const string SegmentName = "greeter-shm";

Console.WriteLine("Starting shared memory gRPC server...");
Console.WriteLine($"Segment name: {SegmentName}");

// Create the greeter service
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

// Simple request handling loop
// In a production implementation, this would use proper stream routing
// and ASP.NET Core integration for full gRPC server capabilities
try
{
    while (!cts.Token.IsCancellationRequested)
    {
        // Check for incoming streams from the connection
        var serverStream = listener.Connection.CreateStream();
        
        // Wait for a request to come in
        // In the current implementation, we're demonstrating the transport pattern
        // Real stream acceptance would come from frame routing on the connection
        
        if (serverStream.RequestHeaders is { Method: var method } && method != null)
        {
            try
            {
                Console.WriteLine($"Received request for method: {method}");

                // Send response headers
                await serverStream.SendResponseHeadersAsync();

                // For demonstration, send a simple response
                // In production, the request message would be read from the stream
                var response = await greeterService.HandleMethodAsync(
                    serverStream, 
                    method, 
                    Array.Empty<byte>());

                await serverStream.SendMessageAsync(response);
                await serverStream.SendTrailersAsync(StatusCode.OK);

                Console.WriteLine("Response sent successfully.");
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"RPC error: {ex.Status}");
                await serverStream.SendTrailersAsync(ex.StatusCode, ex.Status.Detail);
            }
        }
        
        // Brief delay to prevent busy-waiting
        // Real implementation would use signaling from the ring buffer
        await Task.Delay(10, cts.Token);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server stopping...");
}

Console.WriteLine("Server stopped.");
