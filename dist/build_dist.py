# -*- coding: utf-8 -*-
"""Build the distributable package for the SP2 Chinese mod.

Layout produced:
    SP2Chinese-vX.Y.Z-YYYYMMDD/
        SP2汉化.bat
        install.ps1
        Gui.ps1
        安装说明.txt
        payload/ ...        <- what actually gets copied into the game folder

Run:  python build_dist.py
"""
import hashlib
import json
import os
import re
import shutil
import zipfile
from collections import OrderedDict
from datetime import datetime, timezone

# 路径可用环境变量覆盖，不再绑死作者本机：
#   SP2_REPO        仓库根目录（默认 = 本文件所在 dist/ 的上一级）
#   SP2_GAME_DIR    游戏安装目录（默认 D:\steam\...\SimplePlanes 2）
SRC = os.path.dirname(os.path.abspath(__file__))
DIST_ROOT = os.environ.get("SP2_REPO") or os.path.dirname(SRC)
GAME = os.environ.get("SP2_GAME_DIR") or r"D:\steam\steamapps\common\SimplePlanes 2"
# 词库构建序号（UTC yyyyMMddHHmm）：插件与安装器据此判断本地是否已比线上新。
# 用 UTC 而非本地时间，避免跨时区 / 时钟回拨生成更小的 build 导致「本地较新」误判。
DICT_BUILD = int(datetime.now(timezone.utc).strftime("%Y%m%d%H%M"))
PAYLOAD = os.path.join(SRC, "payload")
BEP_SRC = os.path.join(GAME, "BepInEx")
PLUG_SRC = os.path.join(BEP_SRC, "plugins", "SP2Chinese")
ROOT_DICT = os.path.join(DIST_ROOT, "dictionary_zh_cn.tsv")


def read_version():
    src = open(os.path.join(DIST_ROOT, "src", "Plugin.cs"), encoding="utf-8").read()
    m = re.search(r'\[BepInPlugin\("sp2\.zh",\s*"[^"]*",\s*"([\d.]+)"\)\]', src)
    if not m:
        raise SystemExit("cannot read plugin version from Plugin.cs")
    return m.group(1)


def merged_dictionary():
    """Rebuild the runtime dictionary from dict/ batches (sorted + last-wins), exactly like
    tools/verify_dict.py. The root dictionary_zh_cn.tsv is the single source of truth: writing it
    first means payload / stage / zip / manifest all derive from the same bytes."""
    merged = OrderedDict()
    conflicts = []
    origin = {}
    for name in sorted(os.listdir(os.path.join(DIST_ROOT, "dict"))):
        if not name.endswith(".tsv"):
            continue
        with open(os.path.join(DIST_ROOT, "dict", name), encoding="utf-8-sig", newline="") as f:
            for n, line in enumerate(f, 1):
                line = line.rstrip("\r\n")
                if line.count("\t") != 1:
                    raise SystemExit("%s:%d has %d TABs (need exactly 1)"
                                     % (name, n, line.count("\t")))
                k, v = line.split("\t", 1)
                if k in merged and merged[k] != v:
                    conflicts.append((k, origin[k], name))
                merged[k] = v
                origin.setdefault(k, name)
    if conflicts:
        raise SystemExit("cross-batch key conflict(s): " +
                         ", ".join("%r (%s -> %s)" % c for c in conflicts[:5]))
    data = ("\n".join(k + "\t" + v for k, v in merged.items()) + "\n").encode("utf-8")
    with open(ROOT_DICT, "wb") as f:
        f.write(data)
    return data, len(merged)


def load_payload(dict_data):
    if os.path.isdir(PAYLOAD):
        shutil.rmtree(PAYLOAD)
    os.makedirs(PAYLOAD)

    # --- BepInEx runtime (only if the target machine lacks it) ---
    for f in ("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt"):
        src = os.path.join(GAME, f)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(PAYLOAD, f))

    core_dst = os.path.join(PAYLOAD, "BepInEx", "core")
    shutil.copytree(os.path.join(BEP_SRC, "core"), core_dst)

    cfg_src = os.path.join(BEP_SRC, "config", "BepInEx.cfg")
    if os.path.isfile(cfg_src):
        cfg_dst = os.path.join(PAYLOAD, "BepInEx", "config")
        os.makedirs(cfg_dst, exist_ok=True)
        shutil.copy2(cfg_src, cfg_dst)

    # --- the mod itself ---
    plug_dst = os.path.join(PAYLOAD, "BepInEx", "plugins", "SP2Chinese")
    os.makedirs(plug_dst)

    # 发布构建强制使用本次编译出的 DLL（不再按 mtime 与游戏目录里的旧副本二选一）。
    built_dll = os.path.join(DIST_ROOT, "bin", "Release", "SP2Chinese.dll")
    if not os.path.isfile(built_dll):
        raise SystemExit("missing " + built_dll + " — run `dotnet build -c Release` first")
    shutil.copy2(built_dll, plug_dst)
    print("dll          : bin/Release (%s)" %
          datetime.fromtimestamp(os.path.getmtime(built_dll)).strftime("%Y-%m-%d %H:%M"))

    # 字典与根文件同源：直接用 merged_dictionary() 写出的字节，不从游戏目录拷。
    with open(os.path.join(plug_dst, "dictionary_zh_cn.tsv"), "wb") as f:
        f.write(dict_data)
    print("dictionary   : %d bytes (from dict/ batches)" % len(dict_data))

    with open(os.path.join(PAYLOAD, "VERSION.txt"), "w", encoding="ascii", newline="\n") as f:
        f.write(read_version() + "\n")

    return plug_dst


def readme_text(version, rows, dict_chars):
    return r"""SimplePlanes 2 简体中文汉化 v{ver}
================================================================

这是给 SimplePlanes 2 使用的中文界面补丁，基于 BepInEx 运行。
覆盖范围：菜单、设置、设计师属性与提示、部件列表、气象设置、
挑战描述、驾驶舱仪表提示等（共 {rows} 条译文）。


【推荐：用 exe 安装器】
----------------------------------------------------------------
到 Releases 页面下载 SP2Chinese-Setup-v{ver}.exe，双击运行，
点「开始安装」即可。它会自动找游戏目录、自动校验、支持一键回滚。

下面这份 zip 是给需要脚本化 / CI 批量部署的人用的。


【用这个 zip 怎么装】
----------------------------------------------------------------
1. 先完全退出游戏（任务栏里也不能留）
2. 双击「SP2汉化.bat」
3. 弹出管理员授权时点「是」

也可以命令行无人值守：

    SP2汉化.bat -Silent -Yes     装完不弹任何窗口
    SP2汉化.bat -Uninstall       卸载


【怎么确认生效】
----------------------------------------------------------------
启动游戏，原本是英文的地方变成中文，就说明装好了。


【首次进游戏还是英文】
----------------------------------------------------------------
1. 确认是从 Steam 正常启动，不要用别的快捷方式
2. 完全退出游戏（不是最小化）再重新打开
3. 打开游戏目录下的 BepInEx\LogOutput.log，搜索 "SP2 chinese"
   找得到 -> 插件在跑，可能是字典没读到
   找不到 -> 插件没被加载，BepInEx 没装好，重新跑一遍安装


【怎么更新】
----------------------------------------------------------------
直接用新版本覆盖安装，不用先卸载。
旧版会自动挪到 BepInEx\_mod_backups\ 备份，不会弄丢。
词库支持游戏内热更新，平时不需要重装。


【怎么卸载】
----------------------------------------------------------------
双击「SP2汉化.bat」选卸载，或运行 exe 安装器点「卸载汉化」。
它只会把汉化插件目录移走（改名备份，不删除），
BepInEx 本体和其他模组不受影响。


【常见问题】
----------------------------------------------------------------
Q: 会覆盖我的载具吗？
A: 不会。只写游戏目录下的 BepInEx 和 plugins\SP2Chinese，
   存档（LocalLow\Jundroo）完全不碰。

Q: 我装了别的模组，会冲突吗？
A: 不会。检测到已有 BepInEx 时不会改动它的 core，
   只更新 plugins\SP2Chinese 这一个目录，旧版会改名备份。

Q: 游戏更新后还能用吗？
A: 多数情况可以直接用。游戏大更新导致部分文本对不上时，
   等新版词库或插件即可。

Q: 游戏打不开了 / 卡在启动画面？
A: 先卸载汉化看是否恢复，再把 BepInEx\LogOutput.log 发给群主。
   多半是 BepInEx 版本和游戏不匹配。

Q: 为什么有些英文没翻？
A: 涂装名、跑道编号、载具名、挑战名这些是专有名词，
   刻意保留原文，方便和社区对得上。


【校验】
----------------------------------------------------------------
每个文件的 SHA256 见包内 manifest.json。
如果校验对不上，说明包不完整，请重新下载。


版本信息
----------------------------------------------------------------
汉化版本    v{ver}
词条数      {rows} 条
开源地址    https://github.com/baiyangmuzz/SP2Chinese
交流群      QQ 1128992783

本软件开源免费。如果有人在任何平台向你收费，那不是我们。
"""


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def build_manifest(version, rows):
    """File-by-file hashes so the installer can verify an install and the plugin can
    decide purely from content whether its dictionary is stale (no version guessing)."""
    files = {}
    for root, _, names in os.walk(PAYLOAD):
        for fn in names:
            full = os.path.join(root, fn)
            rel = os.path.relpath(full, PAYLOAD).replace("\\", "/")
            # Only files the installer actually writes to the game folder may be listed;
            # VERSION.txt / changelog.txt stay in the package, and BepInEx.cfg is only copied
            # when the target has none (so an existing user config must not fail the check).
            if rel in ("manifest.json", "VERSION.txt", "changelog.txt", "BepInEx/config/BepInEx.cfg"):
                continue
            files[rel] = {"sha256": sha256_of(full), "size": os.path.getsize(full)}

    dict_rel = "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv"
    dict_sha = files.get(dict_rel, {}).get("sha256", "")
    repo = "https://github.com/baiyangmuzz/SP2Chinese"

    return {
        "modVersion": version,
        # Content hash, not a date: unchanged dictionary => identical id => nobody re-downloads.
        "dictVersion": dict_sha[:12],
        "dictBuild": DICT_BUILD,
        "dictRows": rows,
        "dictSha256": dict_sha,
        "generatedAt": datetime.now().astimezone().isoformat(timespec="seconds"),
        "author": "白杨木",
        "qqGroup": "1128992783",
        "repository": repo,
        "files": files,
        "update": {
            # 与 DictionaryUpdater.cs 的 IsAllowedDictionaryUrl 白名单、MirrorUrl 逻辑一致：
            # 主地址用 raw.githubusercontent.com，插件失败时自动切 fastly.jsdelivr.net。
            "dictionary": "https://raw.githubusercontent.com/baiyangmuzz/SP2Chinese/main/dictionary_zh_cn.tsv",
            # Big update (plugin code changed): send people to the full package.
            "release": repo + "/releases/latest",
        },
    }


def write_dict_build(build):
    """把构建号写到字典旁边：插件与安装器据此判断本地是否已经比线上新。"""
    targets = [
        os.path.join(PAYLOAD, "BepInEx", "plugins", "SP2Chinese", "dict.build"),
    ]
    # 同步一份到已部署的游戏目录，便于本机热更新调试；失败不影响发布包。
    if os.path.isdir(PLUG_SRC):
        targets.append(os.path.join(PLUG_SRC, "dict.build"))
    for t in targets:
        try:
            os.makedirs(os.path.dirname(t), exist_ok=True)
            with open(t, "w", encoding="utf-8", newline="\n") as f:
                f.write(str(build) + "\n")
        except OSError:
            pass


def main():
    version = read_version()
    dict_data, rows = merged_dictionary()
    plug_dst = load_payload(dict_data)

    dict_path = os.path.join(plug_dst, "dictionary_zh_cn.tsv")
    chars = set()
    with open(dict_path, encoding="utf-8") as f:
        for line in f:
            i = line.find("\t")
            if i > 0:
                chars.update(c for c in line[i + 1:] if ord(c) > 127)

    # 包名只用 ASCII：GitHub Release 附件上传不接受非 ASCII 文件名，中文「汉化」会被
    # 破坏成 "._"（v1.6.0/1.6.1 的历史附件就是这么坏的）。压缩包内部的文件名与
    # 界面仍是中文，只有最外层这个分发文件名用英文。
    name = "SP2Chinese-v%s-%s" % (version, datetime.now().strftime("%Y%m%d"))
    stage = os.path.join(DIST_ROOT, "_stage", name)
    if os.path.isdir(os.path.dirname(stage)):
        shutil.rmtree(os.path.dirname(stage))
    os.makedirs(stage)

    # scripts + readme (forced to UTF-8 with BOM so PowerShell 5.1 / Notepad read them right).
    # PowerShell 5.1 把无 BOM 的 UTF-8 当 ANSI 读，中文会乱码并导致解析失败，
    # 所以所有 .ps1 无论源文件带不带 BOM，落到 stage 时统一强制加 BOM。
    for f in ("SP2汉化.bat",):
        shutil.copy2(os.path.join(SRC, f), stage)
    for f in ("install.ps1", "Gui.ps1"):
        src_ps = os.path.join(SRC, f)
        if not os.path.isfile(src_ps):
            raise SystemExit("missing GUI script: " + src_ps)
        dst = os.path.join(stage, f)
        text = open(src_ps, encoding="utf-8-sig").read()
        with open(dst, "w", encoding="utf-8-sig", newline="\r\n") as out:
            out.write(text)
        # 同步修正源目录里的 BOM：开发者编辑后可能丢失，避免下次直接跑 dist/ 时出错。
        with open(src_ps, "wb") as out:
            out.write(b"\xef\xbb\xbf" + text.encode("utf-8"))
    with open(os.path.join(stage, "安装说明.txt"), "w",
              encoding="utf-8-sig", newline="\r\n") as out:
        out.write(readme_text(version, rows, len(chars)))

    # 构建号必须先写进 PAYLOAD，再整体拷到 stage，否则安装包里不会有这个文件，
    # 安装器读不到本地 build 就会误判"本地是旧版"而去拉线上旧词库。
    write_dict_build(DICT_BUILD)
    shutil.copytree(PAYLOAD, os.path.join(stage, "payload"))

    # --- manifest: lets the installer verify files and lets the plugin self-update ---
    manifest = build_manifest(version, rows)
    manifest_bytes = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")

    # 同一份 manifest 写到三处：stage 包内、dist/ 源目录（供直接跑 install.ps1）、仓库根
    # （供 raw.githubusercontent.com 发布）。字典也由 merged_dictionary() 统一写到仓库根，
    # 不再从游戏目录回拷，保证七处产物同源。
    for target in (
        os.path.join(stage, "manifest.json"),
        os.path.join(SRC, "manifest.json"),
        os.path.join(DIST_ROOT, "manifest.json"),
    ):
        with open(target, "wb") as f:
            f.write(manifest_bytes)

    zip_path = os.path.join(DIST_ROOT, name + ".zip")
    # "w" truncates an existing archive; deleting first only invites file-lock failures
    # when the previous build is still open in a viewer.
    try:
        write_zip(zip_path, stage)
    except PermissionError:
        # The previous archive is still held open by a preview/archiver process.
        # Write alongside it instead of failing the whole build.
        zip_path = os.path.join(DIST_ROOT, name + "_" +
                                datetime.now().strftime("%H%M") + ".zip")
        write_zip(zip_path, stage)
        print("note         : previous archive was locked, wrote a new file instead")

    size = os.path.getsize(zip_path) / 1024 / 1024
    print("version      : %s" % version)
    print("dictionary   : %d rows, %d unique CJK chars" % (rows, len(chars)))
    print("zip          : %s (%.2f MB)" % (zip_path, size))


def write_zip(zip_path, stage):
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for root, _, files in os.walk(stage):
            for fn in files:
                full = os.path.join(root, fn)
                rel = os.path.relpath(full, os.path.dirname(stage))
                z.write(full, rel)


if __name__ == "__main__":
    main()
