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
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
public class FrameSequenceStreamTests
{
    /// <summary>
    /// Builds an InboundFrame whose payload is a pooled byte[] filled
    /// with deterministic bytes (offset + index pattern). Caller is
    /// responsible for ensuring the frame is released exactly once.
    /// </summary>
    private static InboundFrame MakeFrame(int length, int byteOffset = 0,
        FrameType type = FrameType.Message, byte flags = 0)
    {
        var buf = ArrayPool<byte>.Shared.Rent(length);
        for (int i = 0; i < length; i++)
        {
            buf[i] = (byte)((byteOffset + i) & 0xFF);
        }
        var payload = FramePayload.FromPooled(buf, length);
        return new InboundFrame(type, payload, flags);
    }

    /// <summary>
    /// PullNext source backed by a Queue. Tracks pull count so tests
    /// can assert "next frame pulled only after current consumed".
    /// </summary>
    private sealed class QueuedSource
    {
        private readonly Queue<InboundFrame?> _queue;
        public int PullCount { get; private set; }

        public QueuedSource(IEnumerable<InboundFrame?> frames)
        {
            _queue = new Queue<InboundFrame?>(frames);
        }

        public InboundFrame? PullNext(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            PullCount++;
            return _queue.TryDequeue(out var f) ? f : null;
        }
    }

    [Test]
    public void Read_AcrossThreeFrames_ProducesConcatenatedBytes()
    {
        var first = MakeFrame(length: 100, byteOffset: 0);
        var second = MakeFrame(length: 200, byteOffset: 100);
        var third = MakeFrame(length: 50, byteOffset: 300);
        var src = new QueuedSource(new InboundFrame?[] { second, third, null });

        using var stream = new FrameSequenceStream(first, src.PullNext, default);

        var dest = new byte[350];
        var totalRead = 0;
        int n;
        while ((n = stream.Read(dest.AsSpan(totalRead))) > 0)
        {
            totalRead += n;
        }

        Assert.That(totalRead, Is.EqualTo(350),
            "Read must return all 350 bytes across the 3 frames.");
        for (int i = 0; i < 350; i++)
        {
            Assert.That(dest[i], Is.EqualTo((byte)(i & 0xFF)),
                $"Byte {i} mismatch: expected {i & 0xFF}, got {dest[i]}.");
        }
    }

    [Test]
    public void Read_SmallBufferAcrossLargeFrames_ConsumesPiecewise()
    {
        // Three 1KB frames; read in 100-byte chunks. Verify all 3072 bytes
        // arrive in order and the puller advances one frame per
        // exhaustion.
        var f0 = MakeFrame(length: 1024, byteOffset: 0);
        var f1 = MakeFrame(length: 1024, byteOffset: 1024);
        var f2 = MakeFrame(length: 1024, byteOffset: 2048);
        var src = new QueuedSource(new InboundFrame?[] { f1, f2, null });

        using var stream = new FrameSequenceStream(f0, src.PullNext, default);

        var dest = new byte[3072];
        var pos = 0;
        var chunk = new byte[100];
        while (pos < dest.Length)
        {
            var want = Math.Min(chunk.Length, dest.Length - pos);
            var n = stream.Read(chunk, 0, want);
            if (n == 0) break;
            chunk.AsSpan(0, n).CopyTo(dest.AsSpan(pos, n));
            pos += n;
        }

        Assert.That(pos, Is.EqualTo(3072));
        for (int i = 0; i < 3072; i++)
        {
            Assert.That(dest[i], Is.EqualTo((byte)(i & 0xFF)), $"Byte {i} mismatch.");
        }
        // Two pulls: f1 and f2. The terminating-null pull only happens
        // if the caller asks for more bytes after the last frame is
        // exhausted; this test stops at exactly 3072 bytes so no extra
        // pull is triggered.
        Assert.That(src.PullCount, Is.EqualTo(2));
    }

    [Test]
    public void Read_AfterEndOfChannel_ReturnsZero()
    {
        var first = MakeFrame(length: 50);
        var src = new QueuedSource(new InboundFrame?[] { null });
        using var stream = new FrameSequenceStream(first, src.PullNext, default);

        var dest = new byte[100];
        var n = stream.Read(dest, 0, 100);
        Assert.That(n, Is.EqualTo(50), "First read drains the first frame.");

        n = stream.Read(dest, 0, 100);
        Assert.That(n, Is.EqualTo(0), "Subsequent reads after EOS return 0.");

        n = stream.Read(dest, 0, 100);
        Assert.That(n, Is.EqualTo(0), "Idempotent — repeat returns 0.");
    }

    [Test]
    public void Read_NonMessageFrame_TerminatesAndReleasesIt()
    {
        // Source returns a Trailers frame after the first MESSAGE. The
        // FrameSequenceStream must release the unwanted frame and
        // surface end-of-stream.
        var first = MakeFrame(length: 50, byteOffset: 0);
        var trailers = MakeFrame(length: 30, byteOffset: 50, type: FrameType.Trailers);
        var src = new QueuedSource(new InboundFrame?[] { trailers });

        using var stream = new FrameSequenceStream(first, src.PullNext, default);

        var dest = new byte[200];
#pragma warning disable CA2022 // Inexact read is intentional for this test
        var n = stream.Read(dest, 0, 200);
#pragma warning restore CA2022
        Assert.That(n, Is.EqualTo(50), "Only the MESSAGE bytes are surfaced.");

#pragma warning disable CA2022
        n = stream.Read(dest, 0, 200);
#pragma warning restore CA2022
        Assert.That(n, Is.EqualTo(0), "Trailers terminates the logical message.");
        // No leak assertion possible without a release-tracking pool;
        // covered structurally — `pulled.ReturnToPool()` runs on the
        // non-Message branch, see FrameSequenceStream.Read.
    }

    [Test]
    public void Dispose_AfterPartialConsume_DoesNotThrow()
    {
        var first = MakeFrame(length: 1024);
        var src = new QueuedSource(new InboundFrame?[] { null });
        var stream = new FrameSequenceStream(first, src.PullNext, default);

        var dest = new byte[100];
        var n = stream.Read(dest, 0, 100);
        Assert.That(n, Is.EqualTo(100));

        Assert.DoesNotThrow(() => stream.Dispose(),
            "Dispose mid-read must release the in-hand frame cleanly.");

#pragma warning disable CA2022
        Assert.Throws<ObjectDisposedException>(() => stream.Read(dest, 0, 100));
#pragma warning restore CA2022
    }

    [Test]
    public void Read_EmptyMessageFrameInMiddle_IsSkippedTransparently()
    {
        // Empty MESSAGE frames are legal (e.g., flush markers). The
        // stream must skip them and continue to subsequent frames
        // without surfacing any bytes from them.
        var first = MakeFrame(length: 30, byteOffset: 0);
        var emptyMid = MakeFrame(length: 0, byteOffset: 30);
        var third = MakeFrame(length: 40, byteOffset: 30);
        var src = new QueuedSource(new InboundFrame?[] { emptyMid, third, null });

        using var stream = new FrameSequenceStream(first, src.PullNext, default);

        var dest = new byte[100];
        var pos = 0;
        int n;
        while ((n = stream.Read(dest, pos, dest.Length - pos)) > 0)
        {
            pos += n;
        }

        Assert.That(pos, Is.EqualTo(70), "30 + 0 + 40 = 70 bytes total.");
        for (int i = 0; i < 30; i++)
            Assert.That(dest[i], Is.EqualTo((byte)i));
        for (int i = 0; i < 40; i++)
            Assert.That(dest[30 + i], Is.EqualTo((byte)((30 + i) & 0xFF)));
    }

    [Test]
    public void Read_PullThrows_PropagatesAndDisposeReleasesFirst()
    {
        // pullNext throws OperationCanceledException after first frame
        // exhausts. Caller's exception propagation must not lose the
        // first frame's release on Dispose.
        var first = MakeFrame(length: 50);
        var pullCalled = false;
        InboundFrame? pull(CancellationToken ct)
        {
            pullCalled = true;
            throw new OperationCanceledException(ct);
        }

        using (var stream = new FrameSequenceStream(first, pull, new CancellationToken(canceled: true)))
        {
            var dest = new byte[200];
            // First read for exactly 50 bytes drains the first frame
            // without triggering a pull (loop exits when written ==
            // buffer.Length).
#pragma warning disable CA2022
            var n = stream.Read(dest, 0, 50);
#pragma warning restore CA2022
            Assert.That(n, Is.EqualTo(50));
            Assert.That(pullCalled, Is.False);

            // Second read needs more bytes than _current has; pull is
            // invoked and throws.
#pragma warning disable CA2022
            Assert.Throws<OperationCanceledException>(() => stream.Read(dest, 0, 50));
#pragma warning restore CA2022
            Assert.That(pullCalled, Is.True);
        }
        // Dispose ran via using — first frame already released after
        // the 50-byte exhaustion. No leak.
    }

    [Test]
    public void Read_AsyncOverloadsThrowOrDelegate()
    {
        // The base Stream.ReadAsync default forwards to sync Read for
        // unmodified subclasses; our class doesn't override it, so the
        // existing semantics flow through. Verify a basic ReadAsync call
        // still works (consumers like CodedInputStream use sync Read).
        var first = MakeFrame(length: 10, byteOffset: 0);
        var src = new QueuedSource(new InboundFrame?[] { null });
        using var stream = new FrameSequenceStream(first, src.PullNext, default);

        var dest = new byte[20];
        var n = stream.ReadAsync(dest, 0, 20).GetAwaiter().GetResult();
        Assert.That(n, Is.EqualTo(10));
    }
}
