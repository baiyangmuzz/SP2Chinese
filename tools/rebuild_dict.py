# -*- coding: utf-8 -*-
"""把 dict/ 下的批次合并成运行时字典 dictionary_zh_cn.tsv，并同步 manifest 里的字典字段。

为什么需要这个脚本：
    贡献者按 CONTRIBUTING 新建批次文件后，verify_dict.py 一定会报
    "extra in dict"，因为根目录的 runtime 字典还没包含新词条。旧文档没说清
    这一步，贡献者只能自己猜。这个脚本把「合并 + 更新 manifest」一步做完，
    合并规则与 build_dist.py 完全一致（文件名排序 + 后写覆盖）。

用法：
    python tools/rebuild_dict.py            # 合并并更新 manifest 的字典字段
    python tools/rebuild_dict.py --dry-run  # 只显示会改什么，不写文件
    python tools/rebuild_dict.py --no-manifest  # 只写 dictionary_zh_cn.tsv

之后跑 verify_dict.py / check_dict.py 应当通过（check_dist.py 需要先打包）。
"""
import hashlib
import json
import os
import sys
from collections import OrderedDict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DICT_DIR = os.path.join(ROOT, "dict")
LIVE = os.path.join(ROOT, "dictionary_zh_cn.tsv")
MANIFEST = os.path.join(ROOT, "manifest.json")
DICT_ENTRY = "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv"


def read_rows(path):
    rows = []
    with open(path, encoding="utf-8-sig", newline="") as f:
        for n, line in enumerate(f, 1):
            line = line.rstrip("\r\n")
            if not line.strip():
                continue
            if line.count("\t") != 1:
                raise SystemExit("%s:%d has %d TABs (need exactly 1)"
                                 % (os.path.basename(path), n, line.count("\t")))
            k, v = line.split("\t", 1)
            rows.append((k, v))
    return rows


def main():
    dry_run = "--dry-run" in sys.argv
    with_manifest = "--no-manifest" not in sys.argv

    merged = OrderedDict()
    origin = {}
    conflicts = []
    batch_count = 0
    for name in sorted(os.listdir(DICT_DIR)):
        if not name.endswith(".tsv"):
            continue
        batch_count += 1
        for k, v in read_rows(os.path.join(DICT_DIR, name)):
            if k in merged and merged[k] != v:
                conflicts.append((k, origin[k], name, merged[k], v))
            merged[k] = v
            origin.setdefault(k, name)

    if conflicts:
        print("发现 %d 处跨批次冲突（同一 key 在不同批次里译文不同）：" % len(conflicts))
        for k, a, b, va, vb in conflicts[:10]:
            print("  key  : %r" % k[:70])
            print("    %s -> %r" % (a, va[:50]))
            print("    %s -> %r" % (b, vb[:50]))
        print("\n请先在一处修改，使两批次的译文一致（或删掉重复行），再重新运行。")
        print("RESULT: FAILED (conflicts)")
        return 1

    data = ("\n".join(k + "\t" + v for k, v in merged.items()) + "\n").encode("utf-8")
    sha = hashlib.sha256(data).hexdigest()

    old_rows = 0
    if os.path.isfile(LIVE):
        old = open(LIVE, "rb").read()
        old_rows = sum(1 for l in old.decode("utf-8-sig").splitlines() if "\t" in l)

    print("批次文件    : %d" % batch_count)
    print("合并后行数  : %d（原 %d，%+d）" % (len(merged), old_rows, len(merged) - old_rows))
    print("字典 SHA256 : %s" % sha)

    if dry_run:
        print("\n--dry-run：未写入任何文件")
        return 0

    with open(LIVE, "wb") as f:
        f.write(data)
    print("已写入      : dictionary_zh_cn.tsv (%d 字节)" % len(data))

    if with_manifest and os.path.isfile(MANIFEST):
        with open(MANIFEST, encoding="utf-8-sig") as f:
            mf = json.load(f)
        mf["dictSha256"] = sha
        mf["dictVersion"] = sha[:12]
        mf["dictRows"] = len(merged)
        files = mf.get("files") or {}
        if DICT_ENTRY in files:
            files[DICT_ENTRY]["sha256"] = sha
            files[DICT_ENTRY]["size"] = len(data)
        with open(MANIFEST, "w", encoding="utf-8", newline="\n") as f:
            json.dump(mf, f, ensure_ascii=False, indent=2)
            f.write("\n")
        print("已更新      : manifest.json 的 dictSha256 / dictVersion / dictRows / files 条目")
        print("              （dictBuild 与 payload 哈希要打包时才更新，见 build_dist.py）")

    print()
    print("下一步：")
    print("  python tools/verify_dict.py")
    print("  python tools/check_dict.py")
    return 0


if __name__ == "__main__":
    sys.exit(main())
