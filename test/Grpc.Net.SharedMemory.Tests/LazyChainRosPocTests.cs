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
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Proof-of-concept tests verifying that <see cref="MemoryManager{T}.GetSpan"/>
/// is the right hook point for "lazy-fill segment + release prev frame"
/// streaming-parse approach.
/// </summary>
/// <remarks>
/// <para>
/// Hypothesis: protobuf's <see cref="MessageExtensions.MergeFrom(IMessage, ReadOnlySequence{byte})"/>
/// invokes <see cref="MemoryManager{T}.GetSpan"/> on each segment AFTER fully
/// consuming the previous segment's bytes. If true, we can:
/// </para>
/// <list type="number">
/// <item><description>
///   Pre-allocate N <see cref="ReadOnlySequenceSegment{T}"/>s as placeholders
///   with custom <see cref="MemoryManager{T}"/>s.
/// </description></item>
/// <item><description>
///   In each MM's GetSpan(): pull next ring frame from upstream channel,
///   release the previous frame.
/// </description></item>
/// <item><description>
///   Build a <see cref="ReadOnlySequence{T}"/> over the chain and call
///   <c>MergeFrom(ros)</c>. Result: ring footprint = O(1 frame); memcpy = 1
///   (ring → ByteString backing).
/// </description></item>
/// </list>
/// <para>
/// Test schema: <see cref="BytesValue"/> well-known type — single
/// <c>bytes value = 1</c> field. Wire format = tag (1 B) + length varint
/// + N bytes of payload. Total wire size 4096 B is split into 4 × 1024 B
/// segments to force protobuf's parser through multi-segment refill.
/// </para>
/// </remarks>
[TestFixture]
public class LazyChainRosPocTests
{
    /// <summary>
    /// Records GetSpan invocation order and supports an OnGetSpan callback
    /// that fires the moment protobuf's parser advances to this segment.
    /// </summary>
    private sealed class TrackedMemoryManager : MemoryManager<byte>
    {
        private readonly byte[] _buffer;
        private readonly Memory<byte> _memory;
        public int GetSpanCallCount;
        public bool Released;
        public Action? OnGetSpan;
        public string Name = "?";

        public TrackedMemoryManager(byte[] buffer)
        {
            _buffer = buffer;
            // CreateMemory wraps `this` as the Memory<byte>'s _object; subsequent
            // .Memory.Span / .Memory.GetSpan calls will route through GetSpan().
            _memory = CreateMemory(buffer.Length);
        }

        public override Span<byte> GetSpan()
        {
            if (Released)
            {
                throw new InvalidOperationException(
                    $"GetSpan called on released segment '{Name}'. " +
                    "Parser accessed a segment after we marked it released — " +
                    "lazy-fill + per-frame-release is NOT safe.");
            }
            GetSpanCallCount++;
            OnGetSpan?.Invoke();
            return _buffer.AsSpan();
        }

        public override Memory<byte> Memory => _memory;

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            // Pin is unrelated to our test concern; protobuf's parser does not
            // pin spans during MergeFrom. Returning a no-op handle is fine for
            // ROS Span enumeration.
            return default;
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }

    /// <summary>
    /// ReadOnlySequenceSegment derivative whose Memory is wired to a
    /// <see cref="TrackedMemoryManager"/>.
    /// </summary>
    private sealed class TrackedSegment : ReadOnlySequenceSegment<byte>
    {
        public TrackedSegment(TrackedMemoryManager mm, long runningIndex)
        {
            Memory = mm.Memory;          // protected setter on base; OK from derived
            RunningIndex = runningIndex;
        }

        public void SetNext(TrackedSegment next) => Next = next;
    }

    /// <summary>
    /// Builds the wire-format byte stream for <see cref="BytesValue"/> with
    /// a payload of <paramref name="payloadSize"/> bytes (deterministic
    /// pattern: byte i = i &amp; 0xFF).
    /// </summary>
    /// <returns>The complete wire-format bytes (header + payload).</returns>
    private static byte[] EncodeBytesValueWire(int payloadSize)
    {
        // Tag for field 1, wire type LEN (2): (1 << 3) | 2 = 0x0A.
        // Length varint: 7 bits per byte, MSB set if more bytes follow.
        var lengthBytes = new List<byte>(5);
        var n = (uint)payloadSize;
        while (n >= 0x80u)
        {
            lengthBytes.Add((byte)(n | 0x80u));
            n >>= 7;
        }
        lengthBytes.Add((byte)n);

        var headerLen = 1 + lengthBytes.Count;
        var result = new byte[headerLen + payloadSize];
        result[0] = 0x0A;
        for (var i = 0; i < lengthBytes.Count; i++)
        {
            result[1 + i] = lengthBytes[i];
        }
        for (var i = 0; i < payloadSize; i++)
        {
            result[headerLen + i] = (byte)(i & 0xFF);
        }
        return result;
    }

    private static (TrackedSegment First, TrackedSegment Last,
        TrackedMemoryManager[] Mms) BuildChain(byte[] wire, int segmentSize)
    {
        if (wire.Length % segmentSize != 0)
        {
            throw new ArgumentException(
                $"Wire size {wire.Length} must be a multiple of segmentSize {segmentSize}.");
        }
        var segCount = wire.Length / segmentSize;
        var mms = new TrackedMemoryManager[segCount];
        var segs = new TrackedSegment[segCount];

        for (var i = 0; i < segCount; i++)
        {
            var buf = new byte[segmentSize];
            Array.Copy(wire, i * segmentSize, buf, 0, segmentSize);
            mms[i] = new TrackedMemoryManager(buf) { Name = $"seg{i}" };
            segs[i] = new TrackedSegment(mms[i], i * segmentSize);
            if (i > 0)
            {
                segs[i - 1].SetNext(segs[i]);
            }
        }
        return (segs[0], segs[segCount - 1], mms);
    }

    [Test]
    public void MergeFrom_ROSAcrossFourSegments_ParsesCorrectly()
    {
        // 4 × 1024 B = 4096 B total wire. Header: 0x0A + varint(4093) [2 B] = 3 B.
        // Payload: 4093 B → spans seg0[3..1023], seg1, seg2, seg3[0..1023].
        const int payloadSize = 4093;
        var wire = EncodeBytesValueWire(payloadSize);
        Assert.That(wire.Length, Is.EqualTo(4096));

        var (first, last, mms) = BuildChain(wire, segmentSize: 1024);

        var ros = new ReadOnlySequence<byte>(first, 0, last, 1024);
        Assert.That(ros.Length, Is.EqualTo(4096));

        var msg = new BytesValue();
        msg.MergeFrom(ros);

        Assert.That(msg.Value.Length, Is.EqualTo(payloadSize),
            "Parsed bytes value length must match wire-encoded length.");
        for (var i = 0; i < payloadSize; i++)
        {
            Assert.That(msg.Value[i], Is.EqualTo((byte)(i & 0xFF)),
                $"Payload byte at offset {i} mismatch.");
        }

        // Each segment must have been visited at least once.
        for (var i = 0; i < mms.Length; i++)
        {
            Assert.That(mms[i].GetSpanCallCount, Is.GreaterThan(0),
                $"Segment {i} was never accessed by the parser.");
        }
    }

    [Test]
    public void MergeFrom_ROSAcrossFourSegments_GetSpanInvokedInOrder()
    {
        const int payloadSize = 4093;
        var wire = EncodeBytesValueWire(payloadSize);
        var (first, last, mms) = BuildChain(wire, segmentSize: 1024);

        var callOrder = new List<int>();
        for (var i = 0; i < mms.Length; i++)
        {
            var idx = i;
            mms[i].OnGetSpan = () => callOrder.Add(idx);
        }

        var ros = new ReadOnlySequence<byte>(first, 0, last, 1024);
        var msg = new BytesValue();
        msg.MergeFrom(ros);

        TestContext.Out.WriteLine("GetSpan call order: " + string.Join(",", callOrder));
        TestContext.Out.WriteLine($"Per-segment GetSpan counts: " +
            $"seg0={mms[0].GetSpanCallCount}, seg1={mms[1].GetSpanCallCount}, " +
            $"seg2={mms[2].GetSpanCallCount}, seg3={mms[3].GetSpanCallCount}");

        // The FIRST occurrence of each segment in callOrder must be in [0,1,2,3] order.
        var firstIdxOf = new int[4];
        for (var i = 0; i < 4; i++) firstIdxOf[i] = callOrder.IndexOf(i);

        Assert.That(firstIdxOf[0], Is.GreaterThanOrEqualTo(0), "seg0 never visited.");
        Assert.That(firstIdxOf[1], Is.GreaterThan(firstIdxOf[0]),
            "seg1 must be first-accessed after seg0.");
        Assert.That(firstIdxOf[2], Is.GreaterThan(firstIdxOf[1]),
            "seg2 must be first-accessed after seg1.");
        Assert.That(firstIdxOf[3], Is.GreaterThan(firstIdxOf[2]),
            "seg3 must be first-accessed after seg2.");
    }

    /// <summary>
    /// Critical PoC test: when seg[i].GetSpan() fires, mark seg[i-1] as
    /// "Released". If the parser EVER accesses a Released segment thereafter,
    /// the throw aborts MergeFrom and this test fails. If the test PASSES,
    /// the lazy-fill + per-frame-release approach is timing-safe with the
    /// public protobuf-csharp ROS parser.
    /// </summary>
    [Test]
    public void MergeFrom_ROSAcrossFourSegments_PrevReleasedOnNextGetSpan_NoAccessAfterRelease()
    {
        const int payloadSize = 4093;
        var wire = EncodeBytesValueWire(payloadSize);
        var (first, last, mms) = BuildChain(wire, segmentSize: 1024);

        // Stagger: seg[i].GetSpan callback releases seg[i-1].
        for (var i = 1; i < mms.Length; i++)
        {
            var prev = mms[i - 1];
            mms[i].OnGetSpan = () =>
            {
                if (!prev.Released)
                {
                    prev.Released = true;
                    TestContext.Out.WriteLine($"Releasing {prev.Name}");
                }
            };
        }

        var ros = new ReadOnlySequence<byte>(first, 0, last, 1024);
        var msg = new BytesValue();

        Assert.DoesNotThrow(() => msg.MergeFrom(ros),
            "Parser must NOT access any segment after we marked it released. " +
            "If this throws, the lazy-fill approach is unsafe.");

        Assert.That(msg.Value.Length, Is.EqualTo(payloadSize));
        for (var i = 0; i < payloadSize; i++)
        {
            Assert.That(msg.Value[i], Is.EqualTo((byte)(i & 0xFF)),
                $"Payload byte at offset {i} mismatch.");
        }

        TestContext.Out.WriteLine($"Per-segment GetSpan counts: " +
            $"seg0={mms[0].GetSpanCallCount}, seg1={mms[1].GetSpanCallCount}, " +
            $"seg2={mms[2].GetSpanCallCount}, seg3={mms[3].GetSpanCallCount}");
    }

    /// <summary>
    /// Same as above but with 16 segments × 256 B (more parser refills,
    /// stresses the timing assumption).
    /// </summary>
    [Test]
    public void MergeFrom_ROSAcrossSixteenSmallSegments_PrevReleasedOnNextGetSpan_NoAccessAfterRelease()
    {
        const int totalSize = 4096;
        const int payloadSize = totalSize - 3; // 3-byte header
        var wire = EncodeBytesValueWire(payloadSize);
        Assert.That(wire.Length, Is.EqualTo(totalSize));

        var (first, last, mms) = BuildChain(wire, segmentSize: 256);
        Assert.That(mms.Length, Is.EqualTo(16));

        for (var i = 1; i < mms.Length; i++)
        {
            var prev = mms[i - 1];
            mms[i].OnGetSpan = () =>
            {
                if (!prev.Released) prev.Released = true;
            };
        }

        var ros = new ReadOnlySequence<byte>(first, 0, last, 256);
        var msg = new BytesValue();

        Assert.DoesNotThrow(() => msg.MergeFrom(ros));
        Assert.That(msg.Value.Length, Is.EqualTo(payloadSize));
        for (var i = 0; i < payloadSize; i++)
        {
            Assert.That(msg.Value[i], Is.EqualTo((byte)(i & 0xFF)),
                $"Payload byte at offset {i} mismatch (16-segment).");
        }
    }

    /// <summary>
    /// Verifies the timing for a LARGE message: 256 × 1024 B = 256 KiB,
    /// stressing the streaming-parse hot path. If this passes, a 256 MiB
    /// message split into 256 × 1 MiB ring frames behaves identically.
    /// </summary>
    [Test]
    public void MergeFrom_ROSAcross256Segments_PrevReleasedOnNextGetSpan_NoAccessAfterRelease()
    {
        const int segCount = 256;
        const int segSize = 1024;
        const int totalSize = segCount * segSize;
        // 3-byte header (tag + 3-byte varint for length 262141, since 262141 < 2^21)
        // Actually 262141 in varint: needs 3 bytes since 16384 < 262141 < 2097152.
        // Header = 1 + 3 = 4 B. Adjust payloadSize to make total = exact.
        // Let's compute:
        //   payloadSize = totalSize - headerSize
        //   headerSize depends on payloadSize varint length
        //   For totalSize=262144:
        //     try headerSize=4 → payloadSize=262140 → varint(262140)=3 B → headerSize=4 ✓
        const int payloadSize = totalSize - 4;
        var wire = EncodeBytesValueWire(payloadSize);
        Assert.That(wire.Length, Is.EqualTo(totalSize),
            "Header size assumption broken.");

        var (first, last, mms) = BuildChain(wire, segmentSize: segSize);
        Assert.That(mms.Length, Is.EqualTo(segCount));

        for (var i = 1; i < mms.Length; i++)
        {
            var prev = mms[i - 1];
            mms[i].OnGetSpan = () =>
            {
                if (!prev.Released) prev.Released = true;
            };
        }

        var ros = new ReadOnlySequence<byte>(first, 0, last, segSize);
        var msg = new BytesValue();

        Assert.DoesNotThrow(() => msg.MergeFrom(ros));
        Assert.That(msg.Value.Length, Is.EqualTo(payloadSize));

        // Spot-check some payload bytes
        Assert.That(msg.Value[0], Is.EqualTo((byte)0));
        Assert.That(msg.Value[100], Is.EqualTo((byte)100));
        Assert.That(msg.Value[1023], Is.EqualTo((byte)(1023 & 0xFF)));
        Assert.That(msg.Value[payloadSize - 1], Is.EqualTo((byte)((payloadSize - 1) & 0xFF)));
    }
}
