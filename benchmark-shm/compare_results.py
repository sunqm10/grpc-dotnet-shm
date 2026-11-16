#!/usr/bin/env python3
"""Compare two gRFC G3 benchmark runs (e.g. before/after an optimization).

Reads the `grfc-g3-bench-v1` results.json emitted by `ringbench --matrix`
(flat `runs[]`, transport is a field). UDS is the gRFC G3 baseline: the
"vs UDS" columns are `uds_latency / transport_latency`, so >1 means the
transport beats a Unix domain socket. TCP is reported like any other
transport, not as the baseline.

Cells missing from either file are skipped, never substituted.

Usage:
    python compare_results.py                            # default old/new paths
    python compare_results.py OLD.json NEW.json
    python compare_results.py OLD.json NEW.json --stat p99 --concurrency 8
"""

import argparse
import json
import sys

SCHEMA = "grfc-g3-bench-v1"
BASELINE = "uds"
TRANSPORT_ORDER = ["shm", "uds", "tcp"]
LATENCY_STATS = ("p50", "p90", "p99", "mean")

DEFAULT_OLD = "out/windows/results.json"
DEFAULT_NEW = "out/windows/results_opt.json"


def fmt_size(b):
    if b is None:
        return "?"
    if b == 0:
        return "0B"
    if b < 1024:
        return f"{b}B"
    if b < 1048576:
        return f"{b//1024}KB"
    return f"{b//1048576}MB"


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


def speedup(runs, workload, transport, payload, conc, metric):
    """uds / transport latency -- None if either side is missing."""
    base = value(runs, (workload, BASELINE, payload, conc), metric)
    val = value(runs, (workload, transport, payload, conc), metric)
    if base is None or val is None or val <= 0:
        return None
    return base / val


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("old", nargs="?", default=DEFAULT_OLD,
                    help=f"baseline run results.json (default: {DEFAULT_OLD})")
    ap.add_argument("new", nargs="?", default=DEFAULT_NEW,
                    help=f"candidate run results.json (default: {DEFAULT_NEW})")
    ap.add_argument("--stat", default="p50", choices=LATENCY_STATS,
                    help="latency statistic to compare (default: p50)")
    ap.add_argument("--concurrency", type=int, default=None,
                    help="only this concurrency level (default: all present)")
    args = ap.parse_args()

    old_meta, old_m = load_runs(args.old)
    new_meta, new_m = load_runs(args.new)

    print(f"old: {args.old}  ({old_meta.get('os','?')}/{old_meta.get('arch','?')} "
          f"arm={old_meta.get('arm','?')} {str(old_meta.get('commit',''))[:12]})")
    print(f"new: {args.new}  ({new_meta.get('os','?')}/{new_meta.get('arch','?')} "
          f"arm={new_meta.get('arm','?')} {str(new_meta.get('commit',''))[:12]})")
    print(f"latency stat: {args.stat}    baseline: {BASELINE.upper()}")
    print()

    keys = set(old_m) | set(new_m)
    workloads = [w for w in ("unary", "streaming") if any(k[0] == w for k in keys)]
    workloads += sorted({k[0] for k in keys} - set(workloads) - {None})

    for workload in workloads:
        concs = sorted({k[3] for k in keys if k[0] == workload and k[3] is not None})
        if args.concurrency is not None:
            concs = [c for c in concs if c == args.concurrency]
        for conc in concs:
            payloads = sorted({k[2] for k in keys
                               if k[0] == workload and k[3] == conc and k[2] is not None})
            transports = order_transports({k[1] for k in keys
                                           if k[0] == workload and k[3] == conc and k[1]})
            print("=" * 100)
            print(f"  {workload.upper()}  concurrency={conc}  "
                  f"latency {args.stat} in us, throughput in MB/s")
            print("=" * 100)
            print(f"{'Size':>7} {'Trans':>6} {'old lat':>9} {'new lat':>9} {'lat o/n':>7} "
                  f"{'old MB/s':>9} {'new MB/s':>9} {'tp n/o':>7} "
                  f"{'old vsUDS':>9} {'new vsUDS':>9}")
            print("-" * 100)

            for payload in payloads:
                for transport in transports:
                    key = (workload, transport, payload, conc)
                    ol = value(old_m, key, args.stat)
                    nl = value(new_m, key, args.stat)
                    ot = value(old_m, key, "throughput_mb_per_s")
                    nt = value(new_m, key, "throughput_mb_per_s")
                    if ol is None and nl is None and ot is None and nt is None:
                        continue  # cell absent from both runs -- nothing to say

                    lat_d = f"{ol / nl:.2f}x" if ol and nl else "-"
                    tp_d = f"{nt / ot:.2f}x" if ot and nt else "-"
                    osp = speedup(old_m, workload, transport, payload, conc, args.stat)
                    nsp = speedup(new_m, workload, transport, payload, conc, args.stat)

                    print(f"{fmt_size(payload):>7} {transport.upper():>6} "
                          f"{_f(ol, 9, 2):>9} {_f(nl, 9, 2):>9} {lat_d:>7} "
                          f"{_f(ot, 9, 1):>9} {_f(nt, 9, 1):>9} {tp_d:>7} "
                          f"{_x(osp):>9} {_x(nsp):>9}")
            print()

    print("lat o/n = old / new latency  (>1 = the new run is faster)")
    print("tp n/o  = new / old throughput (>1 = the new run moves more bytes)")
    print(f"vsUDS = {BASELINE}_latency / transport_latency (>1 = beats the baseline)")
    print()

    # --- Biggest latency movements, any transport ---------------------
    moves = []
    for key in sorted(set(old_m) & set(new_m)):
        o = value(old_m, key, args.stat)
        n = value(new_m, key, args.stat)
        if o and n:
            moves.append((o / n, key, o, n))
    moves.sort(reverse=True)

    print(f"=== Biggest latency improvements (old/new, {args.stat}) ===")
    if not moves:
        print("  No cell is present in both runs.")
    for ratio, (w, t, p, c), o, n in moves[:10]:
        print(f"  {w:>9} {t.upper():>4} {fmt_size(p):>7} c={c:<4}: "
              f"{o:.2f} -> {n:.2f} us  ({ratio:.2f}x faster)")

    print()
    print("=== Regressions (> 5% slower) ===")
    regressions = [m for m in moves if m[0] < 0.95]
    for ratio, (w, t, p, c), o, n in regressions:
        print(f"  {w:>9} {t.upper():>4} {fmt_size(p):>7} c={c:<4}: "
              f"{o:.2f} -> {n:.2f} us  ({ratio:.2f}x, regression)")
    if not regressions:
        print("  None (all shared cells improved or within noise)")

    return 0


def _f(v, width, prec):
    return f"{v:{width}.{prec}f}" if v is not None else "-"


def _x(v):
    return f"{v:.2f}x" if v is not None else "-"


if __name__ == "__main__":
    sys.exit(main())
