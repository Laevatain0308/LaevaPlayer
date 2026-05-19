# AnimeOnDemand 番剧点播器模块设计文档

> 日期：2026-05-19  
> 状态：已实现

## 动机

为 LaevaPlayer 新增番剧点播功能，用户可在 VRChat 世界中浏览最近更新番剧、搜索、查看详情、选择剧集播放。利用外部服务端 LaevaAnim4VRC 的番剧数据 API，通过预构建索引 URL + VRCUrlInputField 动态搜索方案，完全避免 RTSP 信号机制。

## 设计目标

1. 番剧浏览：最近更新列表、番剧详情（简介 + 多渠道选集）
2. 番剧搜索：任意关键词搜索（VRCUrlInputField 运行时动态拼接）
3. 视频播放：点击剧集直接播放（302 重定向）
4. 模块化：复用现有 Module 系统，对 Controller 零侵入（无需 TrackResolveHook）
5. 状态保持：面板关闭后重新打开，恢复上次浏览位置

## 核心方案

**预构建索引 URL + 服务端 playerId 会话 + VRCUrlInputField 动态搜索**

- 大部分操作（更新、详情、封面、播放、返回）使用 Build 时预构建的固定 VRCUrl，通过 URL 中的索引参数区分不同番剧/剧集
- 搜索使用 VRCUrlInputField（VRChat 唯一能运行时创建 VRCUrl 的途径），用户在预设基 URL 后补全关键词
- 服务端按 `playerId + clientIP` 维护会话，索引到具体番剧的映射在服务端完成

## 模块结构

```
Modules/AnimeOnDemand/
  AnimeOnDemand.cs                    # 核心逻辑（网络请求、数据解析、状态管理）
  AnimeOnDemand.UI.cs                 # UI 交互（按钮事件、列表刷新）
  Editor/
    AnimeOnDemandBuildProcess.cs      # Build 时 VRCUrl 预生成
  AnimeOnDemand.asmdef                # 程序集定义
  Editor/
    AnimeOnDemand.Editor.asmdef
  AnimeOnDemand.prefab                # 模块预制体（含 YamaPlayerModuleDefinition + UI）
```

## 继承与依赖

```
UdonSharpBehaviour
  └── YamaPlayerBehaviour
       └── YamaPlayerListener              # 接收 Controller 事件
            └── YamaPlayerModule           # 模块基类
                 └── AnimeOnDemand         # 番剧点播器模块
```

**依赖注入（Build 时自动设置）：**

| 字段 | 来源 | 说明 |
|------|------|------|
| `_controller` | YamaPlayerModuleBuildProcess | 同时从中读取 `_playerId` 用于 URL 生成 |
| `_updateUrl` | AnimeOnDemandBuildProcess | 最近更新（1 个 VRCUrl） |
| `_detailUrls[100]` | AnimeOnDemandBuildProcess | 番剧详情（100 个 VRCUrl） |
| `_coverUrls[100]` | AnimeOnDemandBuildProcess | 封面图片（100 个 VRCUrl） |
| `_playUrls[900]` | AnimeOnDemandBuildProcess | 剧集播放（3 渠道 × 300 集，索引 [ch*300+ep]） |
| `_backUrl` | AnimeOnDemandBuildProcess | 返回列表（1 个 VRCUrl） |
| `_searchInputField` | AnimeOnDemandBuildProcess | 搜索栏默认 URL（通过 SetUrl 设置） |
| `_stringDownloader` | 静态 API | VRCStringDownloader.LoadUrl() 静态调用，无需字段 |
| `_coverDownloaders[]` | Prefab 中预设 | VRCImageDownloader 组件引用 |

## Controller 层修改

在 `Controller.cs` 中新增 `_playerId` 字段，供所有模块共享：

```csharp
[SerializeField, Header("播放器序号（每个播放器实例手动指定唯一值，0-255）")]
[Range(0, 255)]
private int _playerId = 0;
public int PlayerId => _playerId;
```

- 世界创建者为场景中每个 YamaPlayer 实例手动分配不同的 `_playerId`
- 现有 `VideoResolver._playerId` 改为从 `_controller.PlayerId` 读取（向后兼容：若无 Controller 则回退使用自身字段）

## 预构建 VRCUrl（分离数组）

**AnimeOnDemandBuildProcess**（callbackOrder -2000，与 VideoResolverBuildProcess 同级）：

```csharp
void ProcessModule(AnimeOnDemand module)
{
    // 1. 从 Controller 读取 playerId（世界创建者手动指定）
    var controller = module.GetComponentInParent<Controller>();
    var playerId = (int)controller.GetProgramVariable("_playerId");

    // 2. 分离数组 — 各自命名，无索引计算
    var baseUrl = $"https://{HOST}/anim/vrc";

    // 更新（1 个）
    module.SetProgramVariable("_updateUrl",
        new VRCUrl($"{baseUrl}/update?pid={playerId}"));

    // 详情（100 个）
    var detailUrls = new VRCUrl[100];
    for (int i = 0; i < 100; i++)
        detailUrls[i] = new VRCUrl($"{baseUrl}/detail?pid={playerId}&idx={i}");
    module.SetProgramVariable("_detailUrls", detailUrls);

    // 封面（100 个）
    var coverUrls = new VRCUrl[100];
    for (int i = 0; i < 100; i++)
        coverUrls[i] = new VRCUrl($"{baseUrl}/cover?pid={playerId}&idx={i}");
    module.SetProgramVariable("_coverUrls", coverUrls);

    // 播放（900 个：3 渠道 × 300 集，展平为 ch*300+ep）
    var playUrls = new VRCUrl[900];
    for (int ch = 0; ch < 3; ch++)
        for (int ep = 0; ep < 300; ep++)
            playUrls[ch * 300 + ep] =
                new VRCUrl($"{baseUrl}/play?pid={playerId}&ch={ch}&ep={ep}");
    module.SetProgramVariable("_playUrls", playUrls);

    // 返回（1 个）
    module.SetProgramVariable("_backUrl",
        new VRCUrl($"{baseUrl}/back?pid={playerId}"));

    // 3. 设置搜索 VRCUrlInputField 默认值
    var searchBaseUrl = new VRCUrl($"{baseUrl}/search?pid={playerId}&q=");
    var searchField = module.GetComponentInChildren<VRCUrlInputField>();
    searchField.SetUrl(searchBaseUrl);
}
```

`HOST` 常量在 BuildProcess 中定义（与 VideoResolverBuildProcess 共享相同服务端地址）。

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

## AnimeOnDemand.cs 核心逻辑

```csharp
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class AnimeOnDemand : YamaPlayerModule
{
    // ── Build 时注入（分离命名数组）──
    [Header("由构建过程自动生成")]
    [SerializeField] private VRCUrl _updateUrl;
    [SerializeField] private VRCUrl[] _detailUrls;    // 100
    [SerializeField] private VRCUrl[] _coverUrls;     // 100
    [SerializeField] private VRCUrl[] _playUrls;      // 900, 索引 [ch*300+ep]
    [SerializeField] private VRCUrl _backUrl;
    [SerializeField] private VRCUrlInputField _searchInputField;
    [SerializeField] private VRCStringDownloader _stringDownloader;
    [SerializeField] private VRCImageDownloader[] _coverDownloaders;

    // ── 运行时状态 ──
    private DataList _currentList;       // 当前番剧列表
    private DataDictionary _currentDetail; // 当前查看的详情
    private DataDictionary _detailCache; // 已加载详情的缓存
    private int _currentDetailIndex;     // 当前详情在列表中的索引
    private string _pendingAction;       // 当前等待的网络操作类型

    // ── 核心方法 ──
    void Start() { FetchUpdateList(); }
    void FetchUpdateList();             // _stringDownloader.LoadUrl(_updateUrl)
    void FetchDetail(int index);        // _stringDownloader.LoadUrl(_detailUrls[index])
    void FetchCover(int index, int slot);// _coverDownloaders[slot].DownloadImage(_coverUrls[index])
    void Play(int chIndex, int epIndex);// _controller.PlayTrack(…, _playUrls[ch*300+ep])
    void OnSearchSubmit();              // VRCUrlInputField.onEndEdit → GetUrl() → VRCStringDownloader
    void BackToUpdate();                // _stringDownloader.LoadUrl(_backUrl)

    // ── 网络回调 ──
    override void OnStringLoadSuccess(IVRCStringDownload result);
    override void OnStringLoadError(IVRCStringDownload result);
    override void OnImageLoadSuccess(IVRCImageDownload result);
    override void OnImageLoadError(IVRCImageDownload result);
}
```

## 搜索流程

```
1. VRCUrlInputField 预设文本: "https://server/anim/vrc/search?pid=1&q="
2. 用户在末尾输入 "eva" → 完整文本: "...&q=eva"
3. 用户按确认键 → VRCUrlInputField.onEndEdit 触发 OnSearchSubmit()
4. OnSearchSubmit() 调用 _searchInputField.GetUrl() 获取动态 VRCUrl
5. 调用 VRCStringDownloader.LoadUrl(url, this)
6. OnStringLoadSuccess() 解析 JSON → 更新 _currentList
7. 刷新 UI 显示搜索结果（含返回按钮 → BackToUpdate()）
```

## 与 Controller 的交互

与 VideoResolver 不同，AnimeOnDemand **不使用 TrackResolveHook**：

- **播放视频**：使用 `_controller.PlayTrack(TrackUtils.NewTrack(playerType, title, playUrl))` 走标准播放流程，VRChat 视频播放器通过 302 重定向自动获取视频直链播放
- **数据获取**：使用自身 `VRCStringDownloader` / `VRCImageDownloader`，完全独立于 Controller 的 track 系统
- **监听事件**：复用 YamaPlayerListener 机制接收必要的事件通知

## UI 设计

通过 `ModuleUISlot` 注入到 UIController，UI 脚本通过 `GetComponentInParent<UIController>()` 获取 UIController 引用。

**UI 组件：**
- 面板根节点（默认隐藏，按钮点击后显示）；面板背景与播放器设置、版本信息等界面一致
- 封面网格（IndexTrigger 按钮 + RawImage，通过 VRCImageDownloader 加载）
- 搜索栏（VRCUrlInputField + 返回按钮）
- 详情视图（封面、标题、简介文本、渠道 Tab 按钮、剧集列表按钮）
- 导航结构（面包屑或标题栏）与播放器现有子面板风格保持一致
- 加载中/错误提示文本

**面板状态保持：**
- 面板显示/隐藏通过 `GameObject.SetActive(true/false)` 控制，**不销毁不重建**
- 所有运行时状态（`_currentList`、`_currentDetail`、封面图片、搜索文本等）均为 `AnimeOnDemand` 的成员字段，随 UdonBehaviour 生命周期自然保持
- 玩家打开详情页后关闭面板 → 再次打开 → 依然在详情页，可直接继续选集
- 服务端会话 TTL 为 30 分钟，覆盖正常使用间隔

## 关键约束与应对

| 约束 | 应对 |
|------|------|
| VRCUrl 运行时不可创建 | Build 时预生成 1,102 个 URL + 1 个搜索基 URL，分离命名数组 |
| 列表长度动态变化 | 预构建 100 个索引上限，超出部分不可访问（实际更新列表通常 < 100） |
| 渠道/集数动态变化 | 预构建 3 渠道 × 300 集限制，UI 仅显示实际存在的剧集 |
| 封面重定向兼容性未知 | 服务端代理下载后直接返回图片 |
| VRCStringDownloader 限流 | 串行化请求，前一次完成后再发起下一次 |
| 多玩家操作同一播放器 | 服务端按 playerId+clientIP 隔离会话；各玩家 UI 本地独立、互不干扰；仅视频播放通过 Controller 同步 |

## 相关文档

- 通信协议：[E2EAPI.md](../E2EAPI.md) — VRC 番剧点播 API 章节
- 服务端修改：[LaevaAnim4VRC docs/2026-05-19-vrc-anime-on-demand.md](../../LaevaAnim4VRC/docs/2026-05-19-vrc-anime-on-demand.md)

> 最后更新：2026-05-19
