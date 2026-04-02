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

using System.Collections.Concurrent;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
public class SegmentCleanupTests
{
    [Test]
    public void TryRemoveSegmentsByPrefix_RemovesMatchingFiles()
    {
        // Arrange — create fake stale segment files
        var prefix = $"test_cleanup_{Guid.NewGuid():N}";
        var tempDir = Path.GetTempPath();
        var files = new[]
        {
            Path.Combine(tempDir, $"grpc_shm_{prefix}_conn_1"),
            Path.Combine(tempDir, $"grpc_shm_{prefix}_conn_2"),
            Path.Combine(tempDir, $"grpc_shm_{prefix}_conn_99"),
        };

        foreach (var f in files)
        {
            File.WriteAllText(f, "stale");
        }

        // Also create a file that should NOT be cleaned (different prefix)
        var unrelatedFile = Path.Combine(tempDir, $"grpc_shm_other_{Guid.NewGuid():N}");
        File.WriteAllText(unrelatedFile, "keep");

        try
        {
            // Act
            var removed = Segment.TryRemoveSegmentsByPrefix($"{prefix}_conn_");

            // Assert
            Assert.That(removed, Is.EqualTo(3));
            foreach (var f in files)
            {
                Assert.That(File.Exists(f), Is.False, $"File should have been deleted: {f}");
            }
            Assert.That(File.Exists(unrelatedFile), Is.True, "Unrelated file should not be deleted");
        }
        finally
        {
            // Cleanup
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { }
            }
            try { File.Delete(unrelatedFile); } catch { }
        }
    }

    [Test]
    public void TryRemoveSegmentsByPrefix_NoMatchingFiles_ReturnsZero()
    {
        var removed = Segment.TryRemoveSegmentsByPrefix($"nonexistent_{Guid.NewGuid():N}_");
        Assert.That(removed, Is.EqualTo(0));
    }

    [Test]
    public void TryRemoveStaleConnectionSegments_CleansUpConnFiles()
    {
        // Arrange
        var baseName = $"test_stale_{Guid.NewGuid():N}";
        var tempDir = Path.GetTempPath();
        var connFile = Path.Combine(tempDir, $"grpc_shm_{baseName}_conn_1");
        File.WriteAllText(connFile, "stale");

        try
        {
            // Act
            Segment.TryRemoveStaleConnectionSegments(baseName);

            // Assert
            Assert.That(File.Exists(connFile), Is.False);
        }
        finally
        {
            try { File.Delete(connFile); } catch { }
        }
    }

    [Test]
    public void CreateControlSegment_DoesNotAutoCleanConnSegments()
    {
        // CreateControlSegment intentionally does NOT clean conn segments
        // because it cannot distinguish a crashed predecessor from a live
        // concurrent instance (especially on Linux where unlink succeeds
        // even for files held by another process).
        var baseName = $"test_ctl_{Guid.NewGuid():N}";
        var tempDir = Path.GetTempPath();
        var staleConn = Path.Combine(tempDir, $"grpc_shm_{baseName}_conn_1");
        File.WriteAllText(staleConn, "stale_data");

        try
        {
            using var ctl = Segment.CreateControlSegment(baseName);

            // Conn segment should still exist — not auto-cleaned
            Assert.That(File.Exists(staleConn), Is.True,
                "CreateControlSegment should NOT auto-clean conn segments");

            // Explicit cleanup works when caller guarantees no live instance
            Segment.TryRemoveStaleConnectionSegments(baseName);
            Assert.That(File.Exists(staleConn), Is.False,
                "Explicit TryRemoveStaleConnectionSegments should clean conn segments");
        }
        finally
        {
            try { File.Delete(staleConn); } catch { }
            Segment.TryRemoveSegment(baseName + ShmConstants.ControlSegmentSuffix);
        }
    }

    [Test]
    public void CreateControlSegment_LiveEndpointRejectsSecondCreation()
    {
        // Sequential contention: while the first server holds a control segment,
        // a second creation attempt on the same baseName must fail with IOException
        // and must NOT silently delete/steal the first server's endpoint.
        var baseName = $"test_seq_{Guid.NewGuid():N}";
        Segment? first = null;

        try
        {
            first = Segment.CreateControlSegment(baseName);
            Assert.That(first, Is.Not.Null, "First server should create successfully");

            // Second server attempts the same baseName while first is alive.
            var ex = Assert.Throws<IOException>(() =>
            {
                using var second = Segment.CreateControlSegment(baseName);
            });

            Assert.That(ex!.Message, Does.Contain(baseName).Or.Contain("already exists"),
                "Exception should identify the contested endpoint");

            // First server's segment must still be functional (not stolen).
            Assert.That(first.RingA, Is.Not.Null, "First server's RingA must still be valid");
            Assert.That(first.RingB, Is.Not.Null, "First server's RingB must still be valid");
        }
        finally
        {
            first?.Dispose();
            Segment.TryRemoveSegment(baseName + ShmConstants.ControlSegmentSuffix);
        }
    }

    [Test]
    public void CreateControlSegment_ConcurrentRace_ExactlyOneWins()
    {
        // True concurrent race: N threads attempt to create the same control
        // segment simultaneously. Exactly one must succeed; all others must
        // throw. No endpoint steal or split-brain may occur.
        const int racers = 8;
        var baseName = $"test_race_{Guid.NewGuid():N}";
        var barrier = new Barrier(racers);
        var winners = new ConcurrentBag<Segment>();
        var losers = new ConcurrentBag<Exception>();

        var threads = new Thread[racers];
        for (var i = 0; i < racers; i++)
        {
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait(); // align all threads to the same instant
                try
                {
                    var seg = Segment.CreateControlSegment(baseName);
                    winners.Add(seg);
                }
                catch (Exception ex)
                {
                    losers.Add(ex);
                }
            });
            threads[i].IsBackground = true;
            threads[i].Start();
        }

        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(10));

        try
        {
            Assert.That(winners.Count, Is.EqualTo(1),
                $"Exactly one racer must win, but {winners.Count} won");
            Assert.That(losers.Count, Is.EqualTo(racers - 1),
                $"All other racers must fail, but {losers.Count} failed");

            // All losers must have thrown IOException.
            foreach (var ex in losers)
            {
                Assert.That(ex, Is.InstanceOf<IOException>(),
                    $"Loser exception should be IOException, got {ex.GetType().Name}: {ex.Message}");
            }

            // Winner's segment must be valid.
            var winner = winners.First();
            Assert.That(winner.RingA, Is.Not.Null);
            Assert.That(winner.RingB, Is.Not.Null);
        }
        finally
        {
            foreach (var seg in winners) seg.Dispose();
            Segment.TryRemoveSegment(baseName + ShmConstants.ControlSegmentSuffix);
        }
    }

    [Test]
    public void CreateControlSegment_SucceedsAfterPreviousDispose()
    {
        // After the first server disposes (simulating clean shutdown or crash
        // recovery), a new server must be able to create the same endpoint.
        var baseName = $"test_reuse_{Guid.NewGuid():N}";

        try
        {
            // First server starts and stops.
            using (var first = Segment.CreateControlSegment(baseName))
            {
                Assert.That(first, Is.Not.Null);
            }

            // Second server starts on the same baseName — must succeed.
            using var second = Segment.CreateControlSegment(baseName);
            Assert.That(second, Is.Not.Null, "Second server should create after first disposed");
            Assert.That(second.RingA, Is.Not.Null);
        }
        finally
        {
            Segment.TryRemoveSegment(baseName + ShmConstants.ControlSegmentSuffix);
        }
    }
}
