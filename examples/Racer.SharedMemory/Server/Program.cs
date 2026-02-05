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

using Grpc.Net.SharedMemory;
using Race;

const string SegmentName = "racer_shm_example";

Console.WriteLine("Racer Server - Shared Memory Concurrent Streams");
Console.WriteLine("================================================");
Console.WriteLine($"Segment name: {SegmentName}");
Console.WriteLine();

// Create the shared memory listener using ShmControlListener for grpc-go-shmem compatibility
using var listener = new ShmControlListener(SegmentName, ringCapacity: 16 * 1024 * 1024, maxStreams: 100);
Console.WriteLine($"Server listening on shared memory segment: {SegmentName}");
Console.WriteLine("Press Ctrl+C to stop the server.");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

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
                    try
                    {
                        // Get race duration from headers
                        var durationStr = stream.RequestHeaders?.Metadata?.FirstOrDefault(h => h.Key == "race-duration")?.Values.FirstOrDefault();
                        var duration = durationStr != null && TimeSpan.TryParse(System.Text.Encoding.UTF8.GetString(durationStr), out var d) 
                            ? d 
                            : TimeSpan.FromSeconds(30);
                        
                        Console.WriteLine($"New race started! Duration: {duration.TotalSeconds}s");
                        
                        // Handle concurrent bidirectional streaming
                        await HandleRaceAsync(stream, duration, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Stream error: {ex.Message}");
                    }
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

static async Task HandleRaceAsync(ShmGrpcStream stream, TimeSpan duration, CancellationToken ct)
{
    var received = 0;
    var sent = 0;
    var startTime = DateTime.UtcNow;
    
    try
    {
        // Concurrent stream: read and write simultaneously
        var readTask = Task.Run(async () =>
        {
            await foreach (var msg in stream.ReceiveMessagesAsync(ct))
            {
                if (msg == null) break;
                var message = RaceMessage.Parser.ParseFrom(msg);
                Interlocked.Increment(ref received);
            }
        });
        
        var writeTask = Task.Run(async () =>
        {
            await stream.SendResponseHeadersAsync();
            while (!ct.IsCancellationRequested && (DateTime.UtcNow - startTime) < duration)
            {
                var count = Interlocked.Increment(ref sent);
                await stream.SendMessageAsync(new RaceMessage { Count = count }.ToByteArray());
            }
        });
        
        await Task.WhenAll(readTask, writeTask);
        
        await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK);
        Console.WriteLine($"Race completed! Received: {received:n0}, Sent: {sent:n0}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Race error: {ex.Message}");
    }
}
