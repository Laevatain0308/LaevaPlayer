using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDK3.Data;
using VRC.SDK3.Image;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace Yamadev.YamaStream.Modules.AnimeOnDemand
{
  [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
  public class AnimeOnDemand : YamaPlayerModule
  {
    [Header("由构建过程自动生成")]
    [SerializeField] private VRCUrl _updateUrl;
    [SerializeField] private VRCUrl[] _detailUrls;
    [SerializeField] private VRCUrl[] _coverUrls;
    [SerializeField] private VRCUrl[] _playUrls;
    [SerializeField] private VRCUrl _backUrl;

    [Header("搜索栏")]
    [SerializeField] private VRCUrlInputField _searchInputField;

    [Header("封面下载器")]
    [SerializeField] private VRCImageDownloader[] _coverDownloaders;

    [Header("UI 面板")]
    [SerializeField] private GameObject _panelRoot;
    [SerializeField] private GameObject _updateListView;
    [SerializeField] private GameObject _detailView;
    [SerializeField] private GameObject _loadingIndicator;
    [SerializeField] private GameObject _errorText;
    [SerializeField] private GameObject _searchResultLabel;
    [SerializeField] private UnityEngine.UI.Text _statusText;
    [SerializeField] private UnityEngine.UI.Text _detailTitleText;
    [SerializeField] private UnityEngine.UI.Text _detailDescText;
    [SerializeField] private UnityEngine.UI.RawImage _detailCoverImage;
    [SerializeField] private Transform _coverGrid;
    [SerializeField] private Transform _episodeList;

    // ── 运行时状态 ──
    private DataList _currentList;
    private DataDictionary _currentDetail;
    private int _currentDetailIndex;
    private string _pendingAction;

    // ── 按钮索引缓存（由 IndexTrigger 设置后调用方法）──
    [HideInInspector] public int _coverClickIndex;
    [HideInInspector] public int _episodeChannelIndex;
    [HideInInspector] public int _episodeClickIndex;

    private const int MAX_CHANNELS = 3;
    private const int MAX_EPISODES = 300;
    private const int MAX_ITEMS = 100;

    public override void Start()
    {
      base.Start();
      if (Utilities.IsValid(_panelRoot)) _panelRoot.SetActive(false);

      _pendingAction = string.Empty;
      _currentList = new DataList();
      _currentDetail = new DataDictionary();

      FetchUpdateList();
    }

    // ═══════════════════════════════════════════════
    //  核心方法
    // ═══════════════════════════════════════════════

    public void FetchUpdateList()
    {
      if (!Utilities.IsValid(_updateUrl)) return;
      _pendingAction = "update";
      ShowLoading(true);
      VRCStringDownloader.LoadUrl(_updateUrl, (IUdonEventReceiver)this);
    }

    public void FetchDetail(int index)
    {
      if (!Utilities.IsValid(_detailUrls)) return;
      if (index < 0 || index >= _detailUrls.Length) return;

      _currentDetailIndex = index;
      _pendingAction = "detail";
      ShowLoading(true);
      VRCStringDownloader.LoadUrl(_detailUrls[index], (IUdonEventReceiver)this);
    }

    public void FetchCover(int index, int slot)
    {
      if (!Utilities.IsValid(_coverDownloaders) || !Utilities.IsValid(_coverUrls)) return;
      if (index < 0 || index >= _coverUrls.Length) return;
      if (slot < 0 || slot >= _coverDownloaders.Length) return;
      if (!Utilities.IsValid(_coverDownloaders[slot])) return;

      _coverDownloaders[slot].DownloadImage(_coverUrls[index], null, (IUdonEventReceiver)this);
    }

    public void Play(int chIndex, int epIndex)
    {
      if (!Utilities.IsValid(_controller) || !Utilities.IsValid(_playUrls)) return;
      if (chIndex < 0 || chIndex >= MAX_CHANNELS) return;
      if (epIndex < 0 || epIndex >= MAX_EPISODES) return;

      var urlIndex = chIndex * MAX_EPISODES + epIndex;
      if (urlIndex >= _playUrls.Length) return;

      var track = TrackUtils.NewTrack(VideoPlayerType.AVProVideoPlayer, string.Empty, _playUrls[urlIndex]);
      _controller.PlayTrack(track);
      PrintLog($"Play: ch={chIndex} ep={epIndex}");
    }

    public void OnSearchSubmit()
    {
      if (!Utilities.IsValid(_searchInputField)) return;

      var url = _searchInputField.GetUrl();
      if (!url.IsValidUrl()) return;

      _pendingAction = "search";
      ShowLoading(true);
      VRCStringDownloader.LoadUrl(url, (IUdonEventReceiver)this);
    }

    public void BackToUpdate()
    {
      if (!Utilities.IsValid(_backUrl)) return;

      VRCStringDownloader.LoadUrl(_backUrl, (IUdonEventReceiver)this);
      // 服务端重置上下文后自动拉取更新列表
      SendCustomEventDelayedSeconds(nameof(FetchUpdateList), 0.5f);
    }

    // ═══════════════════════════════════════════════
    //  IndexTrigger 回调
    // ═══════════════════════════════════════════════

    public void OnCoverClick()
    {
      FetchDetail(_coverClickIndex);
    }

    public void OnEpisodeClick()
    {
      Play(_episodeChannelIndex, _episodeClickIndex);
    }

    public void OnPlayButtonClick()
    {
      if (_currentDetailIndex >= 0)
      {
        FetchDetail(_currentDetailIndex);
      }
    }

    // ═══════════════════════════════════════════════
    //  网络回调
    // ═══════════════════════════════════════════════

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
      ShowLoading(false);

      if (!VRCJson.TryDeserializeFromJson(result.Result, out DataToken json)) return;
      if (json.TokenType != TokenType.DataDictionary) return;

      var dict = json.DataDictionary;

      switch (_pendingAction)
      {
        case "update":
          HandleUpdateResponse(dict);
          break;
        case "detail":
          HandleDetailResponse(dict);
          break;
        case "search":
          HandleSearchResponse(dict);
          break;
        default:
          break;
      }

      _pendingAction = string.Empty;
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
      ShowLoading(false);
      _pendingAction = string.Empty;
      ShowError(true);
      PrintError($"String load error: {result.Error} url={result.Url}");
    }

    public override void OnImageLoadSuccess(IVRCImageDownload result)
    {
      ShowError(false);
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
      PrintError($"Image load error: {result.Error} url={result.Url}");
    }

    // ═══════════════════════════════════════════════
    //  响应处理
    // ═══════════════════════════════════════════════

    private void HandleUpdateResponse(DataDictionary dict)
    {
      if (!dict.TryGetValue("data", out DataToken data)) return;
      if (data.TokenType != TokenType.DataList) return;

      _currentList = data.DataList;
      ShowError(false);

      if (Utilities.IsValid(_updateListView)) _updateListView.SetActive(true);
      if (Utilities.IsValid(_detailView)) _detailView.SetActive(false);
      if (Utilities.IsValid(_searchResultLabel)) _searchResultLabel.SetActive(false);

      int count = _currentList.Count;
      if (Utilities.IsValid(_statusText))
        _statusText.text = $"最近更新 ({count} 部)";

      // 为列表中每个条目加载封面（最多与封面下载器数量取小值）
      int coverSlotCount = Utilities.IsValid(_coverDownloaders) ? _coverDownloaders.Length : 0;
      int loadCount = count < coverSlotCount ? count : coverSlotCount;
      for (int i = 0; i < loadCount; i++)
      {
        FetchCover(i, i);
      }
    }

    private void HandleDetailResponse(DataDictionary dict)
    {
      if (!dict.TryGetValue("data", out DataToken data)) return;
      if (data.TokenType != TokenType.DataDictionary) return;

      _currentDetail = data.DataDictionary;
      ShowError(false);

      if (Utilities.IsValid(_updateListView)) _updateListView.SetActive(false);
      if (Utilities.IsValid(_detailView)) _detailView.SetActive(true);

      // 标题
      if (_currentDetail.TryGetValue("title", out DataToken titleToken))
      {
        if (Utilities.IsValid(_detailTitleText))
          _detailTitleText.text = titleToken.String;
      }

      // 简介
      if (_currentDetail.TryGetValue("description", out DataToken descToken))
      {
        if (Utilities.IsValid(_detailDescText))
          _detailDescText.text = descToken.String;
      }

      // 封面（详情也加载封面到 RawImage）
      if (_currentDetailIndex >= 0 && Utilities.IsValid(_coverDownloaders) && _coverDownloaders.Length > 0)
      {
        // 使用最后一个 coverDownloader 加载详情封面，或重用第一个
        int slot = _coverDownloaders.Length - 1;
        FetchCover(_currentDetailIndex, slot);

        // 将下载结果纹理设置到 RawImage（需要在 OnImageLoadSuccess 中处理）
      }
    }

    private void HandleSearchResponse(DataDictionary dict)
    {
      if (!dict.TryGetValue("data", out DataToken data)) return;
      if (data.TokenType != TokenType.DataList) return;

      _currentList = data.DataList;
      ShowError(false);

      if (Utilities.IsValid(_updateListView)) _updateListView.SetActive(true);
      if (Utilities.IsValid(_detailView)) _detailView.SetActive(false);
      if (Utilities.IsValid(_searchResultLabel)) _searchResultLabel.SetActive(true);

      int count = _currentList.Count;
      if (Utilities.IsValid(_statusText))
        _statusText.text = $"搜索结果 ({count} 部)";

      int coverSlotCount = Utilities.IsValid(_coverDownloaders) ? _coverDownloaders.Length : 0;
      int loadCount = count < coverSlotCount ? count : coverSlotCount;
      for (int i = 0; i < loadCount; i++)
      {
        FetchCover(i, i);
      }
    }

    // ═══════════════════════════════════════════════
    //  UI 辅助
    // ═══════════════════════════════════════════════

    private void ShowLoading(bool show)
    {
      if (Utilities.IsValid(_loadingIndicator))
        _loadingIndicator.SetActive(show);
    }

    private void ShowError(bool show)
    {
      if (Utilities.IsValid(_errorText))
        _errorText.SetActive(show);
    }

    public void TogglePanel()
    {
      if (!Utilities.IsValid(_panelRoot)) return;
      _panelRoot.SetActive(!_panelRoot.activeSelf);
    }

    public void ShowPanel()
    {
      if (Utilities.IsValid(_panelRoot)) _panelRoot.SetActive(true);
    }

    public void HidePanel()
    {
      if (Utilities.IsValid(_panelRoot)) _panelRoot.SetActive(false);
    }
  }
}
