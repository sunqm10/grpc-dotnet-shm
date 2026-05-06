#region Copyright notice and license

// Copyright 2026 The gRPC Authors
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

/// <summary>
/// Phase Y per-frame ZC anchor FIFO unit tests.
/// <para>
/// Tests the new multi-anchor protocol that replaces the legacy
/// single-anchor + chain-budget design. The FIFO supports concurrent
/// in-flight ZC reservations while preserving the cross-process invariant
/// that <c>header.ReadIdx</c> never advances past held bytes.
/// </para>
/// </summary>
[TestFixture]
public class PerFrameZcAnchorFifoTests
{
    private static ShmRing CreateRing(int capacity = 1024 * 1024)
    {
        // 1 MiB minimum so EnsureZcAnchorFifo allocates a non-trivial slot
        // count (adaptiveMin = min(64KiB, cap/16) = 64 KiB → 16 slots).
        var memory = new byte[ShmConstants.RingHeaderSize + capacity];
        return new ShmRing(memory, 0, (ulong)capacity);
    }

    [Test]
    public void EnsureZcAnchorFifo_FirstCall_AllocatesPowerOfTwoCapacity()
    {
        using var ring = CreateRing();
        Assert.That(ring.AnchorFifoCapacity, Is.Zero, "FIFO must start unallocated.");
        ring.EnsureZcAnchorFifo();
        var cap = ring.AnchorFifoCapacity;
        Assert.That(cap, Is.GreaterThanOrEqualTo(16),
            "FIFO must have at least 16 slots (minimum from formula).");
        Assert.That((cap & (cap - 1)), Is.Zero,
            "FIFO capacity must be a power of two for masking arithmetic.");
    }

    [Test]
    public void EnsureZcAnchorFifo_DoubleCall_Idempotent()
    {
        using var ring = CreateRing();
        ring.EnsureZcAnchorFifo();
        var cap1 = ring.AnchorFifoCapacity;
        ring.EnsureZcAnchorFifo();
        var cap2 = ring.AnchorFifoCapacity;
        Assert.That(cap2, Is.EqualTo(cap1),
            "Idempotent init must not reallocate.");
    }

    [Test]
    public void TryBeginPerFrameZc_BeforeInit_ReturnsMinusOne()
    {
        using var ring = CreateRing();
        // No EnsureZcAnchorFifo() call.
        var slot = ring.TryBeginPerFrameZc(baseIdx: 0, size: 65536);
        Assert.That(slot, Is.EqualTo(-1),
            "TryBegin without EnsureZcAnchorFifo must refuse with -1.");
    }

    [Test]
    public void TryBeginPerFrameZc_SingleFrame_ReturnsValidSlot()
    {
        using var ring = CreateRing();
        ring.EnsureZcAnchorFifo();
        var slot = ring.TryBeginPerFrameZc(baseIdx: 100, size: 65536);
        Assert.That(slot, Is.GreaterThanOrEqualTo(0));
        Assert.That(ring.InFlightAnchorCount, Is.EqualTo(1));
    }

    [Test]
    public void TryBeginPerFrameZc_FillToBackpressureThreshold_ReturnsMinusOne()
    {
        using var ring = CreateRing();
        ring.EnsureZcAnchorFifo();
        var cap = ring.AnchorFifoCapacity;
        // Allocate up to (cap * 3 / 4) slots successfully; the next must fail.
        var threshold = cap * 3 / 4;
        for (var i = 0; i < threshold; i++)
        {
            var slot = ring.TryBeginPerFrameZc(baseIdx: (ulong)(i * 65536), size: 65536);
            Assert.That(slot, Is.GreaterThanOrEqualTo(0),
                $"Allocation {i} below threshold must succeed; got {slot}.");
        }
        var rejected = ring.TryBeginPerFrameZc(baseIdx: (ulong)(threshold * 65536), size: 65536);
        Assert.That(rejected, Is.EqualTo(-1),
            "Allocation at >=75% must self-disable to force copy-path.");
    }

    [Test]
    public void ReleasePerFrameZc_FifoEmpty_AdvancesHeaderReadIdx()
    {
        using var ring = CreateRing();
        ring.EnsureZcAnchorFifo();
        // Record starting readIdx (should be 0 on a fresh ring).
        var startReadIdx = ring.GetState().ReadIdx;
        Assert.That(startReadIdx, Is.Zero);

        var slot = ring.TryBeginPerFrameZc(baseIdx: 100, size: 200);
        Assert.That(slot, Is.GreaterThanOrEqualTo(0));
        // readIdx should NOT advance while anchor is held.
        Assert.That(ring.GetState().ReadIdx, Is.EqualTo(startReadIdx),
            "readIdx must stay at start while anchor is held.");

        ring.ReleasePerFrameZc(slot);
        Assert.That(ring.InFlightAnchorCount, Is.Zero);
        Assert.That(ring.GetState().ReadIdx, Is.EqualTo((ulong)300),
            "After release, readIdx must advance to baseIdx + size = 300.");
    }

    [Test]
    public void ReleasePerFrameZc_OutOfOrder_DrainsInFifoOrder()
    {
        // Allocate 3 slots; release in order [2, 0, 1]; verify readIdx
        // advances correctly:
        //   - release(2): head still 0 (not released) → readIdx unchanged
        //   - release(0): drains to slot 0's EndIdx; slot 1 still held → stop
        //   - release(1): drains to slot 1, then slot 2 (already released)
        using var ring = CreateRing();
        ring.EnsureZcAnchorFifo();

        var s0 = ring.TryBeginPerFrameZc(baseIdx: 0, size: 100);
        var s1 = ring.TryBeginPerFrameZc(baseIdx: 100, size: 100);
        var s2 = ring.TryBeginPerFrameZc(baseIdx: 200, size: 100);
        Assert.That(s0, Is.GreaterThanOrEqualTo(0));
        Assert.That(s1, Is.GreaterThanOrEqualTo(0));
        Assert.That(s2, Is.GreaterThanOrEqualTo(0));

        ring.ReleasePerFrameZc(s2);
        Assert.That(ring.GetState().ReadIdx, Is.Zero,
            "Releasing tail slot must NOT advance readIdx (head still held).");
        Assert.That(ring.InFlightAnchorCount, Is.EqualTo(3),
            "Slot count unchanged: head not yet drained.");

        ring.ReleasePerFrameZc(s0);
        Assert.That(ring.GetState().ReadIdx, Is.EqualTo((ulong)100),
            "After head release, readIdx advances to slot 0's EndIdx; " +
            "slot 1 still held so drain stops there.");

        ring.ReleasePerFrameZc(s1);
        Assert.That(ring.GetState().ReadIdx, Is.EqualTo((ulong)300),
            "After slot 1 release, drain proceeds through slot 2 (already " +
            "released) to its EndIdx.");
        Assert.That(ring.InFlightAnchorCount, Is.Zero,
            "All slots drained.");
    }

    [Test]
    public void CommitReadAnchored_NoAnchorsInFlight_AdvancesDirectly()
    {
        using var ring = CreateRing();
        ring.EnsureZcAnchorFifo();
        ring.CommitReadAnchored(baseIdx: 0, size: 100);
        Assert.That(ring.GetState().ReadIdx, Is.EqualTo((ulong)100),
            "With empty FIFO, CommitReadAnchored advances readIdx directly.");
    }

    [Test]
    public void CommitReadAnchored_AnchorsInFlight_StashesUntilDrain()
    {
        // Layout: anchor[0..100], non-ZC[100..200], anchor[200..300]
        // After releasing both anchors, readIdx must advance through 300
        // (the second anchor's EndIdx; the stashed non-ZC max=200 is
        // included implicitly in the FIFO since the second anchor's
        // EndIdx covers it).
        using var ring = CreateRing();
        ring.EnsureZcAnchorFifo();

        var s0 = ring.TryBeginPerFrameZc(baseIdx: 0, size: 100);
        ring.CommitReadAnchored(baseIdx: 100, size: 100);  // non-ZC frame 100..200
        // readIdx must NOT advance: anchor s0 is in flight.
        Assert.That(ring.GetState().ReadIdx, Is.Zero,
            "Non-ZC commit during anchor hold must not advance readIdx.");

        var s1 = ring.TryBeginPerFrameZc(baseIdx: 200, size: 100);

        ring.ReleasePerFrameZc(s0);
        Assert.That(ring.GetState().ReadIdx, Is.EqualTo((ulong)100),
            "After s0 release, readIdx advances to its EndIdx.");

        ring.ReleasePerFrameZc(s1);
        // FIFO is now empty; the non-ZC commit at 100..200 was stashed via
        // _pendingNonZcMax=200; the s1 release fold takes max(s1.EndIdx=300,
        // stashed=200) = 300.
        Assert.That(ring.GetState().ReadIdx, Is.EqualTo((ulong)300),
            "After last anchor release, readIdx advances to max(EndIdx, stash).");
    }

    [Test]
    public void CommitReadAnchored_NonZcAfterLastAnchorRelease_FoldedIn()
    {
        // Layout: anchor[0..100], non-ZC[100..250]
        // Release anchor → drain folds in the stashed non-ZC max (250).
        using var ring = CreateRing();
        ring.EnsureZcAnchorFifo();

        var s0 = ring.TryBeginPerFrameZc(baseIdx: 0, size: 100);
        ring.CommitReadAnchored(baseIdx: 100, size: 150);
        Assert.That(ring.GetState().ReadIdx, Is.Zero);

        ring.ReleasePerFrameZc(s0);
        Assert.That(ring.GetState().ReadIdx, Is.EqualTo((ulong)250),
            "Drain folds in non-ZC max (250) which is > s0.EndIdx (100).");
    }

    [Test]
    public void TryBeginPerFrameZc_FifoWrap_SlotIndicesReused()
    {
        // Allocate + release N times where N > slot capacity, verify the
        // FIFO wraps around its physical slot array correctly.
        using var ring = CreateRing();
        ring.EnsureZcAnchorFifo();
        var cap = ring.AnchorFifoCapacity;

        ulong baseIdx = 0;
        const int frameSize = 100;
        for (var i = 0; i < cap * 3; i++)
        {
            var slot = ring.TryBeginPerFrameZc(baseIdx, frameSize);
            Assert.That(slot, Is.GreaterThanOrEqualTo(0),
                $"Iteration {i}: alloc must succeed (FIFO drained between iters).");
            Assert.That(slot, Is.LessThan(cap),
                $"Iteration {i}: slot index must stay within physical capacity.");
            ring.ReleasePerFrameZc(slot);
            baseIdx += frameSize;
        }
        Assert.That(ring.GetState().ReadIdx, Is.EqualTo((ulong)(cap * 3 * frameSize)),
            "Cumulative readIdx must equal total bytes allocated through wraps.");
    }

    [Test]
    public async Task ReleasePerFrameZc_ConcurrentReleases_NoDoublePublish()
    {
        // Stress test: allocate many slots, release them concurrently from
        // multiple threads, verify final readIdx equals the sum of sizes
        // (i.e., no slot got double-published, no slot lost).
        //
        // Frame count is bounded by FIFO capacity × 75% (back-pressure
        // threshold). On a 1 MiB ring the FIFO holds 16 slots → up to 12
        // concurrent allocations succeed without any release in between.
        // We size the test below that to fit fully in flight at once.
        using var ring = CreateRing();
        ring.EnsureZcAnchorFifo();
        var fifoCap = ring.AnchorFifoCapacity;
        var frameCount = (fifoCap * 3 / 4) - 1; // safely below back-pressure threshold
        Assert.That(frameCount, Is.GreaterThan(0),
            "Test sanity: FIFO must be large enough to hold a stress batch.");
        const int frameSize = 4096;
        var slots = new int[frameCount];

        // Reader-thread phase: allocate all slots (single-producer).
        for (var i = 0; i < frameCount; i++)
        {
            slots[i] = ring.TryBeginPerFrameZc((ulong)(i * frameSize), frameSize);
            Assert.That(slots[i], Is.GreaterThanOrEqualTo(0),
                $"Iter {i}: alloc must succeed (frame fits below back-pressure).");
        }
        Assert.That(ring.InFlightAnchorCount, Is.EqualTo(frameCount));

        // Consumer phase: release concurrently from multiple threads.
        var tasks = new Task[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            var slotIdx = slots[i];
            tasks[i] = Task.Run(() => ring.ReleasePerFrameZc(slotIdx));
        }
        await Task.WhenAll(tasks);

        Assert.That(ring.InFlightAnchorCount, Is.Zero,
            "All slots must be drained.");
        Assert.That(ring.GetState().ReadIdx, Is.EqualTo((ulong)(frameCount * frameSize)),
            "Concurrent releases must produce a final readIdx equal to the " +
            "sum of frame sizes — no double-publish, no lost release.");
    }
}
