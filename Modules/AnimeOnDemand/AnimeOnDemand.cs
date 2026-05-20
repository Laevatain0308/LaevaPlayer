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
  [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
  public class AnimeOnDemand : YamaPlayerModule
  {
    private const int MAX_ITEMS = 100;
    private const int MAX_CHANNELS = 3;
    private const int MAX_EPISODES = 300;

    private const int STATE_INIT = 0;
    private const int STATE_LOADING_UPDATE = 1;
    private const int STATE_LOADING_SEARCH = 2;
    private const int STATE_LOADING_DETAIL = 3;
    private const int STATE_UPDATE_LIST = 4;
    private const int STATE_SEARCH_RESULTS = 5;
    private const int STATE_DETAIL = 6;
    private const int STATE_ERROR = 7;

    private const int CONTEXT_UPDATE = 0;
    private const int CONTEXT_SEARCH = 1;

    [Header("由构建过程自动生成")]
    [SerializeField] private VRCUrl _updateUrl;
    [SerializeField] private VRCUrl[] _detailUrls;
    [SerializeField] private VRCUrl[] _coverUrls;
    [SerializeField] private VRCUrl[] _playUrls;
    [SerializeField] private VRCUrl _backUrl;
    [SerializeField] private VRCUrl _searchBaseUrlField;

    [Header("封面下载器池数量")]
    [SerializeField] [Range(1, 12)] private int _coverDownloaderCount = 8;
    private VRCImageDownloader[] _coverDownloaders;

    // ── Runtime state ──
    private int _state = STATE_INIT;
    private int _context = CONTEXT_UPDATE;
    private DataList _currentList;
    private DataDictionary _currentDetail;
    private int _currentDetailIndex = -1;
    private Texture[] _coverTextures;

    // ── Cover download scheduling ──
    private int[] _slotToListIndex;
    private int[] _pendingDownloadQueue;
    private int _pendingDownloadHead;
    private int _pendingDownloadTail;
    private int _activeDownloadCount;

    private YamaPlayerListener[] _listeners = new YamaPlayerListener[0];

    [HideInInspector] public int _coverClickIndex;
    [HideInInspector] public int _episodeChannelIndex;
    [HideInInspector] public int _episodeClickIndex;

    // ═══════════════════════════════════════════════
    //  Public access
    // ═══════════════════════════════════════════════

    public int GetState() { return _state; }
    public int GetContext() { return _context; }
    public DataList GetCurrentList() { return _currentList; }
    public DataDictionary GetCurrentDetail() { return _currentDetail; }
    public int GetCurrentDetailIndex() { return _currentDetailIndex; }
    public VRCUrl GetSearchBaseUrl() { return _searchBaseUrlField; }

    public Texture GetCoverTexture(int index)
    {
      if (!Utilities.IsValid(_coverTextures)) return null;
      if (index < 0 || index >= _coverTextures.Length) return null;
      return _coverTextures[index];
    }

    // ═══════════════════════════════════════════════
    //  Listener management
    // ═══════════════════════════════════════════════

    public void AddListener(YamaPlayerListener listener)
    {
      if (!Utilities.IsValid(listener)) return;
      if (System.Array.IndexOf(_listeners, listener) >= 0) return;
      _listeners = _listeners.Add(listener);
    }

    private void BroadcastEvent(string eventName)
    {
      int len = _listeners.Length;
      for (int i = 0; i < len; i++)
      {
        var l = _listeners[i];
        if (Utilities.IsValid(l)) l.SendCustomEvent(eventName);
      }
    }

    // ═══════════════════════════════════════════════
    //  Lifecycle
    // ═══════════════════════════════════════════════

    public override void Start()
    {
      base.Start();

      _currentList = new DataList();
      _currentDetail = new DataDictionary();
      _coverTextures = new Texture[0];

      int poolSize = _coverDownloaderCount;
      _coverDownloaders = new VRCImageDownloader[poolSize];
      for (int i = 0; i < poolSize; i++)
        _coverDownloaders[i] = new VRCImageDownloader();
      _slotToListIndex = new int[poolSize];
      for (int i = 0; i < poolSize; i++) _slotToListIndex[i] = -1;
      _pendingDownloadQueue = new int[MAX_ITEMS];
      _pendingDownloadHead = 0;
      _pendingDownloadTail = 0;
      _activeDownloadCount = 0;

      Debug.Log("[AnimeOnDemand] Start, poolSize=" + poolSize);
      FetchUpdateList();
    }

    // ═══════════════════════════════════════════════
    //  Request helper — schedules timeout watchdog before network call
    // ═══════════════════════════════════════════════

    private void BeginRequest(int loadingState, VRCUrl url)
    {
      _state = loadingState;
      Debug.Log("[AnimeOnDemand] BeginRequest state=" + loadingState + " url=" + url);
      BroadcastEvent("AfterAnimeStateChanged");
      SendCustomEventDelayedSeconds(nameof(OnRequestTimeout), 5f);
      VRCStringDownloader.LoadUrl(url, (IUdonEventReceiver)this);
    }

    public void OnRequestTimeout()
    {
      if (_state != STATE_LOADING_UPDATE && _state != STATE_LOADING_SEARCH
          && _state != STATE_LOADING_DETAIL) return;
      Debug.Log("[AnimeOnDemand] OnRequestTimeout, recovering from state=" + _state);
      _state = (_context == CONTEXT_SEARCH) ? STATE_SEARCH_RESULTS : STATE_UPDATE_LIST;
      BroadcastEvent("AfterAnimeStateChanged");
    }

    // ═══════════════════════════════════════════════
    //  Core actions (called by UI layer)
    // ═══════════════════════════════════════════════

    public void FetchUpdateList()
    {
      if (!Utilities.IsValid(_updateUrl)) return;
      _context = CONTEXT_UPDATE;
      BeginRequest(STATE_LOADING_UPDATE, _updateUrl);
    }

    public void FetchDetail(int index)
    {
      if (!Utilities.IsValid(_detailUrls)) return;
      if (index < 0 || index >= _detailUrls.Length) return;
      _currentDetailIndex = index;
      BeginRequest(STATE_LOADING_DETAIL, _detailUrls[index]);
    }

    public void OnSearchSubmit(VRCUrl url)
    {
      if (!url.IsValidUrl()) return;
      _context = CONTEXT_SEARCH;
      Debug.Log("[AnimeOnDemand] OnSearchSubmit url=" + url);
      BeginRequest(STATE_LOADING_SEARCH, url);
    }

    public void BackToUpdate()
    {
      if (!Utilities.IsValid(_backUrl)) return;
      BeginRequest(STATE_LOADING_UPDATE, _backUrl);
    }

    public void Play(int chIndex, int epIndex)
    {
      if (!Utilities.IsValid(_controller) || !Utilities.IsValid(_playUrls)) return;
      if (chIndex < 0 || chIndex >= MAX_CHANNELS) return;
      if (epIndex < 0 || epIndex >= MAX_EPISODES) return;

      int urlIndex = chIndex * MAX_EPISODES + epIndex;
      if (urlIndex >= _playUrls.Length) return;

      var track = TrackUtils.NewTrack(VideoPlayerType.AVProVideoPlayer, string.Empty, _playUrls[urlIndex]);
      _controller.PlayTrack(track);
    }

    public void ClearSearch()
    {
      int prevState = _state;
      if (prevState == STATE_SEARCH_RESULTS)
        BackToUpdate();
      else if (prevState == STATE_DETAIL)
        _context = CONTEXT_UPDATE;
    }

    // ═══════════════════════════════════════════════
    //  IndexTrigger callbacks
    // ═══════════════════════════════════════════════

    public void OnCoverClick()
    {
      FetchDetail(_coverClickIndex);
    }

    public void OnEpisodeClick()
    {
      Play(_episodeChannelIndex, _episodeClickIndex);
    }

    // ═══════════════════════════════════════════════
    //  Network callbacks
    // ═══════════════════════════════════════════════

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
      if (Utilities.IsValid(_backUrl) && result.Url.Get() == _backUrl.Get())
      {
        _context = CONTEXT_UPDATE;
        FetchUpdateList();
        return;
      }

      if (!VRCJson.TryDeserializeFromJson(result.Result, out DataToken json)) return;
      if (json.TokenType != TokenType.DataDictionary) return;

      var dict = json.DataDictionary;

      switch (_state)
      {
        case STATE_LOADING_UPDATE:
          HandleUpdateResponse(dict);
          break;
        case STATE_LOADING_DETAIL:
          HandleDetailResponse(dict);
          break;
        case STATE_LOADING_SEARCH:
          HandleSearchResponse(dict);
          break;
      }
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
      Debug.Log("[AnimeOnDemand] OnStringLoadError error=" + result.Error + " url=" + result.Url);
      _state = STATE_ERROR;
      BroadcastEvent("AfterAnimeStateChanged");
      PrintError($"String load error: {result.Error} url={result.Url}");
    }

    public override void OnImageLoadSuccess(IVRCImageDownload result)
    {
      string resultUrl = result.Url.Get();

      if (!Utilities.IsValid(_coverUrls)) return;
      int coverUrlLen = _coverUrls.Length;
      for (int i = 0; i < coverUrlLen && i < _coverTextures.Length; i++)
      {
        if (_coverUrls[i] != null && _coverUrls[i].Get() == resultUrl)
        {
          _coverTextures[i] = result.Result;

          int poolSize = _slotToListIndex.Length;
          for (int s = 0; s < poolSize; s++)
          {
            if (_slotToListIndex[s] == i)
            {
              _slotToListIndex[s] = -1;
              _activeDownloadCount--;
              break;
            }
          }

          DispatchNextPending();
          BroadcastEvent("AfterCoverLoaded");
          break;
        }
      }
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
      string resultUrl = result.Url.Get();

      if (Utilities.IsValid(_coverUrls))
      {
        int poolSize = _slotToListIndex.Length;
        for (int s = 0; s < poolSize; s++)
        {
          int idx = _slotToListIndex[s];
          if (idx >= 0 && idx < _coverUrls.Length && _coverUrls[idx] != null && _coverUrls[idx].Get() == resultUrl)
          {
            _slotToListIndex[s] = -1;
            _activeDownloadCount--;
            break;
          }
        }
      }

      DispatchNextPending();
      PrintError($"Image load error: {result.Error} url={result.Url}");
    }

    // ═══════════════════════════════════════════════
    //  Response handlers
    // ═══════════════════════════════════════════════

    private void HandleUpdateResponse(DataDictionary dict)
    {
      if (!dict.TryGetValue("data", out DataToken data)) return;
      if (data.TokenType != TokenType.DataList) return;

      _currentList = data.DataList;
      _state = STATE_UPDATE_LIST;
      _context = CONTEXT_UPDATE;

      int count = _currentList.Count;
      Debug.Log("[AnimeOnDemand] HandleUpdateResponse count=" + count);
      if (count > 0) StartCoverDownloads(count);

      BroadcastEvent("AfterAnimeStateChanged");
    }

    private void HandleSearchResponse(DataDictionary dict)
    {
      if (!dict.TryGetValue("data", out DataToken data)) return;
      if (data.TokenType != TokenType.DataList) return;

      _currentList = data.DataList;
      _state = STATE_SEARCH_RESULTS;
      _context = CONTEXT_SEARCH;

      int count = _currentList.Count;
      Debug.Log("[AnimeOnDemand] HandleSearchResponse count=" + count);
      if (count > 0) StartCoverDownloads(count);

      BroadcastEvent("AfterAnimeStateChanged");
    }

    private void HandleDetailResponse(DataDictionary dict)
    {
      if (!dict.TryGetValue("data", out DataToken data)) return;
      if (data.TokenType != TokenType.DataDictionary) return;

      _currentDetail = data.DataDictionary;
      _state = STATE_DETAIL;
      Debug.Log("[AnimeOnDemand] HandleDetailResponse detailIndex=" + _currentDetailIndex);

      BroadcastEvent("AfterAnimeStateChanged");
    }

    // ═══════════════════════════════════════════════
    //  Cover download orchestration
    // ═══════════════════════════════════════════════

    private void StartCoverDownloads(int count)
    {
      _coverTextures = new Texture[count];
      _activeDownloadCount = 0;
      _pendingDownloadHead = 0;
      _pendingDownloadTail = 0;

      int poolSize = _slotToListIndex.Length;
      for (int i = 0; i < poolSize; i++) _slotToListIndex[i] = -1;

      int initial = count < poolSize ? count : poolSize;
      for (int i = 0; i < initial; i++)
        DispatchCoverDownload(i);
      for (int i = initial; i < count; i++)
        EnqueuePending(i);
    }

    private void DispatchCoverDownload(int listIndex)
    {
      int poolSize = _slotToListIndex.Length;
      for (int s = 0; s < poolSize; s++)
      {
        if (_slotToListIndex[s] < 0)
        {
          _slotToListIndex[s] = listIndex;
          if (Utilities.IsValid(_coverDownloaders[s]) && Utilities.IsValid(_coverUrls) && listIndex < _coverUrls.Length)
          {
            _coverDownloaders[s].DownloadImage(_coverUrls[listIndex], null, (IUdonEventReceiver)this);
            _activeDownloadCount++;
          }
          return;
        }
      }
    }

    private void EnqueuePending(int listIndex)
    {
      _pendingDownloadQueue[_pendingDownloadTail] = listIndex;
      _pendingDownloadTail = (_pendingDownloadTail + 1) % MAX_ITEMS;
    }

    private void DispatchNextPending()
    {
      if (_pendingDownloadHead == _pendingDownloadTail) return;

      int poolSize = _slotToListIndex.Length;
      int freeSlots = poolSize - _activeDownloadCount;
      if (freeSlots <= 0) return;

      int index = _pendingDownloadQueue[_pendingDownloadHead];
      _pendingDownloadHead = (_pendingDownloadHead + 1) % MAX_ITEMS;
      DispatchCoverDownload(index);
    }
  }
}
