# SimplePlanes 2 简体中文汉化（国内镜像）

**这是下载镜像。源码、更新日志、问题反馈都在 GitHub：**
https://github.com/baiyangmuzz/SP2Chinese

游戏本身没有中文。这个补丁在运行时把界面、聊天、载具说明等文本换成中文，
共 10469 条词条。

## 下载

到右边的 **发行版 / Releases** 页面，下载 `SP2Chinese-Setup-v<版本>.exe`。

文件是从 GitHub 原样同步过来的，大小和 SHA256 与 GitHub Release 页面一致：

```
SP2Chinese-Setup-v1.7.5.exe
大小   1266688 字节
SHA256 2d512875acdb740e71a5fdb879bd43a7a168e7094458c52d3c7d928ae0efafcc
```

## 安装

1. 退出游戏
2. 双击 exe，管理员授权选「是」
3. 点「开始安装」，显示「安装完成」后就能进游戏

安装器会自己找 Steam 游戏目录；找不到才需要手动选择。
已有 BepInEx 和其它模组不会被覆盖。

## 注意

- **1.7.5 起安装器的自动更新优先走 Gitee**，Gitee 不可达时才回落 GitHub。
  已经装了 1.7.4 或更早版本的用户，需要先手动下载 1.7.5 覆盖安装一次，
  之后才会走 Gitee 更新通道（词库和备份都会保留）。
- 本项目开源免费，**不会收费**。如果有人在任何平台向你收费，那不是我们。

## 交流

QQ 群：1128992783

---

维护者：同步脚本在 https://github.com/baiyangmuzz/SP2Chinese/blob/main/tools/sync_gitee.py
（`GITEE_TOKEN=xxx python tools/sync_gitee.py --repo 你的名字/SP2Chinese-mirror`）
