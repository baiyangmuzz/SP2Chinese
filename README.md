# SimplePlanes 2 简体中文汉化

**English:** Full Simplified Chinese localization for [SimplePlanes 2](https://store.steampowered.com/app/2840470/) — runtime text replacement via BepInEx/Harmony, 10,416 entries, dynamic CJK font atlas (chat renders correctly), one-click installer with SHA256 verification, and in-game dictionary auto-updates. The game has no i18n framework; all strings are replaced at runtime.

[SimplePlanes 2](https://store.steampowered.com/app/2840470/) 的社区汉化补丁。

游戏官方只提供英语界面（Steam 商店页的语言表里简体中文标注为「不支持」），
且游戏内没有 i18n 框架，所有文本硬编码在资源 XML 与 DLL 里。因此这个项目不做
语言文件注入，而是在运行时替换 TMP 文本。

| 项 | 值 |
|---|---|
| 词库条目 | 10416 |
| 插件版本 | 1.5.1 |
| 作者 | 白杨木（[baiyangmuzz](https://github.com/baiyangmuzz)） |
| 交流群 | QQ 1128992783 |
| 下载 | [Releases 最新版](https://github.com/baiyangmuzz/SP2Chinese/releases/latest) |
| 仓库 | https://github.com/baiyangmuzz/SP2Chinese |

游戏处于 Early Access，文本会随更新变动，词库需要持续跟进。

## 安装

1. 到 [Releases 页面](https://github.com/baiyangmuzz/SP2Chinese/releases/latest)
   下载 `SP2汉化_v<版本>_<日期>.zip`。不要下载页面里的 Source code，
   那是源码，不是安装包
2. 退出游戏，解压
3. 运行 ` 一键安装中文.bat` ，管理员授权选「是」
4. 启动游戏

安装器会定位游戏目录：先读 Steam 注册表路径，再解析 `steamapps/libraryfolders.vdf` 里的所有库，
然后在每个库下找 `steamapps\common\SimplePlanes 2` 。找不到时弹出文件夹选择窗口，
再失败才要求手动输入路径。

安装完成后按包内 `manifest.json` 逐文件校验 SHA256，共 23 个文件。

## 卸载

运行 ` 卸载中文.bat` 。插件目录会被移动到 `BepInEx\_mod_backups` ，不删除文件，
BepInEx 本体与其它模组不受影响。

## 更新机制

分两种，由 `manifest.json` 里的字段区分。

**词库更新**：插件启动 15 秒后请求 `manifest.json` ，比对本地字典的 SHA256 与
manifest 里的 `dictVersion` 。不一致时下载新字典、校验 SHA256、替换并热重载，
不需要重装或重启。旧字典保留为 `.bak-auto` 。

下载地址优先 `raw.githubusercontent.com` ，失败回落到 `www.baiyangmu.xyz` 。
断网或校验失败时继续使用本地词库，不阻塞游戏。

**插件更新**：manifest 的 `modVersion` 与本地版本不同时，只在日志里提示到 Releases
或群里下载完整包，不自动替换 DLL。

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

词库按批次管理，每个批次一个 TSV 文件，格式为 ` 英文<TAB>中文 ` 。新增词条时新建
`dict/new<编号>_<主题>_zh_cn.tsv` ，然后运行：

```bash
python tools/verify_dict.py
```

输出 `RESULT: in sync.` 表示批次合并结果与运行时字典一致，可以提交。
详细规则见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 实现说明

### 文本匹配

游戏中的文本有三种形态，字典对应三种写法：

| 游戏文本 | 字典写法 |
|---|---|
| `Fuel: 87.5%` | `Fuel: {0}%` |
| `Part damaged: Fuselage` | `Part damaged: {*}` |
| `Shortcut: [SaveAircraft]` （渲染为 `Shortcut: Ctrl + S` ） | `Shortcut: [SaveAircraft]` |

匹配顺序为：精确匹配 → 数字掩码 → 快捷键前后缀 → `{*}` 分段模板。
分段模板按固定文本长度降序尝试，避免短模板把长消息的剩余部分吞进 `{*}` 。
`{*}` 捕获的内容会再次进入匹配流程，因此 `Part damaged: Fuselage` 会得到
` 部件受损：机身 ` 。

### 文本入口

插件通过 Harmony 拦截以下位置：

- `TMP_Text.set_text` 、 `SetText(string)` 、 `SetText(string, bool)` 、 `SetText(StringBuilder)`
- `TMPro.TextMeshProUGUI.Awake` 、 `TMPro.TextMeshPro.Awake`
- `UnityEngine.EventSystems.UIBehaviour.Awake` （对应旧版 uGUI `Text` ）

补丁逐个类挂载，单个补丁失败不影响其余部分。 `loaded-build.txt` 会记录挂载结果
（形如 `patches=8/8` ）。

序列化在 prefab 里的文本不经过 setter，由 Awake 钩子处理；另有每 300 帧一次的
全场景扫描作为兜底。

### 目录结构

```
src/            插件源码（C# / BepInEx 5）
dict/           词库批次、禁用清单、基线快照
tools/          词库校验、合并、打包
dist/           安装器与打包脚本
test/           离线测试器
manifest.json   文件哈希与版本信息
```

### 构建

需要 .NET SDK，以及一份 SimplePlanes 2 安装（ `SP2Chinese.csproj` 中的 `GameDir` 指向它）。

```bash
dotnet build -c Release
python tools/verify_dict.py
python dist/build_dist.py
```

## 许可

- 插件代码：MIT
- 词库：CC BY-NC-SA 4.0

见 [LICENSE](LICENSE)。

SimplePlanes 2 及其原始文本、素材、载具版权归 Jundroo, LLC 所有。本项目不包含
游戏原始资源，仅提供译文数据与运行时替换逻辑。若权利方认为本项目存在问题，
请通过 Issue 联系。
