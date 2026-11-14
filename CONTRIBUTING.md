# 贡献指南

## 词条 key 必须与游戏渲染文本完全一致

字典按精确字符串匹配。多一个空格、少一个换行、标点不同，都会导致匹配失败，
而且不会报错，只是不生效。

不确定游戏实际渲染出什么字符串时，看 `BepInEx/plugins/SP2Chinese/untranslated.txt` ，
里面是被记录下来的未命中文本原文。

## 提交流程（三步）

**推荐用 Pull Request，不要直接改 `main`。** 流程很短：

```bash
# 1. Fork 仓库并克隆你自己的 fork
git clone https://github.com/<你的用户名>/SP2Chinese
cd SP2Chinese

# 2. 新建批次文件放译文（见下节格式）
#    dict/new40_你的主题_zh_cn.tsv

# 3. 合并进运行时字典（关键一步，别漏）
python tools/rebuild_dict.py

# 4. 校验
python tools/verify_dict.py     # 必须 RESULT: in sync.
python tools/check_dict.py      # 必须 RESULT: OK

# 5. 提交并发起 PR
git add dict/ dictionary_zh_cn.tsv manifest.json
git commit -m "词库：补充 XXX 相关译文"
git push origin master
```

然后在 GitHub 上开 Pull Request 到 `main` 分支。CI 会自动跑全部检查，
**任何一项不过都无法合并**。

### 为什么需要 `rebuild_dict.py`

插件运行时读的是仓库根目录的 `dictionary_zh_cn.tsv`，不是 `dict/` 下的批次。
只改批次而不重建，`verify_dict.py` 会报 `extra in dict`（新词条没进运行时字典）。
`rebuild_dict.py` 做的就是「按文件名排序合并批次 + 更新 manifest 的字典哈希」，
合并规则与发布打包完全一致。

它会顺带校验跨批次冲突：同一个 key 在两个批次里译文不同时会直接报错，
并指出是哪两个文件、各自写了什么，而不是靠文件名顺序静默覆盖。

### 常见问题

**Q：只改一个已有词条的译文怎么办？**
直接改它所在的那个批次文件，然后同样跑 `rebuild_dict.py`。不要新建批次。

**Q：`check_dist.py` 报错？**
它是发布前检查，依赖 `dist/payload` 和本地 zip。贡献翻译不需要跑它，
CI 上也会自动跳过。

**Q：不想装 .NET SDK？**
只改词库的话不需要，`rebuild_dict.py` / `verify_dict.py` / `check_dict.py`
都只要 Python 3。

## 译稿怎么变成玩家的更新

新译稿**不会**一合并就推送给玩家。流程是「审核 + 攒批发布」：

```
你的 PR  ──▶  CI 自动检查 ──▶  维护者审核译稿
                                      │
                                      ▼
                            攒够一批后维护者打包
                            （dist/build_dist.py 生成新 dictBuild）
                                      │
                                      ▼
                            打 Release ──▶ 玩家一次性收到整批
```

所以 `rebuild_dict.py` **只更新字典哈希，不动 `dictBuild`**。`dictBuild` 是
「一次发布」的标识，由维护者打包时统一推进——这样不会因为某一条译稿合并，
就让所有在线玩家的词库热重载一次。

维护者侧的操作：

```bash
# 平时只管 review 和合并 PR，玩家侧不会有任何变化
# 攒够一批译稿后：
python dist/build_dist.py     # 生成新的 dictBuild，七处产物同源
python tools/check_dist.py    # 确认产物一致
git commit -m "词库：XX 批译稿汇总"
# 打 Release 上传 SP2Chinese-v<版本>-<日期>.zip
```

审核时主要看这几类问题（CI 能查格式，查不了译法）：

- **术语统一**：`Aircraft` 统一译「载具」而非「飞机」，`Part` 统一「部件」
- **长度**：按钮/标签文字过长会被 UI 截断，需要更短的译法
- **语气**：游戏是飞行模拟，提示语保持简洁、避免口语化
- **禁译项**：官方载具名、挑战名、品牌、XML 属性名不得翻译（`do-not-translate.txt`）

## 新增词条

在 `dict/` 下新建批次文件，不要修改已有批次：

```
dict/new<编号>_<主题>_zh_cn.tsv
```

UTF-8 无 BOM，LF 换行，每行 ` 英文<TAB>中文 ` ：

```
Delete Selected Part	删除选中部件
```

## 特殊文本的写法

**含可变数字**：用 C# 格式占位符 `{0}`

```
Fuel: {0}%	燃油：{0}%
```

**运行时拼接的消息**：用 `{*}` 标记变量位置，译文中同样保留 `{*}`

```
Part damaged: {*}	部件受损：{*}
Mirroring enabled for part {*} and {*} other parts.	已为部件 {*} 及另外 {*} 个部件开启镜像。
```

**快捷键提示**：原文中的方括号绑定名必须保留

```
Shortcut: [SaveAircraft]
```

游戏渲染时会把 `[SaveAircraft]` 替换成实际按键（如 `Ctrl + S` ），插件通过
前后缀匹配处理。若字典里写成 `Ctrl + S` ，则永远匹配不上。

## 刻意保留英文的写法

不想翻译的条目写成 **恒等行**（`key<TAB>key`），不要写 `保留` 之类的中文哨兵值：

```
NetworkVehicle_Local_{0}	NetworkVehicle_Local_{0}
```

恒等行会被视为「刻意不翻译」：既不进入译文匹配，也不会被记进
`untranslated.txt`。写中文哨兵会真的把界面文本翻成那两个字，并丢掉 `{0}`。

## 提交前校验

```bash
python tools/rebuild_dict.py   # 合并批次进运行时字典并同步 manifest（改词库后必跑）
python tools/verify_dict.py    # 批次能否逐条重建运行时字典（含多 TAB / 跨批次冲突）
python tools/check_dict.py     # 占位符 / 禁译 / 恒等行 / 结构 / 行数口径
```

`verify_dict.py` 必须输出 `RESULT: in sync.`；`check_dict.py` 必须输出
`RESULT: OK`。

发布前额外跑（依赖打包产物，贡献翻译不需要）：

```bash
python dist/build_dist.py      # 重新打包，同步 payload / zip / manifest
python dist/build_setup.py     # 生成单文件安装器 SP2Chinese-Setup-v<版本>.exe
python tools/check_dist.py     # payload / zip / manifest 全量哈希一致
```

`build_setup.py` 会顺带把安装器自身的 `version / sha256 / size / url` 写进
`manifest.json` 的 `setup` 段 —— 旧版安装器的「检查更新」靠它发现新版本并自动替换。
**发布时必须先把 exe 传到同名 Release，再提交 manifest.json**，否则哈希对不上。

安装器工程在 `dist/setup/`（WPF / .NET Framework 4.8，原生 C#，不内嵌脚本）：

```bash
dotnet build dist/setup/SP2Chinese.Setup.csproj -c Release

# 发布前自测：全新安装 / 已装 BepInEx / 卸载 / 坏包拒绝，四场景共 20 项断言
SP2Chinese-Setup-v<版本>.exe --selftest C:\selftest.log
```

要求 `RESULT: ALL PASSED`、退出码 0。安装器本身不参与词库翻译流程，
改界面或安装逻辑时跑这一条即可。

`check_dict.py` 现在还会检查：每行恰好一个 TAB、`{*}` 与 `[Binding]` 在译文中
原样保留、跨批次同 key 不同值必须显式裁决（不能靠文件名顺序静默覆盖）、
`do-not-translate.txt` 中若作为独立 key 出现必须是恒等行，以及 manifest 的
`dictSha256 / dictVersion / dictRows / files 哈希 / dict.build` 与仓库根字典一致。

`.github/workflows/ci.yml` 在 push / PR 时自动跑这些检查，以及离线测试器的
往返审计、格式模板回归、索引诊断、性能基准和 manifest 数字字段回归断言（防止
`dictBuild` 这类 JSON 数字字段再次被解析成 0、导致游戏内词库自更新失效）。
不一致时 PR 不会通过 CI。

`verify_dict.py` 默认比对仓库根目录的 `dictionary_zh_cn.tsv`，不需要本机安装游戏。
要比对已安装的副本：

```bash
python tools/verify_dict.py --live "<游戏目录>\BepInEx\plugins\SP2Chinese\dictionary_zh_cn.tsv"
# 或设置环境变量 SP2_LIVE_GAME_DICT / SP2_DICT
```

## 不允许翻译的内容

`dict/do-not-translate.txt` 中的条目不得翻译，包括官方载具名、挑战与赛道名、
品牌、XML 编辑器的属性名与枚举值。清单里的名字大多不是独立词条（只出现在长
载具说明的开头），`tools/check_dict.py` 会保证：一旦其中某个名字作为独立 key
出现，就必须是恒等行；被译成中文会直接让 CI 失败。

## 含可变数字 / 格式模板的行

简单数字用 `{0}`（数字掩码会把运行时数字按出现顺序编号）。但游戏里还有大量
C# 格式模板，运行时占位符已被替换成实际文本，写法要保留原样：

```
 pitch angle between {2:F0} and {3:F0} degrees.
Area {0,9}:  {1:00.0000}  -->  {2:00.0000}
Exported {1} game objects to a single mesh: {2}
Lap {Lap}/{NumLaps}
```

非零索引、格式/对齐说明符、固定文本里的字面数字、命名槽位都走「格式模板」
正则路径（见 README「文本匹配」），无需改成 `{0}` 形式；但 key 与 value 的
槽位集合必须一致，且 `[Binding]` 必须原样保留。`--format` 回归会逐条验证。

## 代码改动

- `dotnet build -c Release` 须零警告
- 涉及匹配逻辑时，用离线测试器验证（CI 用同样命令）：

```bash
dotnet build test/trtest.csproj -c Release
dotnet test/bin/Release/trtest.dll dictionary_zh_cn.tsv                    # 往返 + 占位符审计
dotnet test/bin/Release/trtest.dll dictionary_zh_cn.tsv --format           # 格式模板回归
dotnet test/bin/Release/trtest.dll dictionary_zh_cn.tsv --diag             # 内部索引状态
dotnet test/bin/Release/trtest.dll dictionary_zh_cn.tsv --bench            # 性能
dotnet test/bin/Release/trtest.dll dictionary_zh_cn.tsv --manifest manifest.json
dotnet test/bin/Release/trtest.dll dictionary_zh_cn.tsv <probe文件>        # 逐行命中
```

- 插件工程引用游戏安装目录下的 DLL；若路径不是默认值，用
  `dotnet build -p:GameDir=<路径>` 或环境变量 `SP2_GAME_DIR` 指定。
- 打包脚本的仓库目录 / 游戏目录可用 `SP2_REPO` / `SP2_GAME_DIR` 覆盖。
- 运行时字体可用 `SP2_FONT` 指定（在插件进程里生效，便于非 Windows 调试；
  打包脚本不读这个变量）。
- 发布前必须 `python dist/build_dist.py` 重跑打包，再 `python tools/check_dist.py`。
- 不要提交 `bin/` 、 `obj/` 、 `_stage/` 、 `_cache/`

## 问题反馈

在 QQ 群 1128992783 反馈，或开 Issue，附：

1. `BepInEx\plugins\SP2Chinese\loaded-build.txt` 的内容
2. 界面截图
3. 若为「未翻译」，附 `untranslated.txt` 中对应的行
