# gRPC .NET Shared Memory Benchmark Results

Generated: 2026-02-06 05:31:39

## Summary

| Transport | Workload | Size | Concurrency | Ops/s | MB/s | P50 (µs) | P99 (µs) |
|-----------|----------|------|-------------|-------|------|----------|----------|
| SHM | streaming | 64B | 1 | 0 | 0.0 | 0 | 0 |
| SHM | streaming | 1KB | 1 | 0 | 0.0 | 0 | 0 |
| SHM | streaming | 4KB | 1 | 0 | 0.0 | 0 | 0 |
| SHM | unary | 64B | 1 | 0 | 0.0 | 0 | 0 |
| SHM | unary | 1KB | 1 | 0 | 0.0 | 0 | 0 |
| SHM | unary | 4KB | 1 | 0 | 0.0 | 0 | 0 |
| TCP | streaming | 64B | 1 | 9,621 | 1.2 | 9029 | 27350 |
| TCP | streaming | 1KB | 1 | 8,944 | 17.5 | 9715 | 32329 |
| TCP | streaming | 4KB | 1 | 7,710 | 60.2 | 11608 | 31790 |
| TCP | unary | 64B | 1 | 849 | 0.1 | 32692 | 1031567 |
| TCP | unary | 1KB | 1 | 35 | 0.1 | 25486 | 367606 |
| TCP | unary | 4KB | 1 | 3 | 0.0 | 34044 | 623008 |

## Speedup (SHM vs TCP)

| Workload | Size | Concurrency | Throughput Speedup | Latency Improvement |
|----------|------|-------------|-------------------|---------------------|
| unary | 64B | 1 | 0.00x | 0.00x |
| unary | 1KB | 1 | 0.00x | 0.00x |
| unary | 4KB | 1 | 0.00x | 0.00x |
| streaming | 64B | 1 | 0.00x | 0.00x |
| streaming | 1KB | 1 | 0.00x | 0.00x |
| streaming | 4KB | 1 | 0.00x | 0.00x |
