#!/bin/bash
# Ring-size sweep: H2 throughput vs ring capacity.
#
# Runs the SHM-H2 benchmark across a range of ring sizes and a
# representative payload set, capturing throughput + iteration count
# into per-config result JSON files. Useful for confirming that
# Phase X chain-ZC doesn't depend on the historical 64 MiB ring and
# for picking a smaller default that keeps tail latency competitive
# while reducing per-connection memory cost.
#
# Usage:
#   bash benchmark-shm/ring-size-sweep.sh [output_dir]
#
# Defaults output_dir to /tmp/ring-sweep. Each run produces:
#   <output_dir>/ring_<bytes>/results.json   (transport=shm-h2 only)
#   <output_dir>/summary.csv                  (size, ring_bytes, iters,
#                                              avg_us, throughput_mb_s,
#                                              streaming_throughput)
#
# Set ENV RINGBENCH_REPEAT to run each (ring,size) cell N times and
# emit the median; defaults to 1 (single run per cell). With the
# default size set the sweep takes ~5 minutes at REPEAT=1 on a 16-core
# Linux Xeon, ~25 minutes at REPEAT=5.
#
# Prerequisites:
#   - dotnet 10 SDK on PATH
#   - benchmark-shm/ringbench/bin/Release/net10.0/RingBench.dll built
#   - jq for JSON parsing in the summary step

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT_DIR="${1:-/tmp/ring-sweep}"
BENCH_DLL="$REPO_ROOT/benchmark-shm/ringbench/bin/Release/net10.0/RingBench.dll"
REPEAT="${RINGBENCH_REPEAT:-1}"

# Sizes spanning small (single-frame fast path) through worst-case
# message-size-vastly-exceeds-ring-capacity (256 MB on a 4 MiB ring
# stresses the chunk-emit slow path with deep chunking — the consumer
# must drain the ring fast enough to keep the writer from stalling on
# WaitForSpace). 64KB / 1MB stresses single-frame ZC; 16MB stresses
# multi-DATA H2 chunking and tests Phase X chain-ZC; 64MB / 256MB
# exceeds most ring caps and exercises the per-frame-copy fallback
# under sustained pressure.
PAYLOAD_SIZES="65536,1048576,16777216,67108864,268435456"

# Ring capacities: 1MiB is the ZC adaptive-threshold floor — anything
# below disables ZC entirely. 64MiB is today's default. 256MiB explores
# whether more headroom on chain-ZC helps the largest messages.
RING_CAPS=(
    1048576       # 1 MiB  — ZC threshold floor
    4194304       # 4 MiB
    8388608       # 8 MiB
    16777216      # 16 MiB
    33554432      # 32 MiB
    67108864      # 64 MiB — current default
    134217728     # 128 MiB
    268435456     # 256 MiB
)

if [ ! -f "$BENCH_DLL" ]; then
    echo "Bench binary not found: $BENCH_DLL" >&2
    echo "Run: dotnet build -c Release benchmark-shm/ringbench/RingBench.csproj" >&2
    exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
    echo "jq not found — install with: sudo apt-get install -y jq" >&2
    exit 1
fi

mkdir -p "$OUT_DIR"
echo "Sweep output: $OUT_DIR"
echo "Ring caps:    ${RING_CAPS[*]}"
echo "Payloads:     $PAYLOAD_SIZES"
echo "Repeat:       $REPEAT per cell"
echo

SUMMARY="$OUT_DIR/summary.csv"
echo "ring_bytes,ring_label,size_bytes,iterations,unary_avg_us,unary_throughput_mb_s,streaming_avg_us,streaming_throughput_mb_s,run" > "$SUMMARY"

run_one() {
    local ring_bytes=$1
    local run_idx=$2
    local cell_dir="$OUT_DIR/ring_${ring_bytes}/run_${run_idx}"
    mkdir -p "$cell_dir"

    # Clean any stale segments from a prior aborted run (best effort).
    rm -f /dev/shm/bench_shm_* 2>/dev/null || true

    echo "  ring=${ring_bytes} run=${run_idx} ..."
    RINGBENCH_RING_BYTES="$ring_bytes" \
        dotnet "$BENCH_DLL" \
            --only shm-h2 \
            --sizes "$PAYLOAD_SIZES" \
            --output "$cell_dir" \
            > "$cell_dir/run.log" 2>&1

    # `--output X` writes results to X/linux/results.json on Linux.
    local results="$cell_dir/linux/results.json"
    if [ ! -f "$results" ]; then
        results="$cell_dir/results.json"
    fi
    if [ ! -f "$results" ]; then
        echo "  ERROR: no results.json under $cell_dir" >&2
        return 1
    fi

    local label
    label=$(awk -v b="$ring_bytes" 'BEGIN {
        if (b >= 1048576) printf "%dMiB", b / 1048576
        else printf "%dKiB", b / 1024
    }')

    # Pair unary & streaming entries by size (transport=shm-h2 only).
    local sizes_arr
    sizes_arr=$(echo "$PAYLOAD_SIZES" | tr ',' '\n')
    while IFS= read -r sz; do
        local row_unary row_stream
        row_unary=$(jq -r --argjson s "$sz" \
            '.unary[] | select(.transport=="shm-h2" and .size_bytes==$s) | "\(.iterations),\(.avg_latency_us),\(.throughput_mb_per_s)"' \
            "$results" 2>/dev/null || true)
        row_stream=$(jq -r --argjson s "$sz" \
            '.streaming[] | select(.transport=="shm-h2" and .size_bytes==$s) | "\(.avg_latency_us),\(.throughput_mb_per_s)"' \
            "$results" 2>/dev/null || true)
        if [ -n "$row_unary" ] && [ -n "$row_stream" ]; then
            echo "${ring_bytes},${label},${sz},${row_unary},${row_stream},${run_idx}" >> "$SUMMARY"
        fi
    done <<< "$sizes_arr"
}

for cap in "${RING_CAPS[@]}"; do
    echo "[ ring=$cap ]"
    for ((r = 1; r <= REPEAT; r++)); do
        run_one "$cap" "$r"
    done
done

echo
echo "Sweep complete. Summary:"
echo "  $SUMMARY"
echo

# Pretty-print median throughput per (ring, size) cell. Requires REPEAT > 1
# for any aggregation; otherwise just shows the single run.
if [ "$REPEAT" -gt 1 ]; then
    echo "Median throughput by ring × size (H2 streaming MB/s):"
    awk -F, 'NR > 1 { key=$1"|"$3; vals[key]=vals[key]" "$8 }
             END { for (k in vals) print k vals[k] }' "$SUMMARY" \
        | while read -r line; do
            key="${line%% *}"; nums="${line#* }"
            ring="${key%|*}"; size="${key#*|}"
            median=$(printf "%s\n" $nums | sort -n | awk 'NR==(NR+1)/2 || NR==NR/2+1 {print; exit}')
            printf "  ring=%s  size=%s  median=%.0f MB/s\n" "$ring" "$size" "$median"
        done
fi

# Single-run quick-look table (always emitted).
echo
echo "Streaming throughput grid (MB/s, run 1):"
echo "ring \\ size  | $(echo "$PAYLOAD_SIZES" | tr ',' ' ')"
for cap in "${RING_CAPS[@]}"; do
    label=$(awk -v b="$cap" 'BEGIN {
        if (b >= 1048576) printf "%dMiB", b / 1048576
        else printf "%dKiB", b / 1024
    }')
    row="$label    "
    for sz in $(echo "$PAYLOAD_SIZES" | tr ',' ' '); do
        thr=$(awk -F, -v c="$cap" -v s="$sz" '$1==c && $3==s && $9==1 {print $8; exit}' "$SUMMARY")
        row+="  ${thr:-N/A}"
    done
    echo "$row"
done
