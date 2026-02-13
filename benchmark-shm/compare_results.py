#!/usr/bin/env python3
"""Compare old (pre-optimization) and new (post-optimization) benchmark results."""

import json, sys

def fmt_size(b):
    if b == 0: return "0B"
    if b < 1024: return f"{b}B"
    if b < 1048576: return f"{b//1024}KB"
    return f"{b//1048576}MB"

old_path = "out/windows/results.json"
new_path = "out/windows/results_opt.json"

with open(old_path) as f: old = json.load(f)
with open(new_path) as f: new = json.load(f)

# Build lookup: (transport, size) -> avg_latency_us
def build_map(data):
    m = {}
    for mode in ("unary", "streaming"):
        for r in data[mode]:
            m[(mode, r["transport"], r["size_bytes"])] = r["avg_latency_us"]
    return m

old_m = build_map(old)
new_m = build_map(new)

sizes = old["sizes_bytes"]

print(f"{'':>6}  {'--- UNARY ---':^56}  {'--- STREAMING ---':^56}")
print(f"{'Size':>6}  {'Old SHM':>9} {'New SHM':>9} {'SHM Δ':>7}  {'Old TCP':>9} {'New TCP':>9}  {'Old X':>6} {'New X':>6}  {'Old SHM':>9} {'New SHM':>9} {'SHM Δ':>7}  {'Old TCP':>9} {'New TCP':>9}  {'Old X':>6} {'New X':>6}")
print("-" * 130)

for sz in sizes:
    ou_shm = old_m.get(("unary","shm",sz), 0)
    nu_shm = new_m.get(("unary","shm",sz), 0)
    ou_tcp = old_m.get(("unary","tcp",sz), 0)
    nu_tcp = new_m.get(("unary","tcp",sz), 0)
    os_shm = old_m.get(("streaming","shm",sz), 0)
    ns_shm = new_m.get(("streaming","shm",sz), 0)
    os_tcp = old_m.get(("streaming","tcp",sz), 0)
    ns_tcp = new_m.get(("streaming","tcp",sz), 0)

    u_delta = f"{ou_shm/nu_shm:.2f}x" if nu_shm > 0 else "N/A"
    s_delta = f"{os_shm/ns_shm:.2f}x" if ns_shm > 0 else "N/A"
    
    old_ux = f"{ou_tcp/ou_shm:.1f}x" if ou_shm > 0 else "N/A"
    new_ux = f"{nu_tcp/nu_shm:.1f}x" if nu_shm > 0 else "N/A"
    old_sx = f"{os_tcp/os_shm:.1f}x" if os_shm > 0 else "N/A"
    new_sx = f"{ns_tcp/ns_shm:.1f}x" if ns_shm > 0 else "N/A"

    print(f"{fmt_size(sz):>6}  {ou_shm:>9.1f} {nu_shm:>9.1f} {u_delta:>7}  {ou_tcp:>9.1f} {nu_tcp:>9.1f}  {old_ux:>6} {new_ux:>6}  {os_shm:>9.1f} {ns_shm:>9.1f} {s_delta:>7}  {os_tcp:>9.1f} {ns_tcp:>9.1f}  {old_sx:>6} {new_sx:>6}")

print()
print("SHM Δ = old_shm / new_shm (>1 = improvement)")
print("X = tcp / shm speedup factor")
print()

# Summary: find biggest absolute SHM improvements
print("=== Biggest SHM latency improvements (old/new ratio) ===")
improvements = []
for mode in ("unary", "streaming"):
    for sz in sizes:
        o = old_m.get((mode,"shm",sz), 0)
        n = new_m.get((mode,"shm",sz), 0)
        if o > 0 and n > 0:
            improvements.append((o/n, mode, sz, o, n))
improvements.sort(reverse=True)
for ratio, mode, sz, o, n in improvements[:10]:
    print(f"  {mode:>9} {fmt_size(sz):>6}: {o:.0f} → {n:.0f} µs  ({ratio:.2f}x faster)")

print()
print("=== SHM regressions (if any) ===")
for ratio, mode, sz, o, n in improvements:
    if ratio < 0.95:
        print(f"  {mode:>9} {fmt_size(sz):>6}: {o:.0f} → {n:.0f} µs  ({ratio:.2f}x, regression)")
if not any(r < 0.95 for r, *_ in improvements):
    print("  None (all sizes improved or within noise)")
