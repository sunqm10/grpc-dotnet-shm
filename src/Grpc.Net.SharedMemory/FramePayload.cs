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

using System.Buffers;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Frame payload wrapper that owns a pooled buffer.
/// Call <see cref="Release"/> to return the buffer to the pool.
/// </summary>
public readonly struct FramePayload
{
    public static readonly FramePayload Empty = new(ReadOnlyMemory<byte>.Empty, null);

    private readonly byte[]? _pooledBuffer;

    // Speculative: ring ref + reserved bytes for safety margin release.
    // Phase Y per-frame ZC: ring + slot index (>= 0). Legacy single-anchor
    // ZC: ring + reservedBytes; _anchorSlot = -1.
    private readonly ShmRing? _speculativeRing;
    private readonly int _speculativeBytes;
    private readonly int _anchorSlot;   // Phase Y FIFO slot, or -1 for legacy/non-ZC

    public ReadOnlyMemory<byte> Memory { get; }

    public int Length => Memory.Length;

    /// <summary>
    /// True if this payload is a speculative zero-copy view of ring memory
    /// (versus a pool-backed copy). Used by upper-layer multi-frame
    /// assemblers to decide whether to chain segments (preserving ZC) or
    /// copy into a contiguous buffer (compressed-message path needs that).
    /// </summary>
    public bool IsSpeculativeZeroCopy => _speculativeRing != null;

    private FramePayload(ReadOnlyMemory<byte> memory, byte[]? pooledBuffer,
        ShmRing? speculativeRing = null, int speculativeBytes = 0, int anchorSlot = -1)
    {
        Memory = memory;
        _pooledBuffer = pooledBuffer;
        _speculativeRing = speculativeRing;
        _speculativeBytes = speculativeBytes;
        _anchorSlot = anchorSlot;
    }

    public static FramePayload FromPooled(byte[] buffer, int length)
    {
        return new FramePayload(buffer.AsMemory(0, length), buffer);
    }

    /// <summary>
    /// Creates a speculative zero-copy payload using the LEGACY single-anchor
    /// protocol. CommitRead has already been called and
    /// SpeculativeReservedBytes incremented. Release decrements
    /// SpeculativeReservedBytes and fires EndZcReservation when the chain
    /// is closed and the count hits zero.
    /// </summary>
    /// <remarks>
    /// Phase Y migration: codec sites should switch to
    /// <see cref="FromRingZcAnchor"/>. This factory remains for the chain-
    /// anchor paths still in flight (Custom16 / H2 chain-ZC) until Y.3-Y.4
    /// migrate them.
    /// </remarks>
    internal static FramePayload FromRingMemorySpeculative(ReadOnlyMemory<byte> memory, ShmRing ring, int reservedBytes)
    {
        return new FramePayload(memory, null,
            speculativeRing: ring, speculativeBytes: reservedBytes, anchorSlot: -1);
    }

    /// <summary>
    /// Phase Y per-frame ZC payload. <paramref name="anchorSlot"/> is the
    /// FIFO slot index returned by <see cref="ShmRing.TryBeginPerFrameZc"/>;
    /// <see cref="Release"/> calls <see cref="ShmRing.ReleasePerFrameZc"/>
    /// which marks the slot released and triggers FIFO drain. Cross-process
    /// <c>header.ReadIdx</c> only advances after the FIFO head drains,
    /// preserving the no-wrap-onto-held-bytes invariant.
    /// </summary>
    internal static FramePayload FromRingZcAnchor(ReadOnlyMemory<byte> memory, ShmRing ring, int anchorSlot)
    {
        return new FramePayload(memory, null,
            speculativeRing: ring, speculativeBytes: 0, anchorSlot: anchorSlot);
    }

    public void Release()
    {
        if (_pooledBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_pooledBuffer);
        }

        if (_speculativeRing == null) return;

        if (_anchorSlot >= 0)
        {
            // Phase Y per-frame ZC: release the FIFO slot. Drain logic
            // CAS-advances header.ReadIdx in FIFO order; out-of-order
            // releases are stashed until the head drains.
            _speculativeRing.ReleasePerFrameZc(_anchorSlot);
            return;
        }

        // Legacy single-anchor: decrement SpeculativeReservedBytes; the
        // last-out release fires EndZcReservation iff the codec has also
        // closed any in-flight chain (chain-ZC for multi-frame messages).
        var remaining = Interlocked.Add(
            ref _speculativeRing.SpeculativeReservedBytes, -_speculativeBytes);
        if (remaining == 0 && !_speculativeRing.IsChainOpen)
        {
            _speculativeRing.EndZcReservation();
        }
    }
}
