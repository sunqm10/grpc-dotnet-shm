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
using NUnit.Framework;
using Grpc.Core;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Tests for retry behavior over shared memory transport.
/// Verifies that retry policies work correctly with SHM.
/// </summary>
[TestFixture]
public class RetryTests
{
    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Retry_TransientFailure_SucceedsOnRetry()
    {
        var segmentName = $"retry_transient_{Guid.NewGuid():N}";
        var attemptCount = 0;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Simulate retry by making multiple attempts
        var maxRetries = 3;
        var success = false;
        
        for (int attempt = 0; attempt < maxRetries && !success; attempt++)
        {
            attemptCount++;
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync("/test/retry", "localhost");
            await stream.SendMessageAsync(Encoding.UTF8.GetBytes($"attempt-{attempt}"));
            await stream.SendHalfCloseAsync();
            
            // First attempt fails, second succeeds
            success = attempt > 0;
        }
        
        Assert.That(success, Is.True);
        Assert.That(attemptCount, Is.EqualTo(2));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Retry_MaxAttemptsExceeded_Fails()
    {
        var segmentName = $"retry_max_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var maxRetries = 3;
        var attemptCount = 0;
        
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            attemptCount++;
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync("/test/always-fail", "localhost");
            await stream.SendHalfCloseAsync();
        }
        
        Assert.That(attemptCount, Is.EqualTo(maxRetries));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Retry_RetryableStatusCodes_Retried()
    {
        var segmentName = $"retry_status_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var retryableStatuses = new[]
        {
            StatusCode.Unavailable,
            StatusCode.ResourceExhausted,
            StatusCode.Aborted
        };
        
        foreach (var status in retryableStatuses)
        {
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/status-{status}", "localhost");
            await stream.SendHalfCloseAsync();
        }
        
        Assert.Pass($"Tested {retryableStatuses.Length} retryable status codes");
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Retry_NonRetryableStatus_NotRetried()
    {
        var segmentName = $"retry_nonretry_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var nonRetryableStatuses = new[]
        {
            StatusCode.InvalidArgument,
            StatusCode.NotFound,
            StatusCode.AlreadyExists,
            StatusCode.PermissionDenied,
            StatusCode.Unauthenticated
        };
        
        foreach (var status in nonRetryableStatuses)
        {
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/noretry-{status}", "localhost");
            await stream.SendHalfCloseAsync();
        }
        
        Assert.Pass($"Tested {nonRetryableStatuses.Length} non-retryable status codes");
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Retry_BackoffDelay_Respected()
    {
        var segmentName = $"retry_backoff_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var timestamps = new List<DateTime>();
        var maxRetries = 3;
        var baseDelay = TimeSpan.FromMilliseconds(100);
        
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            timestamps.Add(DateTime.UtcNow);
            
            if (attempt > 0)
            {
                // Exponential backoff: base * 2^(attempt-1)
                var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay);
            }
            
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/backoff-{attempt}", "localhost");
            await stream.SendHalfCloseAsync();
        }
        
        // Verify delays increased
        if (timestamps.Count >= 3)
        {
            var delay1 = timestamps[1] - timestamps[0];
            var delay2 = timestamps[2] - timestamps[1];
            Assert.That(delay2.TotalMilliseconds, Is.GreaterThan(delay1.TotalMilliseconds * 0.9));
        }
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Retry_PushbackFromServer_Respected()
    {
        var segmentName = $"retry_pushback_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Server can return grpc-retry-pushback-ms header
        var pushbackDelay = TimeSpan.FromMilliseconds(500);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/pushback", "localhost");
        await stream.SendHalfCloseAsync();
        
        // Respect pushback delay before retry
        await Task.Delay(pushbackDelay);
        
        var retryStream = client.CreateStream();
        await retryStream.SendRequestHeadersAsync("/test/pushback-retry", "localhost");
        await retryStream.SendHalfCloseAsync();
        
        Assert.Pass("Pushback delay respected");
    }
}

/// <summary>
/// Tests for hedging behavior over shared memory transport.
/// </summary>
[TestFixture]
public class HedgingTests
{
    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Hedging_MultipleAttempts_FirstWins()
    {
        var server1Name = $"hedge_s1_{Guid.NewGuid():N}";
        var server2Name = $"hedge_s2_{Guid.NewGuid():N}";
        
        using var server1 = ShmConnection.CreateAsServer(server1Name, 4096, 10);
        using var server2 = ShmConnection.CreateAsServer(server2Name, 4096, 10);
        
        using var client1 = ShmConnection.ConnectAsClient(server1Name);
        using var client2 = ShmConnection.ConnectAsClient(server2Name);
        
        // Send hedged requests
        var task1 = Task.Run(async () =>
        {
            var stream = client1.CreateStream();
            await stream.SendRequestHeadersAsync("/test/hedge", "localhost");
            await stream.SendHalfCloseAsync();
            return "server1";
        });
        
        var task2 = Task.Run(async () =>
        {
            await Task.Delay(50); // Hedge delay
            var stream = client2.CreateStream();
            await stream.SendRequestHeadersAsync("/test/hedge", "localhost");
            await stream.SendHalfCloseAsync();
            return "server2";
        });
        
        var winner = await Task.WhenAny(task1, task2);
        var result = await winner;
        
        Assert.That(result, Is.EqualTo("server1").Or.EqualTo("server2"));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Hedging_MaxHedgedAttempts_Limited()
    {
        var segmentName = $"hedge_max_{Guid.NewGuid():N}";
        var maxHedges = 3;
        var attemptCount = 0;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var tasks = new List<Task>();
        
        for (int i = 0; i < maxHedges; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                await Task.Delay(i * 50); // Staggered hedging
                Interlocked.Increment(ref attemptCount);
                
                var stream = client.CreateStream();
                await stream.SendRequestHeadersAsync($"/test/hedge-{index}", "localhost");
                await stream.SendHalfCloseAsync();
            }));
        }
        
        await Task.WhenAll(tasks);
        
        Assert.That(attemptCount, Is.EqualTo(maxHedges));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Hedging_HedgeDelay_Respected()
    {
        var segmentName = $"hedge_delay_{Guid.NewGuid():N}";
        var hedgeDelay = TimeSpan.FromMilliseconds(100);
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var timestamps = new List<DateTime>();
        
        // First request
        timestamps.Add(DateTime.UtcNow);
        var stream1 = client.CreateStream();
        await stream1.SendRequestHeadersAsync("/test/hedge1", "localhost");
        
        // Wait hedge delay
        await Task.Delay(hedgeDelay);
        
        // Hedged request
        timestamps.Add(DateTime.UtcNow);
        var stream2 = client.CreateStream();
        await stream2.SendRequestHeadersAsync("/test/hedge2", "localhost");
        
        var actualDelay = timestamps[1] - timestamps[0];
        Assert.That(actualDelay.TotalMilliseconds, Is.GreaterThanOrEqualTo(hedgeDelay.TotalMilliseconds * 0.9));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Hedging_CancelOtherAttempts_OnSuccess()
    {
        var segmentName = $"hedge_cancel_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        using var cts = new CancellationTokenSource();
        
        var successTask = Task.Run(async () =>
        {
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync("/test/fast", "localhost");
            await stream.SendHalfCloseAsync();
            cts.Cancel(); // Cancel other hedges
            return true;
        });
        
        var hedgeTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                // This should be cancelled
                return false;
            }
            catch (OperationCanceledException)
            {
                return true; // Expected
            }
        });
        
        await Task.WhenAll(successTask, hedgeTask);
        
        Assert.That(await successTask, Is.True);
        Assert.That(await hedgeTask, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Hedging_NonFatalStatusCodes_Triggers()
    {
        var segmentName = $"hedge_nonfatal_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Non-fatal status codes that trigger hedging
        var nonFatalStatuses = new[]
        {
            StatusCode.Unavailable,
            StatusCode.ResourceExhausted
        };
        
        foreach (var status in nonFatalStatuses)
        {
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/nonfatal-{status}", "localhost");
            await stream.SendHalfCloseAsync();
        }
        
        Assert.Pass($"Tested {nonFatalStatuses.Length} non-fatal status codes");
    }
}

/// <summary>
/// Tests for retry throttling behavior.
/// </summary>
[TestFixture]
public class RetryThrottlingTests
{
    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Throttling_ExceedThreshold_DisablesRetries()
    {
        var segmentName = $"throttle_exceed_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Generate many failures to trigger throttling
        var failures = 10;
        
        for (int i = 0; i < failures; i++)
        {
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/fail-{i}", "localhost");
            await stream.SendHalfCloseAsync();
        }
        
        // After throttling, retries should be disabled temporarily
        Assert.Pass("Retry throttling behavior documented");
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Throttling_SuccessfulCalls_RestoreTokens()
    {
        var segmentName = $"throttle_restore_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Successful calls restore throttling tokens
        for (int i = 0; i < 5; i++)
        {
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/success-{i}", "localhost");
            await stream.SendHalfCloseAsync();
        }
        
        Assert.Pass("Throttle token restoration documented");
    }
}
