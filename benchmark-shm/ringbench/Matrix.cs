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

// ============================================================================
// gRFC G3 benchmark matrix driver ("grfc-g3-bench-v1").
//
// This is the contract-shaped benchmark path, distinct from the legacy
// ringbench unary/streaming/concurrent sweep that lives in RingBench.cs.
// Differences that matter for the gRFC Performance section:
//
//   * TIME-based warmup and measurement (>= 5 s / >= 30 s) instead of the
//     legacy fixed iteration counts, so every cell gets the same steady-state
//     budget regardless of payload size.
//   * Full latency DISTRIBUTION (p50/p90/p99 + mean), not just an average.
//     Samples go into a log-linear histogram (~0.4 % relative precision) so a
//     30 s cell at microsecond latencies costs ~128 KB per worker instead of
//     tens of MB of raw samples.
//   * Concurrency is a first-class axis for BOTH workloads. The legacy path
//     only had concurrency for streaming (`--concurrent`); unary was always
//     serial.
//   * Repetitions with per-metric median aggregation.
//   * Emits the cross-language `grfc-g3-bench-v1` schema so the Go and .NET
//     sides of gRFC G3 can be diffed field-by-field.
//
// Throughput convention: single direction (payload bytes x ops / elapsed).
// `throughput_counts_both_directions` is emitted as false to make that
// explicit. NOTE this differs from the legacy ringbench sweep, which doubles
// the byte count (request + response).
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Grpc.Net.SharedMemory;
using Grpc.Testing;

/// <summary>
/// Log-linear latency histogram (HdrHistogram-style) over nanosecond values.
/// Fixed memory, ~0.4 % relative precision, safe for millions of samples.
/// Not thread-safe by design: each worker owns one and they are merged at the
/// end of a cell, which keeps the hot recording path free of interlocks.
/// </summary>
sealed class LatencyHistogram
{
    const int SubBits = 8;
    const int SubCount = 1 << SubBits;   // 256 linear sub-buckets per octave
    const int SubHalf = SubCount >> 1;   // 128
    const int BucketCount = 40;          // covers up to ~2^47 ns (~39 h)

    readonly long[] _counts = new long[SubCount + BucketCount * SubHalf];
    long _total;
    double _sumNs;
    long _maxNs;

    public long Total => _total;
    public double MeanUs => _total == 0 ? 0 : _sumNs / _total / 1000.0;
    public double MaxUs => _maxNs / 1000.0;

    static int IndexOf(long v)
    {
        if (v < 0) v = 0;
        if (v < SubCount) return (int)v;
        int msb = 63 - BitOperations.LeadingZeroCount((ulong)v);
        int bucketIndex = msb - (SubBits - 1);               // >= 1
        if (bucketIndex >= BucketCount) bucketIndex = BucketCount - 1;
        long subIndex = v >> bucketIndex;                    // in [SubHalf, SubCount)
        if (subIndex >= SubCount) subIndex = SubCount - 1;
        return SubCount + (bucketIndex - 1) * SubHalf + (int)(subIndex - SubHalf);
    }

    static long ValueOf(int idx)
    {
        if (idx < SubCount) return idx;
        int rel = idx - SubCount;
        int bucketIndex = rel / SubHalf + 1;
        long subIndex = rel % SubHalf + SubHalf;
        return subIndex << bucketIndex;
    }

    public void Record(long ns)
    {
        _counts[IndexOf(ns)]++;
        _total++;
        _sumNs += ns;
        if (ns > _maxNs) _maxNs = ns;
    }

    public void MergeFrom(LatencyHistogram other)
    {
        for (int i = 0; i < _counts.Length; i++) _counts[i] += other._counts[i];
        _total += other._total;
        _sumNs += other._sumNs;
        if (other._maxNs > _maxNs) _maxNs = other._maxNs;
    }

    /// <summary>Percentile in microseconds. <paramref name="p"/> in [0,1].</summary>
    public double PercentileUs(double p)
    {
        if (_total == 0) return 0;
        long target = (long)Math.Ceiling(p * _total);
        if (target < 1) target = 1;
        long seen = 0;
        for (int i = 0; i < _counts.Length; i++)
        {
            seen += _counts[i];
            if (seen >= target) return ValueOf(i) / 1000.0;
        }
        return _maxNs / 1000.0;
    }
}

/// <summary>One repetition's worth of measurements for a single matrix cell.</summary>
sealed record CellSample(
    double P50Us,
    double P90Us,
    double P99Us,
    double MeanUs,
    double ThroughputMBps,
    double CpuUsPerOp,
    double RssPeakMb,
    long Ops);

sealed class MatrixOptions
{
    public string[] Transports = ["shm", "uds"];
    public string[] Workloads = ["unary", "streaming"];
    public int[] Payloads = [1, 1024, 16384, 65536, 262144, 1048576, 4194304];
    public int[] Concurrency = [1, 8, 64];
    public double WarmupSeconds = 5;
    public double DurationSeconds = 30;
    public int Repetitions = 3;
    public string OutPath = "results.json";
    public string? CommitOverride;
}

static class MatrixRunner
{
    static readonly double s_tickToNs = 1_000_000_000.0 / Stopwatch.Frequency;

    static long NowTicks() => Stopwatch.GetTimestamp();
    static long TicksToNs(long ticks) => (long)(ticks * s_tickToNs);

    sealed class WorkerState
    {
        public readonly LatencyHistogram Hist = new();
        public long Ops;
    }

    public static async Task RunAsync(MatrixOptions opt, Func<string, Task<BenchEnv>> startEnv)
    {
        var meta = BuildMetadata(opt);
        var runs = new List<Dictionary<string, object?>>();
        var skipped = new List<string>();

        // Checkpoint after every cell. A full matrix is hours long, and an
        // interruption (OOM reaper, Ctrl-C, reboot) must not discard everything
        // measured so far. Writing is atomic-ish via a temp file + move so a kill
        // mid-write cannot leave a truncated results.json behind.
        void Checkpoint()
        {
            meta["runs"] = runs;
            if (skipped.Count > 0) meta["skipped"] = skipped;
            meta["complete"] = false;
            WriteResults(meta, opt.OutPath);
        }

        Console.WriteLine($"=== gRFC G3 matrix ({meta["arm"]} arm) ===");
        Console.WriteLine($"commit={meta["commit"]} dirty={meta["dirty"]} os={meta["os"]} cores={meta["cores"]}");
        Console.WriteLine($"warmup={opt.WarmupSeconds}s duration={opt.DurationSeconds}s reps={opt.Repetitions}");
        Console.WriteLine();

        foreach (var transport in opt.Transports)
        {
            Console.WriteLine($"--- transport={transport} ---");

            foreach (var workload in opt.Workloads)
            foreach (var payload in opt.Payloads)
            foreach (var conc in opt.Concurrency)
            {
                string cellId = $"{transport}/{workload}/{payload}B/c{conc}";
                var samples = new List<CellSample>();

                // One fresh client/server process pair per cell.
                //
                // Payloads at and above 85 KB land on the Large Object Heap,
                // which Server GC collects only on gen2 and never compacts by
                // default. On a 64 GB machine nothing creates the memory
                // pressure that would force that collection, so a single
                // long-lived process accumulates tens of GB of dead LOH
                // buffers: two full-matrix attempts were OOM killed, the
                // second at 43 GB RSS by the 256 KiB cells. The server runs in
                // its own process, so collecting in the client alone cannot
                // fix it - the pair has to be recycled.
                //
                // This also makes rss_peak_mb a per-cell number instead of the
                // running high-water mark of every cell measured before it.
                ReclaimHeap();
                BenchEnv? env = null;
                try
                {
                    env = await startEnv(transport).ConfigureAwait(false);

                    for (int rep = 0; rep < opt.Repetitions; rep++)
                    {
                        // A hung cell must not kill a multi-hour matrix run.
                        // Budget generously (3x nominal + 120 s) so only a
                        // genuine stall trips it. The remaining repetitions of
                        // this cell are abandoned; the finally below disposes
                        // the process pair, so the next cell starts clean
                        // regardless of what the stall left wedged.
                        var budget = TimeSpan.FromSeconds(
                            (opt.WarmupSeconds + opt.DurationSeconds) * 3 + 120);
                        using var cts = new CancellationTokenSource(budget);
                        try
                        {
                            var s = await MeasureCellAsync(
                                env, workload, payload, conc,
                                opt.WarmupSeconds, opt.DurationSeconds, cts.Token)
                                .ConfigureAwait(false);
                            samples.Add(s);
                            Console.WriteLine(
                                $"  {cellId} rep{rep + 1}: p50={s.P50Us:F2}us p99={s.P99Us:F2}us " +
                                $"{s.ThroughputMBps:F1}MB/s ops={s.Ops}");
                        }
                        catch (Exception ex)
                        {
                            string why = ex is OperationCanceledException
                                ? $"timed out after {budget.TotalSeconds:F0}s"
                                : ex.GetType().Name + ": " + ex.Message;
                            Console.WriteLine($"  {cellId} rep{rep + 1}: FAILED ({why})");
                            skipped.Add($"{cellId} rep{rep + 1}: {why}");
                            break; // abandon remaining reps for this cell
                        }
                    }

                }
                finally
                {
                    if (env != null)
                    {
                        try { await env.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception ex) { Console.WriteLine($"  env dispose failed: {ex.Message}"); }
                    }
                }

                if (samples.Count > 0)
                    runs.Add(BuildRun(workload, transport, payload, conc, opt, samples));
                Checkpoint();
            }
        }

        meta["runs"] = runs;
        if (skipped.Count > 0) meta["skipped"] = skipped;
        meta["complete"] = true;
        WriteResults(meta, opt.OutPath);

        Console.WriteLine();
        Console.WriteLine($"Wrote {runs.Count} cells to {Path.GetFullPath(opt.OutPath)}");
        if (skipped.Count > 0)
            Console.WriteLine($"{skipped.Count} cell-repetition(s) failed; see \"skipped\" in results.json");
    }

    /// <summary>
    /// Forces a blocking, compacting gen2 collection and returns the freed
    /// pages to the OS. Called only between cells, never inside a measured
    /// window, so it cannot flatter or penalise any transport.
    /// </summary>
    static void ReclaimHeap()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    static void WriteResults(Dictionary<string, object?> meta, string outPath)
    {
        var full = Path.GetFullPath(outPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, full, overwrite: true);
    }

    // ------------------------------------------------------------------
    // Cell measurement
    // ------------------------------------------------------------------

    static async Task<CellSample> MeasureCellAsync(
        BenchEnv env, string workload, int payloadBytes, int concurrency,
        double warmupS, double durationS, CancellationToken ct)
    {
        var client = env.Client;
        var req = new SimpleRequest
        {
            ResponseSize = payloadBytes,
            Payload = MatrixPayload.Make(payloadBytes),
        };

        var rss = new RssSampler(env.ServerProcess);

        if (string.Equals(workload, "unary", StringComparison.Ordinal))
        {
            // Warmup: same shape as the measured phase, nothing recorded.
            await RunUnaryPhaseAsync(client, req, concurrency, warmupS, record: false, ct)
                .ConfigureAwait(false);

            double cpuBefore = CaptureCpuUs(env.ServerProcess);
            rss.Start();
            var sw = Stopwatch.StartNew();
            var states = await RunUnaryPhaseAsync(client, req, concurrency, durationS, record: true, ct)
                .ConfigureAwait(false);
            sw.Stop();
            rss.Stop();
            double cpuAfter = CaptureCpuUs(env.ServerProcess);

            return Summarize(states, sw.Elapsed, payloadBytes, cpuAfter - cpuBefore, rss.PeakMb);
        }

        if (string.Equals(workload, "streaming", StringComparison.Ordinal))
        {
            // Streams are opened once and span warmup + measurement so the
            // measured phase never pays stream-establishment cost.
            var calls = new List<Grpc.Core.AsyncDuplexStreamingCall<SimpleRequest, SimpleResponse>>();
            try
            {
                for (int i = 0; i < concurrency; i++) calls.Add(client.StreamingCall());

                await RunStreamingPhaseAsync(calls, req, warmupS, record: false, ct)
                    .ConfigureAwait(false);

                double cpuBefore = CaptureCpuUs(env.ServerProcess);
                rss.Start();
                var sw = Stopwatch.StartNew();
                var states = await RunStreamingPhaseAsync(calls, req, durationS, record: true, ct)
                    .ConfigureAwait(false);
                sw.Stop();
                rss.Stop();
                double cpuAfter = CaptureCpuUs(env.ServerProcess);

                return Summarize(states, sw.Elapsed, payloadBytes, cpuAfter - cpuBefore, rss.PeakMb);
            }
            finally
            {
                foreach (var c in calls)
                {
                    try { await c.RequestStream.CompleteAsync().ConfigureAwait(false); }
                    catch { /* best effort */ }
                    try { c.Dispose(); }
                    catch { /* best effort */ }
                }
            }
        }

        throw new ArgumentException($"Unknown workload '{workload}'", nameof(workload));
    }

    static async Task<WorkerState[]> RunUnaryPhaseAsync(
        BenchmarkService.BenchmarkServiceClient client, SimpleRequest req,
        int concurrency, double seconds, bool record, CancellationToken ct)
    {
        var states = new WorkerState[concurrency];
        for (int i = 0; i < concurrency; i++) states[i] = new WorkerState();
        long deadline = NowTicks() + (long)(seconds * Stopwatch.Frequency);

        var tasks = new Task[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            var st = states[i];
            tasks[i] = Task.Run(async () =>
            {
                while (NowTicks() < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    long t0 = NowTicks();
                    using var call = client.UnaryCallAsync(req);
                    await call.ResponseAsync.ConfigureAwait(false);
                    long t1 = NowTicks();
                    if (record) st.Hist.Record(TicksToNs(t1 - t0));
                    st.Ops++;
                }
            }, ct);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return states;
    }

    static async Task<WorkerState[]> RunStreamingPhaseAsync(
        List<Grpc.Core.AsyncDuplexStreamingCall<SimpleRequest, SimpleResponse>> calls,
        SimpleRequest req, double seconds, bool record, CancellationToken ct)
    {
        int concurrency = calls.Count;
        var states = new WorkerState[concurrency];
        for (int i = 0; i < concurrency; i++) states[i] = new WorkerState();
        long deadline = NowTicks() + (long)(seconds * Stopwatch.Frequency);

        var tasks = new Task[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            var st = states[i];
            var call = calls[i];
            tasks[i] = Task.Run(async () =>
            {
                while (NowTicks() < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    long t0 = NowTicks();
                    await call.RequestStream.WriteAsync(req).ConfigureAwait(false);
                    if (!await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
                        throw new InvalidOperationException("stream ended early");
                    long t1 = NowTicks();
                    if (record) st.Hist.Record(TicksToNs(t1 - t0));
                    st.Ops++;
                }
            }, ct);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return states;
    }

    static CellSample Summarize(
        WorkerState[] states, TimeSpan elapsed, int payloadBytes,
        double cpuDeltaUs, double rssPeakMb)
    {
        var merged = new LatencyHistogram();
        long ops = 0;
        foreach (var st in states)
        {
            merged.MergeFrom(st.Hist);
            ops += st.Ops;
        }

        double secs = elapsed.TotalSeconds;
        // Single direction: payload bytes per completed op. See the file
        // header note on throughput_counts_both_directions.
        double mbps = secs > 0 ? (double)ops * payloadBytes / (1024.0 * 1024.0) / secs : 0;
        double cpuPerOp = ops > 0 ? cpuDeltaUs / ops : 0;

        return new CellSample(
            merged.PercentileUs(0.50),
            merged.PercentileUs(0.90),
            merged.PercentileUs(0.99),
            merged.MeanUs,
            mbps,
            cpuPerOp,
            rssPeakMb,
            ops);
    }

    // ------------------------------------------------------------------
    // Aggregation + JSON
    // ------------------------------------------------------------------

    static double Median(IEnumerable<double> values)
    {
        var a = values.ToArray();
        Array.Sort(a);
        if (a.Length == 0) return 0;
        return a.Length % 2 == 1
            ? a[a.Length / 2]
            : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2.0;
    }

    static Dictionary<string, object?> BuildRun(
        string workload, string transport, int payload, int conc,
        MatrixOptions opt, List<CellSample> samples)
    {
        var (ring, window, frame) = TransportShape(transport);
        return new Dictionary<string, object?>
        {
            ["workload"] = workload,
            ["transport"] = transport,
            ["payload_bytes"] = payload,
            ["concurrency"] = conc,
            ["warmup_s"] = opt.WarmupSeconds,
            ["duration_s"] = opt.DurationSeconds,
            ["repetitions"] = samples.Count,
            ["latency_us"] = new Dictionary<string, object?>
            {
                ["p50"] = Round3(Median(samples.Select(s => s.P50Us))),
                ["p90"] = Round3(Median(samples.Select(s => s.P90Us))),
                ["p99"] = Round3(Median(samples.Select(s => s.P99Us))),
                ["mean"] = Round3(Median(samples.Select(s => s.MeanUs))),
            },
            ["throughput_mb_per_s"] = Round3(Median(samples.Select(s => s.ThroughputMBps))),
            ["throughput_counts_both_directions"] = false,
            ["cpu_us_per_op"] = Round3(Median(samples.Select(s => s.CpuUsPerOp))),
            ["rss_peak_mb"] = Round3(Median(samples.Select(s => s.RssPeakMb))),
            ["ring_bytes"] = ring,
            ["stream_window_bytes"] = window,
            ["max_frame_bytes"] = frame,
            ["ops_total"] = samples.Sum(s => s.Ops),
        };
    }

    static double Round3(double v) => Math.Round(v, 3);

    /// <summary>
    /// Flow-control / buffer shape actually in effect for a transport, so the
    /// gRFC reader can tell a fair-arm cell from a tuned-arm one without
    /// reverse-engineering the flags.
    /// </summary>
    static (long? Ring, long? Window, long? Frame) TransportShape(string transport)
    {
        if (string.Equals(transport, "shm", StringComparison.Ordinal))
        {
            long ring = (long)ShmBenchConfig.RingCapacityBytes;
            long window = ShmConstants.InitialWindowSize;
            // FairMaxFramePayload is int.MaxValue when unset; the writer then
            // clamps to the protocol max and the ring geometry. Report the
            // value that actually binds.
            long frame = ShmConstants.FairMaxFramePayload == int.MaxValue
                ? Math.Min(128L * 1024 * 1024, ring / 3)
                : ShmConstants.FairMaxFramePayload;
            return (ring, window, frame);
        }

        // UDS / TCP ride Kestrel + SocketsHttpHandler. There is no ring, and
        // grpc-dotnet exposes no knob for SETTINGS_MAX_FRAME_SIZE, so the H2
        // spec default of 16 KiB always binds.
        long? udsWindow = ShmBenchConfig.WindowOverrideBytes;
        return (null, udsWindow, 16384);
    }

    static Dictionary<string, object?> BuildMetadata(MatrixOptions opt)
    {
        var (commit, dirty) = ResolveCommit(opt);
        return new Dictionary<string, object?>
        {
            ["schema"] = "grfc-g3-bench-v1",
            ["lang"] = "dotnet",
            ["commit"] = commit,
            ["dirty"] = dirty,
            ["toolchain"] = $".NET {Environment.Version} / {RuntimeInformation.FrameworkDescription}",
            ["os"] = OsLabel(),
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch
            {
                "x64" => "amd64",
                "arm64" => "arm64",
                var other => other,
            },
            ["cpu"] = CpuName(),
            ["cores"] = PhysicalCores(),
            ["threads"] = Environment.ProcessorCount,
            ["arm"] = ShmBenchConfig.FairMode ? "fair" : "tuned",
            ["process_model"] = "separate",
        };
    }

    static (string Commit, bool Dirty) ResolveCommit(MatrixOptions opt)
    {
        if (!string.IsNullOrWhiteSpace(opt.CommitOverride))
            return (opt.CommitOverride!, false);

        string sha = Git("rev-parse HEAD").Trim();
        string status = Git("status --porcelain --untracked-files=no").Trim();
        return (sha.Length == 40 ? sha : "unknown", status.Length > 0);
    }

    static string Git(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return o;
        }
        catch { return ""; }
    }

    static string OsLabel()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsLinux())
        {
            // WSL2 is not bare metal and must not be silently compared against
            // a native Linux box; label it so the gRFC table can caveat it.
            try
            {
                string v = File.Exists("/proc/version") ? File.ReadAllText("/proc/version") : "";
                if (v.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("WSL", StringComparison.OrdinalIgnoreCase))
                    return "linux-wsl2";
            }
            catch { }
            return "linux";
        }
        return RuntimeInformation.OSDescription;
    }

    static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                        return line.Split(':', 2)[1].Trim();
        }
        catch { }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }

    static int PhysicalCores()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                string phys = "", core = "";
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("physical id", StringComparison.Ordinal))
                        phys = line.Split(':', 2)[1].Trim();
                    else if (line.StartsWith("core id", StringComparison.Ordinal))
                        core = line.Split(':', 2)[1].Trim();
                    else if (line.Length == 0 && core.Length > 0)
                    { ids.Add(phys + "/" + core); phys = ""; core = ""; }
                }
                if (ids.Count > 0) return ids.Count;
            }
        }
        catch { }
        // Fall back to the logical count; `threads` carries the same number
        // and the consumer can tell they were not distinguished.
        return Environment.ProcessorCount;
    }

    static double CaptureCpuUs(Process? serverProcess)
    {
        double us = Process.GetCurrentProcess().TotalProcessorTime.TotalMicroseconds;
        if (serverProcess != null && !serverProcess.HasExited)
        {
            try
            {
                serverProcess.Refresh();
                us += serverProcess.TotalProcessorTime.TotalMicroseconds;
            }
            catch { /* raced with exit */ }
        }
        return us;
    }

    /// <summary>
    /// Samples client+server resident set during a cell. Process.PeakWorkingSet64
    /// is a process-lifetime peak, which would leak the largest earlier cell into
    /// every later one, so we poll instead.
    /// </summary>
    sealed class RssSampler
    {
        readonly Process? _server;
        readonly Process _self = Process.GetCurrentProcess();
        CancellationTokenSource? _cts;
        Task? _loop;
        long _peakBytes;

        public RssSampler(Process? server) => _server = server;

        public double PeakMb => _peakBytes / (1024.0 * 1024.0);

        public void Start()
        {
            _peakBytes = 0;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    Sample();
                    try { await Task.Delay(250, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                Sample();
            }, CancellationToken.None);
        }

        void Sample()
        {
            try
            {
                _self.Refresh();
                long total = _self.WorkingSet64;
                if (_server != null && !_server.HasExited)
                {
                    _server.Refresh();
                    total += _server.WorkingSet64;
                }
                if (total > _peakBytes) _peakBytes = total;
            }
            catch { /* best effort */ }
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _loop?.Wait(2000);
            }
            catch { /* best effort */ }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }
    }
}

/// <summary>Payload cache for the matrix path (one buffer per distinct size).</summary>
static class MatrixPayload
{
    static readonly Dictionary<int, Payload> s_cache = [];
    static readonly object s_lock = new();

    public static Payload Make(int size)
    {
        if (size <= 0) return new Payload();
        lock (s_lock)
        {
            if (s_cache.TryGetValue(size, out var p)) return p;
            p = new Payload { Body = Google.Protobuf.UnsafeByteOperations.UnsafeWrap(new byte[size]) };
            s_cache[size] = p;
            return p;
        }
    }
}
