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

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Grpc.Net.SharedMemory.Wire;
internal static partial class Http2Codec
{
    internal const int MaxPendingSyntheticFrames = 4096;

    // Per-ring decoder state. Tracks which streams have had their initial
    // HEADERS frame so that subsequent HEADERS frames are interpreted as
    // gRPC trailers. Cleared when END_STREAM is observed.
    //
    // Keyed weakly by ring instance via a ConditionalWeakTable so that a
    // disposed ring's state is collected automatically.
    private static readonly ConditionalWeakTable<ShmRing, Http2DecoderState> s_decoderState
        = new();

    /// <summary>Per-ring decoder state used to distinguish HEADERS vs trailers.</summary>
    /// <remarks>
    /// All access goes through the per-ring frame-reader thread (see
    /// <c>ShmConnection.FrameReaderLoopAsync</c>), so the maps are
    /// plain <see cref="Dictionary{TKey,TValue}"/> rather than
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>: the per-frame
    /// CAS overhead inside ConcurrentDictionary's hashing is pure cost
    /// in this single-producer single-consumer use.
    /// <para>
    /// <c>LastStreamId</c> / <c>LastAcc</c> form a one-element MRU
    /// cache for single-stream-mode workloads (the dominant deployment
    /// shape): every DATA frame in single-stream-mode targets the same
    /// stream, so the cache hits 100% and the dictionary lookup is
    /// skipped entirely.
    /// </para>
    /// </remarks>
    private sealed class Http2DecoderState
    {
        public readonly Dictionary<uint, byte> StreamsWithInitialHeaders = new();
        public readonly Dictionary<uint, LpmAccumulator> LpmAccumulators = new();

        // MRU hot cache for the LPM accumulator. SingleStreamMode pins
        // streamId == 1 forever, so the dict lookup is bypassed for the
        // entire stream lifetime. Multi-stream workloads still hit the
        // dict but pay only per-stream-switch overhead.
        public uint LastStreamId;
        public LpmAccumulator? LastAcc;

        // Synthetic-frame queue: a single H2 wire frame can produce more
        // than one logical internal frame. Two scenarios both depend on
        // this:
        //
        //   1) Trailers-only HEADERS (gRFC G3): one HEADERS+END_STREAM
        //      surfaces as Headers + Trailers (see EmitDecodedHeaders).
        //
        //   2) DATA-frame coalescing: a peer-side optimisation (or a
        //      different gRPC implementation) may pack two or more
        //      complete gRPC LPM messages into one H2 DATA frame, which
        //      RFC 7540 §6.1 explicitly permits (DATA carries an opaque
        //      byte stream; LPM message boundaries are not aligned with
        //      H2 frame boundaries). The reader emits the first completed
        //      LPM and stashes the remaining ones here.
        //
        // The queue is drained at the head of every ReadFramePayloadInternal
        // call before the ring is touched, preserving FIFO order between
        // synthetic frames and any subsequent wire frames.
        public readonly Queue<(FrameHeader Header, FramePayload Payload)> PendingFrames = new();
    }

    private static void EnqueuePendingFrame(
        Http2DecoderState state, FrameHeader header, FramePayload payload)
    {
        if (state.PendingFrames.Count >= MaxPendingSyntheticFrames)
        {
            throw new InvalidDataException(
                $"H2 DATA frame produced more than {MaxPendingSyntheticFrames + 1} logical gRPC messages");
        }
        state.PendingFrames.Enqueue((header, payload));
    }

    private static void ReleasePendingFrames(Http2DecoderState state)
    {
        while (state.PendingFrames.Count > 0)
        {
            state.PendingFrames.Dequeue().Payload.Release();
        }
    }

    /// <summary>
    /// Tracks an in-progress gRPC LPM message across multiple H2 DATA
    /// frames. PR2 phase 2.5: the accumulator no longer holds the full
    /// LPM body in a single ArrayPool buffer; instead each DATA frame's
    /// body bytes are emitted as a separate MESSAGE chunk (with the
    /// <see cref="MessageFlags.More"/> flag set on all but the LPM's
    /// final chunk). Upstream readers (ReadSingleMessageAsync and
    /// ShmAsyncStreamReader, both lazy-chain enabled in 5dfb2db2 and
    /// c01f5b8e) consume the chain frame-by-frame, releasing each
    /// pool-backed chunk as the protobuf parser advances. Net pool
    /// footprint per H2 stream drops from O(LPM body size) to
    /// O(per-DATA-frame ~16 MiB ceiling).
    /// </summary>
    private sealed class LpmAccumulator
    {
        // 5-byte LPM header parsing state. Bytes copied here from the
        // wire as DATA frames stream in; once HeaderBytesSeen == 5 the
        // body length is known and chunk emission begins.
        public readonly byte[] HeaderBuf = new byte[5];
        public int HeaderBytesSeen;     // 0..5

        // Body emission state. ExpectedBodyLen is the LPM body length
        // (NOT including the 5-byte header) parsed from HeaderBuf[1..5].
        // BodyEmitted is the cumulative count of body bytes that have
        // been included in MESSAGE chunks emitted so far. The LPM is
        // complete when BodyEmitted == ExpectedBodyLen.
        public int ExpectedBodyLen;
        public int BodyEmitted;

        // True once the first chunk for this LPM has been emitted; that
        // chunk has the 5-byte HeaderBuf prepended so the upstream
        // reader's compFlag sniff (Memory.Span[0]) and LPM body length
        // sniff (Memory.Span.Slice(1,4) BE32) work unchanged.
        // Subsequent chunks contain raw body bytes only.
        public bool HeaderEmittedAsChunk;

        public void Reset()
        {
            HeaderBytesSeen = 0;
            ExpectedBodyLen = 0;
            BodyEmitted = 0;
            HeaderEmittedAsChunk = false;
        }
    }

    private static Http2DecoderState GetState(ShmRing ring)
    {
        return s_decoderState.GetValue(ring, _ => new Http2DecoderState());
    }

    // ===== LPM accumulator dict access helpers (single-threaded reader) =====
    //
    // Wrap the per-stream LpmAccumulator dict with a one-element MRU
    // cache: the dominant deployment shape (single-stream-mode) targets
    // exactly one streamId, so the dict lookup is bypassed entirely after
    // the first frame. Multi-stream workloads pay the dict lookup only
    // on a stream switch, which is the boundary where any decoder needs
    // some state load anyway.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetAcc(Http2DecoderState state, uint streamId, out LpmAccumulator? acc)
    {
        if (state.LastStreamId == streamId && state.LastAcc != null)
        {
            acc = state.LastAcc;
            return true;
        }
        if (state.LpmAccumulators.TryGetValue(streamId, out var found))
        {
            acc = found;
            state.LastStreamId = streamId;
            state.LastAcc = found;
            return true;
        }
        acc = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LpmAccumulator GetOrAddAcc(Http2DecoderState state, uint streamId)
    {
        if (state.LastStreamId == streamId && state.LastAcc != null)
        {
            return state.LastAcc;
        }
        if (!state.LpmAccumulators.TryGetValue(streamId, out var acc))
        {
            acc = new LpmAccumulator();
            state.LpmAccumulators[streamId] = acc;
        }
        state.LastStreamId = streamId;
        state.LastAcc = acc;
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RemoveAcc(Http2DecoderState state, uint streamId, out LpmAccumulator? acc)
    {
        if (state.LastStreamId == streamId)
        {
            state.LastStreamId = 0;
            state.LastAcc = null;
        }
        return state.LpmAccumulators.Remove(streamId, out acc);
    }

    private static (FrameHeader Header, FramePayload Payload) ReadFramePayloadInternal(
        ShmRing ring,
        CancellationToken cancellationToken,
        bool zeroCopy)
    {
        var state = GetState(ring);
        Span<byte> hb = stackalloc byte[Http2FrameHeader.Size];

        // Drain any synthetic frames the previous read split off (used for
        // trailers-only HEADERS, where one H2 frame surfaces as two
        // internal frames, and for DATA-frame coalescing, where one H2
        // DATA carries multiple complete LPM messages). Pulling the queue
        // before touching the ring keeps the FIFO invariant intact: the
        // stash entries we deferred last call must be observed by the
        // upper layer before any subsequent ring frame.
        if (state.PendingFrames.Count > 0)
        {
            return state.PendingFrames.Dequeue();
        }

        while (true)
        {
            // Reserve 9-byte H2 frame header (deferred commit).
            var headerReservation = ring.ReserveRead(Http2FrameHeader.Size, cancellationToken);
            var baseCommitReadIdx = headerReservation.CommitReadIdx;

            CopyFromReservation(headerReservation, hb);
            var (h2Type, h2Flags, payloadLen, streamId) = Http2FrameHeader.Decode(hb);

            if (payloadLen > MaxH2FramePayloadSize)
            {
                ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size);
                throw new InvalidDataException(
                    $"H2 frame payload length {payloadLen} exceeds maximum {MaxH2FramePayloadSize}");
            }

            switch (h2Type)
            {
                case Http2FrameType.Data:
                    {
                        var dataResult = TryReadDataFrame(ring, baseCommitReadIdx, streamId, h2Flags, payloadLen, zeroCopy, state, cancellationToken);
                        if (dataResult is { } completed)
                        {
                            return completed;
                        }
                        // Partial LPM message — keep reading.
                        continue;
                    }

                case Http2FrameType.Headers:
                    return ReadHeadersFrame(ring, baseCommitReadIdx, streamId, h2Flags, payloadLen, state, cancellationToken);

                case Http2FrameType.RstStream:
                    return ReadRstStreamFrame(ring, baseCommitReadIdx, streamId, payloadLen, cancellationToken);

                case Http2FrameType.Settings:
                    HandleSettingsFrame(ring, baseCommitReadIdx, h2Flags, streamId, payloadLen, cancellationToken);
                    continue; // Don't surface to upper layer.

                case Http2FrameType.Ping:
                    return ReadPingFrame(ring, baseCommitReadIdx, h2Flags, streamId, payloadLen, cancellationToken);

                case Http2FrameType.GoAway:
                    return ReadGoAwayFrame(ring, baseCommitReadIdx, streamId, payloadLen, cancellationToken);

                case Http2FrameType.WindowUpdate:
                    return ReadWindowUpdateFrame(ring, baseCommitReadIdx, streamId, payloadLen, cancellationToken);

                case Http2FrameType.Continuation:
                    // CONTINUATION is consumed inline by the HEADERS reader
                    // when it sees a frame without END_HEADERS. Reaching the
                    // dispatcher with a CONTINUATION means the peer emitted
                    // it OUT OF SEQUENCE — there was no preceding HEADERS
                    // (or the preceding HEADERS already had END_HEADERS).
                    // RFC 7540 §6.10: PROTOCOL_ERROR.
                    if (payloadLen > 0)
                    {
                        var _ = ring.ReserveRead((int)payloadLen, cancellationToken);
                    }
                    ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
                    throw new InvalidDataException(
                        "H2 CONTINUATION frame received outside a HEADERS sequence (RFC 7540 §6.10)");

                case Http2FrameType.Priority:
                    // Deprecated by RFC 9113; ignore.
                    if (payloadLen > 0)
                    {
                        var skipReservation = ring.ReserveRead((int)payloadLen, cancellationToken);
                        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
                    }
                    else
                    {
                        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size);
                    }
                    continue;

                default:
                    ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
                    throw new InvalidDataException($"Unknown H2 frame type 0x{(byte)h2Type:X}");
            }
        }
    }

    /// <summary>
    /// Reads an H2 DATA frame and either returns a complete internal MESSAGE
    /// (when the LPM accumulator finishes a logical app message) or returns
    /// <c>null</c> to signal the outer loop to keep reading.
    /// </summary>
    /// <remarks>
    /// gRPC over HTTP/2 transmits each app message as a 5-byte LPM header
    /// (compression flag + body length, big-endian) followed by body bytes,
    /// and DATA frames are a byte-stream window into that LPM stream.
    /// One DATA frame may carry a fragment of a single message, multiple
    /// complete messages, or a mix; the receiver must reassemble.
    /// <para>
    /// Fast path: when the entire DATA payload contains exactly one complete
    /// LPM message and there is no in-flight accumulator state, we surface
    /// the body directly as the internal MESSAGE payload — preserving the
    /// speculative zero-copy capability of the underlying ring.
    /// </para>
    /// </remarks>
    private static (FrameHeader Header, FramePayload Payload)? TryReadDataFrame(
        ShmRing ring, ulong baseCommitReadIdx, uint streamId, byte h2Flags, int payloadLen,
        bool zeroCopy, Http2DecoderState state, CancellationToken ct)
    {
        var endStream = (h2Flags & Http2Flags.EndStream) != 0;
        var padded = (h2Flags & Http2Flags.Padded) != 0;

        if (payloadLen == 0)
        {
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size);
            // Empty DATA: only meaningful when END_STREAM is set (closes stream).
            // No body to feed to the LPM accumulator.
            if (endStream)
            {
                state.StreamsWithInitialHeaders.Remove(streamId);
                if (RemoveAcc(state, streamId, out var stale))
                {
                    stale!.Reset(); // free pooled buffer if any
                }
                return (new FrameHeader(FrameType.HalfClose, streamId, 0, 0), FramePayload.Empty);
            }
            // No-op DATA frame, signal outer loop to read next.
            return null;
        }

        var payloadReservation = ring.ReserveRead(payloadLen, ct);

        // Honour H2 PADDED: first byte is pad-length, last <pad-length> bytes
        // are padding. The body is the slice in between.
        var bodyOffset = 0;
        var bodyLength = payloadLen;
        if (padded)
        {
            byte padLenByte = payloadReservation.First.Length > 0
                ? payloadReservation.First.Span[0]
                : payloadReservation.Second.Span[0];
            bodyOffset = 1;
            bodyLength = payloadLen - 1 - padLenByte;
            if (bodyLength < 0)
            {
                ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
                throw new InvalidDataException("H2 DATA pad length exceeds payload");
            }
        }

        var totalBytes = Http2FrameHeader.Size + payloadLen;

        // === Fast path: single complete LPM message in this DATA frame, ===
        // === no accumulator state, contiguous body. Eligible for zero-copy. ===
        // PR2 phase 2.5: "hasAccumulator" means the accumulator has
        // either started parsing the LPM header (HeaderBytesSeen > 0)
        // or has begun emitting chunks for the current LPM
        // (HeaderEmittedAsChunk == true). The fast path is only safe
        // when no LPM is in progress; if the accumulator already has
        // partial state we MUST take the slow path so the new bytes
        // continue the in-progress LPM.
        var hasAccumulator = TryGetAcc(state, streamId, out var existingAcc)
            && (existingAcc!.HeaderBytesSeen > 0 || existingAcc.HeaderEmittedAsChunk);
        if (!hasAccumulator && bodyLength >= 5 && payloadReservation.Second.IsEmpty)
        {
            var bodySpan = payloadReservation.First.Span.Slice(bodyOffset, bodyLength);
            var declaredLpmBody = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bodySpan.Slice(1, 4));
            // declaredLpmBody as uint cannot overflow int when added to 5 because
            // bodyLength is bounded by MaxH2FramePayloadSize (~16 MiB) and we only
            // take the fast path when the totals match exactly.
            if (declaredLpmBody <= (uint)int.MaxValue - 5
                && (int)declaredLpmBody + 5 == bodyLength)
            {
                // Exactly one complete LPM message — surface directly.
                byte msgFlags = endStream ? MessageFlags.EndStream : (byte)0;
                var hdr = new FrameHeader(FrameType.Message, streamId, (uint)bodyLength, msgFlags);
                if (endStream)
                {
                    state.StreamsWithInitialHeaders.Remove(streamId);
                }

                if (zeroCopy && bodyOffset == 0
                    && ring.IsSpeculativeZcEligible(bodyLength, contiguous: payloadReservation.Second.IsEmpty))
                {
                    // Fused single-frame ZC: see FrameProtocol hot-path
                    // comment. H2 reader only does single-frame ZC (multi-
                    // frame H2 messages always go through the LpmAccumulator
                    // copy path), so the fused commit is always safe here.
                    ring.BeginSingleFrameZcCommit(baseCommitReadIdx, totalBytes);
                    Interlocked.Add(ref ring.SpeculativeReservedBytes, totalBytes);
                    return (hdr, FramePayload.FromRingMemorySpeculative(
                        payloadReservation.First.Slice(0, bodyLength), ring, totalBytes));
                }

                var pooled = ArrayPool<byte>.Shared.Rent(bodyLength);
                bodySpan.CopyTo(pooled);
                ring.CommitReadRaw(baseCommitReadIdx, totalBytes);
                return (hdr, FramePayload.FromPooled(pooled, bodyLength));
            }
            // Falls through to slow path: more bytes needed, or multiple LPMs in this frame.
        }

        // === Slow path: copy body into the per-stream LPM accumulator. ===
        // Materialise body to a contiguous buffer.
        byte[]? bodyHeap = null;
        ReadOnlySpan<byte> bodyBytes;
        if (payloadReservation.Second.IsEmpty)
        {
            bodyBytes = payloadReservation.First.Span.Slice(bodyOffset, bodyLength);
        }
        else
        {
            bodyHeap = ArrayPool<byte>.Shared.Rent(bodyLength == 0 ? 1 : bodyLength);
            CopyFromReservationSlice(payloadReservation, bodyOffset, bodyHeap.AsSpan(0, bodyLength));
            bodyBytes = bodyHeap.AsSpan(0, bodyLength);
        }

        try
        {
            // Feed body into the per-stream accumulator. RFC 7540 §6.1
            // permits an H2 DATA frame to carry an arbitrary slice of the
            // stream's byte sequence: that slice MAY contain a partial
            // LPM, exactly one complete LPM, or multiple complete LPMs
            // back-to-back (writer-side coalescing — common when peers
            // batch small messages, and explicitly allowed by gRFC G3).
            //
            // PR2 phase 2.5: chunk-emit semantics. Each call to
            // <see cref="FeedAccumulator"/> emits AT MOST one MESSAGE
            // chunk covering whatever body bytes were consumed in this
            // call. Chunks are flagged via:
            //
            //   - <see cref="MessageFlags.More"/>: set on chunks that do
            //     NOT complete an LPM (mid-LPM continuation). Cleared on
            //     chunks that DO complete an LPM. Upstream
            //     <see cref="ReadSingleMessageAsync"/> /
            //     <see cref="ShmAsyncStreamReader"/> use the More flag
            //     to drive the lazy-chain pull loop.
            //
            //   - <see cref="MessageFlags.EndStream"/>: set on the LAST
            //     chunk emitted by this DATA frame, IF the H2 frame's
            //     END_STREAM flag is set. Same buffered-tail pattern as
            //     the legacy single-emit code.
            //
            // EndStream semantics: the H2 frame's END_STREAM flag applies
            // logically to whichever chunk is the LAST one this DATA
            // frame produces. To stamp it correctly without patching a
            // queued entry after the fact, we hold the most recent
            // post-first emission in <c>bufferedTail</c> and enqueue it
            // only when another emission overtakes it.
            //
            // Allocation profile is one ArrayPool.Rent per emitted chunk.
            // For the common single-LPM-per-DATA path that's still one
            // pool buffer per DATA frame (matches the legacy path's one
            // big buffer per LPM, just split). For multi-DATA single-LPM
            // it's now N small buffers vs the legacy 1 large buffer —
            // but the upstream lazy-chain consumer releases each as the
            // parser advances, so total in-flight pool footprint drops
            // from O(LPM size) to ~2 chunks.
            var acc = GetOrAddAcc(state, streamId);
            FramePayload? firstEmitted = null;
            bool firstEmittedLpmComplete = false;
            FramePayload? bufferedTail = null;
            bool bufferedTailLpmComplete = false;
            var remaining = bodyBytes;
            var ringCommitted = false;

            try
            {
                while (remaining.Length > 0)
                {
                    var (chunk, consumed, lpmComplete) = FeedAccumulator(acc, remaining);
                    if (consumed == 0)
                    {
                        // Defensive: FeedAccumulator made no progress on a
                        // non-empty input. Should not happen given the logic
                        // above (Phase 1/2 always consumes at least one byte
                        // when src is non-empty), but break to avoid infinite
                        // loop in case of future regressions.
                        break;
                    }
                    remaining = remaining.Slice(consumed);

                    if (chunk is { } payload)
                    {
                        if (firstEmitted == null)
                        {
                            firstEmitted = payload;
                            firstEmittedLpmComplete = lpmComplete;
                        }
                        else if (bufferedTail == null)
                        {
                            bufferedTail = payload;
                            bufferedTailLpmComplete = lpmComplete;
                        }
                        else
                        {
                            // bufferedTail is no longer the terminal chunk —
                            // a newer chunk has arrived. Flush the old tail
                            // to the queue with the appropriate More flag
                            // (no EndStream; that flag belongs to whoever
                            // ends up final).
                            byte tailFlagsMid = (byte)(bufferedTailLpmComplete ? 0 : MessageFlags.More);
                            try
                            {
                                EnqueuePendingFrame(state,
                                    new FrameHeader(FrameType.Message, streamId,
                                        (uint)bufferedTail.Value.Length, tailFlagsMid),
                                    bufferedTail.Value);
                            }
                            catch
                            {
                                payload.Release();
                                throw;
                            }
                            bufferedTail = payload;
                            bufferedTailLpmComplete = lpmComplete;
                        }
                    }
                }

                if (bufferedTail != null && state.PendingFrames.Count >= MaxPendingSyntheticFrames)
                {
                    throw new InvalidDataException(
                        $"H2 DATA frame produced more than {MaxPendingSyntheticFrames + 1} logical gRPC messages");
                }

                // Commit the ring read regardless — we've materialised everything we need.
                ring.CommitReadRaw(baseCommitReadIdx, totalBytes);
                ringCommitted = true;
            }
            finally
            {
                if (!ringCommitted)
                {
                    // FeedAccumulator (or any other inner step) threw. We
                    // already advanced <c>_pendingReadIdx</c> by
                    // <c>payloadLen</c> via the <see cref="ShmRing.ReserveRead"/>
                    // call above, but never published the matching
                    // <see cref="ShmRing.CommitReadRaw"/> on the shared
                    // <c>header.ReadIdx</c>. Defense in depth: keep the
                    // two indices in sync at all exit points.
                    try { ring.CommitReadRaw(baseCommitReadIdx, totalBytes); }
                    catch { /* swallow during exception unwind */ }

                    firstEmitted?.Release();
                    bufferedTail?.Release();
                    ReleasePendingFrames(state);
                }
            }

            if (firstEmitted is { } first)
            {
                // Stamp EndStream on the LAST emitted chunk when the wire
                // frame's H2 END_STREAM was set. EndStream is only valid
                // when the LAST chunk also completes its LPM (otherwise
                // we'd be claiming the stream ends mid-LPM, which is a
                // protocol error caught below).
                if (bufferedTail is { } tail)
                {
                    // Two or more emissions: <c>first</c> goes back as
                    // the call's first MESSAGE, <c>tail</c> rides the
                    // EndStream flag (if applicable) and the More flag
                    // for its own LPM-completeness state.
                    byte tailFlags = (byte)(bufferedTailLpmComplete ? 0 : MessageFlags.More);
                    if (endStream && bufferedTailLpmComplete)
                    {
                        tailFlags |= MessageFlags.EndStream;
                    }
                    EnqueuePendingFrame(state,
                        new FrameHeader(FrameType.Message, streamId,
                            (uint)tail.Length, tailFlags),
                        tail);
                    byte firstFlags = (byte)(firstEmittedLpmComplete ? 0 : MessageFlags.More);
                    var firstHdr = new FrameHeader(FrameType.Message, streamId,
                        (uint)first.Length, firstFlags);

                    if (endStream)
                    {
                        if (!bufferedTailLpmComplete)
                        {
                            // H2 END_STREAM with the final emitted chunk
                            // still mid-LPM: protocol error.
                            throw new InvalidDataException(
                                $"H2 stream {streamId} ended mid-LPM (final chunk has More flag)");
                        }
                        state.StreamsWithInitialHeaders.Remove(streamId);
                        if (RemoveAcc(state, streamId, out var doneAcc))
                        {
                            doneAcc!.Reset();
                        }
                    }
                    return (firstHdr, first);
                }

                // Single emission: stamp EndStream and More directly.
                byte msgFlags = (byte)(firstEmittedLpmComplete ? 0 : MessageFlags.More);
                if (endStream && firstEmittedLpmComplete)
                {
                    msgFlags |= MessageFlags.EndStream;
                }
                var hdr = new FrameHeader(FrameType.Message, streamId,
                    (uint)first.Length, msgFlags);
                if (endStream)
                {
                    if (!firstEmittedLpmComplete)
                    {
                        throw new InvalidDataException(
                            $"H2 stream {streamId} ended mid-LPM (final chunk has More flag)");
                    }
                    state.StreamsWithInitialHeaders.Remove(streamId);
                    if (RemoveAcc(state, streamId, out var doneAcc))
                    {
                        doneAcc!.Reset();
                    }
                }
                return (hdr, first);
            }

            // No complete message yet. If END_STREAM was set without finishing
            // the LPM, that's a protocol error.
            if (endStream)
            {
                if (RemoveAcc(state, streamId, out var orphan))
                {
                    orphan!.Reset();
                }
                state.StreamsWithInitialHeaders.Remove(streamId);
                throw new InvalidDataException(
                    $"H2 stream {streamId} ended mid-LPM (header bytes seen={acc.HeaderBytesSeen}, body emitted={acc.BodyEmitted}, expected body={acc.ExpectedBodyLen})");
            }
            return null; // outer loop will read next frame
        }
        finally
        {
            if (bodyHeap != null)
            {
                ArrayPool<byte>.Shared.Return(bodyHeap);
            }
        }
    }

    /// <summary>
    /// Consumes some bytes from <paramref name="body"/> and returns a
    /// <see cref="FramePayload"/> chunk for the upstream reader if any
    /// non-header bytes were consumed. The chunk represents whatever
    /// body bytes <em>this</em> call covered; the LPM may or may not
    /// be complete — that is signalled separately via
    /// <c>LpmComplete</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One call advances the accumulator by AT MOST one LPM. The caller's
    /// outer loop (<see cref="TryReadDataFrame"/>'s slow path) re-invokes
    /// with the residual span if <c>LpmComplete</c> is true and there
    /// are bytes left in the DATA frame; that next invocation begins
    /// parsing the next LPM's header.
    /// </para>
    /// <para>
    /// PR2 phase 2.5: this is the chunk-emit replacement for the legacy
    /// "accumulate-into-Buffer-then-emit-once" pattern. Each call's body
    /// bytes (whatever fits up to the LPM's remaining body length)
    /// becomes its own pooled chunk. The first chunk for a given LPM
    /// has the 5-byte LPM header prepended (so upstream reader's
    /// <c>compFlag</c> sniff still works); subsequent chunks contain
    /// raw body bytes only.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <c>Completed</c>: a <see cref="FramePayload"/> chunk to surface
    /// upstream (or <c>null</c> if only header bytes were consumed and
    /// no body chunk was emitted). <c>Consumed</c>: bytes consumed from
    /// <paramref name="body"/> (header + body). <c>LpmComplete</c>:
    /// true iff the chunk emitted finishes this LPM (so the caller
    /// should reset accumulator state for the next LPM and propagate
    /// MORE=0 to the chunk's frame flags).
    /// </returns>
    private static (FramePayload? Completed, int Consumed, bool LpmComplete) FeedAccumulator(
        LpmAccumulator acc, ReadOnlySpan<byte> body)
    {
        var src = body;
        var consumed = 0;

        // Phase 1: complete the 5-byte LPM header if necessary. Header
        // bytes are NEVER counted toward chunk emission — the header is
        // prepended to the FIRST body chunk only.
        if (acc.HeaderBytesSeen < 5)
        {
            var need = 5 - acc.HeaderBytesSeen;
            var take = Math.Min(need, src.Length);
            src.Slice(0, take).CopyTo(acc.HeaderBuf.AsSpan(acc.HeaderBytesSeen));
            acc.HeaderBytesSeen += take;
            src = src.Slice(take);
            consumed += take;

            if (acc.HeaderBytesSeen < 5)
            {
                return (null, consumed, false); // header still partial
            }

            var bodyLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                acc.HeaderBuf.AsSpan(1, 4));
            if (bodyLen < 0)
            {
                throw new InvalidDataException("Invalid gRPC LPM body length (negative)");
            }
            // Defend against a peer declaring a giant body length and
            // forcing the receiver to allocate hundreds of megabytes from
            // ArrayPool. The cap mirrors what gRPC implementations use as
            // the default per-message receive ceiling.
            if (bodyLen > MaxLpmBodyLength)
            {
                throw new InvalidDataException(
                    $"gRPC LPM body length {bodyLen} exceeds receiver maximum {MaxLpmBodyLength}");
            }
            acc.ExpectedBodyLen = bodyLen;
            acc.BodyEmitted = 0;
            acc.HeaderEmittedAsChunk = false;
            // Fall through: maybe src has body bytes already (DATA frame
            // contained both the header and at least some body).
        }

        // At this point HeaderBytesSeen == 5 (header is fully parsed).
        // Phase 2: emit a chunk covering as many of THIS LPM's body
        // bytes as fit in src. Stop at the LPM's body boundary; any
        // residue belongs to the next LPM and is surfaced via the
        // returned `Consumed` count.
        var bodyRemaining = acc.ExpectedBodyLen - acc.BodyEmitted;
        if (bodyRemaining == 0 && acc.ExpectedBodyLen == 0 && !acc.HeaderEmittedAsChunk)
        {
            // Empty-body LPM (compFlag + 0-byte body). Emit a 5-byte
            // chunk containing just the LPM header so upstream sees a
            // well-formed (but empty-body) MESSAGE. Reset per-LPM state
            // so the next call begins a fresh header parse.
            var emptyChunk = ArrayPool<byte>.Shared.Rent(5);
            acc.HeaderBuf.AsSpan(0, 5).CopyTo(emptyChunk);
            acc.HeaderBytesSeen = 0;
            acc.ExpectedBodyLen = 0;
            acc.BodyEmitted = 0;
            acc.HeaderEmittedAsChunk = false;
            return (FramePayload.FromPooled(emptyChunk, 5), consumed, true);
        }

        if (bodyRemaining == 0)
        {
            // No body left for this LPM AND it was already chunk-emitted.
            // Caller should have caught LpmComplete=true on the previous
            // call. This path is unreachable in practice but guarded.
            return (null, consumed, false);
        }

        if (src.Length == 0)
        {
            // Header parsed, but no body bytes available in this DATA
            // frame yet. Wait for the next DATA frame.
            return (null, consumed, false);
        }

        var chunkBodyLen = Math.Min(bodyRemaining, src.Length);
        var chunkPayloadLen = acc.HeaderEmittedAsChunk ? chunkBodyLen : 5 + chunkBodyLen;
        var chunk = ArrayPool<byte>.Shared.Rent(chunkPayloadLen);
        var chunkOffset = 0;
        if (!acc.HeaderEmittedAsChunk)
        {
            acc.HeaderBuf.AsSpan(0, 5).CopyTo(chunk);
            chunkOffset = 5;
            acc.HeaderEmittedAsChunk = true;
        }
        src.Slice(0, chunkBodyLen).CopyTo(chunk.AsSpan(chunkOffset, chunkBodyLen));
        acc.BodyEmitted += chunkBodyLen;
        consumed += chunkBodyLen;

        var lpmComplete = acc.BodyEmitted == acc.ExpectedBodyLen;
        if (lpmComplete)
        {
            // LPM done — reset per-LPM state so the next call starts a
            // fresh header parse. (HeaderBuf is reusable and stays as-is.)
            acc.HeaderBytesSeen = 0;
            acc.ExpectedBodyLen = 0;
            acc.BodyEmitted = 0;
            acc.HeaderEmittedAsChunk = false;
        }
        return (FramePayload.FromPooled(chunk, chunkPayloadLen), consumed, lpmComplete);
    }

    private static (FrameHeader Header, FramePayload Payload) ReadHeadersFrame(
        ShmRing ring, ulong baseCommitReadIdx, uint streamId, byte h2Flags, int payloadLen,
        Http2DecoderState state, CancellationToken ct)
    {
        var endStream = (h2Flags & Http2Flags.EndStream) != 0;
        var endHeaders = (h2Flags & Http2Flags.EndHeaders) != 0;
        var padded = (h2Flags & Http2Flags.Padded) != 0;
        var hasPriority = (h2Flags & Http2Flags.Priority) != 0;

        // Empty HEADERS w/ END_HEADERS: trivial path.
        if (endHeaders && payloadLen == 0)
        {
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size);
            return EmitDecodedHeaders(ReadOnlySpan<byte>.Empty, streamId, state, endStream);
        }

        // Materialise the first fragment, accounting for PADDED + PRIORITY
        // prefixes per RFC 7540 §6.2 (these flags appear ONLY on the first
        // HEADERS frame; CONTINUATION carries no flags except END_HEADERS).
        var firstFragment = ArrayPool<byte>.Shared.Rent(payloadLen == 0 ? 1 : payloadLen);
        int firstHeaderBlockOffset;
        int firstHeaderBlockLength;
        try
        {
            if (payloadLen > 0)
            {
                var payloadReservation = ring.ReserveRead(payloadLen, ct);
                CopyFromReservationSlice(payloadReservation, 0, firstFragment.AsSpan(0, payloadLen));
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

            firstHeaderBlockOffset = 0;
            firstHeaderBlockLength = payloadLen;
            if (padded)
            {
                if (payloadLen < 1)
                {
                    throw new InvalidDataException("HEADERS PADDED flag with empty payload");
                }
                int padLen = firstFragment[0];
                firstHeaderBlockOffset += 1;
                firstHeaderBlockLength = payloadLen - 1 - padLen;
            }
            if (hasPriority)
            {
                firstHeaderBlockOffset += 5;
                firstHeaderBlockLength -= 5;
            }
            if (firstHeaderBlockLength < 0)
            {
                throw new InvalidDataException("HEADERS frame: invalid pad/priority prefix");
            }

            // Fast path: single HEADERS w/ END_HEADERS (the dominant case
            // for gRPC traffic — SETTINGS_MAX_FRAME_SIZE = 16 MiB makes
            // CONTINUATION almost never necessary).
            if (endHeaders)
            {
                var single = firstFragment.AsSpan(firstHeaderBlockOffset, firstHeaderBlockLength);
                return EmitDecodedHeaders(single, streamId, state, endStream);
            }

            // Slow path: HEADERS without END_HEADERS — reassemble the
            // header block by reading CONTINUATION frames per RFC 7540
            // §6.10. CONTINUATION constraints we enforce:
            //   - frame type MUST be Continuation (PROTOCOL_ERROR otherwise)
            //   - streamId MUST match the originating HEADERS stream
            //   - cumulative payload bounded by MaxHeaderListSize
            //   - any non-CONTINUATION frame from the peer mid-sequence
            //     is a PROTOCOL_ERROR (peer cannot interleave other
            //     frames between HEADERS and the terminal CONTINUATION)
            return ReadHeadersWithContinuations(
                ring, firstFragment, firstHeaderBlockOffset, firstHeaderBlockLength,
                streamId, endStream, state, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(firstFragment);
        }
    }

    private static (FrameHeader Header, FramePayload Payload) ReadHeadersWithContinuations(
        ShmRing ring, byte[] firstFragment, int firstOff, int firstLen,
        uint streamId, bool endStream, Http2DecoderState state, CancellationToken ct)
    {
        // Accumulate the HPACK header block into a contiguous pooled
        // buffer. Start it at 4× the first fragment's size to avoid
        // growth in the typical 2-3 frame case; cap at MaxHeaderListSize.
        var initialCap = Math.Max(firstLen * 4, 1024);
        if (initialCap > MaxHeaderListSize) initialCap = MaxHeaderListSize;
        var assembled = ArrayPool<byte>.Shared.Rent(initialCap);
        int assembledLen = 0;
        try
        {
            firstFragment.AsSpan(firstOff, firstLen).CopyTo(assembled.AsSpan(0, firstLen));
            assembledLen = firstLen;

            Span<byte> hb = stackalloc byte[Http2FrameHeader.Size];
            while (true)
            {
                var headerReservation = ring.ReserveRead(Http2FrameHeader.Size, ct);
                var contBaseIdx = headerReservation.CommitReadIdx;
                CopyFromReservation(headerReservation, hb);
                var (contType, contFlags, contPayloadLen, contStreamId) = Http2FrameHeader.Decode(hb);

                if (contPayloadLen > MaxH2FramePayloadSize)
                {
                    ring.CommitReadRaw(contBaseIdx, Http2FrameHeader.Size);
                    throw new InvalidDataException(
                        $"H2 frame payload length {contPayloadLen} exceeds maximum {MaxH2FramePayloadSize}");
                }
                if (contType != Http2FrameType.Continuation)
                {
                    // PROTOCOL_ERROR: peer cannot interleave non-CONTINUATION
                    // frames between HEADERS and the terminal CONTINUATION.
                    if (contPayloadLen > 0)
                    {
                        var _ = ring.ReserveRead(contPayloadLen, ct);
                    }
                    ring.CommitReadRaw(contBaseIdx, Http2FrameHeader.Size + contPayloadLen);
                    throw new InvalidDataException(
                        $"H2 expected CONTINUATION (type=9) for stream {streamId}, got type=0x{(byte)contType:X2}");
                }
                if (contStreamId != streamId)
                {
                    if (contPayloadLen > 0)
                    {
                        var _ = ring.ReserveRead(contPayloadLen, ct);
                    }
                    ring.CommitReadRaw(contBaseIdx, Http2FrameHeader.Size + contPayloadLen);
                    throw new InvalidDataException(
                        $"H2 CONTINUATION streamId mismatch (expected {streamId}, got {contStreamId})");
                }

                // Cumulative header-list size check BEFORE materialising.
                if (assembledLen + contPayloadLen > MaxHeaderListSize)
                {
                    if (contPayloadLen > 0)
                    {
                        var _ = ring.ReserveRead(contPayloadLen, ct);
                    }
                    ring.CommitReadRaw(contBaseIdx, Http2FrameHeader.Size + contPayloadLen);
                    throw new InvalidDataException(
                        $"H2 HEADERS+CONTINUATION cumulative payload exceeds {MaxHeaderListSize} bytes");
                }

                // Grow the assembled buffer if needed.
                if (assembledLen + contPayloadLen > assembled.Length)
                {
                    var newSize = Math.Max(assembled.Length * 2, assembledLen + contPayloadLen);
                    if (newSize > MaxHeaderListSize) newSize = MaxHeaderListSize;
                    var bigger = ArrayPool<byte>.Shared.Rent(newSize);
                    assembled.AsSpan(0, assembledLen).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(assembled);
                    assembled = bigger;
                }

                if (contPayloadLen > 0)
                {
                    var contReservation = ring.ReserveRead(contPayloadLen, ct);
                    CopyFromReservationSlice(
                        contReservation, 0,
                        assembled.AsSpan(assembledLen, contPayloadLen));
                }
                ring.CommitReadRaw(contBaseIdx, Http2FrameHeader.Size + contPayloadLen);
                assembledLen += contPayloadLen;

                if ((contFlags & Http2Flags.EndHeaders) != 0)
                {
                    return EmitDecodedHeaders(
                        assembled.AsSpan(0, assembledLen), streamId, state, endStream);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(assembled);
        }
    }

    private static (FrameHeader Header, FramePayload Payload) EmitDecodedHeaders(
        ReadOnlySpan<byte> headerBlock, uint streamId, Http2DecoderState state, bool endStream)
    {
        var hasInitial = state.StreamsWithInitialHeaders.ContainsKey(streamId);

        FrameType internalType;
        byte internalFlags;
        byte[] payloadBytes;
        int payloadLen;

        if (!hasInitial)
        {
            // First HEADERS on this stream.
            //
            // Two cases:
            //  1) Normal: HEADERS without END_STREAM → initial response
            //     headers; subsequent DATA frames carry the body and a
            //     follow-up HEADERS w/ END_STREAM carries the trailers.
            //  2) Trailers-only (gRFC G3): HEADERS w/ END_STREAM as the
            //     SOLE frame on this stream — server is returning a status
            //     without a response body (e.g. NotFound, Unauthenticated).
            //     The single HEADERS block carries response pseudo-headers
            //     (`:status`, `content-type`, …) AND gRPC trailing fields
            //     (`grpc-status`, `grpc-message`, custom trailer metadata).
            //     The upper-layer state machine expects a Headers frame
            //     followed by a Trailers frame to complete the call; if we
            //     surface only one frame the call hangs forever waiting for
            //     trailers that never arrive.
            //
            // For case 2 we split the HPACK block into a Headers half and a
            // Trailers half (see <see cref="HpackHeadersAdapter.DecodeTrailersOnly"/>),
            // emit the Headers immediately, and stash the Trailers in
            // <see cref="Http2DecoderState.PendingFrameHeader"/>. The next
            // call to <see cref="ReadFramePayloadInternal"/> returns the
            // stash before touching the ring, preserving FIFO order.
            if (endStream)
            {
                var (headersV1, trailersV1) = HpackHeadersAdapter.DecodeTrailersOnly(headerBlock);

                var (hPayload, hLen) = headersV1.Encode();
                var (tPayload, tLen) = trailersV1.Encode();

                EnqueuePendingFrame(state,
                    new FrameHeader(FrameType.Trailers, streamId, (uint)tLen, TrailersFlags.EndStream),
                    FramePayload.FromPooled(tPayload, tLen));

                // No persistent stream state to retain: trailers-only means
                // the stream ended in this single HEADERS frame; any further
                // wire frames on this stream id (none expected from a well-
                // behaved peer) get treated as a fresh stream.
                var hHdr = new FrameHeader(
                    FrameType.Headers, streamId, (uint)hLen, HeadersFlags.Initial);
                return (hHdr, FramePayload.FromPooled(hPayload, hLen));
            }

            var v1 = HpackHeadersAdapter.DecodeHeaders(headerBlock);
            (payloadBytes, payloadLen) = v1.Encode();
            internalType = FrameType.Headers;
            internalFlags = (byte)HeadersFlags.Initial;
            state.StreamsWithInitialHeaders[streamId] = 1;
        }
        else
        {
            // Subsequent HEADERS → trailers.
            var v1 = HpackHeadersAdapter.DecodeTrailers(headerBlock);
            (payloadBytes, payloadLen) = v1.Encode();
            internalType = FrameType.Trailers;
            internalFlags = endStream ? TrailersFlags.EndStream : (byte)0;
            state.StreamsWithInitialHeaders.Remove(streamId);
        }

        var hdr = new FrameHeader(internalType, streamId, (uint)payloadLen, internalFlags);
        return (hdr, FramePayload.FromPooled(payloadBytes, payloadLen));
    }

    private static (FrameHeader Header, FramePayload Payload) ReadRstStreamFrame(
        ShmRing ring, ulong baseCommitReadIdx, uint streamId, int payloadLen, CancellationToken ct)
    {
        // RFC 7540 §6.4: RST_STREAM
        //   - payload length MUST be exactly 4 (treat other lengths as
        //     FRAME_SIZE_ERROR connection error)
        //   - stream identifier MUST be non-zero (treat zero as
        //     PROTOCOL_ERROR connection error)
        // We must drain the full malformed payload before throwing so the
        // ring read pointer stays in sync (any subsequent read would
        // otherwise mis-interpret leftover bytes as a new frame header).
        if (payloadLen != 4 || streamId == 0)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 RST_STREAM malformed (streamId={streamId}, payloadLen={payloadLen}; require streamId != 0 && payloadLen == 4)");
        }

        // Drain payload (4 bytes error code; we don't propagate the code).
        var _drain = ring.ReserveRead(payloadLen, ct);
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        // Clean up all per-stream state. A pending LPM accumulator may still
        // hold a pooled buffer; calling Reset() returns it to ArrayPool to
        // prevent a buffer leak when the peer cancels mid-message.
        var state = GetState(ring);
        state.StreamsWithInitialHeaders.Remove(streamId);
        if (RemoveAcc(state, streamId, out var pendingAcc))
        {
            pendingAcc!.Reset();
        }

        var hdr = new FrameHeader(FrameType.Cancel, streamId, 0, 0);
        return (hdr, FramePayload.Empty);
    }

    private static void HandleSettingsFrame(ShmRing ring, ulong baseCommitReadIdx,
        byte h2Flags, uint streamId, int payloadLen, CancellationToken ct)
    {
        // RFC 7540 §6.5: SETTINGS frame
        //   - stream identifier MUST be 0 (treat non-zero as PROTOCOL_ERROR)
        //   - on non-ACK: payload length MUST be a multiple of 6
        //     (treat otherwise as FRAME_SIZE_ERROR)
        //   - on ACK: payload length MUST be 0 (already enforced below)
        if (streamId != 0)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 SETTINGS frame must have streamId=0 (got {streamId})");
        }
        if ((h2Flags & Http2Flags.Ack) != 0)
        {
            // RFC 7540 §6.5.3 / RFC 9113 §6.5.3: a SETTINGS frame with the
            // ACK flag set MUST have a payload length of zero. A peer that
            // sends ACK with a non-zero payload is malformed; the spec
            // requires treating this as a connection error of type
            // FRAME_SIZE_ERROR.
            //
            // We must consume the bogus payload bytes from the ring BEFORE
            // throwing, otherwise the next ReadFramePayload call would
            // interpret those bytes as the start of a new H2 frame header
            // and the ring read pointer would desync (the connection would
            // then dump cryptic "Unknown H2 frame type" errors and either
            // hang or terminate). Committing all 9 + payloadLen bytes
            // matches the spec's "fully consume the frame, then fail the
            // connection" expectation.
            if (payloadLen != 0)
            {
                if (payloadLen > 0)
                {
                    var _ = ring.ReserveRead(payloadLen, ct);
                }
                ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
                throw new InvalidDataException(
                    $"H2 SETTINGS ACK frame must have empty payload (got {payloadLen} bytes)");
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size);
            return;
        }

        // RFC 7540 §6.5: non-ACK SETTINGS payload length MUST be a
        // multiple of 6 (each setting is 2-byte id + 4-byte value).
        if (payloadLen % 6 != 0)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 SETTINGS frame payload length {payloadLen} is not a multiple of 6");
        }

        // Drain settings payload (we don't dynamically apply peer settings;
        // we negotiate via the control segment).
        if (payloadLen > 0)
        {
            var _ = ring.ReserveRead(payloadLen, ct);
        }
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        // SETTINGS ACK is intentionally NOT emitted from this read path.
        //
        // The <paramref name="ring"/> here is the connection's RxRing (the
        // ring this side reads from); the peer is its sole writer. Issuing
        // <see cref="WriteSettings"/> on it would violate the SPSC ring
        // invariant and corrupt the peer's in-flight writes.
        //
        // The transport's wire-format negotiation happens via the control
        // segment (see <c>ShmControlHandler.HandleConnectAsync</c>); peers
        // do not gate behaviour on receiving an HTTP/2 SETTINGS ACK over
        // the data segment, so dropping the ACK here is safe. If a future
        // peer does require the ACK, route the write through this side's
        // <c>ShmFrameWriter</c> on the matching TxRing (would need to map
        // RxRing → ShmConnection); leaving as a no-op until needed.
    }

    private static (FrameHeader Header, FramePayload Payload) ReadPingFrame(
        ShmRing ring, ulong baseCommitReadIdx, byte h2Flags, uint streamId, int payloadLen, CancellationToken ct)
    {
        // RFC 7540 §6.7: PING
        //   - stream identifier MUST be 0 (PROTOCOL_ERROR otherwise)
        //   - payload length MUST be exactly 8 (FRAME_SIZE_ERROR otherwise)
        if (streamId != 0 || payloadLen != 8)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 PING malformed (streamId={streamId}, payloadLen={payloadLen}; require streamId == 0 && payloadLen == 8)");
        }

        var payloadReservation = ring.ReserveRead(payloadLen, ct);
        var pooled = ArrayPool<byte>.Shared.Rent(payloadLen);
        CopyFromReservationSlice(payloadReservation, 0, pooled.AsSpan(0, payloadLen));
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        var ack = (h2Flags & Http2Flags.Ack) != 0;
        var hdr = new FrameHeader(ack ? FrameType.Pong : FrameType.Ping, 0, (uint)payloadLen, 0);
        return (hdr, FramePayload.FromPooled(pooled, payloadLen));
    }

    private static (FrameHeader Header, FramePayload Payload) ReadGoAwayFrame(
        ShmRing ring, ulong baseCommitReadIdx, uint streamId, int payloadLen, CancellationToken ct)
    {
        // RFC 7540 §6.8: GOAWAY
        //   - stream identifier MUST be 0 (PROTOCOL_ERROR otherwise)
        //   - payload at minimum 8 bytes: 4-byte last-stream-id + 4-byte
        //     error code + optional debug data.
        if (streamId != 0 || payloadLen < 8)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 GOAWAY malformed (streamId={streamId}, payloadLen={payloadLen}; require streamId == 0 && payloadLen >= 8)");
        }

        var payloadReservation = ring.ReserveRead(payloadLen, ct);
        var pooled = ArrayPool<byte>.Shared.Rent(payloadLen);
        CopyFromReservationSlice(payloadReservation, 0, pooled.AsSpan(0, payloadLen));
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        // Internal GoAway payload is just a UTF-8 debug string. Skip the 8-byte header.
        var debugLen = payloadLen - 8;
        var debugBuf = ArrayPool<byte>.Shared.Rent(debugLen == 0 ? 1 : debugLen);
        if (debugLen > 0)
        {
            Array.Copy(pooled, 8, debugBuf, 0, debugLen);
        }
        ArrayPool<byte>.Shared.Return(pooled);

        var hdr = new FrameHeader(FrameType.GoAway, 0, (uint)debugLen, 0);
        return (hdr, FramePayload.FromPooled(debugBuf, debugLen));
    }

    private static (FrameHeader Header, FramePayload Payload) ReadWindowUpdateFrame(
        ShmRing ring, ulong baseCommitReadIdx, uint streamId, int payloadLen, CancellationToken ct)
    {
        // RFC 7540 §6.9.1: WINDOW_UPDATE payload length MUST be exactly 4.
        if (payloadLen != 4)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException($"H2 WINDOW_UPDATE frame payload length {payloadLen} != 4");
        }

        var payloadReservation = ring.ReserveRead(payloadLen, ct);
        Span<byte> raw = stackalloc byte[4];
        CopyFromReservationSlice(payloadReservation, 0, raw);
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        // RFC 7540 §6.9.1: increment MUST be a non-zero 31-bit value.
        // Zero is a stream-error / connection-error PROTOCOL_ERROR (a peer
        // that emits zero-increment is malformed; silently accepting it
        // would mask the protocol violation and could mask underlying
        // peer bugs at integration time).
        var increment = BinaryPrimitives.ReadUInt32BigEndian(raw) & 0x7FFFFFFFu;
        if (increment == 0)
        {
            throw new InvalidDataException(
                "H2 WINDOW_UPDATE increment must be non-zero (RFC 7540 §6.9.1)");
        }

        // Internal payload is 4-byte little-endian increment.
        var pooled = ArrayPool<byte>.Shared.Rent(4);
        BinaryPrimitives.WriteUInt32LittleEndian(pooled.AsSpan(0, 4), increment);
        var hdr = new FrameHeader(FrameType.WindowUpdate, streamId, 4, 0);
        return (hdr, FramePayload.FromPooled(pooled, 4));
    }

    /// <summary>Copies the entire contents of a read reservation into <paramref name="destination"/>.</summary>
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

    /// <summary>
    /// Copies <paramref name="length"/> bytes starting at <paramref name="srcOffset"/>
    /// of a read reservation into <paramref name="destination"/>.
    /// </summary>
    private static void CopyFromReservationSlice(ReadReservation reservation, int srcOffset, Span<byte> destination)
    {
        var firstLen = reservation.First.Length;
        var copied = 0;

        if (srcOffset < firstLen)
        {
            var available = firstLen - srcOffset;
            var toCopy = Math.Min(available, destination.Length);
            reservation.First.Span.Slice(srcOffset, toCopy).CopyTo(destination);
            copied += toCopy;
        }

        if (copied < destination.Length)
        {
            var secondOffset = Math.Max(0, srcOffset - firstLen);
            var remaining = destination.Length - copied;
            var toCopy = Math.Min(remaining, reservation.Second.Length - secondOffset);
            if (toCopy > 0)
            {
                reservation.Second.Span.Slice(secondOffset, toCopy).CopyTo(destination[copied..]);
            }
        }
    }
}
