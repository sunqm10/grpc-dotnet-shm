#!/usr/bin/env python3
"""Analyze SHM vs TCP speedup ratios to find weak points."""
import json

with open("out/windows/results.json") as f:
    data = json.load(f)

def fmt_size(b):
    if b >= 1048576: return f"{b//1048576}MB"
    if b >= 1024: return f"{b//1024}KB"
    return f"{b}B"

for mode in ["unary", "streaming"]:
    print(f"\n{'='*80}")
    print(f"  {mode.upper()}: SHM vs TCP")
    print(f"{'='*80}")
    print(f"{'Size':>10} {'TCP_us':>10} {'SHM_us':>10} {'Speedup':>8} {'TCP_MB/s':>10} {'SHM_MB/s':>10} {'TP_ratio':>10}")
    print("-" * 80)

    tcp = {r["size_bytes"]: r for r in data[mode] if r["transport"] == "tcp"}
    shm = {r["size_bytes"]: r for r in data[mode] if r["transport"] == "shm"}

    for sz in data["sizes_bytes"]:
        t = tcp[sz]
        s = shm[sz]
        lat_speedup = t["avg_latency_us"] / s["avg_latency_us"] if s["avg_latency_us"] > 0 else 0
        tp_t = t["throughput_mb_per_s"]
        tp_s = s["throughput_mb_per_s"]
        tp_ratio = tp_s / tp_t if tp_t > 0 else 0

        flag = ""
        if lat_speedup < 2.0:
            flag = " <-- WEAK"
        elif lat_speedup < 3.0:
            flag = " <-- OK"

        print(f"{fmt_size(sz):>10} {t['avg_latency_us']:>10.1f} {s['avg_latency_us']:>10.1f} {lat_speedup:>7.1f}x {tp_t:>10.1f} {tp_s:>10.1f} {tp_ratio:>9.1f}x{flag}")

    # Also show where SHM throughput drops vs its own peak
    shm_tps = [(sz, shm[sz]["throughput_mb_per_s"]) for sz in data["sizes_bytes"] if shm[sz]["throughput_mb_per_s"] > 0]
    if shm_tps:
        peak_sz, peak_tp = max(shm_tps, key=lambda x: x[1])
        print(f"\n  SHM peak throughput: {peak_tp:.0f} MB/s at {fmt_size(peak_sz)}")
        print(f"  Sizes with throughput < 80% of peak:")
        for sz, tp in shm_tps:
            if tp < peak_tp * 0.8:
                pct = tp / peak_tp * 100
                print(f"    {fmt_size(sz):>10}: {tp:.0f} MB/s ({pct:.0f}% of peak)")
