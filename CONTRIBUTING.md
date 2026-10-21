# 贡献指南

## 词条 key 必须与游戏渲染文本完全一致

字典按精确字符串匹配。多一个空格、少一个换行、标点不同，都会导致匹配失败，
而且不会报错，只是不生效。

不确定游戏实际渲染出什么字符串时，看 `BepInEx/plugins/SP2Chinese/untranslated.txt` ，
里面是被记录下来的未命中文本原文。

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

## 提交前校验

```bash
python tools/verify_dict.py
```

必须输出 `RESULT: in sync.` 。该检查确认批次文件合并后能完整重建运行时字典；
不一致时 PR 不会通过 CI。

## 不允许翻译的内容

`dict/do-not-translate.txt` 中的条目不得翻译，包括官方载具名、挑战与赛道名、
品牌、XML 编辑器的属性名与枚举值。CI 会检查。

## 代码改动

- `dotnet build -c Release` 须零警告
- 涉及匹配逻辑时，用离线测试器验证：

```bash
dotnet run --project test -- <字典路径> --bench      # 性能
dotnet run --project test -- <字典路径> --diag       # 内部索引状态
dotnet run --project test -- <字典路径> <probe文件>   # 逐行命中测试
```

- 不要提交 `bin/` 、 `obj/` 、 `_stage/` 、 `_cache/`

## 问题反馈

在 QQ 群 1128992783 反馈，或开 Issue，附：

1. `BepInEx\plugins\SP2Chinese\loaded-build.txt` 的内容
2. 界面截图
3. 若为「未翻译」，附 `untranslated.txt` 中对应的行
