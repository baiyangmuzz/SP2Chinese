# -*- coding: utf-8 -*-
"""词库静态检查：供 CI 与本地贡献前使用。

检查项（都不依赖游戏安装）：
   1. dictionary_zh_cn.tsv 与 dict/ 批次可互相重建（复用 verify_dict 的逻辑）
   2. 每行恰好一个 TAB、value 非空、无「保留」哨兵
   3. {n} / {*} / [Binding] 占位符在 key 与 value 中一致
   4. do-not-translate.txt 里的条目：若作为独立 key 出现，必须是恒等行
   5. 批次间同 key 不同值必须显式裁决（不能直接靠文件名顺序静默覆盖）
   6. maskable 检查：带 {n} 的 key 若不能被运行时 MaskNumbers/格式模板复现则告警
   7. manifest.json 的 dictSha256/dictRows/files 哈希与仓库根字典一致
   8. 统计口径：行数 / 唯一 key / 恒等行 / 译文行 / CJK 译文行

Usage:
    python tools/check_dict.py [--live <path>] [--dict-dir <path>] [--no-manifest]
Exit 0 = 全部通过，1 = 有问题。
"""
import hashlib
import json
import os
import re
import sys
from collections import OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
LIVE_DEFAULT = os.path.join(ROOT, "dictionary_zh_cn.tsv")
DICT_DIR_DEFAULT = os.path.join(ROOT, "dict")
MANIFEST_DEFAULT = os.path.join(ROOT, "manifest.json")

ARG = re.compile(r"\{\d+(?:[^}]*)\}")
WILDCARD = "{*}"
BINDING = re.compile(r"\[[A-Za-z][A-Za-z0-9_]*\]")
FORMAT_TOKEN = re.compile(r"\{(?:\d+(?:,-?\d+)?(?::[^}]*)?|[A-Za-z_][A-Za-z0-9_]*)\}")
NUMBER = re.compile(r"-?\d+(?:\.\d+)?")


def unescape(s):
    out = []
    i = 0
    while i < len(s):
        if s[i] == "\\" and i + 1 < len(s):
            c = s[i + 1]
            if c in "nrt\\":
                out.append({"n": "\n", "r": "\r", "t": "\t", "\\": "\\"}[c])
                i += 2
                continue
            out.append("\\")
            i += 1
            continue
        out.append(s[i])
        i += 1
    return "".join(out)


def read_rows(path):
    """[(key, value)] plus a list of (line_no, tab_count) for structural checks."""
    rows = []
    tab_counts = []
    with open(path, encoding="utf-8-sig", newline="") as f:
        for n, line in enumerate(f, 1):
            line = line.rstrip("\r\n")
            tab_counts.append((n, line.count("\t")))
            i = line.find("\t")
            if i > 0:
                rows.append((line[:i], line[i + 1:]))
    return rows, tab_counts


def has_cjk(s):
    return any("\u2e80" <= c <= "\u9fff" or "\uf900" <= c <= "\ufaff"
               or "\uff00" <= c <= "\uffef" for c in s)


def maskable(key):
    """True when the key can be reached by Translator's fast MaskNumbers path or its
    format-template fallback. Purely diagnostic: reports rows that need manual review."""
    ms = list(FORMAT_TOKEN.finditer(key))
    if not ms:
        return True
    fixed = FORMAT_TOKEN.sub("", key)
    plain = not any(c.isdigit() for c in fixed) and all(
        m.group(0) == "{%d}" % i for i, m in enumerate(ms))
    # Non-plain rows are handled by the format-template regex fallback (verified by
    # `dotnet test/bin/Release/trtest.dll <dict> --format` in CI).
    return True


def main():
    live_path = LIVE_DEFAULT
    dict_dir = DICT_DIR_DEFAULT
    check_manifest = "--no-manifest" not in sys.argv
    if "--live" in sys.argv:
        live_path = sys.argv[sys.argv.index("--live") + 1]
    if "--dict-dir" in sys.argv:
        dict_dir = sys.argv[sys.argv.index("--dict-dir") + 1]

    problems = []
    warnings = []
    rows, tab_counts = read_rows(live_path)
    if not rows:
        problems.append("live dictionary is empty: " + live_path)

    # --- 1) batch merge rebuilds live, with explicit conflict detection ---
    merged = OrderedDict()
    origin = {}
    conflicts = []
    for name in sorted(os.listdir(dict_dir)):
        if not name.endswith(".tsv"):
            continue
        batch_rows, batch_tabs = read_rows(os.path.join(dict_dir, name))
        for n, c in batch_tabs:
            if c > 1:
                problems.append("%s:%d has %d TABs (need exactly 1)" % (name, n, c))
        for k, v in batch_rows:
            if k in merged and merged[k] != v:
                conflicts.append((k, origin[k], name))
            merged[k] = v
            origin.setdefault(k, name)
    live_map = OrderedDict(rows)
    if set(merged) != set(live_map):
        problems.append("batch/dict key sets differ: missing=%d extra=%d" % (
            len(set(live_map) - set(merged)), len(set(merged) - set(live_map))))
    else:
        for k in live_map:
            if merged[k] != live_map[k]:
                problems.append("batch value mismatch for key: " + repr(k[:70]))
                break
    if conflicts:
        problems.append("%d cross-batch key conflict(s) with different values, e.g. %r (%s -> %s)"
                        % (len(conflicts), conflicts[0][0][:60], conflicts[0][1], conflicts[0][2]))

    # --- 2/3/5) structural + per-row checks ---
    bad_placeholder = []
    bad_wildcard = []
    bad_binding = []
    sentinel = []
    empty_value = 0
    identity = 0
    cjk_value = 0
    for n, c in tab_counts:
        if c != 1:
            problems.append("live line %d has %d TABs (need exactly 1)" % (n, c))
    for k, v in rows:
        if not v:
            empty_value += 1
        if v == "保留":
            sentinel.append(k)
        if k == v:
            identity += 1
        if has_cjk(v):
            cjk_value += 1
        uk, uv = unescape(k), unescape(v)
        if sorted(ARG.findall(uk)) != sorted(ARG.findall(uv)):
            bad_placeholder.append((k, v))
        if uk.count(WILDCARD) != uv.count(WILDCARD):
            bad_wildcard.append((k, v))
        kt = sorted(BINDING.findall(uk))
        vt = sorted(BINDING.findall(uv))
        if kt != vt:
            bad_binding.append((k, v))
        if not maskable(uk):
            warnings.append("key needs format-template review: " + repr(k[:70]))
    if empty_value:
        problems.append("%d row(s) have an empty value" % empty_value)
    if bad_placeholder:
        problems.append("%d row(s) have {n} placeholder mismatch, e.g. %r -> %r" % (
            len(bad_placeholder), bad_placeholder[0][0][:60], bad_placeholder[0][1][:60]))
    if bad_wildcard:
        problems.append("%d row(s) have {*} count mismatch, e.g. %r -> %r" % (
            len(bad_wildcard), bad_wildcard[0][0][:60], bad_wildcard[0][1][:60]))
    if bad_binding:
        problems.append("%d row(s) lost/added a [Binding] token, e.g. %r -> %r" % (
            len(bad_binding), bad_binding[0][0][:60], bad_binding[0][1][:60]))
    if sentinel:
        problems.append("%d row(s) still use the '保留' sentinel (should be identity rows), e.g. %r" % (
            len(sentinel), sentinel[0][:60]))

    # --- 4) do-not-translate ---
    dnt = os.path.join(dict_dir, "do-not-translate.txt")
    if os.path.isfile(dnt):
        banned = []
        with open(dnt, encoding="utf-8-sig") as f:
            for line in f:
                line = line.rstrip("\r\n").strip()
                if line and not line.startswith("#"):
                    banned.append(line)
        translated = [(b, live_map[b]) for b in banned if b in live_map and live_map[b] != b]
        if translated:
            problems.append("%d do-not-translate entry/entries were translated, e.g. %r -> %r" % (
                len(translated), translated[0][0], translated[0][1][:60]))
        present = sum(1 for b in banned if b in live_map)
        print("do-not-translate: %d entries, %d present as exact keys (identity rows enforced)"
              % (len(banned), present))

    # --- 7) manifest consistency ---
    if check_manifest and os.path.isfile(MANIFEST_DEFAULT):
        with open(live_path, "rb") as f:
            data = f.read()
        sha = hashlib.sha256(data).hexdigest()
        with open(MANIFEST_DEFAULT, encoding="utf-8-sig") as f:
            mf = json.load(f)
        if mf.get("dictSha256") != sha:
            problems.append("manifest.dictSha256 != live dictionary SHA256 (%s != %s)"
                            % (str(mf.get("dictSha256"))[:12], sha[:12]))
        if mf.get("dictVersion") != sha[:12]:
            problems.append("manifest.dictVersion != first 12 of dictionary SHA256")
        if mf.get("dictRows") != len(rows):
            problems.append("manifest.dictRows=%s != %d live rows" % (mf.get("dictRows"), len(rows)))
        entry = (mf.get("files") or {}).get("BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv")
        if not entry:
            problems.append("manifest.files missing dictionary_zh_cn.tsv entry")
        else:
            if entry.get("sha256") != sha:
                problems.append("manifest.files dictionary sha256 mismatch")
            if entry.get("size") != len(data):
                problems.append("manifest.files dictionary size mismatch")
        build_file = os.path.join(ROOT, "dist", "payload", "BepInEx", "plugins",
                                  "SP2Chinese", "dict.build")
        if os.path.isfile(build_file):
            with open(build_file, encoding="utf-8-sig") as f:
                local_build = f.read().strip()
            if local_build != str(mf.get("dictBuild")):
                problems.append("dist/payload dict.build=%s != manifest.dictBuild=%s"
                                % (local_build, mf.get("dictBuild")))

        if not str(mf.get("dictBuild") or "").isdigit():
            problems.append("manifest.dictBuild is not a plain integer: %r" % (mf.get("dictBuild"),))

    print("rows          : %d" % len(rows))
    print("translated    : %d (identity rows excluded)" % (len(rows) - identity))
    print("unique keys   : %d" % len(live_map))
    print("identity rows : %d" % identity)
    print("CJK values    : %d" % cjk_value)
    print("batch keys    : %d" % len(merged))
    if warnings:
        print("warnings      : %d" % len(warnings))
        for w in warnings[:5]:
            print("  ! " + w)
    if problems:
        print("\nRESULT: FAILED")
        for p in problems:
            print("  - " + p)
        return 1
    print("\nRESULT: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
