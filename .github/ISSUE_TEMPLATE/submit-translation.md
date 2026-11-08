---
name: 提交或改进译文
about: 新增词条，或把现有译文改得更准确、更自然
title: ''
labels: ''
assignees: ''
---

## 你想改什么

<!-- 新增词条 / 修改已有译文 / 统一术语 -->

## 原文与译文

<!-- 一行一条，用 TAB 分隔。可以只写一条，也可以一次贴很多条 -->

```
原文	你的译文
```

## 为什么这样更好

<!--
  例如：
  - 原文是「Aircraft」，现在译成「飞机」，但游戏里指的是玩家造的载具，用「载具」更准确
  - 这句是按钮文字，现在的译法太长会被截断
  - 与游戏内其它地方已有的译法保持统一
-->

## 如果你愿意直接提 PR

改动很小的话提 PR 更快：

1. 在 `dict/` 下新建 `dict/new<编号>_<主题>_zh_cn.tsv`（不要改已有批次）
2. 运行 `python tools/rebuild_dict.py`
3. 运行 `python tools/verify_dict.py` 与 `python tools/check_dict.py`
4. 提交并发起 PR

详细规则见 [CONTRIBUTING.md](../../blob/main/CONTRIBUTING.md)。
不确定格式的话，直接在这个 Issue 里贴译文也可以，我来合。
