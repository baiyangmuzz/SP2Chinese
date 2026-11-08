# -*- coding: utf-8 -*-
"""发布产物一致性检查：manifest.json 必须精确描述 dist/payload 里实际要发给用户的文件。

背景：曾经出现「根字典/根 manifest 更新到新版本，但 dist/payload、_stage、zip 仍是旧版」
的半构建状态，导致安装器 SHA256 校验必报红字、游戏内自更新被 dictBuild 卡死。此脚本把
「manifest 是唯一事实来源」变成可机检断言，供 CI 与发布前本地运行。

检查项：
  1. manifest.json 是合法 JSON，且含必需字段
  2. dictSha256 / dictVersion / dictRows 与仓库根 dictionary_zh_cn.tsv 实测一致
  3. manifest.files 里每个条目的 sha256 + size 与 dist/payload 下对应文件一致
  4. dist/payload 里没有「会被安装器写入但 manifest 未列出」的文件（白名单除外）
  5. dist/payload/.../dict.build 内容 == manifest.dictBuild
  6. update.dictionary 使用允许的域名；dictBuild 为纯数字
  7. 发布 zip（若存在）内 manifest.json 与根 manifest.json 全字段一致

Usage:
    python tools/check_dist.py
Exit 0 = 一致，1 = 不一致。
"""
import glob
import hashlib
import json
import os
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(ROOT, "manifest.json")
DICT = os.path.join(ROOT, "dictionary_zh_cn.tsv")
PAYLOAD = os.path.join(ROOT, "dist", "payload")
DICT_ENTRY = "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv"
BUILD_FILE = os.path.join(PAYLOAD, "BepInEx", "plugins", "SP2Chinese", "dict.build")

# 随包分发但不写入游戏目录（或只在特定条件下写入）的文件，不列入 manifest.files。
PAYLOAD_EXTRA_OK = {
    "VERSION.txt", "changelog.txt", "manifest.json",
    "BepInEx/config/BepInEx.cfg",
}
ALLOWED_HOSTS = ("raw.githubusercontent.com", "github.com", "jsdelivr.net")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    problems = []
    if not os.path.isfile(MANIFEST):
        print("manifest.json missing")
        return 1
    with open(MANIFEST, encoding="utf-8-sig") as f:
        mf = json.load(f)

    for field in ("modVersion", "dictVersion", "dictBuild", "dictRows",
                  "dictSha256", "files", "update"):
        if field not in mf:
            problems.append("manifest.json missing field: " + field)

    # --- 2) 根字典 ---
    if os.path.isfile(DICT):
        data = open(DICT, "rb").read()
        h = hashlib.sha256(data).hexdigest()
        rows = sum(1 for l in data.decode("utf-8-sig").splitlines() if "\t" in l)
        if mf.get("dictSha256") != h:
            problems.append("dictSha256=%s != actual %s" % (str(mf.get("dictSha256"))[:12], h[:12]))
        if mf.get("dictVersion") != h[:12]:
            problems.append("dictVersion != sha256[:12] of root dictionary")
        if mf.get("dictRows") != rows:
            problems.append("dictRows=%s != %d" % (mf.get("dictRows"), rows))
    else:
        problems.append("root dictionary_zh_cn.tsv missing")

    # --- 3/4) payload 全量比对 ---
    # dist/payload 是打包产物，被 .gitignore 排除（不进仓库，CI 上也不存在）。
    # 因此缺目录时跳过这几项并明确说明，而不是报一堆「payload 缺失」的假红灯；
    # 本地发布前必须先跑 build_dist.py，这个检查才有意义。
    files = mf.get("files") or {}
    payload_present = os.path.isdir(PAYLOAD)
    if not payload_present:
        print("payload       : <absent> (dist/payload 未生成，跳过产物比对；"
              "发布前请先运行 python dist/build_dist.py)")
        actual = set()
    else:
        actual = set()
        for base, _, names in os.walk(PAYLOAD):
            for n in names:
                rel = os.path.relpath(os.path.join(base, n), PAYLOAD).replace("\\", "/")
                actual.add(rel)
        for rel, meta in files.items():
            p = os.path.join(PAYLOAD, rel.replace("/", os.sep))
            if not os.path.isfile(p):
                problems.append("manifest.files lists missing payload file: " + rel)
                continue
            h = sha256(p)
            if meta.get("sha256") != h:
                problems.append("payload hash mismatch: %s (%s != %s)"
                                % (rel, str(meta.get("sha256"))[:12], h[:12]))
            if meta.get("size") != os.path.getsize(p):
                problems.append("payload size mismatch: %s (%s != %d)"
                                % (rel, meta.get("size"), os.path.getsize(p)))
        unlisted = sorted(actual - set(files) - PAYLOAD_EXTRA_OK)
        if unlisted:
            problems.append("payload files not covered by manifest: " + ", ".join(unlisted[:8]))

    # --- 5) dict.build ---
    if not payload_present:
        pass   # 产物未生成，上面已提示
    elif os.path.isfile(BUILD_FILE):
        content = open(BUILD_FILE, encoding="utf-8-sig").read().strip()
        if content != str(mf.get("dictBuild")):
            problems.append("dict.build=%s != manifest.dictBuild=%s"
                            % (content, mf.get("dictBuild")))
    else:
        problems.append("dist/payload/.../dict.build missing")

    # --- 6) update URL / build number ---
    url = str((mf.get("update") or {}).get("dictionary") or "")
    if not url.startswith("https://"):
        problems.append("update.dictionary is not https: " + url)
    elif not any(("://" + h) in url or ("." + h) in url for h in ALLOWED_HOSTS):
        problems.append("update.dictionary host not in allowlist: " + url)
    if not str(mf.get("dictBuild", "")).isdigit():
        problems.append("dictBuild is not a plain integer: " + str(mf.get("dictBuild")))

    # --- 7) published zip ---
    # 只校验「与当前 manifest 描述的是同一次构建」的包。历史上留下的一堆 zip 排序会把
    # ..._20261028_1914.zip 排到 ..._20261028.zip 之后，用「排序取最后一个」当最新是错的。
    # 判定条件：包内 manifest 的 dictBuild 与根 manifest 一致。
    zips = sorted(glob.glob(os.path.join(ROOT, "SP2Chinese-v*.zip"))) \
        + sorted(glob.glob(os.path.join(ROOT, "SP2汉化_v*.zip")))
    matched = None
    stale = []
    for z in zips:
        try:
            with zipfile.ZipFile(z) as zf:
                names = [n for n in zf.namelist() if n.endswith("/manifest.json")]
                if not names:
                    stale.append(os.path.basename(z))
                    continue
                zmf = json.loads(zf.read(names[0]))
            if str(zmf.get("dictBuild")) == str(mf.get("dictBuild")):
                matched = (z, zmf)
            else:
                stale.append(os.path.basename(z))
        except (OSError, ValueError, zipfile.BadZipFile):
            stale.append(os.path.basename(z))
    if matched:
        z, zmf = matched
        try:
            with zipfile.ZipFile(z) as zf:
                if {k: v for k, v in zmf.items() if k != "generatedAt"} != \
                   {k: v for k, v in mf.items() if k != "generatedAt"}:
                    problems.append("zip manifest differs from root manifest: " + os.path.basename(z))
                dnames = [n for n in zf.namelist() if n.endswith("dictionary_zh_cn.tsv")]
                if dnames:
                    zh = hashlib.sha256(zf.read(dnames[0])).hexdigest()
                    if zh != mf.get("dictSha256"):
                        problems.append("zip dictionary hash != manifest.dictSha256")
                else:
                    problems.append("zip has no dictionary_zh_cn.tsv: " + os.path.basename(z))
        except (OSError, ValueError, zipfile.BadZipFile) as e:
            problems.append("cannot inspect zip %s: %s" % (os.path.basename(z), e))
        print("current zip   : %s" % os.path.basename(z))
    elif zips:
        # 只有「存在 zip 但都跟当前 manifest 不匹配」才算问题；完全没有 zip（CI 环境，
        # *.zip 被 .gitignore 排除）只是「还没打本地包」，不是错误。
        problems.append("no zip matches manifest.dictBuild=%s (run build_dist.py)"
                        % mf.get("dictBuild"))
    else:
        print("current zip   : <none yet> (未打本地包，已跳过；发布前请运行 build_dist.py)")
    if stale:
        print("older zips    : %d (ignored: %s)" % (len(stale), ", ".join(stale[:3])))

    print("payload files : %d (manifest lists %d)" % (len(actual), len(files)))
    print("dictBuild     : %s" % mf.get("dictBuild"))
    print("dictSha256    : %s" % str(mf.get("dictSha256"))[:12])
    if problems:
        print("\nRESULT: FAILED")
        for p in problems:
            print("  - " + p)
        return 1
    print("\nRESULT: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
