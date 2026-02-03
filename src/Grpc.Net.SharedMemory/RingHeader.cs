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

using System.Runtime.InteropServices;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Ring buffer header structure (64 bytes) that resides in shared memory.
/// This layout matches grpc-go-shmem for interoperability.
/// 
/// Layout (grpc-go-shmem compatible):
/// - Offset 0-7:   capacity (ulong) - ring data area size (power of 2)
/// - Offset 8-15:  writeIdx (ulong) - monotonic write index
/// - Offset 16-23: readIdx (ulong) - monotonic read index  
/// - Offset 24-27: dataSeq (uint) - data availability sequence for signaling
/// - Offset 28-31: spaceSeq (uint) - space availability sequence for signaling
/// - Offset 32-35: contigSeq (uint) - contiguity sequence for signaling
/// - Offset 36-39: closed (uint) - atomic closed flag
/// - Offset 40-43: dataWaiters (uint) - count of readers blocked on data
/// - Offset 44-47: spaceWaiters (uint) - count of writers blocked on space
/// - Offset 48-51: contigWaiters (uint) - count of writers waiting for contiguity
/// - Offset 52-63: reserved (12 bytes) - future use
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct RingHeader
{
    /// <summary>Ring data area capacity in bytes (must be power of 2).</summary>
    [FieldOffset(0)]
    public ulong Capacity;

    /// <summary>Monotonic write index (producer advances this).</summary>
    [FieldOffset(8)]
    public ulong WriteIdx;

    /// <summary>Monotonic read index (consumer advances this).</summary>
    [FieldOffset(16)]
    public ulong ReadIdx;

    /// <summary>Data availability sequence number for futex/event signaling.</summary>
    [FieldOffset(24)]
    public uint DataSeq;

    /// <summary>Space availability sequence number for futex/event signaling.</summary>
    [FieldOffset(28)]
    public uint SpaceSeq;

    /// <summary>Contiguity sequence number for signaling.</summary>
    [FieldOffset(32)]
    public uint ContigSeq;

    /// <summary>Ring closed flag (0 = open, 1 = closed).</summary>
    [FieldOffset(36)]
    public uint Closed;

    /// <summary>Number of readers waiting for data.</summary>
    [FieldOffset(40)]
    public uint DataWaiters;

    /// <summary>Number of writers waiting for space.</summary>
    [FieldOffset(44)]
    public uint SpaceWaiters;

    /// <summary>Number of writers waiting for contiguity.</summary>
    [FieldOffset(48)]
    public uint ContigWaiters;

    // Offset 52-63: Reserved (12 bytes) - implicitly zeroed
}

/// <summary>
/// Snapshot of ring buffer state for debugging and diagnostics.
/// </summary>
public readonly struct RingState
{
    /// <summary>Total ring capacity.</summary>
    public ulong Capacity { get; init; }

    /// <summary>Current write index (monotonic).</summary>
    public ulong WriteIdx { get; init; }

    /// <summary>Current read index (monotonic).</summary>
    public ulong ReadIdx { get; init; }

    /// <summary>Bytes currently in ring (WriteIdx - ReadIdx).</summary>
    public ulong Used => WriteIdx - ReadIdx;

    /// <summary>Bytes available for writing.</summary>
    public ulong Available => Capacity - Used;

    /// <summary>Data availability sequence number.</summary>
    public uint DataSeq { get; init; }

    /// <summary>Space availability sequence number.</summary>
    public uint SpaceSeq { get; init; }

    /// <summary>Contiguity sequence number.</summary>
    public uint ContigSeq { get; init; }

    /// <summary>Ring closed flag.</summary>
    public bool Closed { get; init; }

    /// <summary>Number of readers waiting for data.</summary>
    public uint DataWaiters { get; init; }

    /// <summary>Number of writers waiting for space.</summary>
    public uint SpaceWaiters { get; init; }

    /// <summary>Number of writers waiting for contiguity.</summary>
    public uint ContigWaiters { get; init; }

    /// <summary>
    /// Returns a string representation of this ring state for debugging.
    /// </summary>
    public override string ToString()
    {
        return $"RingState(Used={Used}/{Capacity}, WIdx={WriteIdx}, RIdx={ReadIdx}, Closed={Closed}, DataWaiters={DataWaiters}, SpaceWaiters={SpaceWaiters}, ContigWaiters={ContigWaiters})";
    }
}
