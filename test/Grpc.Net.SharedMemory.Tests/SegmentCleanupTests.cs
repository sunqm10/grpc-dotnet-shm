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
}
