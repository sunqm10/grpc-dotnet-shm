#!/usr/bin/env python3
"""Compare several gRFC G3 benchmark JSONs (master/phase1/phase2/...).

Reads the `grfc-g3-bench-v1` results.json emitted by `ringbench --matrix`.
Transport-generic: every transport present in the files gets its own table,
and the UDS baseline speedup (`uds_latency / transport_latency`) is printed
alongside the raw metric so a throughput win that comes with a latency
regression is visible.

Cells missing from a run print as "-" and are excluded from the delta.

Usage:
    python compare_runs.py master.json phase1.json [phase2.json ...]
    python compare_runs.py a.json b.json --metric p50 --concurrency 8
    python compare_runs.py a.json b.json --transports shm,uds
"""

import argparse
import json
import sys

SCHEMA = "grfc-g3-bench-v1"
BASELINE = "uds"
TRANSPORT_ORDER = ["shm", "uds", "tcp"]
LATENCY_STATS = ("p50", "p90", "p99", "mean")
LOWER_IS_BETTER = set(LATENCY_STATS) | {"cpu_us_per_op", "rss_peak_mb"}


def fmt(b):
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


def by(runs, workload, transport, metric, conc_filter=None):
    """(payload, concurrency) -> metric, for one workload/transport slice."""
    d = {}
    for (w, t, payload, conc) in runs:
        if w != workload or t != transport:
            continue
        if conc_filter is not None and conc != conc_filter:
            continue
        v = value(runs, (w, t, payload, conc), metric)
        if v is not None:
            d[(payload, conc)] = v
    return d


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="+", metavar="RESULTS",
                    help="results.json files; the first one is the reference")
    ap.add_argument("--metric", default="throughput_mb_per_s",
                    help="metric to compare: throughput_mb_per_s (default), "
                         "p50/p90/p99/mean, cpu_us_per_op, rss_peak_mb")
    ap.add_argument("--transports", metavar="CSV", default=None,
                    help="only these transports (default: all present)")
    ap.add_argument("--concurrency", type=int, default=None,
                    help="only this concurrency level (default: all present)")
    args = ap.parse_args()

    if len(args.paths) < 2:
        ap.error("need at least two results.json files to compare")

    loaded = [load_runs(p) for p in args.paths]
    metas = [m for m, _r in loaded]
    runs = [r for _m, r in loaded]

    labels = [f"r{i}" for i in range(len(args.paths))]
    labels[0] = "master"
    if len(args.paths) >= 2:
        labels[1] = "phase1"
    if len(args.paths) >= 3:
        labels[2] = "phase2"

    for label, path, meta in zip(labels, args.paths, metas):
        print(f"{label:>7}: {path}  ({meta.get('os','?')}/{meta.get('arch','?')} "
              f"arm={meta.get('arm','?')} {str(meta.get('commit',''))[:12]})")
    lower = args.metric in LOWER_IS_BETTER
    print(f"metric: {args.metric} ({'lower' if lower else 'higher'} is better)   "
          f"baseline: {BASELINE.upper()}")
    print()

    keys = set().union(*runs)
    wanted = None
    if args.transports:
        wanted = {t.strip().lower() for t in args.transports.split(",") if t.strip()}

    workloads = [w for w in ("unary", "streaming") if any(k[0] == w for k in keys)]
    workloads += sorted({k[0] for k in keys} - set(workloads) - {None})
    transports = order_transports({k[1] for k in keys if k[1]})
    if wanted is not None:
        transports = [t for t in transports if t in wanted]

    for workload in workloads:
        for transport in transports:
            maps = [by(r, workload, transport, args.metric, args.concurrency)
                    for r in runs]
            cells = sorted(set().union(*maps)) if any(maps) else []
            if not cells:
                continue

            print(f"=== {workload.upper()} {transport.upper()} / {args.metric} ===")
            header = "  ".join(f"{lab:>10}" for lab in labels)
            deltas = "  ".join(f"{lab + 'chg%':>9}" for lab in labels[1:])
            print(f"{'Size':<7} {'conc':>5}  {header}  {deltas}  {'vsUDS':>7}")

            for payload, conc in cells:
                vals = [m.get((payload, conc)) for m in maps]
                ref = vals[0]
                val_str = "  ".join(f"{v:>10.1f}" if v is not None else f"{'-':>10}"
                                    for v in vals)
                delta_str = "  ".join(
                    f"{(v - ref) / ref * 100:>+8.1f}%" if (v is not None and ref) else f"{'-':>9}"
                    for v in vals[1:])

                # Baseline speedup in the LAST run, so a regression against
                # UDS shows up next to the raw numbers.
                base = value(runs[-1], (workload, BASELINE, payload, conc), "p50")
                cur = value(runs[-1], (workload, transport, payload, conc), "p50")
                sp = f"{base / cur:.2f}x" if (base and cur) else "-"

                print(f"{fmt(payload):<7} {conc:>5}  {val_str}  {delta_str}  {sp:>7}")
            print()

    print("chg% is relative to the first file; '-' means the cell is missing from that run.")
    print(f"vsUDS = {BASELINE}_p50 / transport_p50 in the LAST file (>1 = beats baseline).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
