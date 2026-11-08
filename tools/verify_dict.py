# -*- coding: utf-8 -*-
"""Verify that dict/ can rebuild the live dictionary byte-for-byte.

Why this exists: on 2026-10-21 the live dictionary held 9475 rows while dict/ only covered
1024 of them - 6673 real translations had no recorded source, so a lost dictionary could not
be rebuilt. That drift went unnoticed for weeks because nothing ever compared the two sides.

Usage:
    python tools/verify_dict.py [--live <path to dictionary_zh_cn.tsv>]

By default the live dictionary is the repo-root dictionary_zh_cn.tsv, so CI and
contributors need no local game install. Override with SP2_DICT=<path> or --live.
Set SP2_LIVE_GAME_DICT=<path> to compare against an installed copy instead.

Exit code 0 = in sync, 1 = drifted (prints the first few offenders).
Run this after every batch is added, and before building a release package.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
DICT_DIR = os.path.join(ROOT, "dict")
# CI / 贡献者机器上没有作者的 D:\steam... 安装：优先用环境变量 SP2_DICT，
# 否则用仓库根目录的 dictionary_zh_cn.tsv（它是打包时同步出来的权威副本）。
# 只有显式设置 SP2_LIVE_GAME_DICT 时才去读游戏安装目录里的那份。
_env = os.environ.get("SP2_DICT")
if _env:
    DEFAULT_LIVE = _env
elif os.environ.get("SP2_LIVE_GAME_DICT"):
    DEFAULT_LIVE = os.environ["SP2_LIVE_GAME_DICT"]
else:
    DEFAULT_LIVE = os.path.join(ROOT, "dictionary_zh_cn.tsv")


def read_rows(path):
    """[(key, value)] in file order. Lines without a tab are comments/blank and are skipped."""
    rows = []
    with open(path, encoding="utf-8-sig", newline="") as f:
        for n, line in enumerate(f, 1):
            line = line.rstrip("\r\n")
            if not line.strip():
                continue
            if line.count("\t") != 1:
                raise SystemExit("%s:%d has %d TABs (need exactly 1)"
                                 % (path, n, line.count("\t")))
            i = line.find("\t")
            if i > 0:
                rows.append((line[:i], line[i + 1:]))
    return rows


def load_batches():
    """Returns (merged, origin, conflicts). A key defined twice with different values is a
    silent filename-order decision and must be reported, not swallowed."""
    merged, origin, conflicts = {}, {}, []
    for name in sorted(os.listdir(DICT_DIR)):
        if not name.endswith(".tsv"):
            continue
        for k, v in read_rows(os.path.join(DICT_DIR, name)):
            if k in merged and merged[k] != v:
                conflicts.append((k, origin[k], name))
            merged[k] = v
            origin.setdefault(k, name)
    return merged, origin, conflicts


def main():
    live_path = DEFAULT_LIVE
    if "--live" in sys.argv:
        live_path = sys.argv[sys.argv.index("--live") + 1]

    if not os.path.isfile(live_path):
        print("live dictionary not found: " + live_path)
        return 2

    live = dict(read_rows(live_path))
    merged, origin, conflicts = load_batches()

    missing = [k for k in live if k not in merged]
    extra = [k for k in merged if k not in live]
    differ = [k for k in live if k in merged and merged[k] != live[k]]

    print("live rows      : %d" % len(live))
    print("batch merge    : %d" % len(merged))
    print("missing in dict: %d" % len(missing))
    print("extra in dict  : %d" % len(extra))
    print("value mismatch : %d" % len(differ))
    print("batch conflicts: %d" % len(conflicts))
    for k, a, b in conflicts[:5]:
        print("  CONFLICT %r (%s -> %s)" % (k[:70], a, b))

    for label, keys in (("MISSING", missing), ("EXTRA", extra), ("DIFFER", differ)):
        for k in keys[:5]:
            print("  %-7s %s" % (label, repr(k[:70])))
            if label == "DIFFER":
                print("          live : %s" % live[k][:60])
                print("          batch: %s" % merged[k][:60])

    if missing or extra or differ or conflicts:
        print("\nRESULT: dict/ CANNOT rebuild the live dictionary.")
        if extra:
            # 最常见的场景：贡献者新建了批次但没重建运行时字典。
            print("提示：有 %d 条只存在于 dict/ 批次、还没进 dictionary_zh_cn.tsv。" % len(extra))
            print("      运行 `python tools/rebuild_dict.py` 合并后重试。")
        if differ:
            print("提示：%d 条 key 的译文在批次与运行时字典里不一致，" % len(differ))
            print("      同样运行 `python tools/rebuild_dict.py` 同步（会以批次为准）。")
        return 1
    print("\nRESULT: in sync.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
