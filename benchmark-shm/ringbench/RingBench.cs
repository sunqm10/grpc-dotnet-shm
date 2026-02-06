// Standalone ring buffer benchmark - comparable to Go's ShmRingWriteRead/ShmRingRoundtrip
// Uses a small ring (4MB) to fit in /dev/shm constraints of this container

using System.Diagnostics;
using Grpc.Net.SharedMemory;

sealed class RingBench
{
    static void Main(string[] args)
    {
        // Clean up any stale segments
        foreach (var f in Directory.GetFiles("/dev/shm/", "grpc_shm_ringbench*"))
            File.Delete(f);

        // Use 4MB ring to fit in 64MB /dev/shm (total ~8MB mapped)
        uint ringCapacity = 4 * 1024 * 1024;
        var segmentName = "ringbench";

        Console.WriteLine($"CPU: {GetCpuInfo()}");
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Ring capacity: {ringCapacity / 1024 / 1024} MB");
        Console.WriteLine();

        var segment = Segment.Create(segmentName, ringCapacity);
        try
        {
            // === WriteRead (one-way streaming) - comparable to Go's BenchmarkShmRingWriteRead ===
            int[] sizes = { 64, 256, 1024, 4096, 16384, 65536, 262144, 1048576 };
            Console.WriteLine("=== Ring Buffer Write+Read (one-way, comparable to Go ShmRingWriteRead) ===");
            Console.WriteLine($"{"Size",-10} {"Ops",-12} {"ns/op",-15} {"MB/s",-15}");
            Console.WriteLine(new string('-', 52));

            foreach (var size in sizes)
            {
                if (size > ringCapacity / 2) continue; // skip sizes that don't fit
                var (ops, nsPerOp, mbps) = BenchWriteRead(segment.RingA, size);
                Console.WriteLine($"{FormatSize(size),-10} {ops,-12} {nsPerOp,-15:F1} {mbps,-15:F2}");
            }

            Console.WriteLine();

            // === Roundtrip - comparable to Go's BenchmarkShmRingRoundtrip ===
            // Note: Go roundtrip tests write to RingA, read from RingB (2 separate rings, 2 threads)
            // Here, we simulate with single-threaded write+read on same ring (analogous to single-direction roundtrip)
            Console.WriteLine("=== Ring Buffer Roundtrip (write+read per op, comparable to Go ShmRingRoundtrip) ===");
            Console.WriteLine($"{"Size",-10} {"Ops",-12} {"ns/op",-15} {"MB/s",-15}");
            Console.WriteLine(new string('-', 52));

            int[] rtSizes = { 64, 256, 1024, 4096 };
            foreach (var size in rtSizes)
            {
                var (ops, nsPerOp, mbps) = BenchRoundtrip(segment.RingA, size);
                Console.WriteLine($"{FormatSize(size),-10} {ops,-12} {nsPerOp,-15:F1} {mbps,-15:F2}");
            }

            Console.WriteLine();

            // === Large payload throughput ===
            Console.WriteLine("=== Large Payload Throughput (comparable to Go ShmRingLargePayloads) ===");
            Console.WriteLine($"{"Size",-10} {"Ops",-12} {"ns/op",-15} {"MB/s",-15}");
            Console.WriteLine(new string('-', 52));

            int[] largeSizes = { 65536, 262144, 1048576 };
            foreach (var size in largeSizes)
            {
                if (size > ringCapacity / 2) continue;
                var (ops, nsPerOp, mbps) = BenchThroughput(segment.RingA, size);
                Console.WriteLine($"{FormatSize(size),-10} {ops,-12} {nsPerOp,-15:F1} {mbps,-15:F2}");
            }
        }
        finally
        {
            segment.Dispose();
            foreach (var f in Directory.GetFiles("/dev/shm/", "grpc_shm_ringbench*"))
                File.Delete(f);
        }
    }

    static (long ops, double nsPerOp, double mbps) BenchWriteRead(ShmRing ring, int size)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var readBuf = new byte[size];

        // Warmup
        for (int i = 0; i < 1000; i++)
        {
            ring.Write(payload);
            ring.Read(readBuf);
        }

        // Determine iteration count (target ~2 seconds)
        long targetNs = 2_000_000_000;
        var sw = Stopwatch.StartNew();
        int calibrationOps = 0;
        while (sw.ElapsedMilliseconds < 100)
        {
            ring.Write(payload);
            ring.Read(readBuf);
            calibrationOps++;
        }
        sw.Stop();
        double nsPerCalibOp = (double)sw.ElapsedTicks / calibrationOps * 1_000_000_000.0 / Stopwatch.Frequency;
        long iterations = Math.Max(1000, (long)(targetNs / nsPerCalibOp));

        // Run 3 times and take best
        double bestNs = double.MaxValue;
        long bestOps = 0;
        for (int run = 0; run < 3; run++)
        {
            sw.Restart();
            for (long i = 0; i < iterations; i++)
            {
                ring.Write(payload);
                ring.Read(readBuf);
            }
            sw.Stop();
            double ns = (double)sw.ElapsedTicks / iterations * 1_000_000_000.0 / Stopwatch.Frequency;
            if (ns < bestNs)
            {
                bestNs = ns;
                bestOps = iterations;
            }
        }

        double mbps = size / bestNs * 1000.0; // MB/s
        return (bestOps, bestNs, mbps);
    }

    static (long ops, double nsPerOp, double mbps) BenchRoundtrip(ShmRing ring, int size)
    {
        // Roundtrip = write + read as a single operation (measures latency)
        return BenchWriteRead(ring, size);
    }

    static (long ops, double nsPerOp, double mbps) BenchThroughput(ShmRing ring, int size)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var readBuf = new byte[size];

        // Warmup
        for (int i = 0; i < 100; i++)
        {
            ring.Write(payload);
            ring.Read(readBuf);
        }

        // Target ~2 seconds
        var sw = Stopwatch.StartNew();
        int calibrationOps = 0;
        while (sw.ElapsedMilliseconds < 200)
        {
            ring.Write(payload);
            ring.Read(readBuf);
            calibrationOps++;
        }
        sw.Stop();
        double nsPerCalibOp = (double)sw.ElapsedTicks / calibrationOps * 1_000_000_000.0 / Stopwatch.Frequency;
        long iterations = Math.Max(100, (long)(2_000_000_000 / nsPerCalibOp));

        // Batch write then batch read for max throughput
        double bestNs = double.MaxValue;
        long bestOps = 0;
        int batchSize = Math.Min(16, (int)(ring.Capacity / (uint)size / 2));
        if (batchSize < 1) batchSize = 1;

        for (int run = 0; run < 3; run++)
        {
            long totalOps = 0;
            sw.Restart();
            while (totalOps < iterations)
            {
                int batch = (int)Math.Min(batchSize, iterations - totalOps);
                for (int i = 0; i < batch; i++)
                    ring.Write(payload);
                for (int i = 0; i < batch; i++)
                    ring.Read(readBuf);
                totalOps += batch;
            }
            sw.Stop();
            double ns = (double)sw.ElapsedTicks / totalOps * 1_000_000_000.0 / Stopwatch.Frequency;
            if (ns < bestNs)
            {
                bestNs = ns;
                bestOps = totalOps;
            }
        }

        double mbps = size / bestNs * 1000.0;
        return (bestOps, bestNs, mbps);
    }

    static string FormatSize(int bytes)
    {
        if (bytes >= 1048576) return $"{bytes / 1048576}MB";
        if (bytes >= 1024) return $"{bytes / 1024}KB";
        return $"{bytes}B";
    }

    static string GetCpuInfo()
    {
        try
        {
            var lines = File.ReadAllLines("/proc/cpuinfo");
            foreach (var line in lines)
                if (line.StartsWith("model name"))
                    return line.Split(':')[1].Trim();
        }
        catch { }
        return "Unknown";
    }
}
