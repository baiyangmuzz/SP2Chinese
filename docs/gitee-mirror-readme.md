# SimplePlanes 2 简体中文汉化（国内镜像）

**这是下载镜像。源码、更新日志、问题反馈都在 GitHub：**
https://github.com/baiyangmuzz/SP2Chinese

游戏本身没有中文。这个补丁在运行时把界面、聊天、载具说明等文本换成中文，
共 10393 条词条。

## 下载

到右边的 **发行版 / Releases** 页面，下载 `SP2Chinese-Setup-v<版本>.exe`。

文件是从 GitHub 原样同步过来的，SHA256 与 GitHub Release 页面上写的一致：

```
SHA256(SP2Chinese-Setup-v1.7.0.exe) = 5ed4b437947c59dfa629a84d7cc1a35ea452cb63db0e313306426f2fe715d4fe
```

## 安装

1. 退出游戏
2. 双击 exe，管理员授权选「是」
3. 点「开始安装」，显示「安装完成」后就能进游戏

安装器会自己找 Steam 游戏目录；找不到才需要手动选择。
已有 BepInEx 和其它模组不会被覆盖。

## 注意

- **安装器的自动更新走 GitHub**。如果你打不开 GitHub，自动更新会失败，
  到时来这里下载新版 exe 覆盖安装即可（会保留你的词库和备份）。
- 本项目开源免费，**不会收费**。如果有人在任何平台向你收费，那不是我们。

## 交流

QQ 群：1128992783

---

维护者：同步脚本在 https://github.com/baiyangmuzz/SP2Chinese/blob/main/tools/sync_gitee.py
（`GITEE_TOKEN=xxx python tools/sync_gitee.py --repo 你的名字/SP2Chinese-mirror`）
