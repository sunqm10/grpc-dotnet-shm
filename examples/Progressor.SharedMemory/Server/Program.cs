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
using Progress;

const string SegmentName = "progressor_shm_example";

Console.WriteLine("Progressor Server - Shared Memory Progress Reporting");
Console.WriteLine("=====================================================");
Console.WriteLine($"Segment name: {SegmentName}");
Console.WriteLine();

// Create the shared memory listener
using var listener = new ShmConnectionListener(SegmentName, ringCapacity: 1024 * 1024, maxStreams: 100);
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
    while (!cts.Token.IsCancellationRequested)
    {
        var stream = listener.Connection.CreateStream();
        
        Console.WriteLine("New progress request received");
        
        // Handle progress streaming
        _ = HandleProgressStreamAsync(stream, cts.Token);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server shutting down...");
}

Console.WriteLine("Server stopped.");

static async Task HandleProgressStreamAsync(ShmGrpcStream stream, CancellationToken ct)
{
    try
    {
        Console.WriteLine("Starting long-running operation with progress reporting...");
        
        // Simulate a long-running operation with progress updates
        var historyItems = new List<string>();
        
        for (int i = 0; i <= 100; i += 10)
        {
            if (ct.IsCancellationRequested) break;
            
            // Send progress update via shared memory
            await stream.SendResponseAsync(new HistoryResponse
            {
                Progress = i
            });
            
            Console.WriteLine($"  Sent progress: {i}%");
            
            // Simulate work
            await Task.Delay(200, ct);
            
            // Generate history items at certain milestones
            if (i % 25 == 0 && i > 0)
            {
                historyItems.Add($"Item processed at {i}% - {DateTime.UtcNow:HH:mm:ss}");
            }
        }
        
        // Add some final items
        historyItems.Add("Final item 1 - Analysis complete");
        historyItems.Add("Final item 2 - Data verified");
        historyItems.Add("Final item 3 - Report generated");
        
        // Send the final result
        var result = new HistoryResult();
        result.Items.AddRange(historyItems);
        
        await stream.SendResponseAsync(new HistoryResponse
        {
            Result = result
        });
        
        Console.WriteLine($"Operation complete! Sent {historyItems.Count} result items.");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Operation cancelled.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}
