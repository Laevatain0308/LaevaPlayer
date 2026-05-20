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

## 2026-05-20: AnimeOnDemand 番剧点播器模块实现

### 背景

通过 UISlots 向播放器 UI 注入番剧点播器界面（与 Settings 面板同级、互斥），利用 VRCUrlInputField 与外部服务端交换信息获取番剧列表、详情及视频直链。

### 架构决策

1. **逻辑层与 UI 层分离（缓存中心化 + 单向数据流）** — 参考 SlideShower + SlideShowerUI 模式。逻辑层 `AnimeOnDemand` 维护 `_currentList` / `_currentDetail` / `_coverTextures[]` 三个缓存结构，网络请求填充缓存后通过 `SendCustomEvent` 广播；UI 层 `AnimeOnDemandUI` 纯消费缓存、管理对象池。广播模式预留后续独立点播器屏幕扩展。

2. **请求超时看门狗** — 每次 `VRCStringDownloader.LoadUrl` 前通过 `SendCustomEventDelayedSeconds` 调度 5s 超时恢复。VRChat 正常流程回调先到 → no-op；Editor 中网络调用抛 `InvalidCastException` 永不回调 → 5s 后自动复位状态到列表视图。不依赖 `Networking.LocalPlayer` 环境检测（部分 SDK 在 Editor 提供 Mock 实例导致误判）。

3. **封面下载池 + 纹理缓存复用** — 6-8 个 `VRCImageDownloader` 运行时 `new` 创建（非 MonoBehaviour，同 `ImageViewerHandler` 模式），通过 `_slotToListIndex` 槽位追踪 + 环形队列调度。详情页封面从 `_coverTextures[i]` 缓存直接读取，不重复下载。

4. **VRCUrlInputField 搜索 URL 前缀** — 不依赖构建过程直写 VRCUrlInputField（跨 Prefab 引用脆弱），改为构建过程向逻辑层注入 `_searchBaseUrlField`，UI 层 `Start()` 时通过 `SetUrl()` 运行时写入。Clear 时同样从逻辑层获取前缀恢复。

5. **对象池（Instantiate 模板模式）** — 封面项和选集按钮通过 `Instantiate(template)` 动态创建（参考 `LoopScroll.cs:92`），模板预置在 Prefab 中设为 inactive。选集按钮按渠道懒加载，首次切换渠道才实例化，后续仅为显隐。

6. **无 Overlay 设计** — 网络请求期间不阻塞 UI，通过列表 Title 文字反馈状态（"加载中..." / "暂无数据"）。界面始终可操作，VRCUrlInputField 的 `onEndEdit` 由 `_suppressSearchSubmit` 标志抑制程序化 SetUrl 触发的事件冒泡。

### 关键疑难点

1. **UdonSharp 不支持 `as` 运算符** — `this as IUdonEventReceiver` 导致 `VisitBinaryExpression` 处 NullReferenceException。解决方案：回到 `Networking.LocalPlayer` 检测 + 直接转型 `(IUdonEventReceiver)this`，配合超时看门狗兜底 Editor 异常。

2. **跨 Prefab 引用清空** — VRCUrlInputField 在 `AnimePanel.prefab` 中，逻辑层 `AnimeOnDemand` 在 `AnimeOnDemand.prefab` 中。UISlot 注入时重映射仅在克隆的 UI Prefab 内部生效，跨 Prefab 引用被清空。解决方案：VRCUrlInputField 移交 UI 层管理（同 Prefab），搜索 URL 通过构建过程注入逻辑层再运行时传递。

3. **`_context` 须在发起 action 时设置** — 最初 `_context` 仅在成功回调 `HandleSearchResponse` 中设置，Editor 中回调永不触发导致上下文始终为 UPDATE。修复：`FetchUpdateList` / `OnSearchSubmit` 入口处即设 `_context`。

4. **本地化分离** — 编辑器本地化（`Localization.Editor.json`，仅模块名/描述）与运行时本地化（`Localization.Runtime.json`，UI 文本）分离。运行时文件通过 `YamaPlayerModuleDefinition.playerTranslationFile` 引用，`LocalizationBuildProcess` 构建时合并。UI 类需双注册监听器（`_animeOnDemand.AddListener(this)` + `_uiController.AddListener(this)`）才能接收语言切换事件。

### 文件变更

| 文件 | 变更类型 |
|------|----------|
| `Modules/AnimeOnDemand/AnimeOnDemand.cs` | 重构 — 逻辑层：状态机、缓存、封面下载调度、超时看门狗 |
| `Modules/AnimeOnDemand/AnimeOnDemandUI.cs` | 新增 — UI 层：对象池、缓存渲染、按钮事件、本地化 |
| `Modules/AnimeOnDemand/Editor/AnimeOnDemandBuildProcess.cs` | 修改 — 新增 `_searchBaseUrlField` 生成，删除 VRCUrlInputField 直写 |
| `Modules/AnimeOnDemand/Localization.Runtime.json` | 新增 — 9 语言运行时 UI 文本 |
| `Modules/AnimeOnDemand/Localization.Editor.json` | 精简 — 仅保留模块名/描述 |

