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

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Grpc.Net.SharedMemory.Synchronization;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Exception thrown when the ring buffer is closed.
/// </summary>
public class RingClosedException : Exception
{
    public RingClosedException() : base("Ring buffer is closed") { }
    public RingClosedException(string message) : base(message) { }
}

/// <summary>
/// Represents a write reservation for zero-copy writes to the ring buffer.
/// </summary>
public readonly struct WriteReservation
{
    /// <summary>First contiguous slice (from write position to end of buffer or requested size).</summary>
    public Memory<byte> First { get; init; }

    /// <summary>Second contiguous slice (from start of buffer) - may be empty if First has enough space.</summary>
    public Memory<byte> Second { get; init; }

    /// <summary>Total reserved bytes.</summary>
    public int Length => First.Length + Second.Length;

    internal ShmRing? Ring { get; init; }
    internal ulong WriteIdx { get; init; }
    internal int MaxBytes { get; init; }
}

/// <summary>
/// MPSC (multi-producer single-consumer) write slot. Returned by
/// <see cref="ShmRing.MpscReserveWrite"/>; multiple writers across threads
/// can hold non-overlapping slots simultaneously and serialize their writes
/// without synchronisation INSIDE the slot region. Publication order
/// is enforced at <see cref="Commit"/>.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><see cref="ShmRing.MpscReserveWrite"/> atomically claims a
///     <c>[base, base+size)</c> region via CAS on a process-local
///     <c>_claimedWriteIdx</c>. Returns when the claim succeeds (and ring
///     space is available; otherwise loops on <see cref="ShmRing.WaitForSpace"/>
///     against <c>header.ReadIdx</c>).</description></item>
///   <item><description>Caller writes payload bytes into <see cref="First"/>
///     / <see cref="Second"/>. No synchronisation needed; the slot is
///     exclusive to this writer.</description></item>
///   <item><description>Caller invokes <see cref="Commit"/>. This spins until
///     <c>header.WriteIdx == base</c> (i.e., all prior claims have published)
///     and then publishes <c>header.WriteIdx = base + size</c>. The last
///     writer to leave the in-flight cohort fires <see cref="ShmRing.SignalDataIfNeeded"/>
///     once for the whole cohort (deferred-signal batching).</description></item>
///   <item><description>If the caller throws or returns without calling
///     <see cref="Commit"/>, <see cref="Dispose"/> writes a PAD frame into
///     the slot and publishes it. Successor writers' publish-spin observes
///     forward progress; reader skips PAD silently.</description></item>
/// </list>
/// </para>
/// <para>
/// The struct is mutable to flip <c>_committed</c> from false to true at
/// commit time, so it must be used via <c>using var</c> or
/// <c>using (...)</c>; copying it inadvertently would let multiple
/// consumers race on Commit/Dispose.
/// </para>
/// </remarks>
public struct MpscWriteSlot : IDisposable
{
    /// <summary>First contiguous slice (from claimed start to end of ring or requested size).</summary>
    public Memory<byte> First { get; internal set; }

    /// <summary>Second contiguous slice (from start of ring) — empty when no wrap.</summary>
    public Memory<byte> Second { get; internal set; }

    /// <summary>Total claimed bytes.</summary>
    public int Length => First.Length + Second.Length;

    internal ShmRing? Ring { get; set; }
    internal ulong BaseIdx { get; set; }
    internal int Size { get; set; }
    private bool _committed;
    private bool _disposed;

    /// <summary>
    /// Publishes the slot to the ring, in claim order.
    /// Spins until <c>header.WriteIdx == BaseIdx</c>, then advances it by
    /// <see cref="Size"/>. After the last in-flight publisher returns,
    /// fires <see cref="ShmRing.SignalDataIfNeeded"/> if any waiters
    /// registered.
    /// </summary>
    public void Commit()
    {
        if (_committed) return;
        if (Ring is null) throw new InvalidOperationException("Slot has no associated ring.");
        Ring.MpscPublish(BaseIdx, Size, isPad: false);
        _committed = true;
    }

    /// <summary>
    /// Releases the slot. If <see cref="Commit"/> was not called (caller
    /// threw or cancelled), fills the slot with a PAD frame and publishes
    /// it so successor writers can advance past this orphan claim.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_committed) return;
        if (Ring is null) return;
        Ring.MpscPublishOrphanAsPad(BaseIdx, Size, First, Second);
    }
}

/// <summary>
/// Represents a read reservation for zero-copy reads from the ring buffer.
/// </summary>
public readonly struct ReadReservation
{
    /// <summary>First contiguous slice.</summary>
    public ReadOnlyMemory<byte> First { get; init; }

    /// <summary>Second contiguous slice (handles wrap-around).</summary>
    public ReadOnlyMemory<byte> Second { get; init; }

    /// <summary>Total bytes available to read.</summary>
    public int Length => First.Length + Second.Length;

    internal ShmRing? Ring { get; init; }
    /// <summary>
    /// For speculative CommitRead: the shared ReadIdx at reservation time.
    /// </summary>
    internal ulong CommitReadIdx { get; init; }
    internal int MaxBytes { get; init; }
}

/// <summary>
/// Single-Producer Single-Consumer (SPSC) ring buffer operating over shared memory
/// with event-driven blocking. This implementation provides high-performance
/// cross-process communication with zero-copy operations and minimal kernel calls
/// through futex-based (Linux) or named event (Windows) synchronization.
///
/// This implementation matches the grpc-go-shmem ring buffer for interoperability.
/// </summary>
public sealed class ShmRing : IDisposable
{
    private readonly Memory<byte> _memory;
    private readonly ulong _capacity;
    private readonly ulong _capMask;
    private readonly int _headerOffset;
    private readonly int _dataOffset;
    private readonly IRingSync? _sync;
    private readonly bool _isOwner;

    private volatile bool _localClosed;
    private ulong _pendingReadIdx;

    // Adaptive spin state
    private int _dataSpinCutoff = ShmConstants.SpinIterationsDefault;
    private int _spaceSpinCutoff = ShmConstants.SpinIterationsDefault;

    // Batch write: suppress OS-level data signals until EndBatchWrite.
    // DataSeq is still incremented per-frame so spin waiters see updates.
    private int _batchWriteDepth;

    // ===== MPSC writer state (PR2 in progress) =====
    //
    // The legacy SPSC writer path (ReserveWrite + CommitWrite +
    // BeginBatchWrite/EndBatchWrite) presumes a single writer at a time.
    // The new MPSC path (MpscReserveWrite + MpscWriteSlot.Commit/Dispose)
    // lets N writers across threads claim non-overlapping slots
    // concurrently; publication is serialized in claim order via the
    // publish-spin in MpscPublish.
    //
    // Two paths coexist temporarily during PR2 migration. After all
    // call sites move, the SPSC ReserveWrite/CommitWrite/BatchWrite
    // surface deletes.
    //
    // _claimedWriteIdx: process-local atomic counter. CAS-advanced by
    //   each MpscReserveWrite. `header.WriteIdx <= _claimedWriteIdx`
    //   always (writers may have claimed but not yet published).
    //
    // _publishersInFlight: count of writers between MpscReserveWrite
    //   and the corresponding Commit/Dispose+publish. The last writer
    //   to leave the cohort (`Decrement → 0`) fires the deferred
    //   SignalData if any was deferred. Same batched-signal semantics
    //   as today's WriterLoop FlushBatch with one SignalData per batch.
    //
    // _pendingSignal: 0 or 1. Set by MpscPublish when a writer commits
    //   real data (i.e., not a pure PAD). Cleared+fired by the last
    //   writer out (publishers-in-flight reaches 0) if any waiters are
    //   registered (header.DataWaiters > 0).
    private long _claimedWriteIdx;
    private int _publishersInFlight;
    private int _pendingSignal;

    // Callback invoked during WaitForSpace before blocking, allowing the
    // WriterLoop to drain control frames (e.g. WindowUpdate) that can
    // free space on the remote side and break bidirectional deadlocks.
    internal Action? WaitForSpaceDrainCallback;
    private int _drainRecursionDepth;

    /// <summary>
    /// Creates a new ShmRing from a memory region.
    /// </summary>
    /// <param name="memory">The memory region containing the ring (header + data).</param>
    /// <param name="headerOffset">Offset to the ring header within the memory.</param>
    /// <param name="capacity">The data area capacity (must be power of 2).</param>
    /// <param name="sync">Optional synchronization primitive for cross-process signaling.</param>
    /// <param name="isOwner">If true, this instance owns the ring and will set the Closed flag in shared memory on dispose.</param>
    public ShmRing(Memory<byte> memory, int headerOffset, ulong capacity, IRingSync? sync = null, bool isOwner = true)
    {
        if (capacity == 0 || !IsPowerOfTwo(capacity))
        {
            throw new ArgumentException("Capacity must be a power of two", nameof(capacity));
        }

        var requiredSize = headerOffset + ShmConstants.RingHeaderSize + (int)capacity;
        if (memory.Length < requiredSize)
        {
            throw new ArgumentException($"Memory region too small. Required: {requiredSize}, Got: {memory.Length}", nameof(memory));
        }

        _memory = memory;
        _headerOffset = headerOffset;
        _dataOffset = headerOffset + ShmConstants.RingHeaderSize;
        _capacity = capacity;
        _capMask = capacity - 1;
        _sync = sync;
        _isOwner = isOwner;

        // Initialize pending read index from current shared read index
        ref var header = ref GetHeader();
        _pendingReadIdx = Volatile.Read(ref header.ReadIdx);

        // Initialize MPSC claim cursor from the shared write index. On a
        // fresh segment both are 0; on reconnect the segment may carry
        // a non-zero value and we must continue past it.
        _claimedWriteIdx = (long)Volatile.Read(ref header.WriteIdx);
    }

    /// <summary>
    /// Gets the ring buffer capacity.
    /// </summary>
    public ulong Capacity => _capacity;

    /// <summary>
    /// Gets or sets the wire-level frame encoding used on this ring.
    /// Set once during connection establishment (after control-plane
    /// negotiation) and read on every frame I/O via <see cref="FrameProtocol"/>.
    /// Default is <see cref="Grpc.Net.SharedMemory.Wire.WireFormat.Custom16"/>.
    /// </summary>
    /// <remarks>
    /// The setter is <c>internal</c> on purpose: the wire format is part of
    /// the ring's contract for its lifetime. Changing it after frames have
    /// flowed would corrupt both the writer's and reader's view of the
    /// on-wire layout. Only the connection-establishment code in
    /// <see cref="ShmConnection"/>, <see cref="ShmControlListener"/> and
    /// <see cref="ShmControlHandler"/> may set it.
    /// </remarks>
    public Grpc.Net.SharedMemory.Wire.WireFormat Wire { get; internal set; } = Grpc.Net.SharedMemory.Wire.WireFormat.Custom16;

    /// <summary>
    /// Whether the owning connection negotiated single-stream (ping-pong)
    /// mode. Set once during connection establishment and read by
    /// <see cref="ChainZcBudget"/> to decide how aggressively a multi-frame
    /// chain ZC anchor may consume the ring.
    /// </summary>
    /// <remarks>
    /// In single-stream / ping-pong mode the writer naturally pauses
    /// after each request — the client only sends the next request
    /// after receiving a response — so the chain anchor may safely hold
    /// up to <c>cap - SmallReserve</c> bytes without risking a writer-side
    /// stall. In multi-stream mode the writer can pipeline a follow-up
    /// message before the consumer parses the current one, so the budget
    /// stays at <c>cap/2</c> to leave headroom for the next message's
    /// first frame.
    /// </remarks>
    public bool SingleStreamMode { get; internal set; }

    /// <summary>
    /// Computes the number of bytes the writer can safely reserve right now.
    /// Cross-process safety: the per-frame ZC anchor FIFO holds
    /// <c>header.ReadIdx</c> at the earliest unreleased anchor's BaseIdx
    /// (see <see cref="DrainReleasedAnchors"/>), so the writer's plain
    /// <c>used = writeIdx - readIdx</c> formula already accounts for held
    /// ZC bytes — no shared-memory ZC field needed.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private ulong ComputeAvailableForWrite(ulong writeIdx, ulong readIdx)
    {
        var used = writeIdx - readIdx;
        return used >= _capacity ? 0UL : _capacity - used;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void PublishTarget(ref RingHeader header, ulong target)
    {
        while (true)
        {
            var current = Volatile.Read(ref header.ReadIdx);
            if (target <= current) return;
            if (Interlocked.CompareExchange(ref header.ReadIdx, target, current) == current)
                return;
        }
    }

    /// <summary>Reads the current WriteIdx (for speculative safety checks).</summary>
    internal ulong PeekWriteIdx()
    {
        ref var header = ref GetHeader();
        return Volatile.Read(ref header.WriteIdx);
    }

    // ===== Phase Y: Per-frame ZC anchor FIFO (replaces single-anchor protocol) =====
    //
    // Goal: support multiple in-flight ZC anchors on the same ring, so that a
    // large LPM that exceeds the legacy ChainZcBudget can still take ZC for
    // every frame — and so that two concurrent streams don't reject each
    // other on the at-most-one-ZC gate.
    //
    // Design summary:
    //   - Pre-allocated PerFrameZcSlot[] sized at construction (powers of 2,
    //     bounded by ring_capacity / minZcFrameSize, capped at 4096).
    //   - Reader thread is single producer of the FIFO: it allocates slots
    //     in order and writes their EndIdx + Released=false.
    //   - Consumers (any thread, FramePayload.Release) flip Released=true
    //     and call DrainReleasedAnchors which CAS-advances the head, owning
    //     the publish for that slot.
    //   - header.ReadIdx is advanced ONLY when the head slot is released;
    //     this guarantees cross-process writers never wrap onto held bytes.
    //   - Non-ZC frames committed during anchor hold are stashed in
    //     _pendingNonZcMax and folded into readIdx when the FIFO drains
    //     to empty.
    //
    // Y.1 keeps this side-by-side with the legacy single-anchor protocol;
    // it is callable but not yet wired into any reader. Subsequent commits
    // (Y.2 = FramePayload routing, Y.3 = Custom16 reader switch, Y.4 = H2
    // reader switch, Y.5 = legacy delete) migrate the codecs and finally
    // remove the old _zcActive / _deferredReadIdxTarget / _chainOpen state.
    [StructLayout(LayoutKind.Sequential)]
    internal struct PerFrameZcSlot
    {
        public ulong EndIdx;     // baseIdx + size: the position readIdx should advance to upon release
        public bool Released;    // consumer release flag
        // 7 bytes implicit padding to 16-byte natural alignment
    }

    private PerFrameZcSlot[]? _zcSlots;     // null until InitZcAnchorFifo; once set, non-null forever
    private int _slotMask;                   // _zcSlots.Length - 1
    private int _slotCapacity;               // _zcSlots.Length

    // Monotonic counters; never wrap in realistic deployments (ulong).
    // Reader thread is the single producer of _zcSlotsTail.
    // Consumers CAS-advance _zcSlotsHead.
    private ulong _zcSlotsTail;
    private ulong _zcSlotsHead;

    // Side-channel: max byte position committed via copy path while anchors
    // are in flight. Folded into readIdx publish when FIFO drains to empty.
    private ulong _pendingNonZcMax;

    /// <summary>
    /// Lazily initialises the per-frame ZC anchor FIFO. Idempotent and
    /// thread-safe via Interlocked.CompareExchange of <c>_zcSlots</c>.
    /// </summary>
    /// <remarks>
    /// Slot count = max(16, ring_capacity / minZcFrameSize), rounded up to
    /// the next power of two and capped at 4096. With 1 MiB ring + 64 KiB
    /// adaptive min the FIFO holds 16 slots (256 B); with 256 MiB ring +
    /// 64 KiB min it holds 4096 slots (~64 KiB). The cap bounds memory
    /// even on misconfigured tiny rings.
    /// </remarks>
    internal void EnsureZcAnchorFifo()
    {
        if (_zcSlots != null) return;

        // Mirror IsSpeculativeZcEligible's adaptive-min calculation so the
        // slot capacity tracks the smallest frame that can claim a slot.
        var adaptiveMin = (int)Math.Min(64UL * 1024UL, _capacity / 16);
        if (adaptiveMin < 4 * 1024) adaptiveMin = 4 * 1024;
        var slotsForRing = (int)Math.Max(16UL, _capacity / (ulong)adaptiveMin);
        if (slotsForRing > 4096) slotsForRing = 4096;
        var capacityPow2 = 1;
        while (capacityPow2 < slotsForRing) capacityPow2 <<= 1;

        var fresh = new PerFrameZcSlot[capacityPow2];
        if (Interlocked.CompareExchange(ref _zcSlots, fresh, null) == null)
        {
            // We won the race; publish the metadata. Other paths reading
            // _slotCapacity must use Volatile.Read to pair with this Volatile.Write.
            Volatile.Write(ref _slotMask, capacityPow2 - 1);
            Volatile.Write(ref _slotCapacity, capacityPow2);
        }
    }

    /// <summary>
    /// Allocates a per-frame ZC anchor for a frame at <paramref name="baseIdx"/>
    /// of <paramref name="size"/> bytes. Returns the slot index, or -1 if
    /// the FIFO is back-pressure self-disabled (>=75% full) and the caller
    /// must fall back to the copy path.
    /// </summary>
    /// <remarks>
    /// Reader thread is the SINGLE PRODUCER of the FIFO; this method assumes
    /// no concurrent BeginPerFrameZc on the same ring. Consumer release is
    /// concurrent (different threads).
    /// <para>
    /// Ordering: writes to the slot fields happen BEFORE the Volatile.Write
    /// of <c>_zcSlotsTail</c>. Consumers reading tail with Volatile.Read get
    /// the acquire barrier paired with our release write — they see a fully
    /// initialised slot.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int TryBeginPerFrameZc(ulong baseIdx, int size)
    {
        // Compatibility overload: derive endIdx from caller-supplied
        // baseIdx + size. Callers in the production codec paths pass
        // headerReservation.CommitReadIdx + totalBytes, which is WRONG
        // when earlier ZC anchors hold the publish (CommitReadIdx is
        // stale). Production callers must use the new
        // <see cref="TryBeginPerFrameZc(ulong)"/> overload that takes the
        // exact post-frame ring position. This shape remains for unit
        // tests that directly stage anchor lifecycle scenarios without
        // going through ReserveRead.
        return TryBeginPerFrameZc(baseIdx + (ulong)size);
    }

    /// <summary>
    /// Allocates a per-frame ZC anchor whose EndIdx is
    /// <paramref name="endIdx"/> (the exact ring position immediately
    /// after this frame's bytes). Production callers obtain this from
    /// <see cref="PeekPendingReadIdx"/> after both header and payload
    /// reservations have advanced the local pending cursor; this avoids
    /// the staleness of <c>headerReservation.CommitReadIdx</c> while
    /// earlier anchors hold the published <c>header.ReadIdx</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int TryBeginPerFrameZc(ulong endIdx)
    {
        var slots = _zcSlots;
        if (slots == null) return -1; // not initialised

        var tail = _zcSlotsTail;     // single producer: plain read
        var head = Volatile.Read(ref _zcSlotsHead);
        var inUse = (int)(tail - head);

        // Back-pressure self-disable (slot count): if FIFO is >=75% full,
        // refuse ZC. Bounds in-flight slot count even if frames are tiny.
        if (inUse * 4 >= _slotCapacity * 3) return -1;

        // Back-pressure self-disable (bytes): if granting this anchor would
        // hold ring bytes >= capacity * 3/4 between header.ReadIdx and the
        // new anchor's EndIdx, refuse ZC. Without this gate, large frames
        // (e.g. 128 KiB chunks on a 1 MiB ring or 8 MiB chunks on a 64 MiB
        // ring) can fill the entire ring with held anchors, leaving the
        // writer with no space to commit the next frame and the consumer
        // waiting for that next frame — a circular deadlock. The legacy
        // single-anchor design had this bound implicitly (always 1 frame
        // held); Phase Y's FIFO must enforce it explicitly across multiple
        // in-flight anchors.
        ref var hdr = ref GetHeader();
        var readIdx = Volatile.Read(ref hdr.ReadIdx);
        // endIdx >= readIdx is invariant (anchors commit forward).
        // Strict > so the boundary case (exactly 75%) is allowed,
        // mirroring the slot-count gate which permits 75% of slot
        // capacity to be in-flight before refusing.
        var anchoredBytes = endIdx - readIdx;
        if (anchoredBytes * 4 > _capacity * 3) return -1;

        var slot = (int)(tail & (uint)_slotMask);
        slots[slot].EndIdx = endIdx;
        Volatile.Write(ref slots[slot].Released, false);
        Volatile.Write(ref _zcSlotsTail, tail + 1);
        return slot;
    }

    /// <summary>
    /// Commits a non-ZC frame's bytes. If anchors are in flight, the target
    /// is stashed via CAS-max; the head anchor's drain folds it in when the
    /// FIFO becomes empty. If no anchors, header.ReadIdx is advanced
    /// directly.
    /// </summary>
    /// <remarks>
    /// Reader thread is the single producer of CommitRead too (called from
    /// the same thread that does ReserveRead). We can therefore use the
    /// non-CAS plain-read tail. But _pendingNonZcMax may be also written by
    /// concurrent calls if the caller ever spawns multi-threaded reads;
    /// CAS-max keeps it correct in either case.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CommitReadAnchored(ulong baseIdx, int size)
        => CommitReadAnchoredCore(baseIdx + (ulong)size);

    /// <summary>
    /// Production-path entry: caller has already advanced
    /// <c>_pendingReadIdx</c> via ReserveRead, and the frame's exact
    /// post-bytes ring position is <paramref name="endIdx"/>. This avoids
    /// the staleness of <c>headerReservation.CommitReadIdx</c> when
    /// earlier ZC anchors hold the published <c>header.ReadIdx</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CommitReadAnchored(ulong endIdx) => CommitReadAnchoredCore(endIdx);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitReadAnchoredCore(ulong target)
    {
        var head = Volatile.Read(ref _zcSlotsHead);
        var tail = Volatile.Read(ref _zcSlotsTail);
        if (head == tail)
        {
            ref var hdr = ref GetHeader();
            PublishTarget(ref hdr, target);
            SignalSpaceAvailability(ref hdr);
            return;
        }
        while (true)
        {
            var cur = Volatile.Read(ref _pendingNonZcMax);
            if (target <= cur) return;
            if (Interlocked.CompareExchange(ref _pendingNonZcMax, target, cur) == cur)
                return;
        }
    }

    /// <summary>
    /// Marks a per-frame ZC slot as released and triggers FIFO drain. May
    /// be called from any thread (typically a consumer parser thread via
    /// <see cref="FramePayload.Release"/>).
    /// </summary>
    internal void ReleasePerFrameZc(int slotIdx)
    {
        var slots = _zcSlots;
        if (slots == null)
        {
            // Defensive: should never hit. Slot allocated => slots non-null.
            return;
        }
        Volatile.Write(ref slots[slotIdx].Released, true);
        DrainReleasedAnchors();
    }

    private void DrainReleasedAnchors()
    {
        var slots = _zcSlots;
        if (slots == null) return;
        ref var hdr = ref GetHeader();

        while (true)
        {
            var head = Volatile.Read(ref _zcSlotsHead);
            var tail = Volatile.Read(ref _zcSlotsTail);
            if (head == tail) return; // FIFO empty

            var headSlotIdx = (int)(head & (uint)_slotMask);
            if (!Volatile.Read(ref slots[headSlotIdx].Released))
                return; // earliest still held

            // Capture the slot's EndIdx into a local BEFORE attempting the
            // CAS. After CAS succeeds, the reader producer may immediately
            // wrap and overwrite this slot index; we must hold our own copy.
            var endIdx = slots[headSlotIdx].EndIdx;

            if (Interlocked.CompareExchange(
                    ref _zcSlotsHead, head + 1, head) != head)
                continue; // raced with another consumer; retry

            // Re-read tail to detect "FIFO is now empty after this drain".
            // If so, fold any pending non-ZC commits that arrived during
            // the anchor hold into the publish target.
            var latestTail = Volatile.Read(ref _zcSlotsTail);
            if (head + 1 == latestTail)
            {
                var pending = Interlocked.Exchange(ref _pendingNonZcMax, 0UL);
                if (pending > endIdx) endIdx = pending;
            }

            PublishTarget(ref hdr, endIdx);
            SignalSpaceAvailability(ref hdr);
        }
    }

    /// <summary>Diagnostic: number of in-flight ZC anchors.</summary>
    internal int InFlightAnchorCount =>
        (int)(Volatile.Read(ref _zcSlotsTail) - Volatile.Read(ref _zcSlotsHead));

    /// <summary>Diagnostic: total slot capacity (post-EnsureZcAnchorFifo).</summary>
    internal int AnchorFifoCapacity => Volatile.Read(ref _slotCapacity);

    /// <summary>
    /// Returns the current pending read index (bytes reserved but not yet committed).
    /// Used by speculative CommitRead to commit all bytes up to the current position,
    /// including any deferred frames that precede the speculative frame.
    /// </summary>
    internal ulong PeekPendingReadIdx() => Volatile.Read(ref _pendingReadIdx);

    /// <summary>
    /// Approximate "ring used bytes from the writer's perspective" —
    /// <c>writeIdx - readIdx</c>. Used by the ZC fast path as a back-pressure
    /// hint: if the ring is already heavily occupied, deferring more reads
    /// (which is what ZC effectively does until the consumer releases) would
    /// risk stalling the writer. In that case it is better to take the copy
    /// path so the reader can publish ReadIdx promptly.
    /// </summary>
    /// <remarks>
    /// Approximate because we read writeIdx and readIdx without a fence
    /// between them; the value can momentarily underestimate or overestimate.
    /// That is acceptable: this is just a heuristic, not a correctness gate.
    /// </remarks>
    internal ulong UsedBytesApprox()
    {
        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        return writeIdx - readIdx;
    }

    /// <summary>
    /// Phase Y eligibility check: ring size + payload size threshold ONLY.
    /// The FIFO has its own 75% back-pressure gate inside
    /// <see cref="TryBeginPerFrameZc"/>, and supports concurrent anchors,
    /// so the legacy at-most-one-ZC and used-bytes gates are unnecessary.
    /// </summary>
    /// <remarks>
    /// <para><b>Adaptive minimum payload threshold</b>: 64 KiB on rings ≥ 1 MiB;
    /// progressively smaller on smaller rings so ZC stays useful for the
    /// dominant message size, but never below 4 KiB where memcpy is
    /// faster than ZC bookkeeping.</para>
    /// <para><b>Ring-size gate</b>: the FIFO drain holds header.ReadIdx at
    /// the earliest unreleased anchor's BaseIdx; on a tiny ring even a
    /// single ZC hold could freeze a significant fraction of capacity.
    /// We disable ZC entirely below 1 MiB.</para>
    /// </remarks>
    internal bool IsZcEligibleForAnchor(int payloadLength, bool contiguous)
    {
        if (!contiguous) return false;
        const ulong MinRingForZeroCopy = 1024UL * 1024UL;
        if (_capacity < MinRingForZeroCopy) return false;
        var adaptiveMin = (int)Math.Min(64UL * 1024UL, _capacity / 16);
        if (adaptiveMin < 4 * 1024) adaptiveMin = 4 * 1024;
        if (payloadLength < adaptiveMin) return false;
        return true;
    }


    /// <summary>
    /// Checks whether a contiguous write of <paramref name="size"/> bytes is
    /// possible without wrap-around. Safe to call from the sole writer thread
    /// while no other writer is active (e.g., WriterLoop is paused).
    /// </summary>
    internal bool HasContiguousWriteSpace(int size)
    {
        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = writeIdx - readIdx;
        if ((ulong)size > _capacity - used)
            return false;
        var writePos = writeIdx & _capMask;
        return writePos + (ulong)size <= _capacity;
    }

    /// <summary>
    /// Returns the number of contiguous bytes available at the current write
    /// position before the ring wraps. Returns 0 if the ring is full.
    /// </summary>
    internal ulong ContiguousWriteSpace()
    {
        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = writeIdx - readIdx;
        var free = _capacity - used;
        if (free == 0) return 0;
        var writePos = writeIdx & _capMask;
        var tailSpace = _capacity - writePos;
        return tailSpace < free ? tailSpace : free;
    }

    /// <summary>
    /// Gets whether the ring is closed.
    /// </summary>
    public bool IsClosed => _localClosed || GetHeader().Closed != 0;

    /// <summary>
    /// Gets a snapshot of the current ring state for debugging.
    /// </summary>
    public RingState GetState()
    {
        if (_localClosed)
        {
            return new RingState { Capacity = _capacity, Closed = true };
        }

        ref var header = ref GetHeader();
        return new RingState
        {
            Capacity = _capacity,
            WriteIdx = Volatile.Read(ref header.WriteIdx),
            ReadIdx = Volatile.Read(ref header.ReadIdx),
            DataSeq = Volatile.Read(ref header.DataSeq),
            SpaceSeq = Volatile.Read(ref header.SpaceSeq),
            ContigSeq = Volatile.Read(ref header.ContigSeq),
            Closed = header.Closed != 0,
            DataWaiters = Volatile.Read(ref header.DataWaiters),
            SpaceWaiters = Volatile.Read(ref header.SpaceWaiters)
        };
    }

    /// <summary>
    /// Writes data to the ring buffer, blocking until space is available.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="RingClosedException">Thrown if the ring is closed.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if ((ulong)data.Length > _capacity)
        {
            throw new ArgumentException($"Data ({data.Length} bytes) exceeds ring capacity ({_capacity} bytes)", nameof(data));
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            var available = ComputeAvailableForWrite(writeIdx, readIdx);

            if ((ulong)data.Length <= available)
            {
                // Space available - perform the write
                var writePos = writeIdx & _capMask;
                var dataSpan = GetDataSpan();

                if (writePos + (ulong)data.Length <= _capacity)
                {
                    // Simple case: no wrap
                    data.CopyTo(dataSpan.Slice((int)writePos, data.Length));
                }
                else
                {
                    // Wrap case: split the write
                    var firstChunk = (int)(_capacity - writePos);
                    data[..firstChunk].CopyTo(dataSpan.Slice((int)writePos, firstChunk));
                    data[firstChunk..].CopyTo(dataSpan[..(data.Length - firstChunk)]);
                }

                // Publish new write index (release semantics)
                Volatile.Write(ref header.WriteIdx, writeIdx + (ulong)data.Length);

                // Signal waiters
                if (data.Length > 0)
                {
                    Interlocked.Increment(ref header.DataSeq);
                    if (_batchWriteDepth == 0 && Volatile.Read(ref header.DataWaiters) > 0)
                    {
                        _sync?.SignalData();
                    }
                }

                return;
            }

            // Not enough space - wait for it
            WaitForSpace(ref header, (ulong)data.Length, cancellationToken);
        }
    }

    /// <summary>
    /// Reads data from the ring buffer, blocking until data is available.
    /// </summary>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of bytes read.</returns>
    /// <exception cref="RingClosedException">Thrown if the ring is closed and empty.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public int Read(Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            var used = writeIdx - readIdx;

            if (used > 0)
            {
                // Data available - perform the read
                var toRead = Math.Min((ulong)buffer.Length, used);
                var readPos = readIdx & _capMask;
                var dataSpan = GetDataSpan();
                var bytesRead = 0;

                if (readPos + toRead <= _capacity)
                {
                    // Simple case: no wrap
                    dataSpan.Slice((int)readPos, (int)toRead).CopyTo(buffer);
                    bytesRead = (int)toRead;
                }
                else
                {
                    // Wrap case: split the read
                    var firstChunk = (int)(_capacity - readPos);
                    dataSpan.Slice((int)readPos, firstChunk).CopyTo(buffer);
                    var secondChunk = (int)toRead - firstChunk;
                    dataSpan[..secondChunk].CopyTo(buffer[firstChunk..]);
                    bytesRead = (int)toRead;
                }

                // Publish new read index (release semantics)
                Volatile.Write(ref header.ReadIdx, readIdx + (ulong)bytesRead);

                // Signal space availability
                if (bytesRead > 0)
                {
                    if (Volatile.Read(ref header.ContigWaiters) > 0)
                    {
                        Interlocked.Increment(ref header.ContigSeq);
                        _sync?.SignalContig();
                    }

                    if (Volatile.Read(ref header.SpaceWaiters) > 0)
                    {
                        Interlocked.Increment(ref header.SpaceSeq);
                        _sync?.SignalSpace();
                    }
                }

                return bytesRead;
            }

            // Check if closed with no data
            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            // No data - wait for it
            WaitForData(ref header, cancellationToken);
        }
    }

    /// <summary>
    /// Reserves space for writing, returning slices for zero-copy writes.
    /// The reservation must be committed via CommitWrite.
    /// </summary>
    /// <param name="size">The number of bytes to reserve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A write reservation containing memory slices.</returns>
    public WriteReservation ReserveWrite(int size, CancellationToken cancellationToken = default)
    {
        if (size <= 0)
        {
            throw new ArgumentException("Size must be positive", nameof(size));
        }
        if ((ulong)size > _capacity)
        {
            throw new ArgumentException(
                $"Size ({size} bytes) exceeds ring capacity ({_capacity} bytes)", nameof(size));
        }

        // PR2: legacy SPSC ReserveWrite is now backed by the MPSC claim
        // path so multiple writers can call it concurrently without
        // overwriting each other's slots. A subsequent CommitWrite
        // publishes via the publish-spin in MpscPublish.
        //
        // The wrap of MpscReserveWrite into a WriteReservation preserves
        // the legacy struct shape so existing call sites compile
        // unchanged. The transitional <c>_claimedWriteIdx</c>-bump
        // inside MpscReserveWrite is what makes a mid-migration ring
        // (some writers SPSC, some MPSC) safe — we now consider the
        // entire writer surface MPSC so the bump is also redundant
        // here, but keeping it costs nothing and protects against any
        // direct <c>header.WriteIdx</c> writer slipped in by tests.
        var slot = MpscReserveWrite(size, cancellationToken);

        return new WriteReservation
        {
            First = slot.First,
            Second = slot.Second,
            Ring = this,
            WriteIdx = slot.BaseIdx,
            MaxBytes = size,
        };
    }

    /// <summary>
    /// Commits a write reservation, publishing the written bytes.
    /// </summary>
    /// <param name="reservation">The reservation to commit.</param>
    /// <param name="bytesWritten">The number of bytes actually written (must not exceed reservation size).</param>
    public void CommitWrite(WriteReservation reservation, int bytesWritten)
    {
        if (reservation.Ring != this)
        {
            throw new ArgumentException("Reservation is not for this ring", nameof(reservation));
        }

        if (bytesWritten < 0 || bytesWritten > reservation.MaxBytes)
        {
            throw new ArgumentException(
                $"Invalid bytes written: {bytesWritten}. Must be 0-{reservation.MaxBytes}",
                nameof(bytesWritten));
        }

        if (_localClosed)
        {
            // Match legacy behaviour: silently drop on closed ring. We
            // still need to balance the _publishersInFlight increment
            // from MpscReserveWrite.
            Interlocked.Decrement(ref _publishersInFlight);
            return;
        }

        // PR2: route through the MPSC publish path. Two cases:
        //
        // 1) Full commit (bytesWritten == MaxBytes): the common path.
        //    Publish exactly the slot. No padding needed.
        //
        // 2) Partial commit (bytesWritten < MaxBytes): the
        //    RingFrameStream truncate-on-dispose path. The slot was
        //    over-reserved (caller asked for cap/8 chunkSize but the
        //    user-supplied IBufferWriter advanced fewer bytes). The
        //    legacy SPSC path simply published bytesWritten and
        //    abandoned the rest, but under MPSC the unwritten tail
        //    occupies a claimed-but-unpublished range that the next
        //    publisher cannot skip past. We MUST publish a contiguous
        //    range from BaseIdx to BaseIdx + MaxBytes, with the trailing
        //    `MaxBytes - bytesWritten` bytes synthesised as PAD frames.
        //
        //    Caller wrote a complete frame for the first bytesWritten;
        //    we append a PAD frame in the unwritten tail. The reader
        //    skips PAD silently. The PAD's header sits at offset
        //    bytesWritten within the slot.
        if (bytesWritten == reservation.MaxBytes)
        {
            MpscPublish(reservation.WriteIdx, reservation.MaxBytes, isPad: false);
            return;
        }

        // Partial commit — rare path (RingFrameStream early dispose).
        var trailingBytes = reservation.MaxBytes - bytesWritten;
        var wireHdrSize = WireHeaderSize;
        if (trailingBytes >= wireHdrSize)
        {
            // Encode a wire-format-appropriate PAD frame header at offset
            // `bytesWritten` within the slot. Custom16 → FrameType.Pad;
            // H2 → PRIORITY (silently skipped by our H2 reader). The
            // trailing bytes occupy [bytesWritten, MaxBytes) within the
            // slot; the header is placed at offset bytesWritten and the
            // remaining trailingBytes - wireHdrSize bytes are unused
            // payload that the reader skips by length.
            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            headerBytes = headerBytes[..wireHdrSize];
            EncodePadHeader(headerBytes, trailingBytes, wireHdrSize);
            WriteSpanAtSlotOffset(reservation.First.Span, reservation.Second.Span,
                bytesWritten, headerBytes);
            MpscPublish(reservation.WriteIdx, reservation.MaxBytes, isPad: false);
        }
        else
        {
            // Trailing region is too small to hold a wire frame header.
            // We cannot leave it unpublished (would stall successor
            // writers' publish-spin) and we cannot publish less (would
            // skew _claimedWriteIdx vs header.WriteIdx). Padding the
            // slot fully and publishing the entire MaxBytes is the
            // safest option — even if the caller's data is partial,
            // the wire is still valid because the small trailing
            // region looks like ungrowable garbage that the reader
            // would reject as a malformed frame anyway. In practice
            // this branch never fires (callers always write at least
            // one full frame header).
            MpscPublish(reservation.WriteIdx, reservation.MaxBytes, isPad: false);
        }
    }

    /// <summary>
    /// Helper for partial-commit PAD insertion: writes <paramref name="data"/>
    /// to position <paramref name="offset"/> within a slot whose first
    /// segment is <paramref name="first"/> and (optional) wrap segment
    /// is <paramref name="second"/>.
    /// </summary>
    private static void WriteSpanAtSlotOffset(
        Span<byte> first, Span<byte> second, int offset, ReadOnlySpan<byte> data)
    {
        if (offset >= first.Length)
        {
            data.CopyTo(second.Slice(offset - first.Length));
            return;
        }
        var firstAvail = first.Length - offset;
        if (data.Length <= firstAvail)
        {
            data.CopyTo(first.Slice(offset, data.Length));
            return;
        }
        data[..firstAvail].CopyTo(first.Slice(offset));
        data[firstAvail..].CopyTo(second);
    }

    /// <summary>
    /// Begins a batch write. OS-level data signals are deferred until
    /// <see cref="EndBatchWrite"/>. DataSeq is still incremented per commit
    /// so spin waiters see updates immediately.
    /// </summary>
    internal void BeginBatchWrite() => _batchWriteDepth++;

    /// <summary>
    /// Ends a batch write and fires the deferred OS-level data signal
    /// if any waiter is blocking.
    /// </summary>
    internal void EndBatchWrite()
    {
        if (--_batchWriteDepth <= 0)
        {
            _batchWriteDepth = 0;
            ref var header = ref GetHeader();
            if (Volatile.Read(ref header.DataWaiters) > 0)
            {
                _sync?.SignalData();
            }
        }
    }

    // ===== MPSC writer path (PR2) =====

    /// <summary>
    /// Atomically claims a contiguous <paramref name="size"/>-byte slot in
    /// the ring for an MPSC writer. Multiple threads may call this
    /// concurrently; each gets a non-overlapping slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim cursor (<see cref="_claimedWriteIdx"/>) is process-local
    /// and CAS-advanced. Ring-space availability is checked against the
    /// shared <c>header.ReadIdx</c>; if the ring is full, this method
    /// blocks the calling thread on <see cref="WaitForSpace"/> until the
    /// reader frees enough bytes.
    /// </para>
    /// <para>
    /// Returned <see cref="MpscWriteSlot"/> MUST be either committed
    /// (<see cref="MpscWriteSlot.Commit"/>) or disposed
    /// (<see cref="MpscWriteSlot.Dispose"/>); use the C#
    /// <c>using var</c> idiom so cancel/throw paths fall through to a
    /// PAD-frame fill that successor writers' publish-spin can advance
    /// past. Forgetting to commit-or-dispose pins
    /// <c>header.WriteIdx</c> at this slot's base forever and stalls
    /// the connection.
    /// </para>
    /// <para>
    /// Minimum slot size: <see cref="ShmConstants.FrameHeaderSize"/>
    /// (16 bytes Custom16; H2 callers should size their reservations
    /// accordingly). The orphan-cleanup path needs at least one
    /// header's worth of space to write a PAD frame.
    /// </para>
    /// </remarks>
    public MpscWriteSlot MpscReserveWrite(int size, CancellationToken cancellationToken = default)
    {
        if (size <= 0)
        {
            throw new ArgumentException("Size must be positive", nameof(size));
        }
        if ((ulong)size > _capacity)
        {
            throw new ArgumentException(
                $"Size ({size} bytes) exceeds ring capacity ({_capacity} bytes)", nameof(size));
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            var current = Volatile.Read(ref _claimedWriteIdx);
            // PR2 transitional: when SPSC ReserveWrite/CommitWrite has
            // run on this ring before we got here, header.WriteIdx may
            // be ahead of our local _claimedWriteIdx. Bump our cursor
            // forward so we don't claim a slot the SPSC writer already
            // wrote. Once SPSC paths delete (Phase 1.4) this branch is
            // dead code and removes with the legacy SPSC API.
            var sharedWrite = (long)Volatile.Read(ref header.WriteIdx);
            if (sharedWrite > current)
            {
                if (Interlocked.CompareExchange(
                        ref _claimedWriteIdx, sharedWrite, current) != current)
                {
                    continue;   // raced; reload
                }
                current = sharedWrite;
            }
            var newClaimed = current + size;
            var readIdx = (long)Volatile.Read(ref header.ReadIdx);
            // used = newClaimed - readIdx in 64-bit signed difference. The
            // reader and writer both wrap at 2^64 in shared shm, but our
            // local cursor is signed long; on a fresh ring readIdx == 0
            // and current == 0 and we never reach the half-range needed
            // to cross the sign boundary in any realistic deployment.
            if ((ulong)(newClaimed - readIdx) > _capacity)
            {
                // Not enough space. Block on SpaceWaiters; reader's
                // ReadIdx-advance will wake us. WaitForSpace's contract
                // takes <c>needed</c> as an INCREMENTAL byte count
                // measured against header.WriteIdx (legacy SPSC API).
                // Under MPSC, header.WriteIdx may lag _claimedWriteIdx
                // by the in-flight publishers' bytes; we still want
                // WaitForSpace to wake when (capacity - (write-read))
                // >= our slot's size after prior publishers commit.
                // The simplest correct value is just `size`: the reader
                // signals SpaceSeq on every commit, so the spin/block
                // loop will re-check our (newClaimed - readIdx) <=
                // capacity condition each iteration.
                WaitForSpace(ref header, (ulong)size, cancellationToken);
                continue;
            }

            if (Interlocked.CompareExchange(
                    ref _claimedWriteIdx, newClaimed, current) != current)
            {
                // Another writer raced us; retry.
                continue;
            }

            // Claim succeeded. Register as in-flight publisher BEFORE
            // returning the slot — the matching Decrement happens in
            // MpscPublish (called by Commit or Dispose).
            Interlocked.Increment(ref _publishersInFlight);

            var writePos = (ulong)current & _capMask;
            Memory<byte> first, second;
            if (writePos + (ulong)size <= _capacity)
            {
                first = _memory.Slice(_dataOffset + (int)writePos, size);
                second = Memory<byte>.Empty;
            }
            else
            {
                var firstLen = (int)(_capacity - writePos);
                first = _memory.Slice(_dataOffset + (int)writePos, firstLen);
                second = _memory.Slice(_dataOffset, size - firstLen);
            }

            return new MpscWriteSlot
            {
                First = first,
                Second = second,
                Ring = this,
                BaseIdx = (ulong)current,
                Size = size,
            };
        }
    }

    /// <summary>
    /// Publishes an MPSC slot. Called by <see cref="MpscWriteSlot.Commit"/>
    /// (real data) or <see cref="MpscWriteSlot.Dispose"/> through
    /// <see cref="MpscPublishOrphanAsPad"/> (PAD frame on uncommitted
    /// slot). Spins until <c>header.WriteIdx == baseIdx</c>, then
    /// advances it by <paramref name="size"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Publish ordering: each writer waits for ALL prior claims to publish.
    /// Publish-spin is short — each step is a <see cref="Volatile.Write"/>
    /// of <c>header.WriteIdx</c> (~50 ns). 32 concurrent writers see
    /// average ~16 × 50 ns = 800 ns wait. <see cref="SpinWait.SpinOnce"/>
    /// degrades to <c>Thread.Yield</c> and <c>Thread.Sleep(0)</c> on
    /// stalls (e.g., a publisher pre-empted before publish), so we do
    /// not livelock under OS preemption.
    /// </para>
    /// <para>
    /// Deferred signal: <c>_pendingSignal</c> is set on every commit
    /// that wrote real data (PAD-only commits skip it because reader
    /// already silently skips PAD; signalling for PAD adds a wakeup
    /// the reader cannot use). The last writer to leave the in-flight
    /// cohort (<c>_publishersInFlight → 0</c>) drains the pending
    /// signal once. Equivalent to today's WriterLoop.FlushBatch's
    /// per-batch SignalData but across ALL concurrent writers, not
    /// just within a single thread's batch.
    /// </para>
    /// </remarks>
    internal void MpscPublish(ulong baseIdx, int size, bool isPad)
    {
        ref var header = ref GetHeader();

        // Spin until prior publishers have advanced WriteIdx to our base.
        var sw = new SpinWait();
        while (Volatile.Read(ref header.WriteIdx) != baseIdx)
        {
            sw.SpinOnce();
        }

        // Advance WriteIdx and bump DataSeq so spinning readers see the
        // change without a kernel signal. PAD frames also bump DataSeq
        // — the reader will read the PAD header and skip it; the wasted
        // wakeup is rare (only on cancel/throw paths).
        Volatile.Write(ref header.WriteIdx, baseIdx + (ulong)size);
        Interlocked.Increment(ref header.DataSeq);

        // Defer the kernel-level SignalData to the cohort tail. PAD-only
        // commits do NOT defer a signal: there is no logically new data
        // for a blocked reader to consume.
        if (!isPad)
        {
            Volatile.Write(ref _pendingSignal, 1);
        }

        // Last publisher out flushes the deferred signal. The atomic
        // pair (Decrement → 0, Exchange pendingSignal → 0) ensures
        // exactly one signal fires per cohort even under N concurrent
        // writers all decrementing through 0 (only ONE of them sees
        // _publishersInFlight transition to 0 under Interlocked).
        var inFlight = Interlocked.Decrement(ref _publishersInFlight);
        if (inFlight == 0
            && Interlocked.Exchange(ref _pendingSignal, 0) != 0
            && Volatile.Read(ref header.DataWaiters) > 0)
        {
            _sync?.SignalData();
        }
    }

    /// <summary>
    /// Called from <see cref="MpscWriteSlot.Dispose"/> when the slot was
    /// never committed (caller threw or cancelled). Fills the slot with
    /// a wire-format-appropriate PAD-equivalent frame header and
    /// publishes it. The reader silently skips the entire slot in both
    /// codecs (see <see cref="FrameProtocol.ReadFramePayloadCustom16"/>
    /// and <see cref="Wire.Http2Codec"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Custom16 ring: emit a 16-byte <see cref="FrameHeader"/> with
    /// <see cref="FrameType.Pad"/>, <c>StreamId = 0</c>, <c>Length =
    /// size - 16</c>, <c>Flags = 0</c>. The remaining
    /// <c>size - 16</c> bytes are unused payload; reader reserves+commits
    /// the length but does not surface the payload.
    /// </para>
    /// <para>
    /// H2 ring: emit a 9-byte H2 PRIORITY frame header
    /// (<see cref="Wire.Http2FrameType.Priority"/>) with
    /// <c>Length = size - 9</c>, <c>StreamId = 0</c>, <c>Flags = 0</c>.
    /// PRIORITY is deprecated by RFC 9113 and our H2 reader silently
    /// skips frames of this type with arbitrary payload length, so the
    /// reader will read the 9-byte header, see PRIORITY, and skip the
    /// remaining <c>size - 9</c> bytes.
    /// </para>
    /// <para>
    /// PAD only fires on cancel/throw paths (rare). Both wire formats
    /// share the same code path here so the bug-correctness invariant
    /// (orphaned slots MUST publish a wire-legal frame) holds for both.
    /// </para>
    /// </remarks>
    internal void MpscPublishOrphanAsPad(
        ulong baseIdx, int size, Memory<byte> first, Memory<byte> second)
    {
        // Minimum size = wire frame header size. Caller guarantees this
        // via the MpscReserveWrite size argument (every legitimate frame
        // request is at least one header).
        var wireHdrSize = WireHeaderSize;
        if (size >= wireHdrSize)
        {
            // Encode to a stack span first, then place into the slot.
            // Max of either header size is 16 (Custom16); H2 uses 9.
            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            headerBytes = headerBytes[..wireHdrSize];
            EncodePadHeader(headerBytes, size, wireHdrSize);

            // Header lives in `first` (always ≥ wireHdrSize bytes
            // contiguous on most reserves); split across First+Second
            // when the slot wraps inside the header.
            if (first.Length >= wireHdrSize)
            {
                headerBytes.CopyTo(first.Span);
            }
            else
            {
                headerBytes[..first.Length].CopyTo(first.Span);
                headerBytes[first.Length..].CopyTo(second.Span);
            }
        }
        // else: caller asked for < wireHdrSize bytes — invariant violation;
        // publish without writing anything; reader will OOB on the next
        // header parse (which is a connection-fatal error anyway).

        MpscPublish(baseIdx, size, isPad: true);
    }

    /// <summary>
    /// On-wire frame header size for this ring. Custom16: 16 bytes;
    /// HTTP/2: 9 bytes.
    /// </summary>
    private int WireHeaderSize => this.Wire == Grpc.Net.SharedMemory.Wire.WireFormat.Http2
        ? Grpc.Net.SharedMemory.Wire.Http2FrameHeader.Size
        : ShmConstants.FrameHeaderSize;

    /// <summary>
    /// Encodes a wire-format-appropriate PAD-equivalent frame header
    /// into <paramref name="dest"/>. The header tells the peer reader
    /// to skip <paramref name="slotSize"/> total bytes (header included).
    /// </summary>
    /// <remarks>
    /// Custom16 → <see cref="FrameType.Pad"/>; H2 → PRIORITY frame.
    /// H2 PRIORITY's payload is at most 24 bits (16 MiB - 1); a single
    /// orphan reservation is bounded by per-frame max payload + 9 byte
    /// header (RingFrameStream chunks at <c>cap/8</c> capped at
    /// <see cref="Grpc.Net.SharedMemory.Wire.Http2FrameHeader.MaxAllowedPayloadLength"/>)
    /// so a single PRIORITY header fits any legal slot.
    /// </remarks>
    private void EncodePadHeader(Span<byte> dest, int slotSize, int wireHdrSize)
    {
        var padPayload = slotSize - wireHdrSize;
        if (this.Wire == Grpc.Net.SharedMemory.Wire.WireFormat.Http2)
        {
            // H2 PRIORITY: deprecated by RFC 9113, our H2 reader (in
            // Http2Codec.Read.cs Priority case) accepts arbitrary
            // payload length and silently skips the entire frame.
            if ((uint)padPayload > Grpc.Net.SharedMemory.Wire.Http2FrameHeader.MaxAllowedPayloadLength)
            {
                throw new InvalidOperationException(
                    $"Orphan slot size {slotSize} exceeds H2 single-frame max " +
                    $"{Grpc.Net.SharedMemory.Wire.Http2FrameHeader.MaxAllowedPayloadLength + Grpc.Net.SharedMemory.Wire.Http2FrameHeader.Size}; " +
                    "caller invariant violation (RingFrameStream chunks should cap reserves).");
            }
            Grpc.Net.SharedMemory.Wire.Http2FrameHeader.Encode(
                dest,
                Grpc.Net.SharedMemory.Wire.Http2FrameType.Priority,
                flags: 0,
                streamId: 0,
                payloadLength: padPayload);
        }
        else
        {
            var hdr = new FrameHeader(
                FrameType.Pad,
                streamId: 0,
                length: (uint)padPayload,
                flags: 0);
            hdr.EncodeTo(dest);
        }
    }

    /// <summary>
    /// Reserves bytes for reading, returning slices for zero-copy reads.
    /// The reservation must be committed via CommitRead.
    /// </summary>
    /// <param name="size">The number of bytes to reserve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read reservation containing memory slices.</returns>
    public ReadReservation ReserveRead(int size, CancellationToken cancellationToken = default)
    {
        if (size <= 0)
        {
            throw new ArgumentException("Size must be positive", nameof(size));
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var pendingIdx = Volatile.Read(ref _pendingReadIdx);
            var available = writeIdx - pendingIdx;

            // If closed, allow draining remaining data
            if ((_localClosed || header.Closed != 0) && available == 0)
            {
                throw new RingClosedException();
            }

            if (available >= (ulong)size)
            {
                var readPos = pendingIdx & _capMask;
                ReadOnlyMemory<byte> first, second;

                if (readPos + (ulong)size <= _capacity)
                {
                    // No wrap needed
                    first = _memory.Slice(_dataOffset + (int)readPos, size);
                    second = ReadOnlyMemory<byte>.Empty;
                }
                else
                {
                    // Wrap case
                    var firstLen = (int)(_capacity - readPos);
                    first = _memory.Slice(_dataOffset + (int)readPos, firstLen);
                    second = _memory.Slice(_dataOffset, size - firstLen);
                }

                // Advance pending read index
                Volatile.Write(ref _pendingReadIdx, pendingIdx + (ulong)size);

                return new ReadReservation
                {
                    First = first,
                    Second = second,
                    Ring = this,
                    CommitReadIdx = Volatile.Read(ref header.ReadIdx),
                    MaxBytes = size
                };
            }

            // If closed with insufficient data
            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            // Wait for the writer to advance PAST our local pending index
            // (the byte boundary up to which the reader has already
            // committed reservations to itself). Using <c>_pendingReadIdx</c>
            // here — instead of the shared <c>header.ReadIdx</c> — is what
            // makes the wait composable with speculative-ZC: while a ZC
            // anchor is held the shared index is intentionally frozen, so
            // a watermark of <c>header.ReadIdx</c> would always satisfy
            // <c>writeIdx &gt; watermark</c> and cause this loop to spin at
            // 100% CPU until the consumer's Release advances the shared
            // index. <c>_pendingReadIdx</c> is the right semantic anchor:
            // the reader has already "claimed" everything up to it, and we
            // genuinely want to wait until NEW bytes arrive past that point.
            WaitForDataAfter(ref header, pendingIdx, cancellationToken);
        }
    }

    /// <summary>
    /// Commits a read reservation, freeing space for the writer.
    /// </summary>
    public void CommitRead(ReadReservation reservation, int bytesConsumed)
    {
        if (reservation.Ring != this)
        {
            throw new ArgumentException("Reservation is not for this ring", nameof(reservation));
        }

        if (bytesConsumed < 0 || bytesConsumed > reservation.MaxBytes)
        {
            throw new ArgumentException($"Invalid bytes consumed: {bytesConsumed}. Must be 0-{reservation.MaxBytes}", nameof(bytesConsumed));
        }

        if (_localClosed)
        {
            return;
        }

        if (bytesConsumed == 0)
        {
            return;
        }

        ref var header = ref GetHeader();

        var newReadIdx = reservation.CommitReadIdx + (ulong)bytesConsumed;
        while (true)
        {
            var current = Volatile.Read(ref header.ReadIdx);
            if (newReadIdx <= current)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref header.ReadIdx, newReadIdx, current) == current)
            {
                break;
            }
        }
        SignalSpaceAvailability(ref header);
    }

    /// <summary>
    /// Signals the writer that space has become available.
    /// </summary>
    private void SignalSpaceAvailability(ref RingHeader header)
    {
        if (Volatile.Read(ref header.ContigWaiters) > 0)
        {
            Interlocked.Increment(ref header.ContigSeq);
            _sync?.SignalContig();
        }

        if (Volatile.Read(ref header.SpaceWaiters) > 0)
        {
            Interlocked.Increment(ref header.SpaceSeq);
            _sync?.SignalSpace();
        }
    }

    /// <summary>
    /// Closes the ring buffer. Readers can still drain remaining data.
    /// Only the owner (server) sets the Closed flag in shared memory.
    /// </summary>
    public void Close()
    {
        if (_localClosed)
        {
            return;
        }

        _localClosed = true;

        // Only the owner (server) should set the Closed flag in shared memory
        // Clients just close locally to stop their own read/write operations
        if (_isOwner)
        {
            ref var header = ref GetHeader();
            Volatile.Write(ref header.Closed, 1);

            // Wake all waiters
            Interlocked.Increment(ref header.DataSeq);
            Interlocked.Increment(ref header.SpaceSeq);
            Interlocked.Increment(ref header.ContigSeq);
        }

        // Signal sync waiters for both owner and non-owner.
        // Non-owner threads may be blocked in futex/WaitOnAddress;
        // without a wake, they'd remain blocked until the memory is unmapped,
        // causing an access violation in the finally block.
        _sync?.SignalData();
        _sync?.SignalSpace();
        _sync?.SignalContig();
    }

    public void Dispose()
    {
        Close();
        _sync?.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref RingHeader GetHeader()
    {
        return ref MemoryMarshal.AsRef<RingHeader>(_memory.Span.Slice(_headerOffset, ShmConstants.RingHeaderSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetDataSpan()
    {
        return _memory.Span.Slice(_dataOffset, (int)_capacity);
    }

    private void WaitForSpace(ref RingHeader header, ulong needed, CancellationToken cancellationToken)
    {
        // Adaptive spin before blocking
        var spinLimit = Volatile.Read(ref _spaceSpinCutoff);
        for (var i = 0; i < spinLimit; i++)
        {
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            if (_capacity - (writeIdx - readIdx) >= needed)
            {
                // Success - adapt spin limit: maintain or raise when we
                // waited longer than 75% of the limit.
                if (i > 0)
                {
                    var newCutoff = i > (spinLimit * 3 / 4)
                        ? Math.Min(ShmConstants.SpinIterationsMax, spinLimit + spinLimit / 8)
                        : spinLimit;
                    Volatile.Write(ref _spaceSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, newCutoff));
                }
                return;
            }

            if (header.Closed != 0 || _localClosed)
            {
                throw new RingClosedException();
            }

            Thread.SpinWait(1);
        }

        // Spin failed - adapt downward and fall back to blocking
        var reducedCutoff = (7 * spinLimit + ShmConstants.SpinIterationsMin) / 8;
        Volatile.Write(ref _spaceSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, reducedCutoff));

        // Distinguish full vs partial: if ring is completely full, wait on spaceSeq
        // (bumped only when transitioning from full to not-full). If ring has some
        // space but not enough, wait on contigSeq (bumped on every read commit),
        // matching grpc-go-shmem's ReserveWrite behavior.
        var writeIdx2 = Volatile.Read(ref header.WriteIdx);
        var readIdx2 = Volatile.Read(ref header.ReadIdx);
        var free = _capacity - (writeIdx2 - readIdx2);

        // Before blocking, drain any pending control frames (WindowUpdate)
        // so the remote side can free ring space. Without this, both sides'
        // WriterLoops can block waiting for space while WindowUpdates sit
        // unreachable in the queue.
        // Guard against recursion: DrainControlFrames → WriteFrame → WaitForSpace → drain.
        // Depth 1 is enough — control frames are tiny and always fit if any space exists.
        if (_drainRecursionDepth == 0)
        {
            _drainRecursionDepth++;
            try { WaitForSpaceDrainCallback?.Invoke(); }
            finally { _drainRecursionDepth--; }
        }

        // Re-check after drain — the remote may have freed space
        writeIdx2 = Volatile.Read(ref header.WriteIdx);
        readIdx2 = Volatile.Read(ref header.ReadIdx);
        free = _capacity - (writeIdx2 - readIdx2);
        if (free >= needed) return;

        // Wait for total space to become available. We always wait on
        // SpaceSeq regardless of whether the ring is completely full
        // (free==0) or partially free (free>0 but <needed). The old
        // code used ContigSeq for the partial case, but ReserveWrite
        // accepts wrap-around reservations so contiguity is not required.
        // Using ContigSeq caused deadlocks when deferred CommitRead
        // held ReadIdx back: the writer waited for ContigSeq signals
        // that never came because no further reads occurred.
        Interlocked.Increment(ref header.SpaceWaiters);
        try
        {
            var seq = Volatile.Read(ref header.SpaceSeq);

            // Re-check before blocking
            var wi = Volatile.Read(ref header.WriteIdx);
            var ri = Volatile.Read(ref header.ReadIdx);
            if (_capacity - (wi - ri) >= needed)
            {
                return;
            }

            _sync?.WaitForSpace(seq, timeout: null, cancellationToken);
        }
        finally
        {
            if (!_localClosed)
            {
                Interlocked.Decrement(ref header.SpaceWaiters);
            }
        }
    }

    private void WaitForData(ref RingHeader header, CancellationToken cancellationToken)
    {
        // Wait until any unconsumed data is visible at <c>header.ReadIdx</c>.
        // Used by the byte-stream <see cref="Read(byte[], int, int, CancellationToken)"/>
        // path which advances <c>header.ReadIdx</c> in lock-step with each
        // copy-out. <see cref="ReserveRead"/> uses
        // <see cref="WaitForDataAfter"/> instead because it advances a local
        // <c>_pendingReadIdx</c> while leaving the shared index frozen
        // during a speculative-ZC hold (see remarks on
        // <see cref="WaitForDataAfter"/>).
        WaitForDataAfter(ref header, Volatile.Read(ref header.ReadIdx), cancellationToken);
    }

    /// <summary>
    /// Spins/blocks until <c>header.WriteIdx</c> advances past
    /// <paramref name="watermark"/>.
    /// </summary>
    /// <remarks>
    /// Crucial for <see cref="ReserveRead"/>'s wait path: that method tracks
    /// reader progress in <see cref="_pendingReadIdx"/> rather than
    /// <c>header.ReadIdx</c>, because the shared index is intentionally
    /// frozen while a speculative-ZC reservation is held (cross-process
    /// writers must not wrap onto bytes the reader still holds — see
    /// <see cref="BeginZcReservation"/>). If <c>ReserveRead</c> instead
    /// blocked on the shared <c>header.ReadIdx</c>, the wait would observe
    /// <c>writeIdx &gt; ReadIdx</c> (the as-yet-unreleased ZC bytes) and
    /// return immediately, even when no NEW frames had arrived. The reader
    /// thread would spin at 100% CPU pulling <c>ReserveRead → WaitForData →
    /// (immediate return) → loop</c> until the consumer's
    /// <see cref="FramePayload.Release"/> advanced <c>header.ReadIdx</c> —
    /// degrading the SingleStreamMode/ZC perf path this PR aims to optimise.
    /// </remarks>
    private void WaitForDataAfter(ref RingHeader header, ulong watermark, CancellationToken cancellationToken)
    {
        var spinLimit = Volatile.Read(ref _dataSpinCutoff);
        for (var i = 0; i < spinLimit; i++)
        {
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            if (writeIdx > watermark)
            {
                // Success - adapt spin limit: if we found data within the
                // spin window, keep the cutoff at least at the current level.
                // Previous formula (7*limit + i*2)/8 would reduce the cutoff
                // when i < 3*limit/8, causing unnecessary kernel waits.
                if (i > 0)
                {
                    // Maintain current cutoff or raise slightly if we waited
                    // longer than 75% of the limit.
                    var newCutoff = i > (spinLimit * 3 / 4)
                        ? Math.Min(ShmConstants.SpinIterationsMax, spinLimit + spinLimit / 8)
                        : spinLimit;
                    Volatile.Write(ref _dataSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, newCutoff));
                }
                return;
            }

            if (header.Closed != 0 || _localClosed)
            {
                throw new RingClosedException();
            }

            Thread.SpinWait(1);
        }

        // Spin failed - adapt downward and fall back to blocking
        var reducedCutoff = (7 * _dataSpinCutoff + ShmConstants.SpinIterationsMin) / 8;
        Volatile.Write(ref _dataSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, reducedCutoff));

        // Block on sync primitive
        Interlocked.Increment(ref header.DataWaiters);
        try
        {
            var seq = Volatile.Read(ref header.DataSeq);

            // Re-check before blocking
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            if (writeIdx > watermark)
            {
                return;
            }

            // Also check if closed to avoid missing close that happened between checks
            if (header.Closed != 0 || _localClosed)
            {
                throw new RingClosedException();
            }

            _sync?.WaitForData(seq, timeout: null, cancellationToken);
        }
        finally
        {
            if (!_localClosed)
            {
                Interlocked.Decrement(ref header.DataWaiters);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPowerOfTwo(ulong value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    /// <summary>
    /// Gets the number of bytes available to read.
    /// </summary>
    public ulong ReadableBytes
    {
        get
        {
            ref var header = ref GetHeader();
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            return writeIdx - readIdx;
        }
    }

    /// <summary>
    /// Gets the number of bytes available to write.
    /// </summary>
    public ulong WritableBytes
    {
        get
        {
            ref var header = ref GetHeader();
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            var used = writeIdx - readIdx;
            return _capacity - used;
        }
    }

    /// <summary>
    /// Checks if at least the specified number of bytes can be read.
    /// </summary>
    public bool TryPeek(int minBytes, out int available)
    {
        if (_localClosed)
        {
            available = 0;
            return false;
        }

        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = (int)(writeIdx - readIdx);
        available = used;
        return used >= minBytes;
    }

    /// <summary>
    /// Checks if at least the specified number of bytes can be written.
    /// </summary>
    public bool CanWrite(int size)
    {
        if (_localClosed)
        {
            return false;
        }

        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var available = ComputeAvailableForWrite(writeIdx, readIdx);
        return (ulong)size <= available;
    }

    /// <summary>
    /// Tries to read data without blocking.
    /// </summary>
    /// <param name="buffer">The buffer to read into.</param>
    /// <returns>True if data was read, false if no data available.</returns>
    public bool TryRead(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _localClosed)
        {
            return false;
        }

        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = writeIdx - readIdx;

        if (used < (ulong)buffer.Length)
        {
            return false; // Not enough data
        }

        // Data available - perform the read
        var readPos = readIdx & _capMask;
        var dataSpan = GetDataSpan();

        if (readPos + (ulong)buffer.Length <= _capacity)
        {
            // Simple case: no wrap
            dataSpan.Slice((int)readPos, buffer.Length).CopyTo(buffer);
        }
        else
        {
            // Wrap case: split the read
            var firstChunk = (int)(_capacity - readPos);
            dataSpan.Slice((int)readPos, firstChunk).CopyTo(buffer);
            var secondChunk = buffer.Length - firstChunk;
            dataSpan[..secondChunk].CopyTo(buffer[firstChunk..]);
        }

        // Publish new read index (release semantics)
        Volatile.Write(ref header.ReadIdx, readIdx + (ulong)buffer.Length);

        // Signal space availability
        if (buffer.Length > 0)
        {
            Interlocked.Increment(ref header.ContigSeq);
            if (Volatile.Read(ref header.ContigWaiters) > 0)
            {
                _sync?.SignalContig();
            }
            if (Volatile.Read(ref header.SpaceWaiters) > 0)
            {
                Interlocked.Increment(ref header.SpaceSeq);
                _sync?.SignalSpace();
            }
        }

        return true;
    }

    /// <summary>
    /// Tries to write data without blocking.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <returns>True if data was written, false if not enough space.</returns>
    public bool TryWrite(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return true;
        }

        if (_localClosed || (ulong)data.Length > _capacity)
        {
            return false;
        }

        ref var header = ref GetHeader();

        if (header.Closed != 0)
        {
            return false;
        }

        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var available = ComputeAvailableForWrite(writeIdx, readIdx);

        if ((ulong)data.Length > available)
        {
            return false; // Not enough space
        }

        // Space available - perform the write
        var writePos = writeIdx & _capMask;
        var dataSpan = GetDataSpan();

        if (writePos + (ulong)data.Length <= _capacity)
        {
            // Simple case: no wrap
            data.CopyTo(dataSpan.Slice((int)writePos, data.Length));
        }
        else
        {
            // Wrap case: split the write
            var firstChunk = (int)(_capacity - writePos);
            data[..firstChunk].CopyTo(dataSpan.Slice((int)writePos, firstChunk));
            data[firstChunk..].CopyTo(dataSpan[..(data.Length - firstChunk)]);
        }

        // Publish new write index (release semantics)
        Volatile.Write(ref header.WriteIdx, writeIdx + (ulong)data.Length);

        // Signal waiters
        if (data.Length > 0)
        {
            Interlocked.Increment(ref header.DataSeq);
            if (Volatile.Read(ref header.DataWaiters) > 0)
            {
                _sync?.SignalData();
            }
        }

        return true;
    }
}
