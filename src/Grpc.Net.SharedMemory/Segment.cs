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

using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Grpc.Net.SharedMemory.Synchronization;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Segment header structure (128 bytes) that identifies and configures a shared memory segment.
/// This layout matches grpc-go-shmem for interoperability.
/// 
/// Layout:
/// - Offset 0-7:    magic (8 bytes) - "GRPCSHM\0"
/// - Offset 8-11:   version (uint32) - protocol version
/// - Offset 12-15:  flags (uint32) - reserved flags
/// - Offset 16-23:  totalSize (uint64) - total segment size
/// - Offset 24-31:  ringAOffset (uint64) - offset to Ring A in segment
/// - Offset 32-39:  ringACapacity (uint64) - data area capacity for Ring A
/// - Offset 40-47:  ringBOffset (uint64) - offset to Ring B in segment
/// - Offset 48-55:  ringBCapacity (uint64) - data area capacity for Ring B
/// - Offset 56-59:  maxStreams (uint32) - maximum concurrent streams
/// - Offset 60-63:  clientReady (uint32) - client ready flag
/// - Offset 64-67:  serverReady (uint32) - server ready flag
/// - Offset 68-127: reserved (60 bytes) - future use
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct SegmentHeader
{
    /// <summary>Magic bytes identifying this as a shared memory segment ("GRPCSHM\0").</summary>
    [FieldOffset(0)]
    public ulong MagicValue;

    /// <summary>Protocol version.</summary>
    [FieldOffset(8)]
    public uint Version;

    /// <summary>Reserved flags.</summary>
    [FieldOffset(12)]
    public uint Flags;

    /// <summary>Total segment size in bytes.</summary>
    [FieldOffset(16)]
    public ulong TotalSize;

    /// <summary>Offset to Ring A (client→server) in the segment.</summary>
    [FieldOffset(24)]
    public ulong RingAOffset;

    /// <summary>Data area capacity for Ring A.</summary>
    [FieldOffset(32)]
    public ulong RingACapacity;

    /// <summary>Offset to Ring B (server→client) in the segment.</summary>
    [FieldOffset(40)]
    public ulong RingBOffset;

    /// <summary>Data area capacity for Ring B.</summary>
    [FieldOffset(48)]
    public ulong RingBCapacity;

    /// <summary>Maximum concurrent streams.</summary>
    [FieldOffset(56)]
    public uint MaxStreams;

    /// <summary>Client ready flag.</summary>
    [FieldOffset(60)]
    public uint ClientReady;

    /// <summary>Server ready flag.</summary>
    [FieldOffset(64)]
    public uint ServerReady;

    // Offset 68-127: Reserved (60 bytes) - implicitly zeroed
}

/// <summary>
/// Represents a shared memory segment containing two ring buffers for bidirectional communication.
/// Ring A is used for client→server data, Ring B for server→client data.
/// </summary>
public sealed class Segment : IDisposable
{
    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly Memory<byte> _memory;
    private readonly byte[] _memoryBuffer;
    private readonly bool _isServer;
    private bool _disposed;

    /// <summary>Gets the segment name.</summary>
    public string Name { get; }

    /// <summary>Gets the Ring A (client→server) ring buffer.</summary>
    public ShmRing RingA { get; }

    /// <summary>Gets the Ring B (server→client) ring buffer.</summary>
    public ShmRing RingB { get; }

    /// <summary>Gets the segment header.</summary>
    public SegmentHeader Header => GetHeader();

    /// <summary>Gets the total segment size in bytes.</summary>
    public long Size { get; }

    private Segment(
        string name,
        MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor accessor,
        byte[] memoryBuffer,
        bool isServer,
        ulong ringAOffset,
        ulong ringACapacity,
        ulong ringBOffset,
        ulong ringBCapacity)
    {
        Name = name;
        _mappedFile = mappedFile;
        _accessor = accessor;
        _memoryBuffer = memoryBuffer;
        _memory = memoryBuffer;
        _isServer = isServer;
        Size = memoryBuffer.Length;

        // Create ring sync primitives
        IRingSync? syncA = null;
        IRingSync? syncB = null;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                syncA = RingSyncFactory.Create(name, "A", isServer);
                syncB = RingSyncFactory.Create(name, "B", isServer);
            }
        }
        catch
        {
            // Sync primitives are optional - fall back to polling if not available
        }

        // Create ring buffers
        RingA = new ShmRing(_memory, (int)ringAOffset, ringACapacity, syncA);
        RingB = new ShmRing(_memory, (int)ringBOffset, ringBCapacity, syncB);
    }

    /// <summary>
    /// Creates a new shared memory segment (server-side).
    /// </summary>
    /// <param name="name">The segment name for identification.</param>
    /// <param name="ringCapacity">The capacity for each ring buffer (must be power of 2).</param>
    /// <param name="maxStreams">Maximum concurrent streams.</param>
    /// <returns>The created segment.</returns>
    public static Segment Create(string name, ulong ringCapacity = 64 * 1024 * 1024, uint maxStreams = 100)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Segment name cannot be null or empty", nameof(name));
        }

        if (ringCapacity == 0 || (ringCapacity & (ringCapacity - 1)) != 0)
        {
            throw new ArgumentException("Ring capacity must be a power of 2", nameof(ringCapacity));
        }

        // Calculate total segment size
        // Layout: [SegmentHeader (128)] [RingA Header (64)] [RingA Data] [RingB Header (64)] [RingB Data]
        var ringAOffset = (ulong)ShmConstants.SegmentHeaderSize;
        var ringBOffset = ringAOffset + (ulong)ShmConstants.RingHeaderSize + ringCapacity;
        var totalSize = ringBOffset + (ulong)ShmConstants.RingHeaderSize + ringCapacity;

        // Create memory-mapped file
        var mappedFile = MemoryMappedFile.CreateNew(
            $"grpc_shm_{name}",
            (long)totalSize,
            MemoryMappedFileAccess.ReadWrite);

        var accessor = mappedFile.CreateViewAccessor(0, (long)totalSize, MemoryMappedFileAccess.ReadWrite);

        // Read the mapped memory into a managed buffer
        var memoryBuffer = new byte[totalSize];
        accessor.ReadArray(0, memoryBuffer, 0, memoryBuffer.Length);

        // Initialize segment header with grpc-go-shmem compatible magic
        var header = new SegmentHeader
        {
            MagicValue = BitConverter.ToUInt64(ShmConstants.SegmentMagicBytes),
            Version = ShmConstants.ProtocolVersion,
            Flags = 0,
            TotalSize = totalSize,
            RingAOffset = ringAOffset,
            RingACapacity = ringCapacity,
            RingBOffset = ringBOffset,
            RingBCapacity = ringCapacity,
            MaxStreams = maxStreams,
            ServerReady = 1  // Server is ready when creating
        };

        // Write header to buffer
        WriteSegmentHeader(memoryBuffer, header);

        // Initialize ring headers
        InitializeRingHeader(memoryBuffer, (int)ringAOffset, ringCapacity);
        InitializeRingHeader(memoryBuffer, (int)ringBOffset, ringCapacity);

        // Write back to mapped file
        accessor.WriteArray(0, memoryBuffer, 0, memoryBuffer.Length);

        return new Segment(name, mappedFile, accessor, memoryBuffer, true,
            ringAOffset, ringCapacity, ringBOffset, ringCapacity);
    }

    /// <summary>
    /// Opens an existing shared memory segment (client-side).
    /// </summary>
    /// <param name="name">The segment name.</param>
    /// <returns>The opened segment.</returns>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static Segment Open(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Segment name cannot be null or empty", nameof(name));
        }

        // Open existing memory-mapped file
        var mappedFile = MemoryMappedFile.OpenExisting($"grpc_shm_{name}", MemoryMappedFileRights.ReadWrite);

        // We need to read the header first to determine the size
        using var headerAccessor = mappedFile.CreateViewAccessor(0, ShmConstants.SegmentHeaderSize, MemoryMappedFileAccess.Read);
        var headerBuffer = new byte[ShmConstants.SegmentHeaderSize];
        headerAccessor.ReadArray(0, headerBuffer, 0, headerBuffer.Length);

        var header = ReadSegmentHeader(headerBuffer);

        // Validate magic - check for grpc-go-shmem compatible "GRPCSHM\0"
        var expectedMagic = BitConverter.ToUInt64(ShmConstants.SegmentMagicBytes);
        if (header.MagicValue != expectedMagic)
        {
            mappedFile.Dispose();
            throw new InvalidDataException($"Invalid segment magic: expected 'GRPCSHM\\0', got 0x{header.MagicValue:X16}");
        }

        if (header.Version != ShmConstants.ProtocolVersion)
        {
            mappedFile.Dispose();
            throw new InvalidDataException($"Unsupported protocol version: expected {ShmConstants.ProtocolVersion}, got {header.Version}");
        }

        // Use TotalSize from header if available, otherwise calculate
        var totalSize = header.TotalSize > 0 
            ? header.TotalSize 
            : header.RingBOffset + (ulong)ShmConstants.RingHeaderSize + header.RingBCapacity;
        var accessor = mappedFile.CreateViewAccessor(0, (long)totalSize, MemoryMappedFileAccess.ReadWrite);

        var memoryBuffer = new byte[totalSize];
        accessor.ReadArray(0, memoryBuffer, 0, memoryBuffer.Length);

        return new Segment(name, mappedFile, accessor, memoryBuffer, false,
            header.RingAOffset, header.RingACapacity, header.RingBOffset, header.RingBCapacity);
    }

    /// <summary>
    /// Synchronizes the in-memory buffer with the memory-mapped file.
    /// Call this periodically or after writes to ensure data is visible to other processes.
    /// </summary>
    public void Flush()
    {
        if (_disposed) return;
        _accessor.WriteArray(0, _memoryBuffer, 0, _memoryBuffer.Length);
        _accessor.Flush();
    }

    /// <summary>
    /// Reads the latest data from the memory-mapped file into the in-memory buffer.
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;
        _accessor.ReadArray(0, _memoryBuffer, 0, _memoryBuffer.Length);
    }

    private SegmentHeader GetHeader()
    {
        return ReadSegmentHeader(_memoryBuffer);
    }

    private static void WriteSegmentHeader(byte[] buffer, SegmentHeader header)
    {
        var span = buffer.AsSpan(0, ShmConstants.SegmentHeaderSize);
        span.Clear(); // Zero all bytes first
        
        // Write grpc-go-shmem compatible header (128 bytes)
        BinaryPrimitives.WriteUInt64LittleEndian(span[0..8], header.MagicValue);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], header.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], header.Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..24], header.TotalSize);
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..32], header.RingAOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..40], header.RingACapacity);
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..48], header.RingBOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[48..56], header.RingBCapacity);
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..60], header.MaxStreams);
        BinaryPrimitives.WriteUInt32LittleEndian(span[60..64], header.ClientReady);
        BinaryPrimitives.WriteUInt32LittleEndian(span[64..68], header.ServerReady);
    }

    private static SegmentHeader ReadSegmentHeader(byte[] buffer)
    {
        var span = buffer.AsSpan(0, ShmConstants.SegmentHeaderSize);
        return new SegmentHeader
        {
            MagicValue = BinaryPrimitives.ReadUInt64LittleEndian(span[0..8]),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(span[8..12]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(span[12..16]),
            TotalSize = BinaryPrimitives.ReadUInt64LittleEndian(span[16..24]),
            RingAOffset = BinaryPrimitives.ReadUInt64LittleEndian(span[24..32]),
            RingACapacity = BinaryPrimitives.ReadUInt64LittleEndian(span[32..40]),
            RingBOffset = BinaryPrimitives.ReadUInt64LittleEndian(span[40..48]),
            RingBCapacity = BinaryPrimitives.ReadUInt64LittleEndian(span[48..56]),
            MaxStreams = BinaryPrimitives.ReadUInt32LittleEndian(span[56..60]),
            ClientReady = BinaryPrimitives.ReadUInt32LittleEndian(span[60..64]),
            ServerReady = BinaryPrimitives.ReadUInt32LittleEndian(span[64..68])
        };
    }

    private static void InitializeRingHeader(byte[] buffer, int offset, ulong capacity)
    {
        var span = buffer.AsSpan(offset, ShmConstants.RingHeaderSize);
        span.Clear(); // Zero all fields
        // Write capacity at offset 0 (grpc-go-shmem layout: capacity is first field)
        BinaryPrimitives.WriteUInt64LittleEndian(span[0..8], capacity);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RingA.Dispose();
        RingB.Dispose();
        _accessor.Dispose();
        _mappedFile.Dispose();
    }
}
