# WebUnit 视频解析模块设计文档

> 日期：2026-05-18  
> 状态：已实现

## 动机

LaevaPlayer（YamaPlayer 2.0.0-beta.6）依赖 VRChat 内置 yt-dlp 解析视频链接，对 Bilibili、抖音等国内视频网站支持不足。需要引入旧版 YamaPlayer 的 WebUnit 外部服务端解析方案。

## 设计目标

1. 接入外部服务端 LaevaVideoResolver4VRC 解析 Bilibili 视频/直播直链
2. 最小化对现有 Controller 核心逻辑的侵入
3. 向后兼容：未安装模块时行为不变
4. 多客户端独立解析，利用服务端 majority voting 冗余机制

## 核心流程

```
用户输入 Bilibili URL
  → VideoResolver 检测平台，提取视频 ID
  → ID 拆为 UTF-8 字节序列
  → 通过 VRCAVProVideoPlayer 逐字节发送 RTSP 信号到服务端
  → 发送完成后播放平台特定 callback URL
  → 服务端返回 302 重定向至视频直链
  → VRChat 播放器跟随播放
```

## 模块结构

```
LaevaPlayer/
  Modules/VideoResolver/
    VideoResolver.cs           # 运行时模块（UdonSharpBehaviour）
    Editor/
      VideoResolverBuildProcess.cs  # 构建时 VRCUrl 预生成
```

## 关键技术决策

### Track 保持 3 元素，VRCUrl 始终不变

Track 结构 `[playerType, title, url]` 不变。VRCUrl 始终存储用户输入的原始链接。
- VideoResolver 通过 `IsResolvablePlatform(url)` 判断是否需要解析
- 需解析时：提取ID → 发信号 → `Handler.LoadUrl(callbackUrl)` 直接播放，不写 Track
- callback URL 是固定 API 入口，不随视频变化，存储无意义

### Controller 钩子模式

通过 `SetTrackResolveHook(target, eventName)` 注入解析逻辑，钩子触发时控制权转移给 VideoResolver：
- 平台 URL → VideoResolver 接管（信号发送 → 回调播放）
- 非平台 URL → VideoResolver 调用 `DirectLoadTrack()` 直接播放

### VRCUrl 预生成

VRChat Udon 不允许运行时 `new VRCUrl(string)`。构建时生成：
- 信号 URL：`(TERMINATOR_POS + 1) × 256 = 5120` 个
- 格式：`rtsp://{host}:3001/vrchat/api/signal/{pos}/{val}/{playerId}`
- 索引：`_urls[pos * 256 + byteValue]`

### 每个客户端独立解析

Owner 和 Non-Owner 均独立执行完整解析流程。服务端按 `playerId` 聚合信号，多客户端提供冗余。

## 协议

详见 [E2EAPI.md](../E2EAPI.md)

## 相关文件

- 实现记录：[DEVELOPMENT.md](../DEVELOPMENT.md)
- 待办：[TODO.md](../TODO.md)
