# 贡献指南

## 词库的黄金法则

**key 必须和游戏里实际渲染出来的字符串完全一致** —— 多一个空格、少一个换行、中英文标点不同，都匹配不上，而且**不会报错**，只会静默地不生效。

这一条踩过太多次，所以放在最前面。

---

## 补词条的流程

### 1. 新建批次文件

不要改别人的批次，每次新增一个文件：

```
dict/new<编号>_<主题>_zh_cn.tsv
```

格式是 **TSV**，一行一条，`英文原文<TAB>中文译文`：

```
Delete Selected Part	删除选中部件
```

### 2. 三种特殊情况

**数字会变的文本** —— 用 `{0}`（C# 格式占位符）：

```
Fuel: {0}%	燃油：{0}%
```

**拼接出来的消息** —— 用 `{*}` 标出变量位置，译文里也用 `{*}`：

```
Part damaged: {*}	部件受损：{*}
Mirroring enabled for part {*} and {*} other parts.	已为部件 {*} 及另外 {*} 个部件开启镜像。
```

**快捷键提示** —— 原文写方括号里的绑定名，译文保留它：

```
Saves the current craft.	保存当前载具。
```

> 注意：游戏渲染时 `[SaveAircraft]` 会被替换成实际按键（如 `Ctrl + S`），
> 引擎用「前后缀匹配」处理这种情况。所以字典里**必须**保留 `[绑定名]` 原文。

### 3. 校验

```bash
python tools/verify_dict.py
```

这个脚本检查「词库批次合并后能否完整重建出运行时字典」。**必须显示 `RESULT: in sync.`**，
否则说明你的批次和运行时字典不一致，PR 会被打回。

### 4. 不要翻的东西

`dict/do-not-translate.txt` 里的条目一律不许翻，包括：

- 官方载具名（社区靠这些名字对号入座）
- 挑战 / 赛道名
- 品牌
- XML 编辑器的属性名与枚举值（玩家会照着界面抄进 XML）

CI 会检查这一点。

---

## 代码贡献

- 改 `src/` 后请确保 `dotnet build -c Release` 零警告
- 涉及匹配逻辑的改动，请用 `test/` 里的离线测试器验证：
  ```bash
  dotnet run --project test -- <字典路径> --bench     # 性能
  dotnet run --project test -- <字典路径> --diag      # 内部索引状态
  dotnet run --project test -- <字典路径> <probe文件>  # 逐行命中测试
  ```
- **不要**把 `bin/`、`obj/`、`_stage/`、`_cache/` 提交进来

---

## 提交 PR 前自查

- [ ] `python tools/verify_dict.py` 输出 `in sync`
- [ ] 没有翻 `do-not-translate.txt` 里的词
- [ ] 新增 batch 文件命名符合 `new<编号>_<主题>_zh_cn.tsv`
- [ ] TSV 是 UTF-8 无 BOM、LF 换行、行尾没有多余空格（除非原文有）

---

## 报告问题

加 QQ 群 **1128992783**，或者开 Issue，附上：

1. `BepInEx\plugins\SP2Chinese\loaded-build.txt` 的内容
2. 出问题的界面截图
3. 如果是「没翻译」，附 `untranslated.txt` 里对应的那几行
