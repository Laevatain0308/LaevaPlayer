# LaevaPlayer 开发记录

## 2026-05-18: WebUnit 视频解析模块集成

### 背景

LaevaPlayer（基于 YamaPlayer 2.0.0-beta.6）的 VRChat 内置 yt-dlp 对国内视频网站（Bilibili）支持不足。
参考旧版 YamaPlayer 1.5.16 + YamachanWebUnit 架构，实现了通过外部服务端（LaevaVideoResolver4VRC）辅助解析视频直链的模块。

### 架构决策

1. **钩子模式而非修改 Controller 核心逻辑** — 通过 `SetTrackResolveHook` 动态注入解析逻辑，未安装 VideoResolver 模块时 Controller 行为保持不变，实现解耦。
2. **Track 保持3元素数组 + IsResolvablePlatform 判断** — 统一用 `VRCUrl` 存储原始链接，VideoResolver 通过 URL 模式匹配判断是否需要解析。需解析时：提取ID → 发信号 → `Handler.LoadUrl(callbackUrl)` 直接播放，Track 不变。设计原因：callback URL 是固定 API 入口，不随视频变化，存储无意义。
3. **每个客户端独立解析** — Owner 和 Non-Owner 各自发送信号、各自请求回调。利用服务端 majority voting 机制，多客户端提供信号冗余。
4. **运行时零配置服务器地址** — 服务器 host/port 仅在构建时由 `VideoResolverBuildProcess` 使用，生成完整 VRCUrl 后注入组件。运行时所有 VRCUrl 已预构建完成。

### 关键疑难点

1. **VRCUrl 运行时不可创建** — VRChat Udon 不允许 `new VRCUrl(string)`。解决方案：构建时预生成全部 5120 个信号 URL + 回调 URL，存储在 `[SerializeField]` 数组中。
2. **UTF-8 编码** — `System.Text.Encoding.UTF8` 在 UdonSharp 中可能不可用。解决方案：手动逐字符 cast，Bilibili BV/AV ID 为纯 ASCII 字符，`(byte)char` 等价于 UTF-8 编码。
3. **UIController 不区分平台 URL** — 用户通过 UI 输入的 URL 直接存入 VRCUrl。VideoResolver 直接读取 VRCUrl.Get() 进行平台检测，无需额外分类。
4. **竞态处理** — 解析过程中用户切换视频。解决方案：新 track 到达时取消当前解析，开始新解析。

### 文件变更

| 文件                                                          | 变更类型                           |
| ------------------------------------------------------------- | ---------------------------------- |
| `Runtime/Internal/Controller.cs`                            | 修改 — 添加钩子 + DirectLoadTrack |
| `Runtime/Internal/Controller.Events.cs`                     | 修改 — 错误重试走钩子             |
| `Modules/VideoResolver/VideoResolver.cs`                    | 新增 — 核心模块                   |
| `Modules/VideoResolver/Editor/VideoResolverBuildProcess.cs` | 新增 — 构建时处理                 |
