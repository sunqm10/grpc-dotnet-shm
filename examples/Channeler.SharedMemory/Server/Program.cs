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

using DataChannel;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.SharedMemory;

const string SegmentName = "channeler_shm_example";

Console.WriteLine("Channeler Server - Shared Memory Multi-Threaded");
Console.WriteLine("================================================");
Console.WriteLine($"Segment name: {SegmentName}");
Console.WriteLine();

// Create the shared memory listener using ShmControlListener for grpc-go-shmem compatibility
using var listener = new ShmControlListener(SegmentName, ringCapacity: 4 * 1024 * 1024, maxStreams: 100);
Console.WriteLine($"Server listening on shared memory segment: {SegmentName}");
Console.WriteLine("Press Ctrl+C to stop the server.");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Track active connections for multi-threaded handling
var activeStreams = 0;

try
{
    await foreach (var connection in listener.AcceptConnectionsAsync(cts.Token))
    {
        Console.WriteLine($"New connection accepted: {connection.Name}");

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var stream in connection.AcceptStreamsAsync(cts.Token))
                {
                    var streamId = Interlocked.Increment(ref activeStreams);
                    var method = stream.RequestHeaders?.Method ?? "unknown";
                    Console.WriteLine($"[Stream-{streamId}] New connection: {method}");
                    
                    // Handle each stream on a separate thread
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (method.EndsWith("UploadData"))
                            {
                                await HandleUploadDataAsync(stream, streamId, cts.Token);
                            }
                            else if (method.EndsWith("DownloadResults"))
                            {
                                await HandleDownloadResultsAsync(stream, streamId, cts.Token);
                            }
                            else
                            {
                                await stream.SendTrailersAsync(StatusCode.Unimplemented, $"Unknown method: {method}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Stream-{streamId}] Error: {ex.Message}");
                        }
                        finally
                        {
                            Interlocked.Decrement(ref activeStreams);
                            Console.WriteLine($"[Stream-{streamId}] Completed. Active streams: {activeStreams}");
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
        });
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server shutting down...");
}

Console.WriteLine("Server stopped.");

static async Task HandleUploadDataAsync(ShmGrpcStream stream, int streamId, CancellationToken ct)
{
    var totalBytes = 0;
    
    await stream.SendResponseHeadersAsync();

    // Receive all chunks from client streaming
    await foreach (var msg in stream.ReceiveMessagesAsync(ct))
    {
        if (msg == null) break;
        var request = DataRequest.Parser.ParseFrom(msg);
        totalBytes += request.Value.Length;
        Console.WriteLine($"[Stream-{streamId}] Received chunk: {request.Value.Length} bytes (total: {totalBytes})");
    }
    
    // Send final result
    await stream.SendMessageAsync(new DataResult { BytesProcessed = totalBytes }.ToByteArray());
    await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    Console.WriteLine($"[Stream-{streamId}] Upload complete: {totalBytes} bytes");
}

static async Task HandleDownloadResultsAsync(ShmGrpcStream stream, int streamId, CancellationToken ct)
{
    // Read the request
    byte[]? requestBytes = null;
    await foreach (var msg in stream.ReceiveMessagesAsync(ct))
    {
        requestBytes = msg;
        break;
    }
    
    if (requestBytes == null) return;
    
    var request = DataRequest.Parser.ParseFrom(requestBytes);
    var requestSize = request.Value.Length;
    Console.WriteLine($"[Stream-{streamId}] Download request: {requestSize} bytes");
    
    await stream.SendResponseHeadersAsync();

    // Stream back multiple results (server streaming)
    for (int i = 0; i < 5; i++)
    {
        await stream.SendMessageAsync(new DataResult { BytesProcessed = requestSize * (i + 1) }.ToByteArray());
        await Task.Delay(50, ct); // Simulate processing
    }
    
    await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    Console.WriteLine($"[Stream-{streamId}] Download complete: 5 chunks sent");
}
