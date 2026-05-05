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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// A read-only <see cref="Stream"/> that exposes a sequence of
/// <see cref="InboundFrame"/>s as a single byte stream, releasing each
/// frame's payload IMMEDIATELY after the last byte is consumed. Lets a
/// consumer (e.g. <c>protobuf.MergeFrom(CodedInputStream)</c>) parse a
/// multi-frame logical message without first reassembling the entire
/// payload into a contiguous buffer.
/// </summary>
/// <remarks>
/// <para>
/// Compared to the today's chain-anchor model (build a <c>ReadOnlySequence</c>
/// over all frames, parse, then release the whole chain), the streaming
/// model lets the writer reclaim ring space frame-by-frame as the parser
/// consumes them. This decouples ring footprint from message size: a
/// 256 MiB message can be received on a 4 MiB ring without ever
/// materialising the whole message in memory.
/// </para>
/// <para>
/// PR2 phase 2 (foundation only): this class is reachable via tests but
/// not yet wired to <see cref="ShmGrpcServer.ReadSingleMessageAsync"/>
/// or the client-side reassembly sites. Migration is intentionally
/// staged: phase 2.1 adds the class and tests; phases 2.2-2.5 migrate
/// the three reassembly sites + the H2 codec slow path.
/// </para>
/// <para>
/// Contract: caller pre-pulls the FIRST <see cref="InboundFrame"/> from
/// upstream and passes it to the constructor. The <c>pullNext</c>
/// delegate is invoked synchronously when more bytes are needed beyond
/// the first frame. <c>pullNext</c> returns <c>null</c> to indicate
/// end-of-message (channel closed, half-close received, trailers
/// arrived); a non-null frame whose <see cref="InboundFrame.Type"/> is
/// not <see cref="FrameType.Message"/> also terminates the stream
/// (the wrong frame is released by this class to avoid leaks).
/// </para>
/// </remarks>
internal sealed class FrameSequenceStream : Stream
{
    private readonly Func<CancellationToken, InboundFrame?> _pullNext;
    private readonly CancellationToken _ct;
    private InboundFrame _current;
    private bool _haveCurrent;
    private int _pos;        // bytes consumed from _current.Memory
    private bool _eos;       // upstream signaled end of logical message
    private bool _disposed;

    /// <summary>
    /// Creates a streaming view over <paramref name="firstFrame"/> plus
    /// any subsequent frames returned by <paramref name="pullNext"/>.
    /// </summary>
    /// <param name="firstFrame">The first inbound frame in the sequence.
    /// Ownership transfers to this stream — it is released either when
    /// fully consumed or when <see cref="Dispose"/> runs.</param>
    /// <param name="pullNext">Synchronous puller for subsequent frames.
    /// Must return <c>null</c> on end-of-message (or end-of-channel).
    /// Must throw <see cref="OperationCanceledException"/> if the
    /// caller's token fires (consistent with <see cref="Stream.Read"/>
    /// expectations).</param>
    /// <param name="ct">Cancellation token forwarded to
    /// <paramref name="pullNext"/> on each subsequent pull.</param>
    public FrameSequenceStream(
        InboundFrame firstFrame,
        Func<CancellationToken, InboundFrame?> pullNext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pullNext);

        _current = firstFrame;
        _haveCurrent = true;
        _pullNext = pullNext;
        _ct = ct;
        _pos = 0;
    }

    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        // Read-only stream; flush is a no-op.
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_eos || buffer.IsEmpty)
        {
            return 0;
        }

        var written = 0;
        while (written < buffer.Length)
        {
            // Need a frame with bytes available.
            if (!_haveCurrent || _pos >= _current.Length)
            {
                if (_haveCurrent)
                {
                    // Released as soon as the last byte is consumed —
                    // writer can reuse the ring slot before the parser
                    // finishes the rest of the message.
                    _current.ReturnToPool();
                    _current = default;
                    _haveCurrent = false;
                    _pos = 0;
                }

                var pulled = _pullNext(_ct);
                if (pulled == null)
                {
                    // End-of-channel.
                    _eos = true;
                    break;
                }

                if (pulled.Value.Type != FrameType.Message)
                {
                    // Non-Message frame (e.g., Trailers, Cancel,
                    // HalfClose) terminates the logical message. The
                    // pulled frame's payload is no longer ours; we own
                    // it now (per pullNext contract) and must release
                    // it to avoid leaking a pooled buffer.
                    pulled.Value.ReturnToPool();
                    _eos = true;
                    break;
                }

                _current = pulled.Value;
                _haveCurrent = true;
                _pos = 0;

                // Empty MESSAGE frame is legal (e.g., flush marker).
                // Loop back to either pull the next frame or fall
                // through to the copy below.
                if (_current.Length == 0)
                {
                    continue;
                }
            }

            var avail = _current.Length - _pos;
            var take = Math.Min(avail, buffer.Length - written);
            _current.Memory.Span.Slice(_pos, take).CopyTo(buffer.Slice(written, take));
            _pos += take;
            written += take;
        }

        return written;
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (_haveCurrent)
            {
                _current.ReturnToPool();
                _current = default;
                _haveCurrent = false;
            }
            // Note: Dispose does NOT drain the upstream channel. Frames
            // still in flight are released by ShmGrpcStream.Dispose
            // when the stream tears down. The contract here is "release
            // what I own"; the pullNext-source owns the rest.
        }
        base.Dispose(disposing);
    }
}
