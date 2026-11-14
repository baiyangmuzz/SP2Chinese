# -*- coding: utf-8 -*-
"""把汉化包打成单个自包含 exe 安装程序。

产物：SP2Chinese-Setup-v<版本>.exe
  - 双击即装：找游戏目录、备份旧版、写文件、SHA256 校验、注册表/Steam 库定位
  - 全原生 C#：不内嵌 PowerShell 脚本、不释放临时文件、不调用外部命令
  - 只用 .NET Framework 4.8（Windows 10/11 系统自带），体积约 1 MB

依赖：先有 dist/payload（由 dist/build_dist.py 生成）+ manifest.json。

用法：
    python dist/build_setup.py
"""
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

# 可覆盖来源，用于构建「旧版本测试包」：
#   SP2_PAYLOAD_SRC  替换 payload 目录（默认 dist/payload）
#   SP2_MANIFEST_SRC 替换 manifest（默认 dist/manifest.json）
#   SP2_VERSION      强制版本号（默认从 Plugin.cs 读取）
#   SP2_OUT_SUFFIX   输出文件名后缀，避免覆盖正式包
PAYLOAD = os.environ.get("SP2_PAYLOAD_SRC") or os.path.join(HERE, "payload")
MANIFEST_SRC = os.environ.get("SP2_MANIFEST_SRC") or os.path.join(HERE, "manifest.json")
SETUP = os.path.join(HERE, "setup")
RESOURCES = os.path.join(SETUP, "resources")


def read_version():
    forced = os.environ.get("SP2_VERSION")
    if forced:
        return forced
    with open(os.path.join(REPO, "src", "Plugin.cs"), encoding="utf-8") as f:
        m = re.search(r'\[BepInPlugin\("sp2\.zh",\s*"[^"]*",\s*"([\d.]+)"\)\]', f.read())
    if not m:
        raise SystemExit("无法从 Plugin.cs 读取版本号")
    return m.group(1)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def prepare_resources():
    if not os.path.isdir(PAYLOAD):
        raise SystemExit("缺少 dist/payload，请先运行 python dist/build_dist.py")

    if os.path.isdir(RESOURCES):
        shutil.rmtree(RESOURCES)
    os.makedirs(RESOURCES)

    # payload -> zip（级别 9：2.6MB 能压到约 0.9MB）
    zip_path = os.path.join(RESOURCES, "payload.zip")
    count = 0
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for root, _, files in os.walk(PAYLOAD):
            for fn in files:
                full = os.path.join(root, fn)
                rel = os.path.relpath(full, PAYLOAD).replace("\\", "/")
                z.write(full, rel)
                count += 1

    # manifest 原样嵌入（安装器从中读哈希与版本，不再依赖外部文件）
    mf_src = MANIFEST_SRC
    if not os.path.isfile(mf_src):
        raise SystemExit("缺少 manifest：" + mf_src + "（请先运行 build_dist.py）")
    shutil.copy2(mf_src, os.path.join(RESOURCES, "manifest.json"))

    raw = sum(os.path.getsize(os.path.join(root, f))
              for root, _, files in os.walk(PAYLOAD) for f in files)
    zsz = os.path.getsize(zip_path)
    print("payload      : %d 个文件, %.2f MB -> 压缩后 %.2f MB (%.0f%%)"
          % (count, raw / 1048576, zsz / 1048576, 100.0 * zsz / raw))
    return zip_path


def sync_manifest_toc():
    """把 manifest 里的字典元数据同步到安装器源码里的常量之外（供界面显示）。"""
    with open(os.path.join(HERE, "manifest.json"), encoding="utf-8-sig") as f:
        mf = json.load(f)
    return mf


def write_setup_block(final, version):
    """把安装器自身的版本/哈希/下载地址写进发布用的 manifest。

    为什么需要：api.github.com 在国内常被墙，安装器更新检查只能靠 manifest
    （raw.githubusercontent / jsDelivr 可达）拿到「带 sha256 的下载地址」。
    没有 setup 段时，旧版安装器只能提示「去 Releases 手动下载」而无法自动更新。

    注意：只写正式包。SP2_MANIFEST_SRC 覆盖（旧版测试包）时不写，避免污染发布清单。
    """
    if os.environ.get("SP2_MANIFEST_SRC"):
        return None

    info = {
        "version": version,
        "sha256": sha256(final),
        "size": os.path.getsize(final),
        "url": ("https://github.com/baiyangmuzz/SP2Chinese/releases/download/"
                "v%s/SP2Chinese-Setup-v%s.exe" % (version, version)),
    }

    for path in (os.path.join(REPO, "manifest.json"),
                 os.path.join(HERE, "manifest.json")):
        try:
            with open(path, encoding="utf-8-sig") as f:
                mf = json.load(f)
        except Exception as e:
            print("  ! 读取 %s 失败：%s" % (path, e))
            continue
        mf["setup"] = info
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            json.dump(mf, f, ensure_ascii=False, indent=2)
            f.write("\n")
        print("setup 段      : 已写入 %s" % os.path.relpath(path, REPO))
    return info


def refresh_zip_manifest(version):
    """把根 manifest（已带 setup 段）同步进同版本的 zip。

    构建顺序决定的：build_dist.py 先打 zip（那时还没有 setup 段），
    build_setup.py 之后才算得出 exe 的哈希并写 setup。不同步的话
    check_dist.py 会报「zip manifest 与根 manifest 不一致」。
    """
    import glob
    zips = sorted(glob.glob(os.path.join(REPO, "SP2Chinese-v%s-*.zip" % version)))
    if not zips:
        return
    zpath = zips[-1]
    with open(os.path.join(REPO, "manifest.json"), "rb") as f:
        root_manifest = f.read()

    tmp = zpath + ".tmp"
    with zipfile.ZipFile(zpath) as zin, \
         zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zout:
        for item in zin.infolist():
            data = zin.read(item.filename)
            # 条目名带包内根目录前缀（如 SP2Chinese-v1.7.0-…/manifest.json）
            if item.filename == "manifest.json" or item.filename.endswith("/manifest.json"):
                data = root_manifest
            zout.writestr(item, data)
    os.replace(tmp, zpath)
    print("zip 同步      : %s" % os.path.basename(zpath))


def build():
    version = read_version()
    prepare_resources()
    mf = sync_manifest_toc()
    print("版本         : %s（词库 %s 条, build %s）"
          % (version, mf.get("dictRows"), mf.get("dictBuild")))

    print("正在编译……")
    cmd = ["dotnet", "build", os.path.join(SETUP, "SP2Chinese.Setup.csproj"),
           "-c", "Release", "--nologo", "-v", "minimal"]
    r = subprocess.run(cmd, cwd=REPO, capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    if r.returncode != 0:
        # 只打印错误行，避免刷屏
        for line in (r.stdout + r.stderr).splitlines():
            if "error" in line.lower():
                print("  " + line.strip())
        raise SystemExit("编译失败")

    built = None
    for root, _, files in os.walk(os.path.join(SETUP, "bin")):
        for fn in files:
            if fn.lower() == "sp2chinese-setup.exe":
                built = os.path.join(root, fn)
    if not built:
        raise SystemExit("编译产物里找不到 exe")

    suffix = os.environ.get("SP2_OUT_SUFFIX", "")
    final = os.path.join(REPO, "SP2Chinese-Setup-v%s%s.exe" % (version, suffix))
    # 上一版可能被杀软扫描锁住，写不进去就换个名字
    if os.path.exists(final):
        try:
            os.remove(final)
        except OSError:
            final = os.path.join(REPO, "SP2Chinese-Setup-v%s-new.exe" % version)
            print("上一版被占用，输出到新文件名")
    shutil.copy2(built, final)

    size = os.path.getsize(final)
    print()
    print("输出         : %s" % os.path.basename(final))
    print("体积         : %.2f MB" % (size / 1048576))
    print("SHA256       : %s" % sha256(final))
    write_setup_block(final, version)
    refresh_zip_manifest(version)
    print()
    print("单个 exe 即完整安装包：原生实现，不释放脚本、不调用 PowerShell。")
    print()
    print("发布清单：")
    print("  1) exe 传到 Release v%s，文件名必须完全一致：%s"
          % (version, os.path.basename(final)))
    print("  2) 提交并推送 manifest.json（安装器自动更新靠里面的 setup 段）")
    return 0


if __name__ == "__main__":
    sys.exit(build())
