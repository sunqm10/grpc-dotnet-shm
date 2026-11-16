#!/usr/bin/env python3
"""Analyze transport speedup over the gRFC G3 baseline to find weak points.

Reads the `grfc-g3-bench-v1` results.json emitted by `ringbench --matrix`.
The baseline is UDS (the fastest portable local transport), so every
"speedup" below is `uds_latency / transport_latency`: >1 means the transport
beats a Unix domain socket. TCP appears as just another transport.

Missing cells are skipped with a note instead of raising KeyError.

Usage:
    python analyze_speedup.py                          # default results path
    python analyze_speedup.py out/linux/results.json
    python analyze_speedup.py RESULTS.json --stat p99 --weak 1.5
"""

import argparse
import json
import sys

SCHEMA = "grfc-g3-bench-v1"
BASELINE = "uds"
TRANSPORT_ORDER = ["shm", "uds", "tcp"]
LATENCY_STATS = ("p50", "p90", "p99", "mean")

DEFAULT_RESULTS = "out/windows/results.json"


def fmt_size(b):
    if b is None:
        return "?"
    if b >= 1048576:
        return f"{b//1048576}MB"
    if b >= 1024:
        return f"{b//1024}KB"
    return f"{b}B"


def order_transports(transports):
    known = [t for t in TRANSPORT_ORDER if t in transports]
    return known + sorted(t for t in transports if t not in TRANSPORT_ORDER)


def load_runs(path):
    """(workload, transport, payload_bytes, concurrency) -> run dict."""
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
    except OSError as e:
        print(f"ERROR: cannot read {path}: {e}")
        sys.exit(1)
    if data.get("schema") != SCHEMA:
        print(f"ERROR: {path} has schema {data.get('schema')!r}, expected {SCHEMA!r}. "
              f"Re-run ringbench with --matrix.")
        sys.exit(1)
    runs = {}
    for r in data.get("runs") or []:
        runs[(r.get("workload"), r.get("transport"),
              r.get("payload_bytes"), r.get("concurrency"))] = r
    return data, runs


def value(runs, key, metric):
    """Latency stat or top-level numeric field; None when absent/null."""
    r = runs.get(key)
    if r is None:
        return None
    v = r.get("latency_us", {}).get(metric) if metric in LATENCY_STATS else r.get(metric)
    if v is None or isinstance(v, bool) or not isinstance(v, (int, float)):
        return None
    return float(v)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("results", nargs="?", default=DEFAULT_RESULTS,
                    help=f"results.json to analyze (default: {DEFAULT_RESULTS})")
    ap.add_argument("--stat", default="p50", choices=LATENCY_STATS,
                    help="latency statistic to analyze (default: p50)")
    ap.add_argument("--weak", type=float, default=2.0,
                    help="flag speedups below this as WEAK (default: 2.0)")
    ap.add_argument("--concurrency", type=int, default=None,
                    help="only this concurrency level (default: all present)")
    args = ap.parse_args()

    meta, runs = load_runs(args.results)

    print(f"{args.results}: {meta.get('lang','?')} {str(meta.get('commit',''))[:12]} "
          f"{meta.get('os','?')}/{meta.get('arch','?')} arm={meta.get('arm','?')}")
    print(f"CPU: {meta.get('cpu','?')}  "
          f"({meta.get('cores','?')}c/{meta.get('threads','?')}t)")
    if meta.get("skipped"):
        print(f"NOTE: {len(meta['skipped'])} cell-repetition(s) were skipped by the runner")

    keys = set(runs)
    if BASELINE not in {k[1] for k in keys}:
        print(f"\nWARNING: no '{BASELINE}' rows -- the gRFC G3 baseline is missing, "
              f"so no speedup can be computed. Re-run with "
              f"--matrix-transports shm,{BASELINE}[,tcp].")

    workloads = [w for w in ("unary", "streaming") if any(k[0] == w for k in keys)]
    workloads += sorted({k[0] for k in keys} - set(workloads) - {None})

    for workload in workloads:
        concs = sorted({k[3] for k in keys if k[0] == workload and k[3] is not None})
        if args.concurrency is not None:
            concs = [c for c in concs if c == args.concurrency]

        for conc in concs:
            payloads = sorted({k[2] for k in keys
                               if k[0] == workload and k[3] == conc and k[2] is not None})
            transports = [t for t in order_transports(
                {k[1] for k in keys if k[0] == workload and k[3] == conc and k[1]})
                if t != BASELINE]

            print(f"\n{'='*92}")
            print(f"  {workload.upper()} c={conc}: speedup vs {BASELINE.upper()} "
                  f"baseline (latency {args.stat})")
            print(f"{'='*92}")
            print(f"{'Size':>8} {'Trans':>6} {'base_us':>9} {'trans_us':>9} {'Speedup':>8} "
                  f"{'base_MB/s':>10} {'tr_MB/s':>10} {'TP_ratio':>9}")
            print("-" * 92)

            for payload in payloads:
                base_lat = value(runs, (workload, BASELINE, payload, conc), args.stat)
                base_tp = value(runs, (workload, BASELINE, payload, conc),
                                "throughput_mb_per_s")
                for transport in transports:
                    key = (workload, transport, payload, conc)
                    lat = value(runs, key, args.stat)
                    tp = value(runs, key, "throughput_mb_per_s")
                    if lat is None and tp is None:
                        continue  # cell never measured -- skip, don't invent
                    if base_lat is None and base_tp is None:
                        print(f"{fmt_size(payload):>8} {transport.upper():>6} "
                              f"{'-':>9} {_f(lat, 9, 2):>9} {'-':>8} "
                              f"{'-':>10} {_f(tp, 10, 1):>10} {'-':>9}"
                              f"  <-- no {BASELINE.upper()} baseline")
                        continue

                    sp = base_lat / lat if base_lat and lat else None
                    tp_ratio = tp / base_tp if base_tp and tp else None

                    flag = ""
                    if sp is not None:
                        if sp < args.weak:
                            flag = " <-- WEAK"
                        elif sp < args.weak * 1.5:
                            flag = " <-- OK"

                    print(f"{fmt_size(payload):>8} {transport.upper():>6} "
                          f"{_f(base_lat, 9, 2):>9} {_f(lat, 9, 2):>9} {_x(sp):>8} "
                          f"{_f(base_tp, 10, 1):>10} {_f(tp, 10, 1):>10} "
                          f"{_x(tp_ratio):>9}{flag}")

            # Where does each transport fall off its own throughput peak?
            for transport in order_transports(
                    {k[1] for k in keys if k[0] == workload and k[3] == conc and k[1]}):
                tps = []
                for payload in payloads:
                    tp = value(runs, (workload, transport, payload, conc),
                               "throughput_mb_per_s")
                    if tp and tp > 0:
                        tps.append((payload, tp))
                if not tps:
                    continue
                peak_sz, peak_tp = max(tps, key=lambda x: x[1])
                print(f"\n  {transport.upper()} peak throughput: {peak_tp:.0f} MB/s "
                      f"at {fmt_size(peak_sz)}")
                below = [(sz, tp) for sz, tp in tps if tp < peak_tp * 0.8]
                if below:
                    print(f"  {transport.upper()} sizes below 80% of peak:")
                    for sz, tp in below:
                        print(f"    {fmt_size(sz):>8}: {tp:.0f} MB/s "
                              f"({tp / peak_tp * 100:.0f}% of peak)")

    return 0


def _f(v, width, prec):
    return f"{v:{width}.{prec}f}" if v is not None else "-"


def _x(v):
    return f"{v:.2f}x" if v is not None else "-"


if __name__ == "__main__":
    sys.exit(main())
