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
/// Frame payload wrapper. One of three forms:
///   - Empty (no payload).
///   - Pooled (heap copy in an ArrayPool buffer; <see cref="Release"/>
///     returns the buffer).
///   - Zero-copy (a slice of ring memory holding an anchor slot in the
///     ring's per-frame ZC FIFO; <see cref="Release"/> marks the slot
///     released and triggers FIFO drain to advance <c>header.ReadIdx</c>).
/// </summary>
public readonly struct FramePayload
{
    public static readonly FramePayload Empty = new(ReadOnlyMemory<byte>.Empty, null);

    private readonly byte[]? _pooledBuffer;
    private readonly ShmRing? _anchorRing;
    private readonly int _anchorSlot;   // FIFO slot index for ZC; unused otherwise

    public ReadOnlyMemory<byte> Memory { get; }

    public int Length => Memory.Length;

    /// <summary>
    /// True if this payload is a zero-copy view of ring memory (versus a
    /// pool-backed copy). Used by upper-layer multi-frame assemblers to
    /// decide whether chaining segments is safe (preserving ZC) or whether
    /// a contiguous copy is needed (compressed-message path).
    /// </summary>
    public bool IsSpeculativeZeroCopy => _anchorRing != null;

    private FramePayload(ReadOnlyMemory<byte> memory, byte[]? pooledBuffer,
        ShmRing? anchorRing = null, int anchorSlot = -1)
    {
        Memory = memory;
        _pooledBuffer = pooledBuffer;
        _anchorRing = anchorRing;
        _anchorSlot = anchorSlot;
    }

    public static FramePayload FromPooled(byte[] buffer, int length)
    {
        return new FramePayload(buffer.AsMemory(0, length), buffer);
    }

    /// <summary>
    /// Creates a zero-copy payload backed by ring memory. The codec has
    /// already allocated a per-frame ZC anchor slot via
    /// <see cref="ShmRing.TryBeginPerFrameZc"/>; <see cref="Release"/>
    /// will mark the slot released and trigger FIFO drain.
    /// </summary>
    internal static FramePayload FromRingZcAnchor(ReadOnlyMemory<byte> memory, ShmRing ring, int anchorSlot)
    {
        return new FramePayload(memory, null, anchorRing: ring, anchorSlot: anchorSlot);
    }

    public void Release()
    {
        if (_pooledBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_pooledBuffer);
        }
        if (_anchorRing != null)
        {
            _anchorRing.ReleasePerFrameZc(_anchorSlot);
        }
    }
}
