# SimplePlanes 2 简体中文汉化

**English:** Full Simplified Chinese localization for [SimplePlanes 2](https://store.steampowered.com/app/2840470/) — runtime text replacement via BepInEx/Harmony, 10,393 dictionary rows (8,675 translated + 1,718 deliberately kept in English), dynamic CJK font atlas that fixes tofu boxes in chat and UI, a single-file one-click installer (auto game detection, SHA256 verification, one-click rollback, self-update), and in-game dictionary auto-updates. The game has no i18n framework; all strings are replaced at runtime.

[SimplePlanes 2](https://store.steampowered.com/app/2840470/) 的社区汉化补丁。

**中文：** 为 SimplePlanes 2 提供完整的简体中文汉化——通过 BepInEx/Harmony 在运行时替换文本，共 10393 条词条（其中 8675 条译文、1718 条刻意保留英文），动态生成中文字体图集（修复聊天与界面的方块字）；单文件一键安装器：自动找游戏目录、SHA256 校验、一键回滚、自动更新，支持游戏内词库热更新。游戏本身没有 i18n 框架，所有文本均在运行时替换。

游戏官方只提供英语界面（Steam 商店页的语言表里简体中文标注为「不支持」），
且游戏内没有 i18n 框架，所有文本硬编码在资源 XML 与 DLL 里。因此这个项目不做
语言文件注入，而是在运行时替换 TMP 文本。

| 项 | 值 |
|---|---|
| 词库条目 | 10393（TSV 行数；每行 key 唯一，无重复；含 1718 条刻意保留英文的恒等行） |
| 插件版本 | 1.7.0 |
| 作者 | 白杨木（[baiyangmuzz](https://github.com/baiyangmuzz)） |
| 交流群 | QQ 1128992783 |
| 下载 | [Releases 最新版](https://github.com/baiyangmuzz/SP2Chinese/releases/latest) |
| 仓库 | https://github.com/baiyangmuzz/SP2Chinese |

游戏处于 Early Access，文本会随更新变动，词库需要持续跟进。

## 安装

下载 exe，双击，点「开始安装」。

1. 到 [Releases 页面](https://github.com/baiyangmuzz/SP2Chinese/releases/latest)
   下载 `SP2Chinese-Setup-v<版本>.exe`（不要下 Source code，那是源码）
2. 双击运行，管理员授权选「是」
3. 点「开始安装」，看到「安装完成」即可启动游戏

安装器会处理这些：

- 自动定位游戏：先读 Steam 注册表，再解析 `steamapps/libraryfolders.vdf` 的所有库；
  找不到才让你选文件夹
- 自动备份旧版本，写入后逐个核对 SHA256，失败自动回滚
- 已有 BepInEx 与其它模组不会被覆盖
- 装完顺带把词库更新到最新，不需要二次操作

这个 exe 是单文件：不用解压、不依赖 PowerShell 脚本，只需要 Windows 10/11
自带的 .NET Framework 4.8。界面用 WPF 写的，支持高分屏。

### 按钮说明

| 按钮 | 作用 |
|---|---|
| 开始安装 / 重新安装 | 安装或覆盖安装汉化 |
| 检查更新 | 检查插件、词库、安装器三者有没有新版 |
| 回滚版本 | 列出历史备份，选一个恢复；恢复前会自动备份当前版本 |
| 卸载汉化 | 把插件目录移到 `BepInEx\_mod_backups`，不删文件 |

**备选：传统压缩包**（脚本化 / CI / 批量部署用）

下载 `SP2Chinese-v<版本>-<日期>.zip`，解压后运行 `SP2汉化.bat`。可用开关：

```bat
SP2汉化.bat -Silent -Yes        :: 无人值守安装（无任何弹窗，供脚本 / CI 使用）
SP2汉化.bat -Yes                :: 单次安装，不弹菜单
SP2汉化.bat -Uninstall          :: 无人值守卸载
SP2汉化.bat -NoGui              :: 回退到旧版数字菜单
SP2汉化.bat -TestUpdate         :: 只诊断更新源连通性，不安装
```

安装器内置自测（供维护者验证发布包）：

```bat
SP2Chinese-Setup-v<版本>.exe --selftest C:\selftest.log
```

它会跑全新安装、已装 BepInEx、卸载、坏包拒绝四个场景，退出码 0 表示全部通过。

## 卸载

直接运行安装器，点**「卸载汉化」**。插件目录会被移动到 `BepInEx\_mod_backups`，
不删除文件，BepInEx 本体与其它模组不受影响。

也可以用压缩包里的 `SP2汉化.bat -Uninstall` 无人值守卸载。

安装备份保留最近 3 份；卸载备份与回滚前的备份单独计数，不会互相挤掉。

## 回滚

安装器保留最近 3 份安装备份、最近 3 份卸载备份、最近 2 份回滚前备份。
点**「回滚版本」**会列出全部可用备份（时间、版本、类型），选一份即可恢复。

回滚本身是可逆的：动手前会先把**当前**版本另存为 `-pre-rollback-` 备份，
所以回滚错了还能再滚回来。选中的历史备份不会被消耗，可以反复使用。

## 更新机制

分三种，由 `manifest.json` 里的字段区分。

**词库更新**：插件启动 15 秒后请求 `manifest.json` ，比对本地字典的 SHA256 与
manifest 里的 `dictVersion` 。不一致时下载新字典、校验 SHA256、原子替换并热重载，
不需要重装或重启。旧字典保留为 `.bak-auto` ，安装失败会自动回滚。

`dictVersion` 是字典内容的 SHA256 前 12 位（内容寻址），`dictBuild` 是构建序号；
本地 `dict.build` 不旧于线上时保留本地词库，不覆盖。安装器也按同一规则跳过已装的
较新词库，不会把本地修复打回旧版。

下载体在后台线程校验后以字节形式交给主线程，安装前再复验一次哈希，避免
「校验内存、却从可替换的临时文件安装」的窗口；下载地址限制为 https 且属于项目分发域名，
并有 8 MB 大小上限。期望哈希缺失时直接拒绝安装（fail-closed）。

下载地址为 `raw.githubusercontent.com`，失败后尝试 jsDelivr 镜像；manifest 从多个
可信源取用并要求内容一致，始终校验 TLS 证书，不使用第三方代理站。网络类失败
会在稍后自动重试（最多 3 次）。断网或校验失败时继续使用本地词库，不阻塞游戏。

**插件更新**：manifest 的 `modVersion` 高于本地版本时，只在日志里提示到 Releases
或群里下载完整包，不自动替换 DLL。

**安装器自更新**：manifest 的 `setup` 段记录了安装器自己的 `version / sha256 /
size / url`。点「检查更新」发现新版后会出现「立即更新」按钮，流程是下载、
校验 SHA256、退出、被新版本覆盖、重启。文件名里的版本号也会跟着改。

`setup` 段走 `raw.githubusercontent` / jsDelivr，不依赖容易失效的 GitHub API。
没有任何哈希可校验时，安装器**只提示、不下载**——不会执行来路不明的程序。

自动检查可在 `BepInEx/config/sp2.zh.cfg` 关闭：

```ini
[更新]
自动检查词库更新 = true
```

## 字体

游戏自带字体不含中文字形，直接显示中文会是方块字。插件把词库里用到的全部汉字
生成到字体图集，并把每个文本组件切换到该字体。聊天框这类运行时创建的文本同样
会切换，所以联机聊天里看到的也是正常文字。

词库热重载时，新词条带来的新字形会一并补上，不需要重新安装。

## 翻译范围

以下内容保留英文原文，这是刻意的：

- 官方载具名（Adder、Dragon、Kicking Fish 等）
- 挑战与赛道名（The Big L、On Rails、Driftwood Air Race 等）
- 品牌名（Jundroo、SP Logo 等）
- XML 编辑器中显示的属性名与枚举值（partType、Part、Slice 等）
- 载具说明中的载具名与作者名

前三类是因为社区用这些名字互相指认；第四类是因为玩家会照着界面抄进 XML，
翻译后会导致属性不生效。

完整清单见 [`dict/do-not-translate.txt`](dict/do-not-translate.txt)。

## 贡献

词库按批次管理，每个批次一个 TSV 文件，格式为 ` 英文<TAB>中文 ` （每行恰好一个 TAB）。
新增词条时新建 `dict/new<编号>_<主题>_zh_cn.tsv` ，然后运行：

```bash
python tools/verify_dict.py
python tools/check_dict.py --no-manifest
```

`verify_dict.py` 输出 `RESULT: in sync.` 表示批次可重建运行时字典；`check_dict.py`
检查占位符、禁译、哨兵与多 TAB 等结构问题，输出 `RESULT: OK` 才可提交。
`--no-manifest` 用于打包前（manifest 尚未按新字典更新）；发布后 CI 会跑不带该参数的
完整检查，并额外由 `tools/check_dist.py` 核对产物哈希。
详细规则见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 实现说明

### 文本匹配

游戏中的文本有四种形态，字典对应四种写法：

| 游戏文本 | 字典写法 |
|---|---|
| `Fuel: 87.5%` | `Fuel: {0}%` （数字掩码） |
| ` pitch angle between 10 and 20 degrees.` | ` pitch angle between {2:F0} and {3:F0} degrees.` （格式模板） |
| `Part damaged: Fuselage` | `Part damaged: {*}` （分段模板） |
| `Shortcut: [SaveAircraft]` （渲染为 `Shortcut: Ctrl + S` ） | `Shortcut: [SaveAircraft]` （快捷键前后缀） |

匹配顺序为：精确匹配 → 数字掩码 → 格式模板 → 快捷键前后缀 → `{*}` 分段模板。

数字掩码把运行时文本里的数字按出现顺序替换成 `{0}`,`{1}`,… 再查字典；
它覆盖不了 C# 格式模板（非零索引 `{2}`、格式说明符 `{0:F0}`、对齐 `{0,9}`、
固定文本里带字面数字、命名槽位 `{Name}`）。这些行会额外编译成锚定正则，
把渲染后的值捕获回填到译文，玩家名/枚举名这类非数值槽位同样能命中。

分段模板按固定文本长度降序尝试，避免短模板把长消息的剩余部分吞进 `{*}` 。
`{*}` 捕获的内容会再次进入匹配流程，因此 `Part damaged: Fuselage` 会得到
` 部件受损：机身 ` 。

### 文本入口

插件通过 Harmony 拦截以下位置：

- `TMP_Text.set_text` 、 `SetText(string)` 、 `SetText(string, bool)` 、 `SetText(StringBuilder)`
- `TMPro.TextMeshProUGUI.Awake` 、 `TMPro.TextMeshPro.Awake`
- `UnityEngine.EventSystems.UIBehaviour.Awake` （对应旧版 uGUI `Text` ）

补丁逐个类挂载，单个补丁失败不影响其余部分。 `loaded-build.txt` 会记录挂载结果
（格式为 `版本<TAB>时间<TAB>identifiers=N<TAB>patches=N/M` ，例如 `patches=8/8` ）。

序列化在 prefab 里的文本不经过 setter，由 Awake 钩子处理；另有每 5 秒一次的
全场景扫描作为兜底（按真实时间计时，不受帧率影响）。

### 目录结构

```
src/                     插件源码（C# / BepInEx 5）
dict/                    词库批次与禁用清单（base_zh_cn.tsv 为基线批次）
tools/                   词库校验、静态检查、发布产物一致性检查
dist/                    安装器（install.ps1 + Gui.ps1）、打包脚本与 payload
test/                    离线测试器
.github/workflows/ci.yml 词库一致性、静态检查、发布产物一致性、离线测试器回归
manifest.json            文件哈希与版本信息
```

### 构建

需要 .NET SDK，以及一份 SimplePlanes 2 安装（ `SP2Chinese.csproj` 中的 `GameDir` 指向它）。
路径不是默认值时可用 `dotnet build -p:GameDir=<路径>` 或 `SP2_GAME_DIR` 指定；
打包脚本另支持 `SP2_REPO` / `SP2_GAME_DIR` 。运行时字体可用 `SP2_FONT` 覆盖
（在插件进程里生效，非打包脚本参数）。

```bash
dotnet build -c Release
python tools/verify_dict.py
python tools/check_dict.py
python dist/build_dist.py          # 会同步重建根字典、payload、stage、zip、manifest
python tools/check_dist.py         # 发布前核对产物与 manifest 完全一致
```

`build_dist.py` 强制使用 `bin/Release/SP2Chinese.dll`，并由 `dict/` 批次重建根字典，
再把同一份字典与 manifest 写入 payload / stage / zip / dist / 仓库根，保证七处同源。
CI 在 push / PR 时自动跑词库检查、发布产物一致性与离线测试器（含格式模板回归），
但插件 DLL 本身需要游戏目录里的 BepInEx / Unity / TMPro 引用，只能在本地编译。

## 许可

- 插件代码：MIT
- 词库：CC BY-NC-SA 4.0

见 [LICENSE](LICENSE)。

SimplePlanes 2 及其原始文本、素材、载具版权归 Jundroo, LLC 所有。本项目不包含
游戏原始资源，仅提供译文数据与运行时替换逻辑。若权利方认为本项目存在问题，
请通过 Issue 联系。
