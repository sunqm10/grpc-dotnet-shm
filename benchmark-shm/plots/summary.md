# gRPC .NET Shared Memory Benchmark Results

Generated: 2026-02-05 04:00:39

## Summary

| Transport | Workload | Size | Concurrency | Ops/s | MB/s | P50 (µs) | P99 (µs) |
|-----------|----------|------|-------------|-------|------|----------|----------|
| TCP | streaming | 64B | 1 | 9,334 | 1.1 | 9289 | 28726 |
| TCP | streaming | 1KB | 1 | 8,814 | 17.2 | 9752 | 35841 |
| TCP | streaming | 4KB | 1 | 7,150 | 55.9 | 12060 | 52933 |
| TCP | unary | 64B | 1 | 851 | 0.1 | 73670 | 634813 |
| TCP | unary | 1KB | 1 | 1,289 | 2.5 | 31834 | 589722 |
| TCP | unary | 4KB | 1 | 1,305 | 10.2 | 29373 | 430585 |

## Speedup (SHM vs TCP)

| Workload | Size | Concurrency | Throughput Speedup | Latency Improvement |
|----------|------|-------------|-------------------|---------------------|
