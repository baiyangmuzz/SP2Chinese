# -*- coding: utf-8 -*-
"""把当前工作区推送到 GitHub。

本机 git push 因 schannel 证书吊销检查失败且无可用 CA 包，gh 的 TLS 栈可用，
因此走 Git Data API：blobs -> tree -> commit -> ref。

默认增量提交（带父提交）。REWRITE=1 时重写公开历史：按模块分组、每个模块
一个提交，让文件列表里每个目录显示的是自己的说明，而不是同一条笼统信息。
"""
import base64
import json
import os
import subprocess
import sys

REPO = "baiyangmuzz/SP2Chinese"
ROOT = r"D:\SP2Chinese"

# 本地产物绝不能进公开仓库：反编译的游戏源码、端到端测试包、分析缓存。
EXCLUDE_PREFIXES = ("_decomp/", "_e2e/", "_ref/", "_stage/", "_archive_zip/")
EXCLUDE_FILES = ("_funcs.json",)
MAX_PUBLISH_BYTES = 5 * 1024 * 1024


def dict_rows():
    try:
        with open(os.path.join(ROOT, "manifest.json"), encoding="utf-8") as fh:
            return int(json.load(fh).get("dictRows") or 0)
    except Exception:
        return 0


# (提交信息, 覆盖的路径前缀或文件名) —— 顺序决定文件列表里显示的说明
def build_groups(rows):
    dict_title = ("词库：%d 条简体中文译文，按批次管理，含禁用清单" % rows
                  if rows else "词库：简体中文译文，按批次管理，含禁用清单")
    return [
        (dict_title, ["dict/", "dictionary_zh_cn.tsv"]),
        ("插件源码：运行时文本替换引擎，Harmony 拦截 TMP 全部文本入口，词库自动更新",
         ["src/"]),
        ("词库校验与合并工具：verify_dict 校验批次能否完整重建运行时字典",
         ["tools/"]),
        ("离线测试器：逐行命中测试、性能基准、内部索引诊断",
         ["test/"]),
        ("安装器：菜单式操作，14 源自动更新，SHA256 校验，回滚管理",
         ["dist/"]),
        ("项目文档、许可证与版本哈希清单",
         ["README.md", "LICENSE", "CONTRIBUTING.md", ".gitignore",
          "manifest.json", "SP2Chinese.csproj"]),
    ]


def gh(args, payload=None):
    import tempfile
    cmd = ["gh", "api"] + args
    tmp = None
    if payload is not None:
        fd, tmp = tempfile.mkstemp(suffix=".json")
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            json.dump(payload, fh)
        cmd += ["--input", tmp]
    proc = subprocess.run(cmd, cwd=ROOT, capture_output=True)
    if tmp:
        try:
            os.unlink(tmp)
        except OSError:
            pass
    # Windows 默认按 GBK 解码 gh 的 UTF-8 输出；commit 回显带中文会炸掉读取线程。
    out = (proc.stdout or b"").decode("utf-8", "replace")
    err = (proc.stderr or b"").decode("utf-8", "replace")
    if proc.returncode != 0:
        raise RuntimeError("gh %s failed:\n%s" % (args[0], err.strip()))
    return json.loads(out) if out.strip() else None


def blob(data_b64):
    res = gh(["repos/%s/git/blobs" % REPO, "-X", "POST"],
             {"content": data_b64, "encoding": "base64"})
    return res["sha"]


def commit_tree(entries, message, parents):
    tree = gh(["repos/%s/git/trees" % REPO, "-X", "POST"], {"tree": entries})
    commit = gh(["repos/%s/git/commits" % REPO, "-X", "POST"],
                {"message": message, "tree": tree["sha"], "parents": parents})
    if not commit or "sha" not in commit:
        raise RuntimeError("commit creation returned no sha")
    return commit


def main():
    rewrite = os.environ.get("REWRITE") == "1"

    raw = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT)
    all_names = [n for n in raw.decode("utf-8").split("\0") if n]
    names = [n for n in all_names
             if not n.startswith(EXCLUDE_PREFIXES) and n not in EXCLUDE_FILES]
    print("files: %d published, %d excluded" % (len(names), len(all_names) - len(names)))

    for rel in names:
        size = os.path.getsize(os.path.join(ROOT, rel.replace("/", os.sep)))
        if size > MAX_PUBLISH_BYTES:
            raise SystemExit("refusing to publish %s: %.1f MB" % (rel, size / 1024.0 / 1024.0))

    groups = build_groups(dict_rows())

    blobs = {}
    for i, rel in enumerate(names, 1):
        full = os.path.join(ROOT, rel.replace("/", os.sep))
        data = base64.b64encode(open(full, "rb").read()).decode("ascii")
        blobs[rel] = blob(data)
        if i % 20 == 0 or i == len(names):
            print("  blob %d/%d" % (i, len(names)))

    def entry(rel):
        mode = "100755" if rel.endswith(".sh") else "100644"
        return {"path": rel, "mode": mode, "type": "blob", "sha": blobs[rel]}

    def group_of(rel):
        for idx, (_, paths) in enumerate(groups):
            for p in paths:
                if rel == p or rel.startswith(p):
                    return idx
        return len(groups) - 1

    parents = []
    if not rewrite:
        try:
            ref = gh(["repos/%s/git/ref/heads/main" % REPO])
            parents = [ref["object"]["sha"]]
            print("parent: %s" % parents[0][:10])
        except RuntimeError:
            print("parent: none")

    # 按组分配文件；rewrite 时每组一个提交，文件归属它首次出现的提交
    buckets = [[] for _ in groups]
    for rel in names:
        buckets[group_of(rel)].append(rel)

    if rewrite:
        done = []
        last = None
        for idx, (message, _) in enumerate(groups):
            items = buckets[idx]
            if not items:
                continue
            done.extend(entry(r) for r in items)
            commit = commit_tree(done, message,
                                 parents if last is None else [last["sha"]])
            print("  [%d] %s -> %s" % (idx + 1, message[:24], commit["sha"][:10]))
            last = commit
    else:
        # 增量：全部文件进一个提交。此前把后续组 continue 掉，
        # 导致 main 只含第一组文件、其余目录从远程消失，已修复。
        all_entries = [entry(r) for r in names]
        msg = os.environ.get('MSG') or ("词库与插件更新：%d 个文件" % len(names))
        last = commit_tree(all_entries, msg, parents)
        print("  增量提交 %d 个文件 -> %s" % (len(names), last["sha"][:10]))

    if last is None:
        print("nothing to commit")
        return 0

    try:
        gh(["repos/%s/git/refs" % REPO, "-X", "POST"],
           {"ref": "refs/heads/main", "sha": last["sha"]})
    except RuntimeError:
        gh(["repos/%s/git/refs/heads/main" % REPO, "-X", "PATCH"],
           {"sha": last["sha"], "force": True})

    print("main -> %s" % last["sha"][:10])
    print("https://github.com/%s" % REPO)
    return 0


if __name__ == "__main__":
    sys.exit(main())
