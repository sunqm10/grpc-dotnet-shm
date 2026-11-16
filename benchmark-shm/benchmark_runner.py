#!/usr/bin/env python3
"""
Benchmark runner and plotter for the gRFC G3 transport matrix.

Reads the cross-language `grfc-g3-bench-v1` results.json emitted by
`ringbench --matrix` (see ringbench/Matrix.cs). That schema is a FLAT
`runs[]` array keyed by (workload, transport, payload_bytes, concurrency),
so any transport set plots without touching the plotting code.

UDS IS THE BASELINE. gRFC G3 compares shared memory against the fastest
portable local transport, which is a Unix domain socket; every ratio printed
or plotted here is `uds / shm` for latency (>1 means SHM is faster) and
`shm / uds` for throughput. TCP is kept as a supplementary series only.

Nothing is estimated or extrapolated: a cell that was not measured is left
as a hole in the plot and labelled "No data".

Usage:
    python benchmark_runner.py              # Plot from cached results (or run if none)
    python benchmark_runner.py --run        # Force rerun the matrix, then plot
    python benchmark_runner.py --plot-only  # Only plot (fail if no cached results)
    python benchmark_runner.py --run --fair # Run the fair arm instead of tuned
    python benchmark_runner.py --compare    # Cross-platform comparison plots
"""

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np

# Directory setup -- matches Go's out/<platform>/ convention
SCRIPT_DIR = Path(__file__).parent.absolute()
OUT_ROOT = SCRIPT_DIR / "out"
PLATFORM_NAME = "windows" if os.name == "nt" else ("linux" if sys.platform.startswith("linux") else sys.platform)
OUT_DIR = OUT_ROOT / PLATFORM_NAME
RESULTS_FILE = OUT_DIR / "results.json"

# gRFC G3 cross-language contract
SCHEMA = "grfc-g3-bench-v1"
BASELINE = "uds"

# Latency statistics available in `latency_us`; anything else is a top-level
# numeric field of the run (throughput_mb_per_s, cpu_us_per_op, ...).
LATENCY_STATS = ("p50", "p90", "p99", "mean")

# Transport presentation. Transports not listed here still plot, using a
# fallback colour -- the data structure never hard-codes the transport set.
TRANSPORT_ORDER = ["shm", "uds", "tcp"]
TRANSPORT_COLORS = {'shm': '#00cc6a', 'uds': '#4472C4', 'tcp': '#ff5555'}
TRANSPORT_LABELS = {'shm': 'SHM', 'uds': 'UDS', 'tcp': 'TCP'}
FALLBACK_COLORS = ['#ED7D31', '#7030A0', '#808080', '#C00000']

# Default timeout for a full matrix run. The contract sweep is
# transports x workloads x payloads x concurrency x repetitions at
# >= 35 s per repetition, which runs for hours -- 1800 s is nowhere near
# enough. Override with --timeout (0 disables the timeout entirely).
DEFAULT_RUN_TIMEOUT_S = 8 * 3600


def transport_color(transport: str) -> str:
    if transport in TRANSPORT_COLORS:
        return TRANSPORT_COLORS[transport]
    idx = abs(hash(transport)) % len(FALLBACK_COLORS)
    return FALLBACK_COLORS[idx]


def transport_label(transport: str) -> str:
    return TRANSPORT_LABELS.get(transport, transport.upper())


def order_transports(transports) -> list:
    """Sort transports into the canonical order, unknown ones last."""
    known = [t for t in TRANSPORT_ORDER if t in transports]
    extra = sorted(t for t in transports if t not in TRANSPORT_ORDER)
    return known + extra


def format_size(b):
    if b is None:
        return '?'
    if b == 0:
        return '0B'
    if b >= 1048576:
        return f'{b // 1048576}MB'
    if b >= 1024:
        return f'{b // 1024}KB'
    return f'{b}B'


def _num(v):
    """Return *v* as a float if it is a real number, else None."""
    if v is None or isinstance(v, bool):
        return None
    if isinstance(v, (int, float)):
        return float(v)
    return None


# =========================================================================
# Transport-generic view over a grfc-g3-bench-v1 results.json
# =========================================================================

class Matrix:
    """Indexed view over the flat `runs[]` array.

    Cells are stored under the full contract key so that adding a transport
    (or a concurrency level, or a payload) needs no code change:

        self.cells[(workload, transport, payload_bytes, concurrency)] -> run

    All accessors return None for a missing cell or a null metric; callers
    are expected to leave such points out of the plot rather than fill them.
    """

    def __init__(self, results: dict, source: Path = None):
        self.source = source
        self.meta = {k: v for k, v in results.items() if k not in ("runs", "skipped")}
        self.skipped = list(results.get("skipped") or [])
        self.cells = {}
        for r in results.get("runs") or []:
            if not isinstance(r, dict):
                continue
            key = (r.get("workload"), r.get("transport"),
                   r.get("payload_bytes"), r.get("concurrency"))
            self.cells[key] = r

        self.transports = order_transports({k[1] for k in self.cells if k[1]})
        workloads = {k[0] for k in self.cells if k[0]}
        self.workloads = ([w for w in ("unary", "streaming") if w in workloads]
                          + sorted(w for w in workloads if w not in ("unary", "streaming")))
        self.payloads = sorted({k[2] for k in self.cells if k[2] is not None})
        self.concurrency = sorted({k[3] for k in self.cells if k[3] is not None})

    # ---- identity -------------------------------------------------------

    def __bool__(self):
        return bool(self.cells)

    @property
    def cpu(self) -> str:
        return self.meta.get("cpu") or "Unknown CPU"

    @property
    def arm(self) -> str:
        return self.meta.get("arm") or "?"

    @property
    def os_label(self) -> str:
        return self.meta.get("os") or "?"

    @property
    def commit(self) -> str:
        sha = self.meta.get("commit") or "unknown"
        short = sha[:12]
        return short + ("+dirty" if self.meta.get("dirty") else "")

    def subtitle(self) -> str:
        """One-line provenance string for plot titles."""
        cores = self.meta.get("cores")
        threads = self.meta.get("threads")
        topo = f'{cores}c/{threads}t' if cores and threads else ''
        bits = [self.cpu[:40], self.os_label, self.meta.get("arch", ""), topo,
                f'arm={self.arm}', self.commit]
        return ' • '.join(b for b in bits if b)

    # ---- cell access ----------------------------------------------------

    def cell(self, workload, transport, payload, conc):
        return self.cells.get((workload, transport, payload, conc))

    def value(self, workload, transport, payload, conc, metric):
        """Metric for one cell: a latency_us stat, or a top-level field."""
        c = self.cell(workload, transport, payload, conc)
        if c is None:
            return None
        if metric in LATENCY_STATS:
            lat = c.get("latency_us")
            return _num(lat.get(metric)) if isinstance(lat, dict) else None
        return _num(c.get(metric))

    def series(self, workload, transport, payloads, conc, metric) -> list:
        """Metric across *payloads* at fixed concurrency; None marks holes."""
        return [self.value(workload, transport, p, conc, metric) for p in payloads]

    def conc_series(self, workload, transport, payload, concurrency, metric) -> list:
        """Metric across *concurrency* at fixed payload; None marks holes."""
        return [self.value(workload, transport, payload, c, metric) for c in concurrency]

    def transport_series(self, workload, payloads, conc, metric, transports=None) -> list:
        """[(transport, values)] for every transport present, in plot order."""
        return [(t, self.series(workload, t, payloads, conc, metric))
                for t in (transports or self.transports)]

    # ---- baseline-relative ratios (UDS is the baseline) -----------------

    def latency_speedup(self, workload, transport, payload, conc,
                        stat="p50", baseline=BASELINE):
        """baseline_latency / transport_latency -- >1 means *transport* wins.

        Returns None when either side is missing, so the caller plots a hole
        instead of a fabricated 1.0x.
        """
        base = self.value(workload, baseline, payload, conc, stat)
        val = self.value(workload, transport, payload, conc, stat)
        if base is None or val is None or val <= 0:
            return None
        return base / val

    def throughput_ratio(self, workload, transport, payload, conc,
                         baseline=BASELINE):
        """transport_throughput / baseline_throughput -- >1 means it wins."""
        base = self.value(workload, baseline, payload, conc, "throughput_mb_per_s")
        val = self.value(workload, transport, payload, conc, "throughput_mb_per_s")
        if base is None or val is None or base <= 0:
            return None
        return val / base

    def speedup_series(self, workload, payloads, conc, stat="p50",
                       baseline=BASELINE) -> list:
        """[(transport, speedups)] for every non-baseline transport."""
        out = []
        for t in self.transports:
            if t == baseline:
                continue
            out.append((t, [self.latency_speedup(workload, t, p, conc, stat, baseline)
                            for p in payloads]))
        return out

    def has_baseline(self, baseline=BASELINE) -> bool:
        return baseline in self.transports

    # ---- axis selection -------------------------------------------------

    def ref_payload(self, preferred=1024):
        """Reference payload: *preferred* if measured, else the closest one.

        Never returns a payload under a wrong label -- callers label panels
        with format_size() of the value returned here.
        """
        if not self.payloads:
            return None
        if preferred in self.payloads:
            return preferred
        return min(self.payloads, key=lambda p: abs(p - preferred))

    def ref_concurrency(self, preferred=1):
        """Reference concurrency for latency panels (lowest by default)."""
        if not self.concurrency:
            return None
        if preferred in self.concurrency:
            return preferred
        return min(self.concurrency)

    def peak_concurrency(self):
        return max(self.concurrency) if self.concurrency else None

    def large_payloads(self, threshold=1048576) -> list:
        return [p for p in self.payloads if p >= threshold]

    # ---- aggregates -----------------------------------------------------

    def extremum(self, transport, metric, workload=None, how=max):
        """max/min of *metric* over all cells of *transport*, or None."""
        vals = []
        for (w, t, p, c), _cell in self.cells.items():
            if t != transport or (workload is not None and w != workload):
                continue
            v = self.value(w, t, p, c, metric)
            if v is not None and v > 0:
                vals.append(v)
        return how(vals) if vals else None


def _load_json(path: Path) -> dict:
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def load_matrix(path: Path, quiet=False) -> Matrix:
    """Load and validate a grfc-g3-bench-v1 results.json."""
    if not Path(path).exists():
        return None
    try:
        results = _load_json(Path(path))
    except Exception as e:
        print(f"ERROR: Failed to parse {path}: {e}")
        return None

    schema = results.get("schema")
    if schema != SCHEMA:
        if "unary" in results or "streaming" in results:
            print(f"ERROR: {path} uses the retired pre-matrix format "
                  f"(top-level unary[]/streaming[]). Re-run with --run to "
                  f"produce a '{SCHEMA}' results.json.")
        else:
            print(f"ERROR: {path} has schema '{schema}', expected '{SCHEMA}'.")
        return None

    m = Matrix(results, source=Path(path))
    if not quiet:
        print(f"Loaded results from: {path}")
        print(f"  Schema:      {schema} ({m.meta.get('lang', '?')})")
        print(f"  Commit:      {m.commit}")
        print(f"  Platform:    {m.os_label}/{m.meta.get('arch', '?')}  arm={m.arm}")
        print(f"  CPU:         {m.cpu}")
        print(f"  Cells:       {len(m.cells)} "
              f"(transports: {', '.join(m.transports) or 'none'})")
        if m.skipped:
            print(f"  Skipped:     {len(m.skipped)} cell-repetition(s)")
    return m


# =========================================================================
# Benchmark execution -- drives ringbench's gRFC G3 matrix mode
# =========================================================================

def run_ringbench(args, results_file: Path) -> Matrix:
    """Run the .NET gRFC G3 matrix sweep and return the parsed results."""
    print("=" * 70)
    print(f"Running .NET gRPC gRFC G3 benchmark matrix "
          f"({'fair' if args.fair else 'tuned'} arm)...")
    print("=" * 70)

    results_file.parent.mkdir(parents=True, exist_ok=True)

    cmd = [
        "dotnet", "run",
        "--project", str(SCRIPT_DIR / "ringbench" / "RingBench.csproj"),
        "-c", "Release",
        "--",
        "--matrix",
        "--matrix-out", str(results_file),
    ]
    if args.fair:
        cmd.append("--fair")
    for flag, value in (("--matrix-transports", args.transports),
                        ("--matrix-workloads", args.workloads),
                        ("--matrix-payloads", args.payloads),
                        ("--matrix-concurrency", args.concurrency),
                        ("--matrix-warmup-s", args.warmup_s),
                        ("--matrix-duration-s", args.duration_s),
                        ("--matrix-reps", args.reps)):
        if value is not None:
            cmd += [flag, str(value)]

    timeout = args.timeout if args.timeout and args.timeout > 0 else None
    print(f"Running: {' '.join(cmd)}")
    print(f"Timeout: {str(timeout) + 's' if timeout else 'none'}")
    print("-" * 70)

    try:
        result = subprocess.run(cmd, capture_output=False, text=True, timeout=timeout)
        if result.returncode != 0:
            print(f"ERROR: Benchmark exited with code {result.returncode}")
            return None
    except subprocess.TimeoutExpired:
        print(f"ERROR: Benchmark timed out after {timeout}s "
              f"(raise it with --timeout, or 0 to disable)")
        return None
    except Exception as e:
        print(f"ERROR: Failed to run benchmarks: {e}")
        return None

    print("-" * 70)
    return load_matrix(results_file)


# =========================================================================
# Plot helpers
# =========================================================================

def _no_data(ax, title: str):
    """Mark a panel that has nothing measured to show."""
    ax.text(0.5, 0.5, 'No data', ha='center', va='center',
            transform=ax.transAxes, fontsize=11, color='#888888')
    ax.set_xticks([])
    ax.set_yticks([])
    ax.set_title(title)


def _any_value(series) -> bool:
    """True if any (transport, values) pair carries at least one number."""
    return any(v is not None for _t, values in series for v in values)


def grouped_bars(ax, labels, series, total_width=0.8, fontsize=9):
    """Draw an N-way grouped bar chart over an arbitrary transport set.

    *series* is [(transport, values)]; a None value is a cell that was not
    measured and simply gets no bar -- never a substituted number. Columns
    where every transport is missing are tagged "No data".

    Returns (x positions, per-bar width) for callers that annotate.
    """
    x = np.arange(len(labels))
    n = max(len(series), 1)
    width = total_width / n

    for i, (transport, values) in enumerate(series):
        offset = (i - (n - 1) / 2) * width
        heights = [np.nan if v is None else v for v in values]
        ax.bar(x + offset, heights, width, label=transport_label(transport),
               color=transport_color(transport), edgecolor='black', linewidth=0.5)

    # Holes are called out explicitly instead of silently looking like a
    # zero. The axes-fraction y keeps the marker visible on log scales too.
    small = max(fontsize - 2, 6)
    for i in range(len(labels)):
        if all(values[i] is None for _t, values in series):
            ax.annotate('No data', xy=(x[i], 0.03), xycoords=('data', 'axes fraction'),
                        ha='center', va='bottom', fontsize=small,
                        color='#888888', rotation=90)
            continue
        for j, (_transport, values) in enumerate(series):
            if values[i] is None:
                offset = (j - (n - 1) / 2) * width
                ax.annotate('n/a', xy=(x[i] + offset, 0.03),
                            xycoords=('data', 'axes fraction'), ha='center',
                            va='bottom', fontsize=small, color='#888888', rotation=90)

    # Pin the x range: autoscale over partially missing groups otherwise
    # stretches a lone bar across the whole panel.
    ax.set_xlim(-0.5, len(labels) - 0.5)
    ax.set_xticks(x)
    ax.set_xticklabels(labels)
    return x, width


def annotate_speedup(ax, x, width, series, speedups, transport='shm', fontsize=8):
    """Annotate the *transport* bars with their speedup over the baseline."""
    try:
        idx = [t for t, _v in series].index(transport)
    except ValueError:
        return
    n = max(len(series), 1)
    offset = (idx - (n - 1) / 2) * width
    values = series[idx][1]
    for i, (v, s) in enumerate(zip(values, speedups)):
        if v is None or s is None:
            continue
        ax.annotate(f'{s:.1f}x', xy=(x[i] + offset, v), xytext=(0, 4),
                    textcoords='offset points', ha='center', fontsize=fontsize,
                    color=transport_color(transport), fontweight='bold')


def _log_if_wide(ax, series, factor=50.0):
    """Use a log y-axis when the plotted range spans more than *factor*."""
    vals = [v for _t, values in series for v in values if v is not None and v > 0]
    if vals and max(vals) / min(vals) > factor:
        ax.set_yscale('log')


def _baseline_note(m: Matrix) -> str:
    if m.has_baseline():
        return f'baseline: {transport_label(BASELINE)}'
    return f'{transport_label(BASELINE)} baseline MISSING'


# =========================================================================
# Plot generation
# =========================================================================

def generate_plots(m: Matrix, out_dir: Path):
    """Main dashboard + summary + large-payload plots."""
    out_dir.mkdir(parents=True, exist_ok=True)

    plt.style.use('default')
    plt.rcParams.update({
        'figure.facecolor': 'white',
        'axes.facecolor': 'white',
        'axes.grid': True,
        'grid.alpha': 0.3,
        'font.size': 10,
    })

    payloads = m.payloads
    labels = [format_size(p) for p in payloads]
    conc = m.ref_concurrency()
    ref_payload = m.ref_payload(1024)
    ref_label = format_size(ref_payload)

    # ================================================================
    # Plot 1: Communication Pattern Benchmarks (main dashboard)
    # ================================================================
    fig, axes = plt.subplots(2, 2, figsize=(14, 10))
    fig.suptitle(
        f'gRPC Shared Memory Transport — gRFC G3 Matrix ({_baseline_note(m)})\n'
        f'concurrency={conc} • {m.subtitle()}',
        fontsize=13, fontweight='bold')

    for row, workload in enumerate(("unary", "streaming")):
        tag = 'UNARY' if workload == 'unary' else 'STREAM'
        title = 'Unary RPC' if workload == 'unary' else 'Unidirectional Streaming'

        # Latency (p50)
        ax = axes[row, 0]
        lat = m.transport_series(workload, payloads, conc, "p50")
        if _any_value(lat):
            x, width = grouped_bars(ax, labels, lat)
            ax.set_xlabel('Payload Size')
            ax.set_ylabel('Latency p50 (µs)')
            ax.set_title(f'[{tag}] {title} — Latency p50\n(lower is better)')
            ax.legend(loc='upper left', fontsize=8)
            _log_if_wide(ax, lat)
            speedups = [m.latency_speedup(workload, 'shm', p, conc) for p in payloads]
            annotate_speedup(ax, x, width, lat, speedups)
        else:
            _no_data(ax, f'[{tag}] {title} — Latency p50')

        # Throughput
        ax = axes[row, 1]
        tp = m.transport_series(workload, payloads, conc, "throughput_mb_per_s")
        if _any_value(tp):
            grouped_bars(ax, labels, tp)
            ax.set_xlabel('Payload Size')
            ax.set_ylabel('Throughput (MB/s)')
            ax.set_title(f'[{tag}] {title} — Throughput\n(higher is better)')
            ax.legend(loc='upper left', fontsize=8)
            _log_if_wide(ax, tp)
        else:
            _no_data(ax, f'[{tag}] {title} — Throughput')

    plt.tight_layout(rect=[0, 0, 1, 0.93])
    patterns_file = out_dir / "benchmark_patterns.png"
    plt.savefig(patterns_file, dpi=150, bbox_inches='tight', facecolor='white')
    plt.close()
    print(f"Created: {patterns_file}")

    # ================================================================
    # Plot 2: Summary Comparison (at the reference payload)
    # ================================================================
    fig, axes = plt.subplots(2, 2, figsize=(14, 10))
    fig.suptitle(f'gRPC Transport Performance Summary — {_baseline_note(m)}\n'
                 f'{m.subtitle()}', fontsize=13, fontweight='bold')

    workloads = m.workloads or ["unary", "streaming"]
    cat_labels = [w.capitalize() for w in workloads]

    # Latency at the reference payload, per workload
    ax = axes[0, 0]
    lat_cat = [(t, [m.value(w, t, ref_payload, conc, "p50") for w in workloads])
               for t in m.transports]
    if _any_value(lat_cat):
        grouped_bars(ax, cat_labels, lat_cat)
        ax.set_ylabel('Latency p50 (µs)')
        ax.set_title(f'Latency p50 @ {ref_label}, concurrency={conc}')
        ax.legend(fontsize=8)
        _log_if_wide(ax, lat_cat)
    else:
        _no_data(ax, f'Latency p50 @ {ref_label}')

    # Peak throughput per transport
    ax = axes[0, 1]
    peaks = [(t, m.extremum(t, "throughput_mb_per_s")) for t in m.transports]
    peaks = [(t, v) for t, v in peaks if v is not None]
    if peaks:
        bars = ax.bar([transport_label(t) for t, _v in peaks], [v for _t, v in peaks],
                      color=[transport_color(t) for t, _v in peaks], edgecolor='black')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('Peak Throughput (all cells)')
        for bar, (_t, val) in zip(bars, peaks):
            label = f'{val / 1000:.1f} GB/s' if val >= 1000 else f'{val:.0f} MB/s'
            ax.annotate(label, xy=(bar.get_x() + bar.get_width() / 2, val),
                        xytext=(0, 5), textcoords='offset points',
                        ha='center', fontweight='bold')
    else:
        _no_data(ax, 'Peak Throughput')

    # Latency speedup over the UDS baseline, per workload
    ax = axes[1, 0]
    sp_cat = [(t, [m.latency_speedup(w, t, ref_payload, conc) for w in workloads])
              for t in m.transports if t != BASELINE]
    if _any_value(sp_cat):
        grouped_bars(ax, cat_labels, sp_cat)
        ax.set_ylabel('Speedup vs UDS (x)')
        ax.set_title(f'Latency Speedup over {transport_label(BASELINE)} @ {ref_label}')
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
        ax.legend(fontsize=8)
    else:
        _no_data(ax, f'Latency Speedup over {transport_label(BASELINE)}')

    # Text summary
    ax = axes[1, 1]
    ax.axis('off')
    ax.text(0.02, 0.98, _summary_text(m, ref_payload, conc), transform=ax.transAxes,
            fontsize=9, verticalalignment='top', fontfamily='monospace',
            bbox=dict(boxstyle='round', facecolor='wheat', alpha=0.5))

    plt.tight_layout(rect=[0, 0, 1, 0.93])
    summary_file = out_dir / "benchmark_summary.png"
    plt.savefig(summary_file, dpi=150, bbox_inches='tight', facecolor='white')
    plt.close()
    print(f"Created: {summary_file}")

    plot_files = [patterns_file, summary_file]

    # ================================================================
    # Plot 3: Large Payloads (>= 1 MiB)
    # ================================================================
    large = m.large_payloads()
    if large:
        large_labels = [format_size(p) for p in large]
        fig, axes_lp = plt.subplots(1, 2, figsize=(14, 6))
        fig.suptitle(f'Large Payload Performance (>= 1MB, streaming, '
                     f'concurrency={conc})\n{m.subtitle()}',
                     fontsize=13, fontweight='bold')

        ax = axes_lp[0]
        tp = m.transport_series("streaming", large, conc, "throughput_mb_per_s")
        if _any_value(tp):
            grouped_bars(ax, large_labels, tp)
            ax.set_xlabel('Payload Size')
            ax.set_ylabel('Throughput (MB/s)')
            ax.set_title('Large Payload Throughput\n(higher is better)')
            ax.legend(loc='upper left', fontsize=8)
            _log_if_wide(ax, tp)
        else:
            _no_data(ax, 'Large Payload Throughput')

        ax = axes_lp[1]
        lat = m.transport_series("streaming", large, conc, "p50")
        if _any_value(lat):
            x, width = grouped_bars(ax, large_labels, lat)
            ax.set_xlabel('Payload Size')
            ax.set_ylabel('Latency p50 (µs)')
            ax.set_title('Large Payload Latency\n(lower is better)')
            ax.legend(loc='upper left', fontsize=8)
            _log_if_wide(ax, lat)
            speedups = [m.latency_speedup("streaming", 'shm', p, conc) for p in large]
            annotate_speedup(ax, x, width, lat, speedups)
        else:
            _no_data(ax, 'Large Payload Latency')

        plt.tight_layout(rect=[0, 0, 1, 0.92])
        large_file = out_dir / "benchmark_large_payloads.png"
        plt.savefig(large_file, dpi=150, bbox_inches='tight', facecolor='white')
        plt.close()
        print(f"Created: {large_file}")
        plot_files.append(large_file)

    return plot_files


def _summary_text(m: Matrix, ref_payload, conc) -> str:
    """Key numbers at the reference payload, UDS-relative."""
    ref_label = format_size(ref_payload)
    lines = [
        "BENCHMARK SUMMARY",
        "=" * 46,
        f"Schema:   {m.meta.get('schema', '?')} / {m.meta.get('lang', '?')}",
        f"Commit:   {m.commit}",
        f"Host:     {m.cpu[:38]}",
        f"OS/Arch:  {m.os_label} / {m.meta.get('arch', '?')}  "
        f"({m.meta.get('cores', '?')}c/{m.meta.get('threads', '?')}t)",
        f"Arm:      {m.arm}    Baseline: {transport_label(BASELINE)}",
        "",
        f"KEY RESULTS @ {ref_label}, concurrency={conc}:",
    ]

    for workload in (m.workloads or []):
        lines.append("")
        lines.append(f"• {workload.capitalize()}:")
        any_row = False
        for t in m.transports:
            lat = m.value(workload, t, ref_payload, conc, "p50")
            tp = m.value(workload, t, ref_payload, conc, "throughput_mb_per_s")
            if lat is None and tp is None:
                continue
            any_row = True
            lat_s = f'{lat:>9.2f} us' if lat is not None else '        n/a'
            tp_s = f'{tp:>9.1f} MB/s' if tp is not None else '        n/a'
            lines.append(f"  {transport_label(t):<4} p50 {lat_s}   {tp_s}")
        for t in m.transports:
            if t == BASELINE:
                continue
            sp = m.latency_speedup(workload, t, ref_payload, conc)
            if sp is not None:
                lines.append(f"  {transport_label(t)} vs {transport_label(BASELINE)}: "
                             f"{sp:.2f}x latency")
        if not any_row:
            lines.append("  No data")

    if m.skipped:
        lines += ["", f"SKIPPED CELLS: {len(m.skipped)} (see results.json)"]

    return "\n".join(lines)


def generate_consolidated_plot(m: Matrix, out_dir: Path):
    """Single consolidated plot with every measured dimension.

    Layout (all transports present in results.json, UDS as baseline):
      Row 1 -- UNARY:        Latency p50 | Throughput | Speedup vs UDS
      Row 2 -- STREAMING:    Latency p50 | Throughput | Speedup vs UDS
      Row 3 -- TAIL LATENCY: Unary p99   | Streaming p99 | p99/p50 ratio
      Row 4 -- CONCURRENCY:  Latency     | Throughput  | Speedup vs UDS
      Row 5 -- EFFICIENCY:   CPU us/op (unary) | CPU us/op (streaming) | RSS peak
      Row 6 -- SUMMARY:      Peak throughput | Min latency | Advantage vs UDS
      Row 7 -- Text summary
    """
    out_dir.mkdir(parents=True, exist_ok=True)

    plt.style.use('default')
    plt.rcParams.update({
        'figure.facecolor': 'white',
        'axes.facecolor': 'white',
        'axes.grid': True,
        'grid.alpha': 0.3,
        'font.size': 9,
    })

    payloads = m.payloads
    labels = [format_size(p) for p in payloads]
    conc = m.ref_concurrency()
    ref_payload = m.ref_payload(1024)
    ref_label = format_size(ref_payload)

    fig = plt.figure(figsize=(18, 36))
    gs = fig.add_gridspec(7, 3, hspace=0.45, wspace=0.25, top=0.955, bottom=0.03,
                          height_ratios=[1, 1, 1, 1, 1, 1, 0.9])

    fig.suptitle(
        f'gRPC Shared Memory Transport — Complete gRFC G3 Matrix '
        f'({_baseline_note(m)})\n{m.subtitle()}',
        fontsize=15, fontweight='bold', y=0.985)

    # ============================================================
    # ROWS 1-2: per-workload latency / throughput / speedup
    # ============================================================
    for row, workload in enumerate(("unary", "streaming")):
        tag = workload.upper()

        ax = fig.add_subplot(gs[row, 0])
        lat = m.transport_series(workload, payloads, conc, "p50")
        if _any_value(lat):
            x, width = grouped_bars(ax, labels, lat, fontsize=8)
            ax.set_xlabel('Payload Size')
            ax.set_ylabel('Latency p50 (µs)')
            ax.set_title(f'{tag} — Latency p50 (c={conc})\n(lower is better)',
                         fontweight='bold')
            ax.set_xticklabels(labels, rotation=45, ha='right')
            ax.legend(loc='upper left', fontsize=8)
            _log_if_wide(ax, lat)
            speedups = [m.latency_speedup(workload, 'shm', p, conc) for p in payloads]
            annotate_speedup(ax, x, width, lat, speedups, fontsize=7)
        else:
            _no_data(ax, f'{tag} — Latency p50')

        ax = fig.add_subplot(gs[row, 1])
        tp = m.transport_series(workload, payloads, conc, "throughput_mb_per_s")
        if _any_value(tp):
            grouped_bars(ax, labels, tp, fontsize=8)
            ax.set_xlabel('Payload Size')
            ax.set_ylabel('Throughput (MB/s)')
            ax.set_title(f'{tag} — Throughput (c={conc})\n(higher is better)',
                         fontweight='bold')
            ax.set_xticklabels(labels, rotation=45, ha='right')
            ax.legend(loc='upper left', fontsize=8)
            _log_if_wide(ax, tp)
        else:
            _no_data(ax, f'{tag} — Throughput')

        ax = fig.add_subplot(gs[row, 2])
        sp = m.speedup_series(workload, payloads, conc)
        if _any_value(sp):
            grouped_bars(ax, labels, sp, fontsize=8)
            ax.set_xlabel('Payload Size')
            ax.set_ylabel(f'Latency speedup vs {transport_label(BASELINE)} (x)')
            ax.set_title(f'{tag} — Speedup over {transport_label(BASELINE)}\n'
                         f'(higher is better)', fontweight='bold')
            ax.set_xticklabels(labels, rotation=45, ha='right')
            ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
            ax.legend(loc='upper right', fontsize=8)
        else:
            _no_data(ax, f'{tag} — Speedup over {transport_label(BASELINE)}')

    # ============================================================
    # ROW 3: tail latency
    # ============================================================
    for col, workload in enumerate(("unary", "streaming")):
        ax = fig.add_subplot(gs[2, col])
        p99 = m.transport_series(workload, payloads, conc, "p99")
        if _any_value(p99):
            grouped_bars(ax, labels, p99, fontsize=8)
            ax.set_xlabel('Payload Size')
            ax.set_ylabel('Latency p99 (µs)')
            ax.set_title(f'{workload.upper()} — Tail Latency p99 (c={conc})\n'
                         f'(lower is better)', fontweight='bold')
            ax.set_xticklabels(labels, rotation=45, ha='right')
            ax.legend(loc='upper left', fontsize=8)
            _log_if_wide(ax, p99)
        else:
            _no_data(ax, f'{workload.upper()} — Tail Latency p99')

    # p99/p50 jitter ratio for streaming
    ax = fig.add_subplot(gs[2, 2])
    jitter = []
    for t in m.transports:
        vals = []
        for p in payloads:
            p50 = m.value("streaming", t, p, conc, "p50")
            p99 = m.value("streaming", t, p, conc, "p99")
            vals.append(p99 / p50 if p50 and p99 else None)
        jitter.append((t, vals))
    if _any_value(jitter):
        grouped_bars(ax, labels, jitter, fontsize=8)
        ax.set_xlabel('Payload Size')
        ax.set_ylabel('p99 / p50')
        ax.set_title(f'STREAMING — Tail Inflation p99/p50 (c={conc})\n'
                     f'(lower is better)', fontweight='bold')
        ax.set_xticklabels(labels, rotation=45, ha='right')
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
        ax.legend(loc='upper left', fontsize=8)
    else:
        _no_data(ax, 'STREAMING — Tail Inflation p99/p50')

    # ============================================================
    # ROW 4: concurrency scaling at the reference payload
    # ============================================================
    conc_levels = m.concurrency
    conc_labels = [f'c={c}' for c in conc_levels]

    ax = fig.add_subplot(gs[3, 0])
    lat_c = [(t, m.conc_series("unary", t, ref_payload, conc_levels, "p50"))
             for t in m.transports]
    if _any_value(lat_c):
        grouped_bars(ax, conc_labels, lat_c, fontsize=8)
        ax.set_xlabel('Concurrency')
        ax.set_ylabel('Latency p50 (µs)')
        ax.set_title(f'UNARY — Latency vs Concurrency @ {ref_label}\n'
                     f'(lower is better)', fontweight='bold')
        ax.legend(loc='upper left', fontsize=8)
        _log_if_wide(ax, lat_c)
    else:
        _no_data(ax, 'UNARY — Latency vs Concurrency')

    ax = fig.add_subplot(gs[3, 1])
    tp_c = [(t, m.conc_series("streaming", t, ref_payload, conc_levels,
                              "throughput_mb_per_s"))
            for t in m.transports]
    if _any_value(tp_c):
        grouped_bars(ax, conc_labels, tp_c, fontsize=8)
        ax.set_xlabel('Concurrency')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title(f'STREAMING — Throughput vs Concurrency @ {ref_label}\n'
                     f'(higher is better)', fontweight='bold')
        ax.legend(loc='upper left', fontsize=8)
        _log_if_wide(ax, tp_c)
    else:
        _no_data(ax, 'STREAMING — Throughput vs Concurrency')

    ax = fig.add_subplot(gs[3, 2])
    sp_c = []
    for t in m.transports:
        if t == BASELINE:
            continue
        sp_c.append((t, [m.latency_speedup("streaming", t, ref_payload, c)
                         for c in conc_levels]))
    if _any_value(sp_c):
        grouped_bars(ax, conc_labels, sp_c, fontsize=8)
        ax.set_xlabel('Concurrency')
        ax.set_ylabel(f'Latency speedup vs {transport_label(BASELINE)} (x)')
        ax.set_title(f'STREAMING — Speedup over {transport_label(BASELINE)} '
                     f'@ {ref_label}\n(higher is better)', fontweight='bold')
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
        ax.legend(loc='upper right', fontsize=8)
    else:
        _no_data(ax, f'STREAMING — Speedup over {transport_label(BASELINE)}')

    # ============================================================
    # ROW 5: CPU / memory efficiency
    # ============================================================
    for col, workload in enumerate(("unary", "streaming")):
        ax = fig.add_subplot(gs[4, col])
        cpu = m.transport_series(workload, payloads, conc, "cpu_us_per_op")
        if _any_value(cpu):
            grouped_bars(ax, labels, cpu, fontsize=8)
            ax.set_xlabel('Payload Size')
            ax.set_ylabel('CPU (µs/op)')
            ax.set_title(f'{workload.upper()} — CPU Cost (c={conc})\n'
                         f'(lower is better)', fontweight='bold')
            ax.set_xticklabels(labels, rotation=45, ha='right')
            ax.legend(loc='upper left', fontsize=8)
            _log_if_wide(ax, cpu)
        else:
            _no_data(ax, f'{workload.upper()} — CPU Cost')

    ax = fig.add_subplot(gs[4, 2])
    rss = m.transport_series("streaming", payloads, conc, "rss_peak_mb")
    if _any_value(rss):
        grouped_bars(ax, labels, rss, fontsize=8)
        ax.set_xlabel('Payload Size')
        ax.set_ylabel('Peak RSS (MB)')
        ax.set_title(f'STREAMING — Peak RSS (c={conc})\n(lower is better)',
                     fontweight='bold')
        ax.set_xticklabels(labels, rotation=45, ha='right')
        ax.legend(loc='upper left', fontsize=8)
    else:
        _no_data(ax, 'STREAMING — Peak RSS')

    # ============================================================
    # ROW 6: peak / floor summary per transport
    # ============================================================
    ax = fig.add_subplot(gs[5, 0])
    peaks = [(t, m.extremum(t, "throughput_mb_per_s")) for t in m.transports]
    peaks = [(t, v) for t, v in peaks if v is not None]
    if peaks:
        bars = ax.bar([transport_label(t) for t, _v in peaks], [v for _t, v in peaks],
                      color=[transport_color(t) for t, _v in peaks],
                      edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('Peak Throughput (all cells)\n(higher is better)', fontweight='bold')
        for bar, (_t, val) in zip(bars, peaks):
            label = f'{val / 1000:.1f} GB/s' if val >= 1000 else f'{val:.0f} MB/s'
            ax.annotate(label, xy=(bar.get_x() + bar.get_width() / 2, val),
                        xytext=(0, 3), textcoords='offset points',
                        ha='center', fontsize=9, fontweight='bold')
    else:
        _no_data(ax, 'Peak Throughput')

    ax = fig.add_subplot(gs[5, 1])
    floors = [(t, m.extremum(t, "p50", how=min)) for t in m.transports]
    floors = [(t, v) for t, v in floors if v is not None]
    if floors:
        bars = ax.bar([transport_label(t) for t, _v in floors], [v for _t, v in floors],
                      color=[transport_color(t) for t, _v in floors],
                      edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Latency p50 (µs)')
        ax.set_title('Minimum p50 Latency (all cells)\n(lower is better)',
                     fontweight='bold')
        for bar, (_t, val) in zip(bars, floors):
            ax.annotate(f'{val:.2f} µs', xy=(bar.get_x() + bar.get_width() / 2, val),
                        xytext=(0, 3), textcoords='offset points',
                        ha='center', fontsize=9, fontweight='bold')
    else:
        _no_data(ax, 'Minimum p50 Latency')

    # Advantage over the UDS baseline across the workload/scale corners that
    # were actually measured -- every category is labelled with its real
    # payload, never a hard-coded index.
    ax = fig.add_subplot(gs[5, 2])
    corners = []
    largest = m.payloads[-1] if m.payloads else None
    peak_conc = m.peak_concurrency()
    for workload in ("unary", "streaming"):
        if ref_payload is not None:
            corners.append((f'{workload[:6].capitalize()}\n{ref_label} c={conc}',
                            workload, ref_payload, conc))
        if largest is not None and largest != ref_payload:
            corners.append((f'{workload[:6].capitalize()}\n{format_size(largest)} c={conc}',
                            workload, largest, conc))
        if peak_conc is not None and peak_conc != conc and ref_payload is not None:
            corners.append((f'{workload[:6].capitalize()}\n{ref_label} c={peak_conc}',
                            workload, ref_payload, peak_conc))

    adv = []
    for t in m.transports:
        if t == BASELINE:
            continue
        adv.append((t, [m.latency_speedup(w, t, p, c) for _lab, w, p, c in corners]))
    if corners and _any_value(adv):
        grouped_bars(ax, [lab for lab, _w, _p, _c in corners], adv, fontsize=8)
        ax.set_ylabel(f'Latency speedup vs {transport_label(BASELINE)} (x)')
        ax.set_title(f'Performance Advantage over {transport_label(BASELINE)}\n'
                     f'(higher is better)', fontweight='bold')
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
        ax.legend(loc='upper right', fontsize=8)
    else:
        _no_data(ax, f'Performance Advantage over {transport_label(BASELINE)}')

    # ============================================================
    # ROW 7: text summary
    # ============================================================
    ax = fig.add_subplot(gs[6, :])
    ax.axis('off')

    sep = "═" * 118
    lines = [
        sep,
        f"BENCHMARK SUMMARY  -  gRFC G3 matrix, {transport_label(BASELINE)} baseline "
        f"(latency ratios are {BASELINE}/transport; >1 means the transport wins)",
        sep,
        "",
    ]
    for workload in (m.workloads or []):
        for p in payloads:
            cells = [(t, m.value(workload, t, p, conc, "p50"),
                      m.value(workload, t, p, conc, "throughput_mb_per_s"))
                     for t in m.transports]
            cells = [c for c in cells if c[1] is not None or c[2] is not None]
            if not cells:
                continue
            parts = []
            for t, lat, tp in cells:
                lat_s = f'{lat:.2f}us' if lat is not None else 'n/a'
                tp_s = f'{tp:.0f}MB/s' if tp is not None else 'n/a'
                parts.append(f'{transport_label(t)} {lat_s}/{tp_s}')
            sp = m.latency_speedup(workload, 'shm', p, conc)
            sp_s = f'   SHM vs UDS: {sp:.2f}x' if sp is not None else ''
            lines.append(f"{workload.upper():<10} {format_size(p):>6} (c={conc}):  "
                         + "   ".join(parts) + sp_s)
    if len(lines) == 4:
        lines.append("No measured cells.")
    if m.skipped:
        lines += ["", f"SKIPPED ({len(m.skipped)}): "
                      + "; ".join(str(s) for s in m.skipped[:3])
                      + (" ..." if len(m.skipped) > 3 else "")]
    lines += ["", f"{m.subtitle()}", sep]

    ax.text(0.5, 0.5, "\n".join(lines), transform=ax.transAxes, fontsize=9,
            verticalalignment='center', horizontalalignment='center',
            fontfamily='monospace',
            bbox=dict(boxstyle='round', facecolor='#f0f0f0', edgecolor='gray', alpha=0.8))

    consolidated_file = out_dir / "benchmark_consolidated.png"
    plt.savefig(consolidated_file, dpi=150, bbox_inches='tight', facecolor='white')
    plt.close()
    print(f"Created: {consolidated_file}")

    return consolidated_file


# =========================================================================
# Cross-platform comparison
# =========================================================================

PLATFORM_STYLES = ['-', '--', ':', '-.']


def generate_cross_platform_plots(matrices: list, out_dir: Path = None):
    """Compare the same matrix measured on several platforms/arms.

    *matrices* is [(label, Matrix)]. Series are generated from the transports
    actually present in each file -- colour encodes the transport, line style
    encodes the platform, so a 2- or 4-transport run needs no code change.
    """
    compare_dir = out_dir or (OUT_ROOT / "comparison")
    compare_dir.mkdir(parents=True, exist_ok=True)

    plt.style.use('default')
    plt.rcParams.update({
        'figure.facecolor': 'white',
        'axes.facecolor': 'white',
        'axes.grid': True,
        'grid.alpha': 0.3,
        'font.size': 10,
    })

    all_payloads = sorted({p for _lab, m in matrices for p in m.payloads})
    all_labels = [format_size(p) for p in all_payloads]
    workloads = []
    for _lab, m in matrices:
        for w in m.workloads:
            if w not in workloads:
                workloads.append(w)

    # ================================================================
    # Plot 1: SHM speedup over the UDS baseline, per platform
    # ================================================================
    for workload in workloads:
        series = []
        for label, m in matrices:
            conc = m.ref_concurrency()
            series.append((label,
                           [m.latency_speedup(workload, 'shm', p, conc)
                            for p in all_payloads]))

        if not any(v is not None for _l, vals in series for v in vals):
            continue

        fig, ax = plt.subplots(figsize=(14, 6))
        x = np.arange(len(all_labels))
        n = len(series)
        w = 0.8 / n
        for i, (label, vals) in enumerate(series):
            offset = (i - (n - 1) / 2) * w
            ax.bar(x + offset, [np.nan if v is None else v for v in vals], w,
                   label=label, color=FALLBACK_COLORS[i % len(FALLBACK_COLORS)],
                   edgecolor='black', linewidth=0.5)
            for j, v in enumerate(vals):
                if v is not None:
                    ax.annotate(f'{v:.2f}', xy=(x[j] + offset, v), xytext=(0, 3),
                                textcoords='offset points', ha='center', fontsize=8,
                                fontweight='bold')
        for j in range(len(all_labels)):
            if all(vals[j] is None for _l, vals in series):
                ax.annotate('No data', xy=(x[j], 0.03), xycoords=('data', 'axes fraction'),
                            ha='center', va='bottom', fontsize=7,
                            color='#888888', rotation=90)

        ax.axhline(y=1.0, color='#e74c3c', linestyle='--', linewidth=1.5, alpha=0.8,
                   label='Break-even (1.0)')
        ax.set_xlabel('Payload Size', fontsize=12)
        ax.set_ylabel(f'SHM speedup vs {transport_label(BASELINE)} (x, higher = SHM wins)',
                      fontsize=12)
        ax.set_title(f'{workload.capitalize()}: SHM Latency Speedup over '
                     f'{transport_label(BASELINE)}', fontsize=14, fontweight='bold')
        ax.set_xlim(-0.5, len(all_labels) - 0.5)
        ax.margins(y=0.15)  # headroom so the value labels clear the legend
        ax.set_xticks(x)
        ax.set_xticklabels(all_labels)
        ax.legend(loc='upper right')

        plt.tight_layout()
        fname = compare_dir / f"{workload}_speedup.png"
        plt.savefig(fname, dpi=150, bbox_inches='tight', facecolor='white')
        plt.close()
        print(f"Created: {fname}")

    # ================================================================
    # Plots 2 & 3: latency / throughput lines, one series per
    #              (platform, transport) pair present in the data
    # ================================================================
    for metric, unit, fname_suffix, marker in (
            ("p50", "Latency p50 (µs, log scale)", "latency", 'o'),
            ("throughput_mb_per_s", "Throughput (MB/s, log scale)", "throughput", 's')):
        for workload in workloads:
            fig, ax = plt.subplots(figsize=(14, 7))
            plotted = False
            for pi, (label, m) in enumerate(matrices):
                conc = m.ref_concurrency()
                style = PLATFORM_STYLES[pi % len(PLATFORM_STYLES)]
                for transport in m.transports:
                    xs, ys = [], []
                    for i, p in enumerate(all_payloads):
                        v = m.value(workload, transport, p, conc, metric)
                        if v is not None and v > 0:
                            xs.append(i)
                            ys.append(v)
                    if xs:
                        plotted = True
                        ax.plot(xs, ys, marker=marker, markersize=5,
                                color=transport_color(transport), linestyle=style,
                                linewidth=2,
                                label=f'{label} {transport_label(transport)}')

            if not plotted:
                plt.close()
                continue

            ax.set_yscale('log')
            ax.set_xlabel('Payload Size', fontsize=12)
            ax.set_ylabel(unit, fontsize=12)
            ax.set_title(f'{workload.capitalize()}: {fname_suffix.capitalize()} '
                         f'Comparison (log scale)', fontsize=14, fontweight='bold')
            ax.set_xticks(range(len(all_labels)))
            ax.set_xticklabels(all_labels, fontsize=9)
            ax.legend(loc='upper left', fontsize=9)
            ax.grid(True, alpha=0.3)

            plt.tight_layout()
            fname = compare_dir / f"{workload}_{fname_suffix}.png"
            plt.savefig(fname, dpi=150, bbox_inches='tight', facecolor='white')
            plt.close()
            print(f"Created: {fname}")

    print(f"\nAll cross-platform plots saved to: {compare_dir}")


def _matrix_label(path: Path, m: Matrix) -> str:
    """Short series label: OS + arm, falling back to the containing dir."""
    bits = [m.os_label if m.os_label != '?' else path.parent.name]
    if m.arm != '?':
        bits.append(m.arm)
    return '/'.join(bits)


# =========================================================================
# Entry point
# =========================================================================

def main():
    parser = argparse.ArgumentParser(
        description='Run the .NET gRFC G3 benchmark matrix (SHM vs UDS baseline, '
                    'TCP supplementary) and generate plots',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python benchmark_runner.py                       # Use cached results or run if none
  python benchmark_runner.py --run                 # Force rerun the matrix (tuned arm)
  python benchmark_runner.py --run --fair          # Run the fair arm
  python benchmark_runner.py --plot-only           # Only plot, fail if no cached data
  python benchmark_runner.py --results out/linux/fair/results.json --plot-only
  python benchmark_runner.py --compare out/windows/results.json out/linux/results.json
        """
    )
    parser.add_argument('--run', action='store_true',
                        help='Force rerun the benchmark matrix even if cached results exist')
    parser.add_argument('--plot-only', action='store_true',
                        help='Only generate plots, fail if no cached results')
    parser.add_argument('--compare', nargs='*', metavar='RESULTS',
                        help='Cross-platform comparison plots; defaults to '
                             'out/windows/results.json + out/linux/results.json')
    parser.add_argument('--results', metavar='PATH', default=str(RESULTS_FILE),
                        help=f'results.json to read/write (default: {RESULTS_FILE})')

    run_opts = parser.add_argument_group('matrix run options (used with --run)')
    run_opts.add_argument('--fair', action='store_true',
                          help='Run the fair arm (ringbench --fair); default is tuned')
    run_opts.add_argument('--transports', metavar='CSV',
                          help='Transports to measure, e.g. shm,uds,tcp')
    run_opts.add_argument('--workloads', metavar='CSV',
                          help='Workloads to measure, e.g. unary,streaming')
    run_opts.add_argument('--payloads', metavar='CSV',
                          help='Payload sizes in bytes, e.g. 1,1024,65536')
    run_opts.add_argument('--concurrency', metavar='CSV',
                          help='Concurrency levels, e.g. 1,8,64')
    run_opts.add_argument('--warmup-s', metavar='SEC', help='Warmup seconds per cell')
    run_opts.add_argument('--duration-s', metavar='SEC', help='Measured seconds per cell')
    run_opts.add_argument('--reps', metavar='N', help='Repetitions per cell')
    run_opts.add_argument('--timeout', type=int, default=DEFAULT_RUN_TIMEOUT_S,
                          metavar='SEC',
                          help=f'Subprocess timeout in seconds, 0 to disable '
                               f'(default: {DEFAULT_RUN_TIMEOUT_S})')

    args = parser.parse_args()

    results_file = Path(args.results)
    out_dir = results_file.parent

    # Cross-platform comparison mode
    if args.compare is not None:
        paths = [Path(p) for p in args.compare] or [
            OUT_ROOT / "windows" / "results.json",
            OUT_ROOT / "linux" / "results.json",
        ]
        matrices = []
        for p in paths:
            if not p.exists():
                print(f"ERROR: Missing results file: {p}")
                sys.exit(1)
            m = load_matrix(p)
            if m is None:
                sys.exit(1)
            matrices.append((_matrix_label(p, m), m))
        generate_cross_platform_plots(matrices)
        return 0

    if args.run:
        m = run_ringbench(args, results_file)
        if m is None:
            print("ERROR: Benchmark run failed")
            sys.exit(1)
    elif args.plot_only:
        m = load_matrix(results_file)
        if m is None:
            print(f"ERROR: No usable results at {results_file}. Run with --run first.")
            sys.exit(1)
    else:
        m = load_matrix(results_file)
        if m is None:
            print("No cached results found, running benchmarks...")
            m = run_ringbench(args, results_file)
            if m is None:
                print("ERROR: Benchmark run failed")
                sys.exit(1)

    if not m.cells:
        print("WARNING: results.json contains no runs; plots will say 'No data'.")
    if not m.has_baseline():
        print(f"WARNING: no '{BASELINE}' rows found -- the gRFC G3 baseline is "
              f"missing, so speedup panels will be empty. Re-run with "
              f"--transports shm,{BASELINE}[,tcp].")

    plot_files = generate_plots(m, out_dir)
    consolidated_file = generate_consolidated_plot(m, out_dir)

    print("\n" + "=" * 70)
    print("BENCHMARK PLOTS GENERATED")
    print("=" * 70)
    for f in plot_files:
        print(f"  {f}")
    print(f"  {consolidated_file}  <-- CONSOLIDATED (all data)")
    print(f"\nConsolidated plot: {consolidated_file}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
