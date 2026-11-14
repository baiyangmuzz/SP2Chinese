# 安装器回归测试

这些是离线测试驱动，不进 CI，发布前手动跑。都直接调用 `dist/setup/src` 里的
`Installer / Payload / Manifest / Updater`，在临时目录里模拟游戏目录，不需要装游戏。

| 测试 | 验证内容 | 参数 |
|---|---|---|
| `Cycle` | 连续 5 轮安装 + 卸载，备份不无限堆积 | `payload.zip manifest.json` |
| `KeepDictTest` | 本地词库比包内新时保留本地，不报校验失败 | `payload.zip manifest.json 新词库.tsv` |
| `RollbackTest` | 历史备份列表、回滚内容正确、目录穿越被拒 | `payload.zip manifest.json` |
| `UpdateTest` | 更新检查四个分支（有新版 / 已最新 / 网络失败 / 拒收坏哈希） | 见文件内注释 |
| `E2E` | 安装器自更新全链路（发现 → 下载 → 校验 → 替换） | `manifest.json` |

跑法（以 RollbackTest 为例）：

```bash
dotnet build dist/setup/test/RollbackTest.csproj -c Release
dist/setup/test/bin/Release/net48/RollbackTest.exe dist/payload.zip dist/manifest.json
```

全部输出 `RESULT: ALL PASSED` 且退出码 0 才算通过。
