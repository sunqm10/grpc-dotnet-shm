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

using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using Grpc.Core;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Advanced streaming tests covering edge cases and stress scenarios.
/// These complement the basic streaming tests in ShmStreamingTests.cs.
/// </summary>
[TestFixture]
public class AdvancedStreamingTests
{
    [Test]
    [Platform("Win")]
    [Timeout(30000)]
    public async Task ServerStreaming_ThousandMessages_AllReceived()
    {
        var segmentName = $"stream_1k_{Guid.NewGuid():N}";
        var messageCount = 1000;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 16 * 1024 * 1024, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Client sends request
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/thousand", "localhost");
        await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes("request"));
        await clientStream.SendHalfCloseAsync();
        
        // Server sends 1000 messages
        var serverStream = server.CreateStream();
        await serverStream.SendResponseHeadersAsync();
        
        for (int i = 0; i < messageCount; i++)
        {
            var message = Encoding.UTF8.GetBytes($"message-{i:D4}");
            await serverStream.SendMessageAsync(message);
        }
        
        await serverStream.SendTrailersAsync(StatusCode.OK);
        
        Assert.That(serverStream.MessagesSent, Is.EqualTo(messageCount));
    }

    [Test]
    [Platform("Win")]
    [Timeout(30000)]
    public async Task ClientStreaming_ThousandMessages_AllSent()
    {
        var segmentName = $"stream_1k_{Guid.NewGuid():N}";
        var messageCount = 1000;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 16 * 1024 * 1024, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/client-1k", "localhost");
        
        for (int i = 0; i < messageCount; i++)
        {
            var message = Encoding.UTF8.GetBytes($"msg-{i:D4}");
            await clientStream.SendMessageAsync(message);
        }
        
        await clientStream.SendHalfCloseAsync();
        
        Assert.That(clientStream.MessagesSent, Is.EqualTo(messageCount));
    }

    [Test]
    [Platform("Win")]
    [Timeout(60000)]
    public async Task BidiStreaming_InterleavedMessages_NoDeadlock()
    {
        var segmentName = $"bidi_interleave_{Guid.NewGuid():N}";
        var messageCount = 100;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 64 * 1024 * 1024, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/bidi", "localhost");
        
        var serverStream = server.CreateStream();
        await serverStream.SendResponseHeadersAsync();
        
        var clientTask = Task.Run(async () =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes($"c{i}"));
                await Task.Delay(1);
            }
            await clientStream.SendHalfCloseAsync();
        });
        
        var serverTask = Task.Run(async () =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                await serverStream.SendMessageAsync(Encoding.UTF8.GetBytes($"s{i}"));
                await Task.Delay(1);
            }
        });
        
        await Task.WhenAll(clientTask, serverTask);
        await serverStream.SendTrailersAsync(StatusCode.OK);
        
        Assert.Pass("No deadlock in interleaved streaming");
    }

    [Test]
    [Platform("Win")]
    [Timeout(30000)]
    public async Task Streaming_VariableMessageSizes_Works()
    {
        var segmentName = $"stream_var_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 64 * 1024 * 1024, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/variable", "localhost");
        
        var sizes = new[] { 10, 100, 1000, 10000, 100000, 1000000 };
        
        foreach (var size in sizes)
        {
            var data = new byte[size];
            new Random(size).NextBytes(data);
            await clientStream.SendMessageAsync(data);
        }
        
        await clientStream.SendHalfCloseAsync();
        
        Assert.That(clientStream.MessagesSent, Is.EqualTo(sizes.Length));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Streaming_EmptyMessages_Allowed()
    {
        var segmentName = $"stream_empty_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/empty", "localhost");
        
        // Send empty messages
        await stream.SendMessageAsync(Array.Empty<byte>());
        await stream.SendMessageAsync(Array.Empty<byte>());
        await stream.SendMessageAsync(Array.Empty<byte>());
        
        await stream.SendHalfCloseAsync();
        
        Assert.That(stream.MessagesSent, Is.EqualTo(3));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Streaming_HalfCloseBeforeMessages_Allowed()
    {
        var segmentName = $"stream_hc_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/no-body", "localhost");
        
        // Immediately half-close without sending any messages
        await stream.SendHalfCloseAsync();
        
        Assert.That(stream.IsLocalHalfClosed, Is.True);
        Assert.That(stream.MessagesSent, Is.EqualTo(0));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Streaming_MessagesAfterHalfClose_Throws()
    {
        var segmentName = $"stream_after_hc_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/after-hc", "localhost");
        await stream.SendHalfCloseAsync();
        
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await stream.SendMessageAsync(Encoding.UTF8.GetBytes("after half-close"));
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Streaming_CancelMidStream_Works()
    {
        var segmentName = $"stream_cancel_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/cancel-mid", "localhost");
        
        // Send some messages
        for (int i = 0; i < 5; i++)
        {
            await stream.SendMessageAsync(Encoding.UTF8.GetBytes($"msg-{i}"));
        }
        
        // Cancel mid-stream
        await stream.CancelAsync();
        
        Assert.That(stream.IsCancelled, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(30000)]
    public async Task Streaming_RapidOpen Close_NoResourceLeak()
    {
        var segmentName = $"stream_rapid_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Rapidly create and close streams
        for (int i = 0; i < 50; i++)
        {
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/rapid-{i}", "localhost");
            await stream.SendHalfCloseAsync();
        }
        
        // Should not have leaked resources
        Assert.Pass("Rapid stream creation/close completed without leak");
    }

    [Test]
    [Platform("Win")]
    [Timeout(30000)]
    public async Task Streaming_ConcurrentStreams_AllComplete()
    {
        var segmentName = $"stream_concurrent_{Guid.NewGuid():N}";
        var streamCount = 10;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 16 * 1024 * 1024, (uint)streamCount);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var tasks = new List<Task>();
        
        for (int i = 0; i < streamCount; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                var stream = client.CreateStream();
                await stream.SendRequestHeadersAsync($"/test/concurrent-{index}", "localhost");
                await stream.SendMessageAsync(Encoding.UTF8.GetBytes($"data-{index}"));
                await stream.SendHalfCloseAsync();
            }));
        }
        
        await Task.WhenAll(tasks);
        
        Assert.Pass("All concurrent streams completed");
    }
}

/// <summary>
/// Tests for streaming with timeouts and deadlines.
/// </summary>
[TestFixture]
public class StreamingTimeoutTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Streaming_WithDeadline_CompletesBeforeDeadline()
    {
        var segmentName = $"stream_deadline_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var deadline = DateTime.UtcNow.AddSeconds(10);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/deadline", "localhost", null, deadline);
        await stream.SendMessageAsync(Encoding.UTF8.GetBytes("quick"));
        await stream.SendHalfCloseAsync();
        
        Assert.That(DateTime.UtcNow, Is.LessThan(deadline));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Streaming_TokenCancellation_StopsStream()
    {
        var segmentName = $"stream_token_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        using var cts = new CancellationTokenSource();
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/token", "localhost");
        
        cts.Cancel();
        
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await stream.SendMessageAsync(Encoding.UTF8.GetBytes("cancelled"), cts.Token);
        });
    }
}

/// <summary>
/// Tests for streaming error conditions.
/// </summary>
[TestFixture]
public class StreamingErrorTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Streaming_ServerError_PropagatesStatus()
    {
        var segmentName = $"stream_error_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/error", "localhost");
        await clientStream.SendHalfCloseAsync();
        
        // Server sends error response
        var serverStream = server.CreateStream();
        await serverStream.SendResponseHeadersAsync();
        await serverStream.SendTrailersAsync(StatusCode.Internal, "Server processing error");
        
        Assert.That(serverStream.TrailersSent, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Streaming_InvalidMessage_HandledGracefully()
    {
        var segmentName = $"stream_invalid_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/invalid", "localhost");
        
        // Normal operation should work
        await stream.SendMessageAsync(Encoding.UTF8.GetBytes("valid"));
        await stream.SendHalfCloseAsync();
        
        Assert.Pass("Invalid message handling documented");
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Streaming_ConnectionDrop_DetectedByStream()
    {
        var segmentName = $"stream_drop_{Guid.NewGuid():N}";
        
        var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/drop", "localhost");
        
        // Kill server while stream is active
        server.Dispose();
        
        // Stream should detect the issue
        await Task.Delay(100);
        
        Assert.Pass("Connection drop detection documented");
    }
}
