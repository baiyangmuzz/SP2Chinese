# -*- coding: utf-8 -*-
"""Build the distributable package for the SP2 Chinese mod.

Layout produced:
    SP2汉化_vX.Y.Z_YYYYMMDD/
        SP2汉化.bat
        install.ps1
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
from datetime import datetime

GAME = r"D:\steam\steamapps\common\SimplePlanes 2"
SRC = r"D:\SP2Chinese\dist"
# 词库构建序号（yyyyMMddHHmm）：插件与安装器据此判断本地是否已比线上新，
# 避免本地修好的词库被线上旧版覆盖。
DICT_BUILD = int(__import__("time").strftime("%Y%m%d%H%M"))
PAYLOAD = os.path.join(SRC, "payload")
BEP_SRC = os.path.join(GAME, "BepInEx")
PLUG_SRC = os.path.join(BEP_SRC, "plugins", "SP2Chinese")

DIST_ROOT = os.path.dirname(SRC)


def read_version():
    src = open(os.path.join(DIST_ROOT, "src", "Plugin.cs"), encoding="utf-8").read()
    m = re.search(r'\[BepInPlugin\("sp2\.zh",\s*"[^"]*",\s*"([\d.]+)"\)\]', src)
    if not m:
        raise SystemExit("cannot read plugin version from Plugin.cs")
    return m.group(1)


def load_payload():
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

    # 优先用 bin/Release 的新构建，避免“改了源码没部署，打包还是旧 DLL”。
    built_dll = os.path.join(DIST_ROOT, "bin", "Release", "SP2Chinese.dll")
    deployed_dll = os.path.join(PLUG_SRC, "SP2Chinese.dll")
    if os.path.isfile(built_dll) and (
            not os.path.isfile(deployed_dll)
            or os.path.getmtime(built_dll) >= os.path.getmtime(deployed_dll)):
        dll_src, dll_from = built_dll, "bin/Release"
    else:
        dll_src, dll_from = deployed_dll, "game dir"
    if not os.path.isfile(dll_src):
        raise SystemExit("missing " + dll_src)

    dict_src = os.path.join(PLUG_SRC, "dictionary_zh_cn.tsv")
    if not os.path.isfile(dict_src):
        raise SystemExit("missing " + dict_src)

    shutil.copy2(dll_src, plug_dst)
    shutil.copy2(dict_src, plug_dst)
    print("dll          : %s (%s)" % (
        dll_from, datetime.fromtimestamp(os.path.getmtime(dll_src)).strftime("%Y-%m-%d %H:%M")))

    with open(os.path.join(PAYLOAD, "VERSION.txt"), "w", encoding="ascii", newline="\n") as f:
        f.write(read_version() + "\n")

    return plug_dst


def readme_text(version, rows, dict_chars):
    return """SimplePlanes 2 简体中文汉化 v{ver}
================================================================

这是给 SimplePlanes 2 使用的中文界面补丁，基于 BepInEx 运行。
覆盖范围：菜单、设置、设计师属性与提示、部件列表、气象设置、
挑战描述、驾驶舱仪表提示等（共 {rows} 条译文）。


【怎么装】三步
----------------------------------------------------------------
1. 先完全退出游戏（任务栏里也不能留）
2. 双击「SP2汉化.bat」
3. 如果弹出管理员授权，点是（游戏一般在 Program Files 下，
   没有管理员权限写不进去）

安装程序会自己找游戏目录，找到就直接装完。找不到的话，
会提示你把游戏文件夹路径粘贴进去：
Steam 库里右键游戏 → 管理 → 浏览本地文件 → 复制地址栏。


【怎么确认生效】
----------------------------------------------------------------
启动游戏，如果原本是英文的地方变成中文，就说明装好了。
如果还是英文，看下面「首次进游戏还是英文」。


【首次进游戏还是英文】
----------------------------------------------------------------
1. 确认是从 Steam 正常启动，不要用别的快捷方式
2. 完全退出游戏（不是最小化）再重新打开
3. 打开游戏目录下的 BepInEx\\LogOutput.log，搜索
   "SP2 chinese"
   找得到 → 插件在跑，可能是字典没读到
   找不到 → 插件没被加载，BepInEx 没装好，重新跑一遍安装


【怎么更新到新版本】
----------------------------------------------------------------
直接双击新版本里的「SP2汉化.bat」，不用先卸载。
旧版会自动挪到 BepInEx\\_mod_backups\\ 备份，不会弄丢。


【怎么卸载】
----------------------------------------------------------------
双击「SP2汉化.bat」选 2 卸载。
它只会把汉化插件目录移走（改成 .uninstalled-时间 的形式，
不删除），BepInEx 本体和其他模组不受影响。


【常见问题】
----------------------------------------------------------------
Q: 会覆盖我的载具吗？
A: 不会。只写游戏目录下的 BepInEx 和 plugins\\SP2Chinese，
   存档（LocalLow\\Jundroo）完全不碰。

Q: 我装了别的模组，会冲突吗？
A: 不会。安装时如果检测到已有 BepInEx，不会改动它的 core，
   只更新 plugins\\SP2Chinese 这一个目录，旧版会改名备份。

Q: 游戏更新后还能用吗？
A: 能，但新版本新增的界面文字可能要等汉化更新。装上仍可用。

Q: 游戏打不开了 / 卡在启动画面？
A: 先跑「SP2汉化.bat」选 2 卸载看是否恢复，再把 BepInEx\\LogOutput.log
   发给群主。多半是 BepInEx 版本和游戏不匹配。

Q: 为什么有些英文没翻？
A: 涂装名、机场跑道编号、载具名、挑战名这些是专有名词，
   刻意保留原文，方便和社区对得上。


版本信息
----------------------------------------------------------------
汉化版本    v{ver}
译文条数    {rows}
需渲染汉字  {chars}
构建时间    {when}
""".format(ver=version, rows=rows, chars=dict_chars,
           when=datetime.now().strftime("%Y-%m-%d %H:%M"))


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
            # Small update: dictionary only, downloaded and hot-reloaded by the plugin.
            "dictionary": repo + "/raw/main/dictionary_zh_cn.tsv",
            # Big update (plugin code changed): send people to the full package.
            "release": repo + "/releases/latest",
        },
    }


def write_dict_build(build):
    """把构建号写到字典旁边：插件与安装器据此判断本地是否已经比线上新。"""
    targets = [
        os.path.join(PAYLOAD, "BepInEx", "plugins", "SP2Chinese", "dict.build"),
        os.path.join(PLUG_SRC, "dict.build"),
    ]
    for t in targets:
        try:
            os.makedirs(os.path.dirname(t), exist_ok=True)
            with open(t, "w", encoding="utf-8", newline="\n") as f:
                f.write(str(build) + "\n")
        except OSError:
            pass


def main():
    version = read_version()
    plug_dst = load_payload()

    dict_path = os.path.join(plug_dst, "dictionary_zh_cn.tsv")
    with open(dict_path, encoding="utf-8") as f:
        rows = sum(1 for line in f if "\t" in line)
    chars = set()
    with open(dict_path, encoding="utf-8") as f:
        for line in f:
            i = line.find("\t")
            if i > 0:
                chars.update(c for c in line[i + 1:] if ord(c) > 127)

    name = "SP2汉化_v%s_%s" % (version, datetime.now().strftime("%Y%m%d"))
    stage = os.path.join(DIST_ROOT, "_stage", name)
    if os.path.isdir(os.path.dirname(stage)):
        shutil.rmtree(os.path.dirname(stage))
    os.makedirs(stage)

    # scripts + readme (forced to UTF-8 with BOM so PowerShell 5.1 / Notepad read them right)
    for f in ("SP2汉化.bat",):
        shutil.copy2(os.path.join(SRC, f), stage)
    for f in ("install.ps1",):
        dst = os.path.join(stage, f)
        text = open(os.path.join(SRC, f), encoding="utf-8-sig").read()
        with open(dst, "w", encoding="utf-8-sig", newline="\r\n") as out:
            out.write(text)
    with open(os.path.join(stage, "安装说明.txt"), "w",
              encoding="utf-8-sig", newline="\r\n") as out:
        out.write(readme_text(version, rows, len(chars)))

    # 构建号必须先写进 PAYLOAD，再整体拷到 stage，否则安装包里不会有这个文件，
    # 安装器读不到本地 build 就会误判"本地是旧版"而去拉线上旧词库。
    write_dict_build(DICT_BUILD)
    shutil.copytree(PAYLOAD, os.path.join(stage, "payload"))

    # --- manifest: lets the installer verify files and lets the plugin self-update ---
    manifest = build_manifest(version, rows)
    with open(os.path.join(stage, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
        f.write("\n")
    # Full merged dictionary at the repo root: this is the exact file the plugin downloads.
    shutil.copy2(os.path.join(PLUG_SRC, "dictionary_zh_cn.tsv"),
                 os.path.join(DIST_ROOT, "dictionary_zh_cn.tsv"))

    # A copy at the repo root is what the plugin fetches from raw.githubusercontent.com.
    with open(os.path.join(DIST_ROOT, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
        f.write("\n")

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
