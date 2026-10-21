# SimplePlanes 2 简体中文汉化

给 [SimplePlanes 2](https://store.steampowered.com/) 用的社区汉化补丁。游戏本身**没有多语言架构**，所有界面文字都硬编码在 XML 和 DLL 里，所以这里用的是运行时文本替换，而不是语言文件。

- 作者：**白杨木**（[baiyangmuzz](https://github.com/baiyangmuzz)）
- 交流群：**QQ 1128992783**
- 当前词库：**10220 条**
- 支持版本：SimplePlanes 2 v1.4.x（游戏更新后可能需要补词条）

> 非官方项目。SimplePlanes 2 及其内容的版权归 Jundroo 所有。

---

## 给玩家

### 安装

1. 完全退出游戏
2. 解压，双击 `一键安装中文.bat`
3. 弹管理员授权时选「是」（游戏一般在 `Program Files` 下）

安装器会自己找游戏目录（读注册表 + 解析 `libraryfolders.vdf`，支持多硬盘多 Steam 库）；找不到会弹出文件夹选择窗口，再不行才让你粘贴路径。

装完会自动按 `manifest.json` 做 **SHA256 校验**，23 个文件逐个核对，装歪了当场报错，不会让你进游戏才发现没生效。

### 更新

- **小更新（只改词库）**：插件自动下载，几秒内热重载，**不用重装也不用重启**。
- **大更新（插件代码变了）**：启动日志会提示新版本，到群里或 Releases 下载完整包。

### 卸载

双击 `卸载中文.bat`。只把 `BepInEx\plugins\SP2Chinese` 挪进 `BepInEx\_mod_backups`，不删除，BepInEx 本体和其他模组不受影响。

### 不翻译的东西

这是刻意的，不是漏翻：

- **官方载具名**（`Adder`、`Dragon`、`Kicking Fish`…）——社区靠这些名字对号入座
- **挑战/赛道名**（`The Big L`、`On Rails`、`Driftwood Air Race`…）
- **品牌**（`Jundroo`、`SP Logo`…）
- **XML 编辑器的属性名与枚举值**（`partType`、`Part`、`Slice`…）——你要照着界面抄进 XML，翻了就错
- 载具说明里的作者名与载具名

完整清单见 [`dict/do-not-translate.txt`](dict/do-not-translate.txt)。

---

## 给贡献者

想补词条、修错译，欢迎。流程很短：

```bash
# 1. 克隆
git clone https://github.com/baiyangmuzz/SP2Chinese.git
cd SP2Chinese

# 2. 新建一个批次文件（不要改别人的）
#    dict/new<编号>_<主题>_zh_cn.tsv
#    格式：英文原文<TAB>中文译文，一行一条

# 3. 把新条目合并进运行时字典并校验
python tools/verify_dict.py

# 4. 提交 PR
```

详细规则见 [CONTRIBUTING.md](CONTRIBUTING.md)。

**词库的黄金法则：key 必须和游戏里渲染出来的字符串完全一致。** 差一个空格、一个换行、一个标点都匹配不上。改之前先跑 `verify_dict.py`，它会告诉你字典树能不能重建出运行时字典。

---

## 给好奇的技术人

### 为什么不用语言文件

查过 `resources.assets` 和 `Game.dll`：`Localization`、`zh_CN`、`LanguageTable`、`TranslationTable` 全部不存在。没有 i18n 框架，就没有可注入的语言文件，只能在运行时替换 TMP 文本。

### 匹配引擎（`src/Translator.cs`）

游戏里大量文本是**拼出来的**，精确匹配必然失败。字典支持三种写法：

| 情况 | 字典写法 |
|---|---|
| 普通文本 | 直接写原文 |
| 数字插值 `Fuel: 87.5%` | `Fuel: {0}%` |
| 拼接消息 `Part damaged: Fuselage` | `Part damaged: {*}` |
| 快捷键提示 `Shortcut: Ctrl + S` | `Shortcut: [SaveAircraft]` |

引擎会依次尝试：精确匹配 → 数字掩码 → 快捷键前后缀匹配 → `{*}` 分段模板。捕获到的变量**还会再翻一次**，所以 `Part damaged: Fuselage` 会输出「部件受损：机身」，不会留个英文部件名。

几个踩过的坑，写在注释里了：

- 分段模板必须**按固定文本长度降序**匹配，否则短模板会把长消息的尾巴吞进 `{*}`
- 快捷键索引只对含 `Shortcut` 的键建立 —— 字典里有大量 `[Error] ...` 日志行，方括号是日志级别不是按键
- `TMPro.TMP_Text` 没有声明 `Awake`，`TextMeshProUGUI` 声明了但**不调用 `base.Awake()`**，所以只能补具体类
- TMP 有 `SetText(string)`、`SetText(string, bool)`、`SetText(string, float...)` 一整套入口，漏一个就有一整块界面翻不了

### 目录结构

```
src/            插件源码（C# / BepInEx）
dict/           词库批次 + 禁止翻译清单
tools/          校验与合并工具
dist/           打包脚本、安装器
test/           离线测试器（--bench / --diag / probe）
manifest.json   文件哈希 + 版本，插件靠它自更新
```

### 构建

需要 .NET SDK 和一份 SimplePlanes 2 安装（csproj 里 `GameDir` 指向它）。

```bash
dotnet build -c Release            # 产出 bin/Release/SP2Chinese.dll
python tools/verify_dict.py        # 词库自检
python dist/build_dist.py          # 打包 + 生成 manifest.json
```

---

## 许可

- **插件代码**（`src/`、`tools/`、`dist/`）：MIT
- **词库**（`dict/`、`dictionary_zh_cn.tsv`）：CC BY-NC-SA 4.0

详见 [LICENSE](LICENSE)。
