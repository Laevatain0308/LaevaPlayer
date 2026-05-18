# LaevaPlayer 客户端-服务端通信协议

## 概述

LaevaPlayer 通过外部服务端（LaevaVideoResolver4VRC）解析国内视频网站（Bilibili）的直链。
通信分为两个阶段：**信号发送阶段**（RTSP）和**回调播放阶段**（HTTP）。

---

## 服务端地址

服务端监听两个端口：

| 端口 | 协议 | 用途 |
|---|---|---|
| 3001 | TCP（RTSP） | 接收信号 URL 请求 |
| 3000 | HTTP | API 回调入口 |

---

## 信号协议

### 信号 URL 格式

```
rtsp://{host}:3001/vrchat/api/signal/{pos}/{val}/{playerId}
```

### 参数说明

| 参数 | 类型 | 说明 |
|---|---|---|
| `pos` | int (0-20) | 字符在视频ID中的位置。`pos=20` 为 terminator |
| `val` | int (0-255) | 该位置上的 UTF-8 字节值 |
| `playerId` | int (0-255, 可选, 默认0) | 播放器唯一标识，同一世界中区分多个播放器实例 |

### Terminator 机制

视频 ID 长度最多 19 字符（`TERMINATOR_POS = 20`）。

当所有字符位置发送完毕后，发送 terminator：`pos=20, val={ID长度}`。

服务端收到 terminator 后，通过 `isComplete()` 检查所有位置的信号是否已 settled（多数投票达到 `VOTE_THRESHOLD = 2`）。

### 信号发送策略

- 每个位置重复发送 `SIGNALS_PER_POS = 3` 次，确保可靠性
- 发送间隔 `200ms`（`SIGNAL_INTERVAL = 0.2f`）
- 服务端采用 majority voting，达到 `VOTE_THRESHOLD = 2` 即 settle
- **每个客户端独立发送信号**，多客户端提供额外冗余

---

## 回调 API

### 播放回调

```
GET https://{host}:3000/vrchat/api/play?target={platform}&playerId={playerId}
```

#### 请求参数

| 参数 | 类型 | 说明 |
|---|---|---|
| `target` | string | 平台标识，当前支持 `bilibili_video`、`bilibili_live` |
| `playerId` | int (0-255) | 播放器唯一标识，与信号 URL 中的 playerId 一致 |

#### 响应

- **成功**: `302 Found`，`Location` 头指向解析出的视频直链
- **未就绪**: `425 Too Early`，表示信号收集尚未完整，客户端应稍后重试
- **无效平台**: `400 Bad Request`
- **解析失败**: `500 Internal Server Error`

### HTTP 重定向播放流程

1. VRChat 视频播放器请求回调 URL
2. 服务端查 session，若完整则解析视频URL
3. 返回 `302` 重定向至实际视频直链
4. VRChat 播放器自动跟随重定向，播放视频

---

## 支持平台

| 平台标识 | 说明 | 示例 URL |
|---|---|---|
| `bilibili_video` | Bilibili 视频 | `https://www.bilibili.com/video/BV1xx411c7mD` |
| `bilibili_live` | Bilibili 直播 | `https://live.bilibili.com/12345` |

---

## 服务端 Session 管理

- Session 按 `playerId` 聚合所有 IP 的信号
- 多数投票阈值：`VOTE_THRESHOLD = 2`
- Session TTL：`SIGNAL_TTL = 60s`
- 视频切换检测：terminator 变化立即重置，或冲突数达阈值重置
- 解析结果缓存：`RESOLVED_RESULT_TTL = 600s`

---

## 客户端预生成 VRCUrl

VRChat 不允许在运行时动态创建 VRCUrl，因此必须在构建时预生成所有可能的信号 URL：

- 总数量：`(TERMINATOR_POS + 1) × 256 = 5120` 个
- 索引公式：`_urls[pos * 256 + byteValue]`
- 回调 URL 同样在构建时预生成（每个平台一个）

构建过程由 `VideoResolverBuildProcess` 在场景构建时自动执行。
