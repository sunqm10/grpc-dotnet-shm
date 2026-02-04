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
using Grpc.Net.SharedMemory;

const string SegmentName = "channeler_shm_example";

Console.WriteLine("Channeler Server - Shared Memory Multi-Threaded");
Console.WriteLine("================================================");
Console.WriteLine($"Segment name: {SegmentName}");
Console.WriteLine();

// Create the shared memory listener
using var listener = new ShmConnectionListener(SegmentName, ringCapacity: 4 * 1024 * 1024, maxStreams: 100);
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
    while (!cts.Token.IsCancellationRequested)
    {
        var stream = listener.Connection.CreateStream();
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
                    await HandleUploadDataAsync(stream, streamId);
                }
                else if (method.EndsWith("DownloadResults"))
                {
                    await HandleDownloadResultsAsync(stream, streamId);
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
catch (OperationCanceledException)
{
    Console.WriteLine("Server shutting down...");
}

Console.WriteLine("Server stopped.");

static async Task HandleUploadDataAsync(ShmGrpcStream stream, int streamId)
{
    var totalBytes = 0;
    
    // Receive all chunks from client streaming
    while (true)
    {
        var request = await stream.ReceiveRequestAsync<DataRequest>();
        if (request == null) break;
        
        totalBytes += request.Value.Length;
        Console.WriteLine($"[Stream-{streamId}] Received chunk: {request.Value.Length} bytes (total: {totalBytes})");
    }
    
    // Send final result
    await stream.SendResponseAsync(new DataResult { BytesProcessed = totalBytes });
    Console.WriteLine($"[Stream-{streamId}] Upload complete: {totalBytes} bytes");
}

static async Task HandleDownloadResultsAsync(ShmGrpcStream stream, int streamId)
{
    var request = await stream.ReceiveRequestAsync<DataRequest>();
    if (request == null) return;
    
    var requestSize = request.Value.Length;
    Console.WriteLine($"[Stream-{streamId}] Download request: {requestSize} bytes");
    
    // Stream back multiple results (server streaming)
    for (int i = 0; i < 5; i++)
    {
        await stream.SendResponseAsync(new DataResult { BytesProcessed = requestSize * (i + 1) });
        await Task.Delay(50); // Simulate processing
    }
    
    Console.WriteLine($"[Stream-{streamId}] Download complete: 5 chunks sent");
}
