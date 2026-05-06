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

namespace Grpc.Net.SharedMemory;

using System.Buffers;

/// <summary>
/// High-level frame protocol operations for reading and writing gRPC frames
/// to the shared memory ring buffer.
/// </summary>
public static class FrameProtocol
{
    /// <summary>
    /// Maximum allowed frame payload size (128 MiB). Any frame header claiming a
    /// payload larger than this is treated as data corruption (e.g., from a SPSC
    /// ring buffer violation or stale shared memory) and will throw rather than
    /// attempting a huge allocation that would hang or OOM.
    /// </summary>
    internal const int MaxFramePayloadSize = 128 * 1024 * 1024;

    /// <summary>
    /// Reads a frame from the ring and returns a pooled-buffer payload.
    /// Dispatches to the configured <see cref="Wire.WireFormat"/> codec.
    /// </summary>
    public static (FrameHeader Header, FramePayload Payload) ReadFramePayload(
        ShmRing ring,
        CancellationToken cancellationToken = default,
        bool zeroCopy = false)
    {
        // Wire is set once at connection establishment and never changes.
        // The branch predictor learns the per-ring direction after one
        // frame; the JIT inlines this wrapper and the impl into
        // FrameReaderLoop. Cost is one field load + a 100%-predictable
        // branch.
        return ring.Wire == Wire.WireFormat.Http2
            ? Wire.Http2Codec.ReadFramePayload(ring, cancellationToken, zeroCopy)
            : ReadFramePayloadCustom16(ring, cancellationToken, zeroCopy);
    }

    /// <summary>
    /// Test/diagnostic helper retained for back-compat with
    /// <c>RingBench</c>. Returns zeros: per-frame counters were dropped
    /// in favour of branch-predictor-friendly dispatch. To check which
    /// codec a connection negotiated, inspect <see cref="ShmRing.Wire"/>
    /// on the ring directly.
    /// </summary>
    public static (long Custom16Read, long Http2Read, long Custom16Write, long Http2Write) GetCodecCounters()
        => (0L, 0L, 0L, 0L);

    /// <summary>No-op retained for <c>RingBench</c> back-compat.</summary>
    public static void ResetCodecCounters() { }

    /// <summary>
    /// Reads a Custom16-encoded frame and returns a pooled-buffer payload.
    /// </summary>
    internal static (FrameHeader Header, FramePayload Payload) ReadFramePayloadCustom16(
        ShmRing ring,
        CancellationToken cancellationToken = default,
        bool zeroCopy = false)
    {
        while (true)
        {
            // Read frame header — reserve but defer CommitRead until payload
            // is also read, so we issue a single Volatile.Write to shared
            // ReadIdx per frame instead of two.
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);
            var baseCommitReadIdx = headerReservation.CommitReadIdx;

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            // Note: CommitRead deferred — will be batched with payload below.

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers that could cause huge
            // allocations or block forever trying to read from the ring.
            if (header.Length > MaxFramePayloadSize)
            {
                ring.CommitReadAnchored(ring.PeekPendingReadIdx());
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}. " +
                    "This may indicate data corruption in the shared memory ring buffer.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                ring.CommitReadAnchored(ring.PeekPendingReadIdx());
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}. " +
                    "This may indicate data corruption in the shared memory ring buffer.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitReadAnchored(ring.PeekPendingReadIdx());
                }
                else
                {
                    ring.CommitReadAnchored(ring.PeekPendingReadIdx());
                }
                continue;
            }

            if (header.Length == 0)
            {
                ring.CommitReadAnchored(ring.PeekPendingReadIdx());
                return (header, FramePayload.Empty);
            }

            var payloadLength = (int)header.Length;
            var payloadReservation = ring.ReserveRead(payloadLength, cancellationToken);
            var totalBytes = ShmConstants.FrameHeaderSize + payloadLength;
            var contiguous = payloadReservation.Second.IsEmpty;

            // ===== Phase Y: per-frame ZC anchor FIFO =====
            //
            // Replaces the legacy chain-anchor + ChainCopyMode dispatch
            // tree with a single decision: per-frame ZC vs copy. The
            // FIFO supports any number of in-flight anchors, releasing
            // independently — there is no "chain" concept and no
            // budget-vs-message-size eligibility step. Each frame stands
            // on its own.
            //
            // Cross-process correctness: the FIFO drain (in ReleasePerFrameZc)
            // CAS-advances header.ReadIdx only as far as the earliest
            // unreleased anchor's BaseIdx. Out-of-order releases stash
            // until the head drains, preserving the invariant that the
            // writer never wraps onto held bytes.
            if (zeroCopy && contiguous && ring.IsZcEligibleForAnchor(payloadLength, contiguous: true))
            {
                ring.EnsureZcAnchorFifo();
                // Pass the EXACT post-frame ring position (from
                // _pendingReadIdx, advanced by both ReserveRead calls
                // above). Using baseCommitReadIdx + totalBytes is wrong
                // when earlier ZC anchors hold header.ReadIdx — the stale
                // baseCommitReadIdx silently shrinks slot.EndIdx and the
                // FIFO drain under-advances readIdx, leaking ring bytes
                // each iteration until the writer blocks forever in
                // WaitForSpace.
                var endIdx = ring.PeekPendingReadIdx();
                var slot = ring.TryBeginPerFrameZc(endIdx);
                if (slot >= 0)
                {
                    return (header, FramePayload.FromRingZcAnchor(
                        payloadReservation.First.Slice(0, payloadLength), ring, slot));
                }
                // FIFO at >=75% capacity: fall through to copy. The drain
                // back-pressure gate keeps consumers from pinning the head
                // long enough to indefinitely block the producer.
            }

            // Copy fallback: sub-ZC-threshold, ring < 1 MiB, wrap, ZC
            // disabled, or FIFO back-pressure. CommitReadAnchored advances
            // header.ReadIdx directly when the FIFO is empty, or stashes
            // the target via _pendingNonZcMax to be folded in at the next
            // FIFO drain.
            var pooled = ArrayPool<byte>.Shared.Rent(payloadLength);
            if (contiguous)
            {
                payloadReservation.First.Span.Slice(0, payloadLength).CopyTo(pooled);
            }
            else
            {
                CopyFromReservation(payloadReservation, pooled.AsSpan(0, payloadLength));
            }
            ring.CommitReadAnchored(ring.PeekPendingReadIdx());
            return (header, FramePayload.FromPooled(pooled, payloadLength));
        }
    }

    /// <summary>
    /// Writes a frame (header + payload) to the ring buffer atomically.
    /// Blocks until space is available.
    /// </summary>
    /// <param name="ring">The ring buffer to write to.</param>
    /// <param name="header">The frame header.</param>
    /// <param name="payload">The frame payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static void WriteFrame(ShmRing ring, FrameHeader header, ReadOnlySpan<byte> payload, CancellationToken cancellationToken = default)
    {
        // Delegate to the scatter-write overload with an empty second payload
        WriteFrame(ring, header, payload, ReadOnlySpan<byte>.Empty, cancellationToken);
    }

    /// <summary>
    /// Writes a Custom16-encoded frame to the ring. Used by the dispatch shim
    /// when <see cref="ShmRing.Wire"/> is <see cref="Wire.WireFormat.Custom16"/>
    /// and by the H2 codec for the rare cases where it falls back.
    /// </summary>
    internal static void WriteFrameCustom16(ShmRing ring, FrameHeader header,
        ReadOnlySpan<byte> payload1, ReadOnlySpan<byte> payload2, CancellationToken cancellationToken = default)
    {
        WriteFrameCore(ring, header, payload1, payload2, cancellationToken);
    }



    /// <summary>
    /// Writes a frame with a two-part payload (scatter write) to the ring buffer atomically.
    /// This avoids an intermediate copy when the payload is logically split (e.g., gRPC prefix + data).
    /// The frame header's Length is set to payload1.Length + payload2.Length.
    /// </summary>
    /// <param name="ring">The ring buffer to write to.</param>
    /// <param name="header">The frame header.</param>
    /// <param name="payload1">The first part of the frame payload (e.g., gRPC length-prefix header).</param>
    /// <param name="payload2">The second part of the frame payload (e.g., protobuf message data).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static void WriteFrame(ShmRing ring, FrameHeader header, ReadOnlySpan<byte> payload1, ReadOnlySpan<byte> payload2, CancellationToken cancellationToken = default)
    {
        if (ring.Wire == Wire.WireFormat.Http2)
        {
            Wire.Http2Codec.WriteFrame(ring, header, payload1, payload2, cancellationToken);
            return;
        }
        WriteFrameCore(ring, header, payload1, payload2, cancellationToken);
    }

    private static void WriteFrameCore(ShmRing ring, FrameHeader header, ReadOnlySpan<byte> payload1, ReadOnlySpan<byte> payload2, CancellationToken cancellationToken = default)
    {
        var totalPayloadSize = payload1.Length + payload2.Length;
        header.Length = (uint)totalPayloadSize;
        header.Reserved = 0;
        header.Reserved2 = 0;

        var totalSize = ShmConstants.FrameHeaderSize + totalPayloadSize;

        // Reserve space for the entire frame atomically
        var reservation = ring.ReserveWrite(totalSize, cancellationToken);

        // Encode header
        Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(headerBytes);

        // We need to write 3 parts into potentially 2 slices (First/Second).
        // Use a helper approach: treat the reservation as a linear span and write sequentially.
        var firstSpan = reservation.First.Span;
        var secondSpan = reservation.Second.Span;
        var written = 0;

        // Write header
        written = WriteToReservation(firstSpan, secondSpan, written, headerBytes);

        // Write payload1
        if (payload1.Length > 0)
        {
            written = WriteToReservation(firstSpan, secondSpan, written, payload1);
        }

        // Write payload2
        if (payload2.Length > 0)
        {
            written = WriteToReservation(firstSpan, secondSpan, written, payload2);
        }

        // Commit the write
        ring.CommitWrite(reservation, written);
    }

    /// <summary>
    /// Writes data to a two-part reservation (First/Second spans) starting at the given offset.
    /// Returns the new offset after writing.
    /// </summary>
    private static int WriteToReservation(Span<byte> first, Span<byte> second, int offset, ReadOnlySpan<byte> data)
    {
        var remaining = data.Length;
        var dataOffset = 0;

        // Write to First span if we haven't passed it yet
        if (offset < first.Length && remaining > 0)
        {
            var available = first.Length - offset;
            var toCopy = Math.Min(remaining, available);
            data.Slice(dataOffset, toCopy).CopyTo(first.Slice(offset));
            offset += toCopy;
            dataOffset += toCopy;
            remaining -= toCopy;
        }

        // Write to Second span for anything remaining
        if (remaining > 0)
        {
            var secondOffset = offset - first.Length;
            data.Slice(dataOffset, remaining).CopyTo(second.Slice(secondOffset));
            offset += remaining;
        }

        return offset;
    }

    // ===== MPSC writer helpers (PR2 phase 1.2+) =====

    /// <summary>
    /// MPSC equivalent of <see cref="WriteFrame(ShmRing, FrameHeader, ReadOnlySpan{byte}, CancellationToken)"/>:
    /// dispatches to Custom16 or H2 wire format and writes the frame via
    /// <see cref="ShmRing.MpscReserveWrite"/> + <see cref="MpscWriteSlot.Commit"/>.
    /// Multiple writers may call this concurrently on the same ring;
    /// each gets a non-overlapping slot. Commit ordering is enforced by
    /// the ring's publish-spin so on-wire frames appear in claim order.
    /// </summary>
    public static void WriteFrameMpsc(
        ShmRing ring, FrameHeader header,
        ReadOnlySpan<byte> payload, CancellationToken cancellationToken = default)
    {
        if (ring.Wire == Wire.WireFormat.Http2)
        {
            Wire.Http2Codec.WriteFrameMpsc(ring, header, payload, cancellationToken);
            return;
        }
        WriteFrameMpscCustom16(ring, header, payload, cancellationToken);
    }

    private static void WriteFrameMpscCustom16(
        ShmRing ring, FrameHeader header,
        ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        header.Length = (uint)payload.Length;
        header.Reserved = 0;
        header.Reserved2 = 0;

        var totalSize = ShmConstants.FrameHeaderSize + payload.Length;
        using var slot = ring.MpscReserveWrite(totalSize, cancellationToken);

        // Encode the 16-byte header into the slot, handling wrap by
        // splitting between First and Second.
        Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(headerBytes);

        var firstSpan = slot.First.Span;
        var secondSpan = slot.Second.Span;
        var written = WriteToReservation(firstSpan, secondSpan, 0, headerBytes);
        if (payload.Length > 0)
        {
            written = WriteToReservation(firstSpan, secondSpan, written, payload);
        }
        slot.Commit();
    }

    /// <summary>
    /// MPSC HALF_CLOSE: emits the wire-format-appropriate end-of-stream
    /// frame. On Custom16: a 16-byte HALF_CLOSE-typed frame. On H2: a
    /// 9-byte DATA frame with END_STREAM and zero payload. Equivalent
    /// in semantics to <see cref="WriteHalfClose"/> but uses the MPSC
    /// claim+publish path (safe for concurrent writers on the same ring).
    /// </summary>
    public static void WriteHalfCloseMpsc(
        ShmRing ring, uint streamId, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.HalfClose, streamId, 0, 0);
        WriteFrameMpsc(ring, header, ReadOnlySpan<byte>.Empty, cancellationToken);
    }

    /// <summary>
    /// Reads a frame from the ring buffer, skipping PAD frames.
    /// Blocks until a frame is available.
    /// </summary>
    /// <param name="ring">The ring buffer to read from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The frame header and payload.</returns>
    public static (FrameHeader Header, byte[] Payload) ReadFrame(ShmRing ring, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Read frame header
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            ring.CommitRead(headerReservation, ShmConstants.FrameHeaderSize);

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers
            if (header.Length > MaxFramePayloadSize)
            {
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    // Skip the padding payload
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitRead(padReservation, (int)header.Length);
                }
                continue;
            }

            // Read payload if present
            byte[] payload;
            if (header.Length > 0)
            {
                payload = new byte[header.Length];
                var payloadReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                CopyFromReservation(payloadReservation, payload);
                ring.CommitRead(payloadReservation, (int)header.Length);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            return (header, payload);
        }
    }

    /// <summary>
    /// Reads a frame from the ring buffer using ArrayPool to avoid per-frame heap allocation.
    /// The caller is responsible for returning the payload array to <see cref="ArrayPool{T}.Shared"/>
    /// when done (unless PayloadLength is 0, in which case Payload is <see cref="Array.Empty{T}"/>).
    /// </summary>
    /// <param name="ring">The ring buffer to read from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The frame header, pooled payload buffer, and actual payload length.</returns>
    public static (FrameHeader Header, byte[] Payload, int PayloadLength) ReadFramePooled(ShmRing ring, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Read frame header
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            ring.CommitRead(headerReservation, ShmConstants.FrameHeaderSize);

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers
            if (header.Length > MaxFramePayloadSize)
            {
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitRead(padReservation, (int)header.Length);
                }
                continue;
            }

            // Read payload into a pooled buffer if present
            if (header.Length > 0)
            {
                var payloadLength = (int)header.Length;
                var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
                var payloadReservation = ring.ReserveRead(payloadLength, cancellationToken);
                CopyFromReservation(payloadReservation, payload.AsSpan(0, payloadLength));
                ring.CommitRead(payloadReservation, payloadLength);
                return (header, payload, payloadLength);
            }

            return (header, Array.Empty<byte>(), 0);
        }
    }

    /// <summary>
    /// Reads a frame without allocating a new payload array.
    /// The payload is written to the provided buffer.
    /// </summary>
    /// <param name="ring">The ring buffer to read from.</param>
    /// <param name="payloadBuffer">Buffer to receive the payload. Must be large enough.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The frame header and actual payload length.</returns>
    public static (FrameHeader Header, int PayloadLength) ReadFrameInto(
        ShmRing ring,
        Span<byte> payloadBuffer,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Read frame header
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            ring.CommitRead(headerReservation, ShmConstants.FrameHeaderSize);

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers
            if (header.Length > MaxFramePayloadSize)
            {
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitRead(padReservation, (int)header.Length);
                }
                continue;
            }

            // Read payload if present
            if (header.Length > 0)
            {
                if (payloadBuffer.Length < header.Length)
                {
                    throw new ArgumentException($"Payload buffer too small: need {header.Length}, have {payloadBuffer.Length}");
                }

                var payloadReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                CopyFromReservation(payloadReservation, payloadBuffer[..(int)header.Length]);
                ring.CommitRead(payloadReservation, (int)header.Length);
            }

            return (header, (int)header.Length);
        }
    }

    /// <summary>
    /// Writes a PING frame.
    /// </summary>
    public static void WritePing(ShmRing ring, byte flags, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.Ping, 0, (uint)data.Length, flags);
        WriteFrame(ring, header, data, cancellationToken);
    }

    /// <summary>
    /// Writes a PONG frame.
    /// </summary>
    public static void WritePong(ShmRing ring, byte flags, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.Pong, 0, (uint)data.Length, flags);
        WriteFrame(ring, header, data, cancellationToken);
    }

    /// <summary>
    /// Writes a GOAWAY frame.
    /// </summary>
    public static void WriteGoAway(ShmRing ring, byte flags, string? debugMessage = null, CancellationToken cancellationToken = default)
    {
        var payload = debugMessage != null ? System.Text.Encoding.UTF8.GetBytes(debugMessage) : Array.Empty<byte>();
        var header = new FrameHeader(FrameType.GoAway, 0, (uint)payload.Length, flags);
        WriteFrame(ring, header, payload.AsSpan(), cancellationToken);
    }

    /// <summary>
    /// Writes a CANCEL frame.
    /// </summary>
    public static void WriteCancel(ShmRing ring, uint streamId, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.Cancel, streamId, 0, 0);
        WriteFrame(ring, header, ReadOnlySpan<byte>.Empty, cancellationToken);
    }

    /// <summary>
    /// Writes a WINDOW_UPDATE frame.
    /// </summary>
    public static void WriteWindowUpdate(ShmRing ring, uint streamId, uint windowSizeIncrement, CancellationToken cancellationToken = default)
    {
        Span<byte> payload = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, windowSizeIncrement);
        var header = new FrameHeader(FrameType.WindowUpdate, streamId, 4, 0);
        WriteFrame(ring, header, payload, cancellationToken);
    }

    /// <summary>
    /// Writes a MESSAGE frame, automatically chunking if the payload exceeds
    /// the ring capacity. Matches grpc-go-shmem's writeFrameBuffersChunked.
    /// </summary>
    public static void WriteMessage(ShmRing ring, uint streamId, ReadOnlySpan<byte> data, bool isLast, CancellationToken cancellationToken = default, byte extraFlags = 0)
    {
        var flags = (byte)((isLast ? 0 : MessageFlags.More) | extraFlags);

        var cap = (int)ring.Capacity;
        // Max frame payload = ringCap/3. Chosen so that:
        // 1. Common payloads (4MB, 16MB) fit in a single frame →
        //    speculative zero-copy read path (no More flag).
        //    16MB protobuf CalculateSize ≈ 16.8MB < 64MB/3 = 22.4MB.
        // 2. Speculative safety: N=2 in-flight × cap/3 = 2cap/3 < cap,
        //    so writer needs cap/3 (~22MB) to reach oldest frame.
        // 3. Pipeline: 3 frames fit simultaneously in the ring,
        //    providing good overlap for streaming workloads.
        var maxFramePayload = Math.Max(1, cap / 3);

        // HTTP/2 has an absolute hard cap on per-frame payload length
        // (24-bit field; RFC 7540 §6.5.2 SETTINGS_MAX_FRAME_SIZE upper bound
        // is 2^24 - 1). Cap our chunk size below that so the H2 codec can
        // encode every frame.
        if (ring.Wire == Wire.WireFormat.Http2 && maxFramePayload > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
        {
            maxFramePayload = Wire.Http2FrameHeader.MaxAllowedPayloadLength;
        }

        if (data.Length <= maxFramePayload)
        {
            var header = new FrameHeader(FrameType.Message, streamId, (uint)data.Length, flags);
            WriteFrame(ring, header, data, cancellationToken);
            return;
        }

        var remaining = data;
        while (remaining.Length > 0)
        {
            var chunkSize = Math.Min(maxFramePayload, remaining.Length);
            var chunk = remaining[..chunkSize];
            remaining = remaining[chunkSize..];

            byte chunkFlags;
            if (remaining.Length > 0)
            {
                chunkFlags = MessageFlags.More;
            }
            else
            {
                chunkFlags = flags;
            }

            var header = new FrameHeader(FrameType.Message, streamId, (uint)chunkSize, chunkFlags);
            WriteFrame(ring, header, chunk, cancellationToken);
        }
    }

    /// <summary>
    /// Writes a HALF_CLOSE frame.
    /// </summary>
    public static void WriteHalfClose(ShmRing ring, uint streamId, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.HalfClose, streamId, 0, 0);
        WriteFrame(ring, header, ReadOnlySpan<byte>.Empty, cancellationToken);
    }

    private static void CopyFromReservation(ReadReservation reservation, Span<byte> destination)
    {
        var copied = 0;
        if (reservation.First.Length > 0)
        {
            var toCopy = Math.Min(reservation.First.Length, destination.Length);
            reservation.First.Span[..toCopy].CopyTo(destination);
            copied += toCopy;
        }
        if (reservation.Second.Length > 0 && copied < destination.Length)
        {
            var toCopy = Math.Min(reservation.Second.Length, destination.Length - copied);
            reservation.Second.Span[..toCopy].CopyTo(destination[copied..]);
        }
    }
}
