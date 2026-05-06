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

using System.Diagnostics;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Microbenchmark for the per-frame ZC anchor FIFO bookkeeping cost.
/// Validates the assumption that anchor allocation + release stays well
/// under the per-frame I/O budget.
/// <para>
/// Skipped by default; run with environment variable
/// <c>RINGBENCH_RUN_ZC_MICRO=1</c>. Use <c>-c Release</c> to avoid debug
/// overhead.
/// </para>
/// </summary>
[TestFixture]
[Category("Microbench")]
[Explicit("Microbenchmark — set RINGBENCH_RUN_ZC_MICRO=1 to opt in.")]
public class ZcAnchorFifoMicrobench
{
    private const int WarmupIters = 50_000;
    private const int MeasureIters = 1_000_000;

    private static ShmRing CreateRing()
    {
        var memory = new byte[ShmConstants.RingHeaderSize + 1024 * 1024];
        return new ShmRing(memory, 0, 1024 * 1024);
    }

    /// <summary>
    /// Per-frame ZC anchor FULL cycle: matches the reader+consumer pair
    /// exercised on every ZC frame in the codec.
    ///   Reader:    TryBeginPerFrameZc (alloc slot + Volatile.Write fields)
    ///   Consumer:  ReleasePerFrameZc → DrainReleasedAnchors (CAS head + publish)
    /// </summary>
    private static long MeasurePerFrameFifo(ShmRing ring, int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var slot = ring.TryBeginPerFrameZc((ulong)(i * 64 * 1024), 64 * 1024);
            ring.ReleasePerFrameZc(slot);
        }
        sw.Stop();
        return sw.ElapsedTicks;
    }

    [Test]
    public void Bookkeeping_FullCycleOverhead_StaysUnderBudget()
    {
        if (Environment.GetEnvironmentVariable("RINGBENCH_RUN_ZC_MICRO") != "1")
        {
            Assert.Ignore("Set RINGBENCH_RUN_ZC_MICRO=1 to run.");
        }

        var ring = CreateRing();
        ring.EnsureZcAnchorFifo();

        // Warmup
        MeasurePerFrameFifo(ring, WarmupIters);

        // Measurement (3 trials, take median to reduce JIT/GC noise).
        var trials = new long[3];
        for (var t = 0; t < 3; t++)
        {
            trials[t] = MeasurePerFrameFifo(ring, MeasureIters);
        }
        Array.Sort(trials);
        var median = trials[1];

        var ticksPerNs = (double)Stopwatch.Frequency / 1_000_000_000.0;
        var nsPerOp = median / ticksPerNs / MeasureIters;

        TestContext.WriteLine($"Per-frame ZC anchor (TryBegin+Release): {nsPerOp,7:F2} ns/op");

        // Absolute budget gate: per-frame bookkeeping must stay < 200 ns/op
        // so it stays negligible against per-frame I/O cost. At 2000 frames
        // per 256 MB / 1 MiB-ring RPC, total bookkeeping = 200 ns × 2000 =
        // 400 µs / 154 ms = 0.26%, which is acceptable headroom against the
        // ~+10% throughput win from eliminating the codec memcpy.
        Assert.That(nsPerOp, Is.LessThan(200.0),
            $"Per-frame ZC anchor bookkeeping ({nsPerOp:F2} ns/op) exceeds 200 ns " +
            $"budget. Investigate slot field layout, atomic op count, false sharing.");
    }
}
