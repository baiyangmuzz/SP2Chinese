# -*- coding: utf-8 -*-
"""Verify that dict/ can rebuild the live dictionary byte-for-byte.

Why this exists: on 2026-10-21 the live dictionary held 9475 rows while dict/ only covered
1024 of them - 6673 real translations had no recorded source, so a lost dictionary could not
be rebuilt. That drift went unnoticed for weeks because nothing ever compared the two sides.

Usage:
    python tools/verify_dict.py [--live <path to dictionary_zh_cn.tsv>]

Exit code 0 = in sync, 1 = drifted (prints the first few offenders).
Run this after every batch is added, and before building a release package.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DICT_DIR = os.path.join(os.path.dirname(HERE), "dict")
DEFAULT_LIVE = r"D:\steam\steamapps\common\SimplePlanes 2\BepInEx\plugins\SP2Chinese\dictionary_zh_cn.tsv"


def read_rows(path):
    """[(key, value)] in file order. Lines without a tab are comments/blank and are skipped."""
    rows = []
    with open(path, encoding="utf-8-sig") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line.strip():
                continue
            i = line.find("\t")
            if i > 0:
                rows.append((line[:i], line[i + 1:]))
    return rows


def load_batches():
    merged, origin = {}, {}
    for name in sorted(os.listdir(DICT_DIR)):
        if not name.endswith(".tsv"):
            continue
        for k, v in read_rows(os.path.join(DICT_DIR, name)):
            merged[k] = v
            origin.setdefault(k, name)
    return merged, origin


def main():
    live_path = DEFAULT_LIVE
    if "--live" in sys.argv:
        live_path = sys.argv[sys.argv.index("--live") + 1]

    if not os.path.isfile(live_path):
        print("live dictionary not found: " + live_path)
        return 2

    live = dict(read_rows(live_path))
    merged, origin = load_batches()

    missing = [k for k in live if k not in merged]
    extra = [k for k in merged if k not in live]
    differ = [k for k in live if k in merged and merged[k] != live[k]]

    print("live rows      : %d" % len(live))
    print("batch merge    : %d" % len(merged))
    print("missing in dict: %d" % len(missing))
    print("extra in dict  : %d" % len(extra))
    print("value mismatch : %d" % len(differ))

    for label, keys in (("MISSING", missing), ("EXTRA", extra), ("DIFFER", differ)):
        for k in keys[:5]:
            print("  %-7s %s" % (label, repr(k[:70])))
            if label == "DIFFER":
                print("          live : %s" % live[k][:60])
                print("          batch: %s" % merged[k][:60])

    if missing or extra or differ:
        print("\nRESULT: dict/ CANNOT rebuild the live dictionary.")
        return 1
    print("\nRESULT: in sync.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
