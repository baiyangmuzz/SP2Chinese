#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 GitHub Release 的安装包同步到 Gitee 仓库的发行版。

背景：国内用户下载 GitHub Releases 经常很慢甚至失败，Gitee 的下载速度正常。
Gitee Pages 已经下线，但「仓库 + 发行版附件」仍然可用，所以镜像只做两件事：
  1. 用仓库 README 当展示页（Gitee 首页会自动渲染）
  2. 把 exe / zip 挂到发行版附件上

用法：

    set GITEE_TOKEN=xxxxxxxx        # 在 gitee.com/personal_access_tokens 生成
    python tools/sync_gitee.py --repo 你的用户名/SP2Chinese-mirror

默认上传仓库根目录下当前版本的 exe 与 zip。加 --dry-run 只看会传什么。

注意：Gitee 的发行版附件有单文件大小限制（通常 100 MB 内），本项目约 1 MB，没问题。
"""
import argparse
import json
import os
import re
import sys
import urllib.parse
import urllib.request

API = "https://gitee.com/api/v5"


def read_version():
    here = os.path.dirname(os.path.abspath(__file__))
    plugin = os.path.join(os.path.dirname(here), "src", "Plugin.cs")
    with open(plugin, encoding="utf-8") as f:
        m = re.search(r'"(\d+\.\d+\.\d+)"\)\]', f.read())
    if not m:
        raise SystemExit("无法从 src/Plugin.cs 读出版本号")
    return m.group(1)


def api_post(url, fields, token):
    """发一个 application/x-www-form-urlencoded 请求（Gitee v5 常见写法）。"""
    fields = dict(fields)
    fields["access_token"] = token
    data = urllib.parse.urlencode(fields).encode()
    req = urllib.request.Request(url, data=data, method="POST")
    req.add_header("Content-Type", "application/x-www-form-urlencoded")
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read().decode("utf-8"))


def api_get(url, token):
    sep = "&" if "?" in url else "?"
    url = "%s%saccess_token=%s" % (url, sep, urllib.parse.quote(token))
    req = urllib.request.Request(url)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise


def upload_file(url, path, token):
    """multipart/form-data 上传附件。"""
    boundary = "----sp2sync%d" % os.getpid()
    with open(path, "rb") as f:
        body = f.read()
    name = os.path.basename(path)
    parts = []
    parts.append(("--" + boundary + "\r\n").encode())
    parts.append(("Content-Disposition: form-data; name=\"access_token\"\r\n\r\n").encode())
    parts.append((token + "\r\n").encode())
    parts.append(("--" + boundary + "\r\n").encode())
    parts.append(("Content-Disposition: form-data; name=\"file\"; filename=\"%s\"\r\n" % name).encode())
    parts.append(b"Content-Type: application/octet-stream\r\n\r\n")
    parts.append(body)
    parts.append(b"\r\n")
    parts.append(("--" + boundary + "--\r\n").encode())
    payload = b"".join(parts)

    req = urllib.request.Request(url, data=payload, method="POST")
    req.add_header("Content-Type", "multipart/form-data; boundary=" + boundary)
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.loads(r.read().decode("utf-8"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", required=True, help="owner/name，例如 baiyangmuzz/SP2Chinese-mirror")
    ap.add_argument("--tag", default=None, help="默认 v<版本号>")
    ap.add_argument("--files", nargs="*", default=None, help="默认自动找 exe 和 zip")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    token = os.environ.get("GITEE_TOKEN", "").strip()
    if not token and not args.dry_run:
        raise SystemExit("缺少 GITEE_TOKEN 环境变量（gitee.com/personal_access_tokens 生成）")

    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    version = read_version()
    tag = args.tag or ("v" + version)

    if args.files:
        files = [os.path.abspath(p) for p in args.files]
    else:
        exe = os.path.join(root, "SP2Chinese-Setup-v%s.exe" % version)
        zips = sorted(
            [os.path.join(root, f) for f in os.listdir(root)
             if f.startswith("SP2Chinese-v%s-" % version) and f.endswith(".zip")]
        )
        files = ([exe] if os.path.isfile(exe) else []) + zips

    if not files:
        raise SystemExit("没找到可上传的文件（先跑 dist/build_setup.py）")

    print("仓库     :", args.repo)
    print("版本     :", version, " tag:", tag)
    for f in files:
        print("  待上传 : %s (%.2f MB)" % (os.path.basename(f), os.path.getsize(f) / 1048576))

    if args.dry_run:
        print("\n--dry-run：不实际请求 Gitee")
        return 0

    # 1) 取或建发行版
    rel = api_get("%s/repos/%s/releases/tags/%s" % (API, args.repo, tag), token)
    if rel:
        print("\n发行版已存在，id=%s" % rel["id"])
    else:
        print("\n创建发行版 %s" % tag)
        rel = api_post("%s/repos/%s/releases" % (API, args.repo), {
            "tag_name": tag,
            "name": "SP2Chinese " + tag,
            "body": "国内镜像。正式发布在 https://github.com/baiyangmuzz/SP2Chinese/releases",
            "target_commitish": "master",
        }, token)
        print("  创建成功 id=%s" % rel["id"])

    # 2) 上传附件（先看有没有同名，避免重复）
    existing = {a["name"] for a in (rel.get("assets") or [])}
    for f in files:
        name = os.path.basename(f)
        if name in existing:
            print("  已存在，跳过:", name)
            continue
        print("  上传:", name)
        upload_file("%s/repos/%s/releases/%s/attach_files" % (API, args.repo, rel["id"]),
                    f, token)
        print("    完成")

    print("\n镜像地址：https://gitee.com/%s/releases" % args.repo)
    return 0


if __name__ == "__main__":
    sys.exit(main())
