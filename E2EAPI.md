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

---

## VRC 番剧点播 API

> 版本：1.0.0　日期：2026-05-19

### 概述

LaevaPlayer 番剧点播器通过外部服务端 LaevaAnim4VRC 获取番剧数据与视频直链。通信使用 HTTP 协议，客户端通过预构建 VRCUrl + VRCStringDownloader（JSON 数据）/ BaseVRCVideoPlayer（视频播放）/ VRCImageDownloader（封面图片）与服务端交互。

### 服务端地址

| 端口 | 协议 | 用途 |
|------|------|------|
| 3002 | HTTP | 番剧数据 API + VRC 适配端点 |

### VRC 适配端点

所有端点位于 `/anim/vrc/` 下，专为 VRChat 客户端设计。

#### 通用约定

- **playerId**：播放器序号（`int`，范围 0-255）。世界创建者在每个 YamaPlayer 实例的 Inspector 中手动指定唯一值，确保同一场景中多个播放器实例不冲突。该值定义在 Controller 层，供 VideoResolver、AnimeOnDemand 等多模块共享。
- **索引**：所有 `idx` 参数引用当前会话上下文中列表的位置（0-based），映射关系在服务端维护。
- **JSON 响应格式**：复用现有 `{ data, updatedAt, meta }` 信封。
- **会话隔离**：服务端按 `playerId + clientIP` 组合键维护会话，实现多玩家操作同一播放器实例时的上下文隔离。

#### GET /anim/vrc/update?pid={playerId}

获取最近更新列表，服务端将列表存入该 playerId 的会话上下文。

**请求参数：**

| 参数 | 类型 | 说明 |
|------|------|------|
| `pid` | int | 播放器序号（必填，0-255） |

**响应（200）：**

```json
{
  "data": [
    {
      "aid": "20260104",
      "sourceId": "agedm",
      "sourceName": "AGE动漫",
      "title": "某动漫标题",
      "coverURL": "https://cdn.aqdstatic.com:966/age/20260104.jpg",
      "latestEpisode": "第12集"
    }
  ],
  "updatedAt": "2026-05-16T08:00:00.000Z",
  "meta": { "source": "agedm", "total": 67 }
}
```

**说明：** 服务端调用现有 `/anim/api/update` 逻辑，同时将返回的列表存入 `session.updateList`，设置 `session.context = 'update'`。

#### GET /anim/vrc/detail?pid={playerId}&idx={index}

获取当前会话上下文中指定索引番剧的详情。

**请求参数：**

| 参数 | 类型 | 说明 |
|------|------|------|
| `pid` | int | 播放器序号（必填，0-255） |
| `idx` | int | 列表索引，0-based（必填，范围 0..99） |

**响应（200）：**

```json
{
  "data": {
    "aid": "20260104",
    "sourceId": "agedm",
    "title": "某动漫标题",
    "coverURL": "https://...",
    "description": "剧情简介文本...",
    "channels": [
      {
        "name": "非凡",
        "id": "playlist-ffm3u8",
        "episodes": [
          { "name": "第01集", "episodeNumber": 1 },
          { "name": "第02集", "episodeNumber": 2 }
        ]
      }
    ]
  },
  "updatedAt": null,
  "meta": { "freshness": "fresh", "cached": true }
}
```

**说明：**
- 服务端从当前会话的列表（updateList 或 searchResults）中取 `index` 位置，获取 sourceId/aid。
- 调用现有 `/anim/api/detail?source=&aid=` 逻辑获取详情。
- 将详情存入 `session.currentDetail`。
- **剧集 URL 不返回给客户端**（客户端只用索引播放），减少 JSON 体积。

**错误响应：**
- 400：`{ "meta": { "warnings": ["索引超出范围"] }, "data": null }`

#### GET /anim/vrc/cover?pid={playerId}&idx={index}

代理获取当前会话上下文中指定索引番剧的封面图片。

**请求参数：**

| 参数 | 类型 | 说明 |
|------|------|------|
| `pid` | int | 播放器序号（必填，0-255） |
| `idx` | int | 列表索引，0-based（必填，范围 0..99） |

**响应：** 图片二进制数据（`Content-Type: image/jpeg` 或 `image/png`）

**说明：** 服务端从当前列表取 coverURL，下载后直接返回图片数据。避免 VRCImageDownloader 重定向兼容性问题。

#### GET /anim/vrc/play?pid={playerId}&ch={chIdx}&ep={epIdx}

获取视频播放地址，302 重定向到实际视频直链。

**请求参数：**

| 参数 | 类型 | 说明 |
|------|------|------|
| `pid` | int | 播放器序号（必填，0-255） |
| `ch` | int | 渠道索引，0-based（必填，范围 0..2） |
| `ep` | int | 剧集索引，0-based（必填，范围 0..299） |

**响应：**
- **成功**：`302 Found`，`Location` 指向视频直链
- **404**：剧集不存在
- **503**：解析失败
- **409**：会话不存在或已过期（安全兜底，正常流程不会触发）

**说明：** 服务端从 `session.currentDetail` 中取 `channels[ch].episodes[ep]` 的播放页 URL，调用现有 `/anim/api/play` 逻辑，返回 302 重定向（而非 JSON 中的 videoURL）。VRChat 视频播放器自动跟随重定向播放。

#### GET /anim/vrc/search?pid={playerId}&q={keyword}

搜索番剧。此 URL 由客户端的 VRCUrlInputField 在运行时动态拼接关键字后生成（Build 时预设基 URL：`/anim/vrc/search?pid={pid}&q=`，用户在 VRCUrlInputField 中输入关键词后按确认键，`onEndEdit` 事件直接触发搜索）。

**请求参数：**

| 参数 | 类型 | 说明 |
|------|------|------|
| `pid` | int | 播放器序号（必填，0-255） |
| `q` | string | 搜索关键词（必填，至少 2 字符） |

**响应（200）：**

```json
{
  "data": [
    {
      "aid": "20260104",
      "sourceId": "agedm",
      "sourceName": "AGE动漫",
      "title": "某动漫标题",
      "coverURL": "https://...",
      "latestEpisode": "第12集"
    }
  ],
  "updatedAt": null,
  "meta": { "query": "eva", "total": 5 }
}
```

**说明：** 服务端调用现有 `/anim/api/search` 逻辑，将搜索结果存入 `session.searchResults`，设置 `session.context = 'search'`。

#### GET /anim/vrc/back?pid={playerId}

将当前会话上下文重置为「最近更新」。

**请求参数：**

| 参数 | 类型 | 说明 |
|------|------|------|
| `pid` | int | 播放器序号（必填，0-255） |

**响应（200）：** `{ "ok": true }`

**说明：** 设置 `session.context = 'update'`，清除 `session.searchResults`。

---

### 服务端会话管理

**会话 Key**：`${playerId}:${clientIP}`，同时按播放器实例和玩家 IP 隔离。

```
Session 结构（内存，Map<"pid:ip", Session>）：
{
  key: "0:192.168.1.100",
  playerId: 0,
  clientIP: "192.168.1.100",
  context: 'update' | 'search',
  updateList: [{ aid, sourceId, title, coverURL, latestEpisode, ... }],
  searchResults: [{ ... }],
  currentDetail: { sourceId, aid, title, coverURL, description, channels },
  lastAccess: timestamp
}

TTL: 30 分钟
```

**设计理由：**
- VRChat 中每个玩家的 Udon 在其本地机器运行，HTTP 请求从各自 IP 发出
- 玩家 A（IP 1.2.3.4）搜索 eva → 会话 `0:1.2.3.4` 上下文切换为 search
- 玩家 B（IP 5.6.7.8）点返回 → 仅影响会话 `0:5.6.7.8`
- 各玩家上下文完全隔离，互不干扰

### 预构建 VRCUrl 清单

| 用途 | URL 模板 | 数组字段 | 数量 |
|------|---------|---------|------|
| 最近更新 | `/anim/vrc/update?pid={pid}` | `_updateUrl` | 1 |
| 番剧详情 | `/anim/vrc/detail?pid={pid}&idx={0..99}` | `_detailUrls[]` | 100 |
| 封面图片 | `/anim/vrc/cover?pid={pid}&idx={0..99}` | `_coverUrls[]` | 100 |
| 剧集播放 | `/anim/vrc/play?pid={pid}&ch={0..2}&ep={0..299}` | `_playUrls[]` | 900 |
| 返回列表 | `/anim/vrc/back?pid={pid}` | `_backUrl` | 1 |
| 搜索基 URL | `/anim/vrc/search?pid={pid}&q=` | VRCUrlInputField (SetUrl) | 1 |
| **总计** | | | **1,103** |

> 最后更新：2026-05-19
