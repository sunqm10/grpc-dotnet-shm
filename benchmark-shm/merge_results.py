#!/usr/bin/env python3
"""Merge re-measured cells from a patch run into a base grfc-g3-bench-v1 file.

Re-running a handful of cells is cheaper than re-running a multi-hour matrix,
but the patched cells are usually measured at a *different* commit than the
base. Silently mixing them would make the single top-level "commit" field a
lie, so every replaced cell records where it actually came from:

    runs[i]["measured_at_commit"] = "<sha of the patch run>"

Cells inherited from the base run are left unannotated; the top-level
"commit" still describes them. A "patched" block records the merge itself.

Both files must agree on schema, lang, os, arch and arm - merging a fair-arm
cell into a tuned-arm file, or a Linux cell into a Windows file, is refused.

Usage:
    python -I merge_results.py BASE.json PATCH.json [-o OUT.json]

With no -o the base file is updated in place (after a .bak copy).
"""

import argparse
import json
import shutil
import sys

SCHEMA = "grfc-g3-bench-v1"
KEY_FIELDS = ("workload", "transport", "payload_bytes", "concurrency")
# Merging across any of these would compare measurements that are not
# comparable, so a mismatch is a hard error rather than a warning.
MUST_MATCH = ("schema", "lang", "os", "arch", "arm")


def cell_key(run):
    return tuple(run[f] for f in KEY_FIELDS)


def key_str(key):
    workload, transport, payload, conc = key
    return f"{transport}/{workload}/{payload}B/c{conc}"


def load(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    if data.get("schema") != SCHEMA:
        sys.exit(f"{path}: expected schema {SCHEMA}, got {data.get('schema')!r}")
    return data


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("base", help="results.json to merge into")
    ap.add_argument("patch", help="results.json holding the re-measured cells")
    ap.add_argument("-o", "--out", help="write here instead of updating base in place")
    ap.add_argument("--allow-commit-mismatch", action="store_true",
                    help="permitted by default; kept for symmetry with stricter callers")
    args = ap.parse_args()

    base = load(args.base)
    patch = load(args.patch)

    for field in MUST_MATCH:
        if base.get(field) != patch.get(field):
            sys.exit(f"refusing to merge: {field} differs "
                     f"({base.get(field)!r} vs {patch.get(field)!r})")

    base_commit = base.get("commit")
    patch_commit = patch.get("commit")
    if patch.get("dirty"):
        sys.exit("refusing to merge: patch run was measured with a dirty tree")

    by_key = {cell_key(r): i for i, r in enumerate(base["runs"])}
    replaced, added = [], []

    for run in patch["runs"]:
        key = cell_key(run)
        merged = dict(run)
        if patch_commit != base_commit:
            merged["measured_at_commit"] = patch_commit
        if key in by_key:
            base["runs"][by_key[key]] = merged
            replaced.append(key)
        else:
            by_key[key] = len(base["runs"])
            base["runs"].append(merged)
            added.append(key)

    # A cell that has just been re-measured successfully should no longer be
    # listed as skipped.
    touched = {key_str(k) for k in replaced + added}
    before = base.get("skipped", [])
    kept = [s for s in before if s.split(" rep")[0] not in touched]
    cleared = len(before) - len(kept)
    if kept:
        base["skipped"] = kept
    else:
        base.pop("skipped", None)

    if patch_commit != base_commit:
        base.setdefault("patched", []).append({
            "commit": patch_commit,
            "cells": sorted(key_str(k) for k in replaced + added),
            "reason": "re-measured after a benchmark-harness fix; "
                      "the transport under test is unchanged",
        })

    out = args.out or args.base
    if out == args.base:
        shutil.copyfile(args.base, args.base + ".bak")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(base, f, indent=2)

    print(f"base  : {args.base}  ({base_commit})")
    print(f"patch : {args.patch}  ({patch_commit})")
    print(f"replaced {len(replaced)} cell(s), added {len(added)}, "
          f"cleared {cleared} skipped entr(ies)")
    for k in replaced + added:
        print("   ", key_str(k))
    if patch_commit != base_commit:
        print("note  : patched cells carry measured_at_commit; "
              "top-level commit still describes the rest")
    print(f"wrote : {out}")


if __name__ == "__main__":
    main()
