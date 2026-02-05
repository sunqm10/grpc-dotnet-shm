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
using Mail;
using System.Collections.Concurrent;
using System.Text;

const string SegmentName = "mailer_shm_example";

Console.WriteLine("Mailer Server - Shared Memory Bidirectional Streaming");
Console.WriteLine("======================================================");
Console.WriteLine($"Segment name: {SegmentName}");
Console.WriteLine();

// Repository to track mailboxes
var mailQueues = new ConcurrentDictionary<string, MailQueue>();

// Create the shared memory listener using ShmControlListener for grpc-go-shmem compatibility
using var listener = new ShmControlListener(SegmentName, ringCapacity: 1024 * 1024, maxStreams: 100);
Console.WriteLine($"Server listening on shared memory segment: {SegmentName}");
Console.WriteLine("Press Ctrl+C to stop the server.");
Console.WriteLine();

// Simulate incoming mail in background
var mailSimulator = Task.Run(async () =>
{
    var random = new Random();
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(random.Next(1, 5)));
        foreach (var queue in mailQueues.Values)
        {
            queue.AddMail();
        }
    }
});

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Handle incoming bidirectional streaming connections
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
                        // Get mailbox name from headers
                        var mailboxNameBytes = stream.RequestHeaders?.Metadata?.FirstOrDefault(h => h.Key == "mailbox-name")?.Values.FirstOrDefault();
                        var mailboxName = mailboxNameBytes != null ? Encoding.UTF8.GetString(mailboxNameBytes) : "default";
                        var queue = mailQueues.GetOrAdd(mailboxName, _ => new MailQueue());
                        
                        Console.WriteLine($"New mailbox connection: {mailboxName}");
                        
                        // Handle bidirectional streaming
                        await HandleMailboxStreamAsync(stream, queue, mailboxName, cts.Token);
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

static async Task HandleMailboxStreamAsync(ShmGrpcStream stream, MailQueue queue, string mailboxName, CancellationToken ct)
{
    try
    {
        await stream.SendResponseHeadersAsync();

        // Send initial state
        await stream.SendMessageAsync(new MailboxMessage
        {
            New = queue.NewCount,
            Forwarded = queue.ForwardedCount,
            Reason = MailboxMessage.Types.Reason.Received
        }.ToByteArray());
        
        // Subscribe to mail updates
        queue.OnMailReceived += async () =>
        {
            try
            {
                await stream.SendMessageAsync(new MailboxMessage
                {
                    New = queue.NewCount,
                    Forwarded = queue.ForwardedCount,
                    Reason = MailboxMessage.Types.Reason.Received
                }.ToByteArray());
            }
            catch { }
        };
        
        // Handle forward requests from client
        await foreach (var msg in stream.ReceiveMessagesAsync(ct))
        {
            if (msg == null) break;
            
            var request = ForwardMailMessage.Parser.ParseFrom(msg);
            queue.ForwardMail();
            Console.WriteLine($"[{mailboxName}] Mail forwarded");
            
            await stream.SendMessageAsync(new MailboxMessage
            {
                New = queue.NewCount,
                Forwarded = queue.ForwardedCount,
                Reason = MailboxMessage.Types.Reason.Forwarded
            }.ToByteArray());
        }

        await stream.SendTrailersAsync(StatusCode.OK);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{mailboxName}] Error: {ex.Message}");
    }
    finally
    {
        Console.WriteLine($"[{mailboxName}] Disconnected");
    }
}

class MailQueue
{
    private int _newCount;
    private int _forwardedCount;
    
    public event Action? OnMailReceived;
    
    public int NewCount => _newCount;
    public int ForwardedCount => _forwardedCount;
    
    public void AddMail()
    {
        Interlocked.Increment(ref _newCount);
        OnMailReceived?.Invoke();
    }
    
    public void ForwardMail()
    {
        if (_newCount > 0)
        {
            Interlocked.Decrement(ref _newCount);
            Interlocked.Increment(ref _forwardedCount);
        }
    }
}
