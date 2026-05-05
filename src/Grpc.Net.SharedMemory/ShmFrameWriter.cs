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
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Google.Protobuf;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Batched frame writer inspired by Kestrel's <c>Http2FrameWriter</c>.
/// Uses a lock-free <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>
/// for MPSC enqueue (multiple app threads → single writer thread) to avoid
/// <c>Monitor.Enter</c> contention in high-concurrency streaming scenarios.
/// Small payloads are defensively copied into pooled buffers at enqueue time.
/// Large payloads can be enqueued zero-copy; the pooled buffer is returned
/// after the data has been written to the ring buffer.
/// </summary>
internal sealed class ShmFrameWriter : IDisposable
{
    // PR2 phase 1.4: WriterLoop / queue / TryPause / ExecuteInline / phase
    // machine all deleted. Every Enqueue* and WriteInline* method writes
    // synchronously to the ring on the caller's thread via the MPSC
    // primitives in <see cref="ShmRing.MpscReserveWrite"/>. Cross-thread
    // ordering is enforced by the publish-spin against header.WriteIdx;
    // ring-full back-pressure goes directly through WaitForSpace (no
    // unbounded queue growth in user-process memory).
    //
    // Public Enqueue* methods are kept for back-compat with current call
    // sites (ShmConnection.SendFrame / SendFrameZeroCopy / SendFrameAndWait).
    // Internal WriteInline* / TryPauseWriterLoop / ResumeWriterLoop /
    // ExecuteInline / EnableSingleStreamMode are kept as thin wrappers /
    // no-op stubs for the 14 single-stream call sites that still use them;
    // those call sites can migrate to direct MPSC any time without changing
    // the writer's surface.

    private readonly ShmRing _ring;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _ct;
    private int _disposed;

    public ShmFrameWriter(ShmRing ring, CancellationTokenSource cts)
    {
        _ring = ring;
        _cts = cts;
        _ct = cts.Token;
    }

    /// <summary>
    /// Phase 1.4 stub: SingleStreamMode is now metadata-only (carried on
    /// the connection for diagnostics). The writer no longer maintains
    /// any state that varies on this flag.
    /// </summary>
#pragma warning disable CA1822 // No-op stub kept as instance method for caller back-compat
    internal void EnableSingleStreamMode()
    {
        // No-op. Kept for caller back-compat.
    }
#pragma warning restore CA1822

    /// <summary>
    /// Writes a frame to the ring on the caller's thread. The payload is
    /// copied into the ring's MPSC slot; no intermediate pooling needed
    /// because the write is synchronous.
    /// </summary>
    /// <exception cref="InvalidOperationException">The writer has been disposed.</exception>
    public void Enqueue(FrameType type, uint streamId, byte flags, ReadOnlySpan<byte> payload)
    {
        if (_disposed != 0)
            throw new InvalidOperationException("Frame writer has been disposed.");

        if (type == FrameType.Message)
        {
            var isLast = (flags & MessageFlags.More) == 0;
            var extraFlags = (byte)(flags & ~MessageFlags.More);
            FrameProtocol.WriteMessage(_ring, streamId, payload, isLast, _ct, extraFlags);
        }
        else
        {
            var header = new FrameHeader(type, streamId, (uint)payload.Length, flags);
            FrameProtocol.WriteFrame(_ring, header, payload, _ct);
        }
    }

    /// <summary>
    /// Writes a frame whose payload is in a caller-owned pooled buffer.
    /// The buffer is returned to <see cref="ArrayPool{T}.Shared"/> after
    /// the synchronous ring write completes (success or throw).
    /// Pass <c>null</c> if the payload does not need to be returned.
    /// </summary>
    /// <exception cref="InvalidOperationException">The writer has been disposed.</exception>
    public void EnqueueZeroCopy(FrameType type, uint streamId, byte flags,
        ReadOnlyMemory<byte> payload, byte[]? pooledBuffer)
    {
        if (_disposed != 0)
        {
            if (pooledBuffer != null)
                ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw new InvalidOperationException("Frame writer has been disposed.");
        }

        try
        {
            if (type == FrameType.Message)
            {
                var isLast = (flags & MessageFlags.More) == 0;
                var extraFlags = (byte)(flags & ~MessageFlags.More);
                FrameProtocol.WriteMessage(_ring, streamId, payload.Span, isLast, _ct, extraFlags);
            }
            else
            {
                var header = new FrameHeader(type, streamId, (uint)payload.Length, flags);
                FrameProtocol.WriteFrame(_ring, header, payload.Span, _ct);
            }
        }
        finally
        {
            if (pooledBuffer != null)
                ArrayPool<byte>.Shared.Return(pooledBuffer);
        }
    }

    /// <summary>
    /// Writes a frame and returns when the bytes are in the ring. Under
    /// the synchronous MPSC writer this is just a write — "AndWait" is
    /// automatic. The caller may safely reuse <paramref name="payload"/>
    /// after this method returns.
    /// </summary>
    public void EnqueueZeroCopyAndWait(FrameType type, uint streamId, byte flags,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_disposed != 0)
            throw new InvalidOperationException("Frame writer has been disposed.");

        if (type == FrameType.Message)
        {
            var isLast = (flags & MessageFlags.More) == 0;
            var extraFlags = (byte)(flags & ~MessageFlags.More);
            FrameProtocol.WriteMessage(_ring, streamId, payload.Span, isLast, cancellationToken, extraFlags);
        }
        else
        {
            var header = new FrameHeader(type, streamId, (uint)payload.Length, flags);
            FrameProtocol.WriteFrame(_ring, header, payload.Span, cancellationToken);
        }
    }

    /// <summary>
    /// <summary>
    /// Phase 1.4 stub: with the WriterLoop deleted, "inline" is the only
    /// path. Just runs <paramref name="action"/> on the caller's thread.
    /// MPSC publish-spin in <see cref="ShmRing.MpscPublish"/> serialises
    /// concurrent ring writers in claim order.
    /// </summary>
#pragma warning disable CA1822 // No-op stub kept as instance method for caller back-compat
    internal void ExecuteInline(Action action)
    {
        action();
    }

    /// <summary>
    /// Phase 1.4 stub: with the WriterLoop deleted, no pause coordination
    /// is needed. Always returns true; callers that conditioned on the
    /// return value still take the inline path.
    /// </summary>
    internal bool TryPauseWriterLoop()
    {
        return true;
    }

    /// <summary>Phase 1.4 stub: no-op (no WriterLoop to resume).</summary>
    internal void ResumeWriterLoop()
    {
        // No-op.
    }
#pragma warning restore CA1822

    /// <summary>
    /// Writes a message frame on the caller's thread via the MPSC writer
    /// path (today routed through <see cref="ShmRing.ReserveWrite"/> /
    /// <see cref="ShmRing.CommitWrite"/> which delegate to
    /// <see cref="ShmRing.MpscReserveWrite"/>).
    /// </summary>
    internal void WriteInline(uint streamId, ReadOnlySpan<byte> payload, byte extraFlags, CancellationToken ct)
    {
        var isLast = (extraFlags & MessageFlags.More) == 0;
        FrameProtocol.WriteMessage(_ring, streamId, payload, isLast, ct, extraFlags);
    }

    /// <summary>
    /// Writes an arbitrary frame inline on the caller's thread.
    /// Caller MUST have called PauseWriterLoop first.
    /// Does NOT drain the queue — caller is responsible for ordering.
    /// </summary>
    internal void WriteInlineFrame(FrameType type, uint streamId, byte flags, ReadOnlySpan<byte> payload, CancellationToken ct)
    {
        var header = new FrameHeader(type, streamId, (uint)payload.Length, flags);
        FrameProtocol.WriteFrame(_ring, header, payload, ct);
    }

    /// <summary>
    /// On-wire frame header size for the active wire format on this writer's ring.
    /// Custom16: 16 bytes; HTTP/2: 9 bytes.
    /// </summary>
    private int WireHeaderSize => _ring.Wire == Wire.WireFormat.Http2
        ? Wire.Http2FrameHeader.Size
        : ShmConstants.FrameHeaderSize;

    /// <summary>
    /// Encodes a MESSAGE/DATA wire-format frame header into <paramref name="dest"/>.
    /// <paramref name="internalFlags"/> uses the SHM-internal convention
    /// (<see cref="MessageFlags"/>): the H2 path translates to <c>END_STREAM</c>.
    /// </summary>
    private void EncodeMessageWireHeader(Span<byte> dest, uint streamId, int payloadLen, byte internalFlags)
    {
        if (_ring.Wire == Wire.WireFormat.Http2)
        {
            // Defensive: callers (WriteInlineDirectMultiFrame, RingFrameStream)
            // are expected to chunk so each call's payload fits in a 24-bit
            // length field. Bail out loudly rather than silently truncating
            // or corrupting the on-wire frame.
            if ((uint)payloadLen > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
            {
                throw new InvalidOperationException(
                    $"H2 wire frame payload {payloadLen} exceeds 24-bit limit. " +
                    "Caller must apply Http2FrameHeader.MaxAllowedPayloadLength chunk cap.");
            }
            // Mirror Http2Codec.WriteH2Data: END_STREAM only on a final non-More chunk.
            var isMore = (internalFlags & MessageFlags.More) != 0;
            var isEndStream = (internalFlags & MessageFlags.EndStream) != 0 && !isMore;
            Wire.Http2FrameHeader.Encode(
                dest,
                Wire.Http2FrameType.Data,
                (byte)(isEndStream ? Wire.Http2Flags.EndStream : 0),
                streamId,
                payloadLen);
            return;
        }
        var header = new FrameHeader(FrameType.Message, streamId, (uint)payloadLen, internalFlags);
        header.EncodeTo(dest);
    }

    /// <summary>
    /// Serializes a protobuf message directly into the ring buffer as one or
    /// more frames, bypassing any intermediate byte[] buffer. A custom
    /// <see cref="RingFrameStream"/> feeds <see cref="CodedOutputStream"/>
    /// writes into per-frame ring reservations. Each frame is committed as
    /// it fills, allowing the reader to start processing early and freeing
    /// ring space for subsequent frames. Works for all message sizes —
    /// single-frame and multi-frame are handled uniformly.
    /// </summary>
    internal void WriteInlineDirectMultiFrame(uint streamId, int payloadSize, IMessage message, byte extraFlags, CancellationToken ct)
    {
        var wireHdrSize = WireHeaderSize;
        var cap = (int)_ring.Capacity;
        // Single-frame threshold: payload ≤ cap/3 → WriteTo(Span) direct ring write.
        // Kept high to maximize speculative zero-copy on the reader side.
        var singleFrameThreshold = Math.Max(1, cap / 3);
        // Multi-frame chunk size: cap/8 for deeper pipeline (~8 chunks in-flight).
        // More reader/writer overlap reduces WaitForSpace stalls on large messages.
        var chunkSize = Math.Max(1, cap / 8);

        // HTTP/2 hard limit (RFC 7540 §4.2 / §6.5.2): per-frame payload must
        // fit in 24 bits (≤ 2^24 - 1). Cap both thresholds below that so a
        // 16 MiB protobuf (which yields a 16 MiB + 5 B framePayloadSize) is
        // not handed to Http2FrameHeader.Encode where it would throw.
        if (_ring.Wire == Wire.WireFormat.Http2)
        {
            if (singleFrameThreshold > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
                singleFrameThreshold = Wire.Http2FrameHeader.MaxAllowedPayloadLength;
            if (chunkSize > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
                chunkSize = Wire.Http2FrameHeader.MaxAllowedPayloadLength;
        }

        // The MESSAGE frame payload includes a 5-byte gRPC length-prefix
        // header (compression flag + big-endian uint32 length) followed by
        // the protobuf bytes. This is required for cross-language interop.
        const int GrpcHeaderSize = 5;
        var framePayloadSize = GrpcHeaderSize + payloadSize;

        // Single-frame + contiguous: use WriteTo(Span<byte>) to serialize
        // protobuf directly into the ring reservation. No CodedOutputStream,
        // no intermediate buffer, no Stream abstraction — one copy from
        // protobuf fields to ring memory.
        if (framePayloadSize <= singleFrameThreshold)
        {
            var totalSize = wireHdrSize + framePayloadSize;
            var reservation = _ring.ReserveWrite(totalSize, ct);
            var isLast = (extraFlags & MessageFlags.More) == 0;
            var flags = (byte)((isLast ? 0 : MessageFlags.More) | extraFlags);
            if (reservation.Second.IsEmpty)
            {
                // Contiguous slot: WriteTo(Span<byte>) serializes protobuf
                // directly into the ring reservation. No intermediate buffer.
                EncodeMessageWireHeader(reservation.First.Span, streamId, framePayloadSize, flags);

                // 5-byte gRPC LPM header.
                var grpcHdr = reservation.First.Span.Slice(wireHdrSize, GrpcHeaderSize);
                grpcHdr[0] = 0; // no compression
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(grpcHdr.Slice(1), (uint)payloadSize);

                // Serialize directly into ring span — zero intermediate buffer.
                if (payloadSize > 0)
                {
                    var payloadSpan = reservation.First.Span.Slice(wireHdrSize + GrpcHeaderSize, payloadSize);
                    message.WriteTo(payloadSpan);
                }

                _ring.CommitWrite(reservation, totalSize);
                return;
            }

            // Wrap-around: the reservation straddles the ring boundary so
            // WriteTo(Span) (needs contiguous memory) cannot be used. Under
            // PR1 SPSC the original code abandoned the reservation and
            // fell through to RingFrameStream which got a fresh one — that
            // was a free leak because WriteIdx had not yet advanced. Under
            // PR2 MPSC the slot is a REAL claim (_claimedWriteIdx already
            // advanced, _publishersInFlight incremented), so it MUST be
            // committed or every successor writer's publish-spin stalls
            // forever.
            //
            // Strategy: serialize protobuf into a pooled stage buffer,
            // then write the whole [wire header | LPM header | body] byte
            // sequence across the slot's First+Second spans via the
            // existing wrap-aware copy helper. One extra memcpy of
            // ≤singleFrameThreshold bytes — only on the rare wrap path,
            // which fires once per ring-capacity worth of writes (e.g.
            // ~once per 64 MiB on a default ring).
            var stageBuf = ArrayPool<byte>.Shared.Rent(totalSize);
            try
            {
                var stage = stageBuf.AsSpan(0, totalSize);
                EncodeMessageWireHeader(stage, streamId, framePayloadSize, flags);
                stage[wireHdrSize] = 0; // no compression
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                    stage.Slice(wireHdrSize + 1, 4), (uint)payloadSize);
                if (payloadSize > 0)
                {
                    message.WriteTo(stage.Slice(wireHdrSize + GrpcHeaderSize, payloadSize));
                }

                // Copy header+body across First+Second. The reservation's
                // First holds bytes up to the ring tail; Second holds the
                // wrap continuation at offset 0.
                var firstSpan = reservation.First.Span;
                var secondSpan = reservation.Second.Span;
                stage[..firstSpan.Length].CopyTo(firstSpan);
                stage[firstSpan.Length..].CopyTo(secondSpan);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(stageBuf);
            }
            _ring.CommitWrite(reservation, totalSize);
            return;
        }

        // Multi-frame or wrap-around: prepend 5-byte gRPC header, then
        // WriteTo(IBufferWriter) through RingFrameStream.
        using var rfs = new RingFrameStream(this, streamId, framePayloadSize, chunkSize, extraFlags, ct);
        // Write gRPC header as first 5 bytes
        Span<byte> grpcHeader = stackalloc byte[GrpcHeaderSize];
        grpcHeader[0] = 0; // no compression
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(grpcHeader.Slice(1), (uint)payloadSize);
        rfs.Write(grpcHeader);
        // Serialize protobuf directly into ring spans
        message.WriteTo((IBufferWriter<byte>)rfs);
        rfs.CommitFinalFrame();
    }

    /// <summary>
    /// A write-only <see cref="Stream"/> that feeds <see cref="CodedOutputStream"/>
    /// data directly into the ring buffer, splitting across frame boundaries
    /// automatically. Each frame is reserved, header-stamped, and committed
    /// independently so the reader can pipeline consumption.
    /// </summary>
    private sealed class RingFrameStream : Stream, IBufferWriter<byte>
    {
        private readonly ShmFrameWriter _owner;
        private readonly ShmRing _ring;
        private readonly uint _streamId;
        private readonly int _maxFramePayload;
        private readonly byte _extraFlags;
        private readonly int _wireHeaderSize;
        private readonly CancellationToken _ct;
        private int _remainingPayload;     // total protobuf bytes left to write
        private int _currentFrameCapacity; // payload capacity of current frame
        private int _currentFrameWritten;  // bytes written into current frame payload
        private WriteReservation _currentReservation;
        private bool _reservationActive;

        public RingFrameStream(ShmFrameWriter owner, uint streamId, int totalPayload,
            int maxFramePayload, byte extraFlags, CancellationToken ct)
        {
            _owner = owner;
            _ring = owner._ring;
            _streamId = streamId;
            _maxFramePayload = maxFramePayload;
            _extraFlags = extraFlags;
            _wireHeaderSize = owner.WireHeaderSize;
            _ct = ct;
            _remainingPayload = totalPayload;
            ReserveNextFrame();
        }

        private void ReserveNextFrame()
        {
            var chunkPayload = Math.Min(_maxFramePayload, _remainingPayload);
            var totalSize = _wireHeaderSize + chunkPayload;
            _currentReservation = _ring.ReserveWrite(totalSize, _ct);
            _currentFrameCapacity = chunkPayload;
            _currentFrameWritten = 0;
            _reservationActive = true;

            // Wire-format-aware frame header.
            var isLastFrame = (chunkPayload >= _remainingPayload);
            var isLast = isLastFrame && (_extraFlags & MessageFlags.More) == 0;
            byte flags;
            if (isLast)
                flags = _extraFlags;
            else
                flags = (byte)(MessageFlags.More | _extraFlags);

            // Stack-allocate up to 16 B (Custom16); H2 only needs 9.
            Span<byte> headerBytes = stackalloc byte[16];
            headerBytes = headerBytes[.._wireHeaderSize];
            _owner.EncodeMessageWireHeader(headerBytes, _streamId, chunkPayload, flags);

            WriteToReservation(_currentReservation, 0, headerBytes);
        }

        /// <summary>Commits the current frame after all payload bytes have been written.</summary>
        internal void CommitFinalFrame()
        {
            if (_reservationActive)
            {
                var totalSize = _wireHeaderSize + _currentFrameWritten;
                _ring.CommitWrite(_currentReservation, totalSize);
                _reservationActive = false;
            }
        }

        // IBufferWriter<byte>: protobuf WriteTo(IBufferWriter) calls GetSpan →
        // writes directly into ring span → Advance. No COS, no intermediate
        // buffer. Frame boundaries are handled automatically in Advance.

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            var spaceInFrame = _currentFrameCapacity - _currentFrameWritten;
            if (spaceInFrame <= 0)
            {
                var totalSize = _wireHeaderSize + _currentFrameWritten;
                _ring.CommitWrite(_currentReservation, totalSize);
                _remainingPayload -= _currentFrameWritten;
                _reservationActive = false;
                ReserveNextFrame();
                spaceInFrame = _currentFrameCapacity;
            }

            var writeOffset = _wireHeaderSize + _currentFrameWritten;
            // Return contiguous span within current reservation's First slice.
            // If reservation wraps (Second non-empty), limit to First's remaining.
            var firstLen = _currentReservation.First.Length;
            if (writeOffset < firstLen)
            {
                var available = Math.Min(spaceInFrame, firstLen - writeOffset);
                return _currentReservation.First.Span.Slice(writeOffset, available);
            }
            else
            {
                var secondOffset = writeOffset - firstLen;
                var available = Math.Min(spaceInFrame, _currentReservation.Second.Length - secondOffset);
                return _currentReservation.Second.Span.Slice(secondOffset, available);
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var spaceInFrame = _currentFrameCapacity - _currentFrameWritten;
            if (spaceInFrame <= 0)
            {
                var totalSize = _wireHeaderSize + _currentFrameWritten;
                _ring.CommitWrite(_currentReservation, totalSize);
                _remainingPayload -= _currentFrameWritten;
                _reservationActive = false;
                ReserveNextFrame();
                spaceInFrame = _currentFrameCapacity;
            }

            var writeOffset = _wireHeaderSize + _currentFrameWritten;
            var firstLen = _currentReservation.First.Length;
            if (writeOffset < firstLen)
            {
                var available = Math.Min(spaceInFrame, firstLen - writeOffset);
                return _currentReservation.First.Slice(writeOffset, available);
            }
            else
            {
                var secondOffset = writeOffset - firstLen;
                var available = Math.Min(spaceInFrame, _currentReservation.Second.Length - secondOffset);
                return _currentReservation.Second.Slice(secondOffset, available);
            }
        }

        public void Advance(int count)
        {
            if ((uint)count > (uint)(_currentFrameCapacity - _currentFrameWritten))
                throw new ArgumentOutOfRangeException(nameof(count));
            _currentFrameWritten += count;
        }

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                var spaceInFrame = _currentFrameCapacity - _currentFrameWritten;
                if (spaceInFrame <= 0)
                {
                    // Current frame full — commit and reserve next.
                    // Do NOT use BeginBatchWrite here: the next ReserveWrite
                    // may WaitForSpace, which needs the reader to consume
                    // data. If the OS signal is deferred, the reader may be
                    // blocked in a kernel wait and never see the data →
                    // deadlock. Each per-frame signal costs ~10µs (futex
                    // wake), negligible for multi-frame messages (≥16MB).
                    var totalSize = _wireHeaderSize + _currentFrameWritten;
                    _ring.CommitWrite(_currentReservation, totalSize);
                    _remainingPayload -= _currentFrameWritten;
                    _reservationActive = false;
                    ReserveNextFrame();
                    spaceInFrame = _currentFrameCapacity;
                }

                var toCopy = Math.Min(buffer.Length, spaceInFrame);
                var writeOffset = _wireHeaderSize + _currentFrameWritten;
                WriteToReservation(_currentReservation, writeOffset, buffer[..toCopy]);
                _currentFrameWritten += toCopy;
                buffer = buffer[toCopy..];
            }
        }

        /// <summary>
        /// Writes data into a reservation at a given byte offset, handling
        /// the First/Second wrap-around split.
        /// </summary>
        private static void WriteToReservation(WriteReservation reservation, int offset, ReadOnlySpan<byte> data)
        {
            var firstLen = reservation.First.Length;
            if (offset < firstLen)
            {
                var available = firstLen - offset;
                if (data.Length <= available)
                {
                    data.CopyTo(reservation.First.Span.Slice(offset));
                }
                else
                {
                    data[..available].CopyTo(reservation.First.Span.Slice(offset));
                    data[available..].CopyTo(reservation.Second.Span);
                }
            }
            else
            {
                var secondOffset = offset - firstLen;
                data.CopyTo(reservation.Second.Span.Slice(secondOffset));
            }
        }

        protected override void Dispose(bool disposing)
        {
            // If an exception interrupted WriteTo/Flush, we have an
            // uncommitted reservation with a header stamped for the
            // full planned payload but only partial data written.
            // We must NOT commit it as-is: the reader reads by header
            // length, so it would block waiting for bytes that will
            // never arrive, or interpret following data as this frame's
            // tail — corrupting the connection.
            //
            // Instead, rewrite the header with the actual bytes written
            // and commit only that. The frame contains truncated protobuf
            // (will fail deserialization), but the ring stays consistent
            // and the reader can skip/error the frame cleanly.
            if (disposing && _reservationActive)
            {
                try
                {
                    // Rewrite header with actual payload length, in the
                    // wire format active on this ring.
                    Span<byte> headerBytes = stackalloc byte[16];
                    headerBytes = headerBytes[.._wireHeaderSize];
                    _owner.EncodeMessageWireHeader(headerBytes, _streamId, _currentFrameWritten, _extraFlags);
                    WriteToReservation(_currentReservation, 0, headerBytes);

                    var written = _wireHeaderSize + _currentFrameWritten;
                    _ring.CommitWrite(_currentReservation, written);
                }
                catch { /* ring may be closed */ }
                _reservationActive = false;
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // PR2 phase 1.4: no WriterLoop thread to join, no queues to drain,
        // no pooled signals to dispose. The CTS is owned by the caller
        // (ShmConnection) which disposes it after we return.
    }
}
