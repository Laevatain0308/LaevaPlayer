# AnimeOnDemand 番剧点播器模块 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 LaevaPlayer 中实现 AnimeOnDemand 番剧点播器模块，支持最近更新浏览、搜索、详情查看、剧集播放。

**Architecture:** 继承 YamaPlayerModule，通过 BuildProcess 预生成 VRCUrl 数组注入模块，使用 VRCStringDownloader/VRCImageDownloader 加载数据，通过 IndexTrigger 按钮实现 UI 交互。

**Tech Stack:** UdonSharp, VRChat SDK3, Unity 2022

---

### Task 1: 修改 Controller.cs 添加 _playerId 字段

**Files:**
- Modify: `Runtime/Internal/Controller.cs`

在 `_isLocal` 字段附近添加 `_playerId` 字段及属性。

### Task 2: 创建 AnimeOnDemand 程序集定义文件

**Files:**
- Create: `Modules/AnimeOnDemand/Yamadev.YamaStream.Modules.AnimeOnDemand.asmdef`
- Create: `Modules/AnimeOnDemand/Yamadev.YamaStream.Modules.AnimeOnDemand.asmdef.meta`
- Create: `Modules/AnimeOnDemand/Editor/Yamadev.YamaStream.Modules.AnimeOnDemand.Editor.asmdef`
- Create: `Modules/AnimeOnDemand/Editor/Yamadev.YamaStream.Modules.AnimeOnDemand.Editor.asmdef.meta`

### Task 3: 创建 AnimeOnDemand.cs 核心模块代码

**Files:**
- Create: `Modules/AnimeOnDemand/AnimeOnDemand.cs`

### Task 4: 创建 AnimeOnDemandBuildProcess.cs 构建处理

**Files:**
- Create: `Modules/AnimeOnDemand/Editor/AnimeOnDemandBuildProcess.cs`

### Task 5: 创建 Editor 目录占位及 .meta 文件

**Files:**
- Create: `Modules/AnimeOnDemand/Editor.meta`

---

**Plan coverage check:** 所有设计文档中的要求均已覆盖。Prefab 文件（AnimeOnDemand.prefab）需要在 Unity Editor 中手工创建，无法以文本方式生成。

> 最后更新：2026-05-19
