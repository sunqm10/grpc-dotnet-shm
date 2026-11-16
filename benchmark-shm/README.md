# gRPC .NET Shared Memory Benchmarks

Benchmarks comparing gRPC .NET over **shared memory (SHM)** against **Unix domain
sockets (UDS)** and **TCP** loopback.

UDS is the baseline. It is the transport gRFC G3 is actually arguing against —
it is the fastest widely-deployed local IPC option for gRPC today, and it shares
the HTTP/2 framing and Kestrel server stack with the SHM path. TCP loopback is
reported as a supplementary series only.

## What is actually here

| Path | Status | What it is |
|---|---|---|
| `ringbench/` | **live** | The benchmark suite. `RingBench.cs` (legacy sweep) + `Matrix.cs` (gRFC G3 contract sweep). |
| `benchmark_runner.py` | **live** | Drives the matrix run and generates all plots. |
| `compare_results.py`, `analyze_speedup.py`, `compare_runs.py` | **live** | Post-hoc analysis over `results.json`. |
| `out/` | **live** | Results and plots, one subdirectory per platform / run. |
| `client/`, `server/` | **dead** | A superseded two-project harness. Not built, not referenced, no test coverage. Do not revive. |
| `run_benchmarks.sh` | **dead** | Driver for the dead harness. |

The client and the server are always **separate OS processes**: `ringbench`
re-executes its own assembly with `--server` and talks to that child over the
transport under test.

Only `src/Grpc.Net.SharedMemory` is referenced. It provides both the client
(`ShmControlHandler`) and the standalone server (`ShmGrpcServer`). This is the
only SHM path with test and benchmark coverage.
`src/Grpc.AspNetCore.Server.SharedMemory` is **not** used here and currently
does not compile.

## Two ways to run

### 1. gRFC G3 contract matrix (use this for gRFC numbers)

Time-based, with latency percentiles, concurrency as a first-class axis, and a
cross-language JSON schema shared with the grpc-go side.

```bash
dotnet run --project ringbench/RingBench.csproj -c Release -- \
  --matrix \
  --matrix-transports shm,uds \
  --matrix-workloads unary,streaming \
  --matrix-payloads 1,1024,16384,65536,262144,1048576,4194304 \
  --matrix-concurrency 1,8,64 \
  --matrix-warmup-s 5 --matrix-duration-s 30 --matrix-reps 3 \
  --matrix-out out/grfc-g3/windows/tuned/results.json
```

Add `--fair` for the fair arm (see *Config arms* below). Every cell is run
`--matrix-reps` times and the **median** of each metric is reported.

| Flag | Meaning | Default |
|---|---|---|
| `--matrix` | Run the contract sweep instead of the legacy sweep | off |
| `--matrix-transports` | `shm`, `uds`, `tcp` (comma-separated) | `shm,uds` |
| `--matrix-workloads` | `unary`, `streaming` | both |
| `--matrix-payloads` | Payload sizes in bytes | 1 B … 4 MiB |
| `--matrix-concurrency` | In-flight calls (unary) / concurrent streams (streaming) | `1,8,64` |
| `--matrix-warmup-s` | Warmup seconds per cell, not recorded | 5 |
| `--matrix-duration-s` | Measured seconds per cell | 30 |
| `--matrix-reps` | Repetitions per cell | 3 |
| `--matrix-out` | Path of the `results.json` to write | `results.json` |
| `--matrix-commit` | Override the recorded commit SHA | from `git` |

A cell that stalls is abandoned after `3 x (warmup + duration) + 120 s`, the
transport environment is rebuilt, and the failure is recorded in the `skipped`
array of `results.json` rather than killing a multi-hour run.

### 2. Legacy sweep

The original iteration-count sweep (`unary` / `streaming` / `--concurrent`).
Still useful for quick A/B work on a single transport; it has **no percentiles
and no time-based budget**, so it is not what gRFC numbers should come from.

```bash
dotnet run --project ringbench/RingBench.csproj -c Release -- --only shm --sizes 65536
```

## Output schema (`grfc-g3-bench-v1`)

```json
{"schema":"grfc-g3-bench-v1","lang":"dotnet",
 "commit":"<40-hex>","dirty":false,
 "toolchain":"...","os":"windows|linux|linux-wsl2","arch":"amd64",
 "cpu":"...","cores":16,"threads":16,
 "arm":"fair|tuned","process_model":"separate",
 "runs":[{"workload":"unary","transport":"shm","payload_bytes":1024,"concurrency":8,
          "warmup_s":5,"duration_s":30,"repetitions":3,
          "latency_us":{"p50":0,"p90":0,"p99":0,"mean":0},
          "throughput_mb_per_s":0,"throughput_counts_both_directions":false,
          "cpu_us_per_op":0,"rss_peak_mb":0,
          "ring_bytes":67108864,"stream_window_bytes":65535,"max_frame_bytes":16384}]}
```

Notes on fields that are easy to misread:

- **`throughput_mb_per_s` counts one direction**: `payload_bytes x ops / elapsed`.
  `throughput_counts_both_directions` is emitted as `false` to say so. The legacy
  sweep instead doubles the byte count (request + response); the two numbers
  differ by 2x by construction.
- **`cpu_us_per_op`** is client CPU **plus** server-process CPU, divided by ops.
- **`rss_peak_mb`** is the peak of (client RSS + server RSS) sampled at 250 ms
  during the measured phase. It is deliberately *not* `PeakWorkingSet64`, which
  is a process-lifetime peak and would leak the largest earlier cell into every
  later one.
- **`ring_bytes` / `stream_window_bytes` / `max_frame_bytes`** record the
  flow-control shape actually in effect, so a fair-arm cell can be told from a
  tuned-arm one without reverse-engineering the flags. `ring_bytes` is `null`
  for UDS/TCP, which have no ring.
- **`os`** reports `linux-wsl2` rather than `linux` when `/proc/version` shows
  WSL. WSL2 is not bare metal and must not be silently compared against a native
  Linux box.

## Config arms

Both arms are reported, because neither alone is honest on its own.

| | `tuned` (default) | `fair` (`--fair`) |
|---|---|---|
| SHM initial window | 32 MiB | 65535 B |
| SHM max frame payload | ring-bound (~21 MiB) | 16384 B |
| UDS/TCP initial window | Kestrel default (128 MiB) | 65535 B |
| Pooled deserialization | on | off |

`fair` pins both sides to the HTTP/2 spec defaults (RFC 7540 §6.9.2) so the
comparison reflects wire and wake-up cost rather than SHM-only buffer tuning.
`tuned` is what each transport does out of the box. UDS/TCP frame size stays at
the 16 KiB spec default in both arms because grpc-dotnet exposes no knob for
`SETTINGS_MAX_FRAME_SIZE`.

## Plots and analysis

```bash
python -I benchmark_runner.py --plot-only --results out/grfc-g3/windows/tuned/results.json
python -I analyze_speedup.py out/grfc-g3/windows/tuned/results.json
python -I compare_results.py OLD.json NEW.json
python -I compare_runs.py A.json B.json C.json
```

All ratios are relative to the UDS baseline: latency speedup is
`uds_latency / transport_latency` (>1 means the transport wins). A cell missing
from either side is **dropped, not substituted** — panels show `No data` or
`n/a` rather than a fabricated 1.0x.

## Requirements

- .NET 10 SDK (`global.json` pins 10.0.103+, `rollForward: latestFeature`)
- Python 3 with `matplotlib`

The SHM library picks its platform code path from the **build host OS**, not the
RID (`Grpc.Net.SharedMemory.csproj` `DefineConstants`). You must build on the OS
you intend to measure on, or the run throws `PlatformNotSupportedException`.

## Segment hygiene

Every SHM run must remove **both** `<name>` and `<name>_ctl`; forgetting the
control segment leaks it. `ringbench` sweeps `bench_shm_*` at startup and
removes both segments on environment teardown, but after a hard kill you may
need to clean up manually:

```bash
ls /dev/shm | grep bench_shm_     # Linux
```

On Windows, segments are backed by the pagefile and disappear when the last
handle closes, so a leak only survives as long as a stuck process does.

## Known issues

- **1000 concurrent streams abort on Linux** (`EXIT 134`). Not reproduced on the
  current branch, and the original failure was on an abandoned experimental
  commit. The contract matrix tops out at concurrency 64, which stays under the
  100-stream-per-connection limit and therefore never exercises the
  connection-pool expansion and segment-unmap path the abort is believed to
  involve. Investigate before raising concurrency past 100.
- `src/Grpc.AspNetCore.Server.SharedMemory` does not compile
  (`ShmConnectionListenerAdapter.cs` calls `ControlWire.EncodeConnectResponse`
  with one argument; the declaration takes two). The benchmarks do not use it.
