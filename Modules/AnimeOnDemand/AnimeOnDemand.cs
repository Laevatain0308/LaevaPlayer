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
    [SerializeField] [Range(1, 12)] private int _coverDownloaderCount = 12;
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
    private int[] _slotAid;
    private bool[] _downloaderBusy;
    private int[] _pendingDownloadQueue;
    private int _pendingDownloadHead;
    private int _pendingDownloadTail;
    private int _activeDownloadCount;

    // ── Texture cache (keyed by aid) ──
    private const int MAX_CACHED_TEXTURES = 100;
    private Texture[] _cachedTextures;
    private int[] _cachedAids;
    private int _cachedTextureCount;

    // ── Search state ──
    private string _currentSearchQuery = string.Empty;
    private DataList _cachedSearchList;
    private string _cachedSearchQuery = string.Empty;

    // ── Retry state ──
    private int _retryListIndex = -1;

    private YamaPlayerListener[] _listeners = new YamaPlayerListener[0];

    [HideInInspector] public int _coverClickIndex;
    [HideInInspector] public int _episodeChannelIndex;
    [HideInInspector] public int _episodeClickIndex;
    [HideInInspector] public string _searchQueryFromUI;

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

    public string GetCurrentSearchQuery() { return _currentSearchQuery; }

    // ── Texture cache helpers ──

    private int TryGetAid(DataToken item)
    {
      if (item.TokenType != TokenType.DataDictionary) return -1;
      var dict = item.DataDictionary;
      if (!dict.TryGetValue("aid", out DataToken aidToken)) return -1;
      if (aidToken.TokenType == TokenType.Double)
        return (int)aidToken.Double;
      if (aidToken.TokenType == TokenType.String)
      {
        string s = aidToken.String;
        if (string.IsNullOrEmpty(s)) return -1;
        int result = 0;
        for (int i = 0; i < s.Length; i++)
        {
          char c = s[i];
          if (c < '0' || c > '9') return -1;
          result = result * 10 + (c - '0');
        }
        return result;
      }
      return -1;
    }

    private Texture FindCachedTexture(int aid)
    {
      if (aid <= 0 || !Utilities.IsValid(_cachedTextures)) return null;
      for (int i = 0; i < _cachedTextureCount; i++)
      {
        if (_cachedAids[i] == aid && Utilities.IsValid(_cachedTextures[i]))
          return _cachedTextures[i];
      }
      return null;
    }

    private void AddToCache(int aid, Texture tex)
    {
      if (aid <= 0 || !Utilities.IsValid(tex)) return;
      // Check for existing entry
      for (int i = 0; i < _cachedTextureCount; i++)
      {
        if (_cachedAids[i] == aid)
        {
          _cachedTextures[i] = tex;
          return;
        }
      }
      // Evict oldest if full (FIFO)
      if (_cachedTextureCount >= MAX_CACHED_TEXTURES)
      {
        for (int i = 0; i < _cachedTextureCount - 1; i++)
        {
          _cachedTextures[i] = _cachedTextures[i + 1];
          _cachedAids[i] = _cachedAids[i + 1];
        }
        _cachedTextureCount--;
      }
      _cachedTextures[_cachedTextureCount] = tex;
      _cachedAids[_cachedTextureCount] = aid;
      _cachedTextureCount++;
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
      _slotAid = new int[poolSize];
      _downloaderBusy = new bool[poolSize];
      for (int i = 0; i < poolSize; i++) { _slotToListIndex[i] = -1; _slotAid[i] = -1; }
      _pendingDownloadQueue = new int[MAX_ITEMS];
      _pendingDownloadHead = 0;
      _pendingDownloadTail = 0;
      _activeDownloadCount = 0;

      _cachedTextures = new Texture[MAX_CACHED_TEXTURES];
      _cachedAids = new int[MAX_CACHED_TEXTURES];
      _cachedTextureCount = 0;

      Debug.Log("[AnimeOnDemand] Start, poolSize=" + poolSize);
      if (Utilities.IsValid(_updateUrl) && _updateUrl.IsValidUrl())
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
      _currentSearchQuery = _searchQueryFromUI;
      Debug.Log("[AnimeOnDemand] OnSearchSubmit url=" + url + " query=" + _currentSearchQuery);

      // Check search cache
      if (!string.IsNullOrEmpty(_currentSearchQuery)
          && _currentSearchQuery == _cachedSearchQuery
          && Utilities.IsValid(_cachedSearchList))
      {
        Debug.Log("[AnimeOnDemand] Search cache hit: " + _currentSearchQuery);
        _currentList = _cachedSearchList;
        _state = STATE_SEARCH_RESULTS;
        int count = _currentList.Count;
        if (count > 0) StartCoverDownloads(count);
        BroadcastEvent("AfterAnimeStateChanged");
        return;
      }

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
      {
        _currentList = new DataList();
        _coverTextures = new Texture[0];
        _currentSearchQuery = string.Empty;
        BroadcastEvent("AfterAnimeStateChanged");
        FetchUpdateList();
      }
      else if (prevState == STATE_DETAIL)
        _context = CONTEXT_UPDATE;
    }

    // ═══════════════════════════════════════════════
    //  IndexTrigger callbacks
    // ═══════════════════════════════════════════════

    public void OnCoverClick()
    {
      Debug.Log("[AnimeOnDemand] OnCoverClick _coverClickIndex=" + _coverClickIndex);
      FetchDetail(_coverClickIndex);
    }

    public void OnEpisodeClick()
    {
      Debug.Log("[AnimeOnDemand] OnEpisodeClick ch=" + _episodeChannelIndex + " ep=" + _episodeClickIndex);
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

      // Step 1: Find the aid recorded at dispatch time (not current _currentList[idx])
      int downloadedAid = -1;
      int poolSize = _slotToListIndex.Length;
      for (int s = 0; s < poolSize; s++)
      {
        int idx = _slotToListIndex[s];
        if (idx >= 0 && idx < _coverUrls.Length && _coverUrls[idx] != null && _coverUrls[idx].Get() == resultUrl)
        {
          downloadedAid = _slotAid[s];
          break;
        }
      }
      AddToCache(downloadedAid, result.Result);

      // Step 2: Free the matching downloader slot
      for (int s = 0; s < poolSize; s++)
      {
        int idx = _slotToListIndex[s];
        if (idx >= 0 && idx < _coverUrls.Length && _coverUrls[idx] != null && _coverUrls[idx].Get() == resultUrl)
        {
          _slotToListIndex[s] = -1;
          _slotAid[s] = -1;
          _downloaderBusy[s] = false;
          _activeDownloadCount--;
          break;
        }
      }

      // Step 3: Apply cached texture to all matching items in CURRENT view
      ApplyCacheToCurrentView(downloadedAid, result.Result);

      DispatchNextPending();
      BroadcastEvent("AfterCoverLoaded");
    }

    // Pull cached texture into current view for all items matching the given aid
    private void ApplyCacheToCurrentView(int aid, Texture tex)
    {
      if (aid <= 0 || !Utilities.IsValid(tex)) return;
      if (!Utilities.IsValid(_currentList) || !Utilities.IsValid(_coverTextures)) return;
      int count = _currentList.Count;
      for (int i = 0; i < count && i < _coverTextures.Length; i++)
      {
        if (_coverTextures[i] != null) continue; // already filled
        if (TryGetAid(_currentList[i]) == aid)
          _coverTextures[i] = tex;
      }
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
      string resultUrl = result.Url.Get();
      int failedListIndex = -1;

      if (Utilities.IsValid(_coverUrls))
      {
        int poolSize = _slotToListIndex.Length;
        for (int s = 0; s < poolSize; s++)
        {
          int idx = _slotToListIndex[s];
          if (idx >= 0 && idx < _coverUrls.Length && _coverUrls[idx] != null && _coverUrls[idx].Get() == resultUrl)
          {
            failedListIndex = idx;
            _slotToListIndex[s] = -1;
            _slotAid[s] = -1;
            _downloaderBusy[s] = false;
            _activeDownloadCount--;
            break;
          }
        }
      }

      if (result.Error == VRCImageDownloadError.TooManyRequests && failedListIndex >= 0)
      {
        _retryListIndex = failedListIndex;
        SendCustomEventDelayedSeconds(nameof(_RetryDispatch), 2f);
        return;
      }

      DispatchNextPending();
      PrintError($"Image load error: {result.Error} url={result.Url}");
    }

    public void _RetryDispatch()
    {
      if (_retryListIndex < 0) return;
      int idx = _retryListIndex;
      _retryListIndex = -1;
      EnqueuePending(idx);
      DispatchNextPending();
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

      // Extract search query from meta
      if (dict.TryGetValue("meta", out DataToken metaToken) && metaToken.TokenType == TokenType.DataDictionary)
      {
        var meta = metaToken.DataDictionary;
        if (meta.TryGetValue("query", out DataToken queryToken) && queryToken.TokenType == TokenType.String)
          _currentSearchQuery = queryToken.String;
      }

      // Cache search results
      _cachedSearchList = _currentList;
      _cachedSearchQuery = _currentSearchQuery;

      int count = _currentList.Count;
      Debug.Log("[AnimeOnDemand] HandleSearchResponse count=" + count + " query=" + _currentSearchQuery);
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

      int poolSize = _slotToListIndex.Length;

      // Free non-busy slots only — keep orphaned downloads running
      for (int i = 0; i < poolSize; i++)
      {
        if (!_downloaderBusy[i])
        {
          _slotToListIndex[i] = -1;
          _slotAid[i] = -1;
        }
      }

      // Clear pending queue (old items irrelevant for new view)
      _pendingDownloadHead = 0;
      _pendingDownloadTail = 0;

      // Try cache hits; queue misses
      int pendingStart = 0;
      for (int i = 0; i < count; i++)
      {
        if (Utilities.IsValid(_currentList) && i < _currentList.Count)
        {
          int aid = TryGetAid(_currentList[i]);
          Texture cached = FindCachedTexture(aid);
          if (cached != null)
          {
            _coverTextures[i] = cached;
            continue;
          }
          // Check if this aid is already being downloaded by an active slot
          bool alreadyDownloading = false;
          for (int s = 0; s < poolSize; s++)
          {
            if (_downloaderBusy[s] && _slotAid[s] == aid)
            {
              alreadyDownloading = true;
              break;
            }
          }
          if (alreadyDownloading) continue; // skip — download in progress
        }
        // Queue for download
        EnqueuePending(i);
        if (pendingStart < poolSize) pendingStart++;
      }

      // Dispatch initial batch to free downloaders (skip busy ones)
      int dispatched = 0;
      while (dispatched < pendingStart && _pendingDownloadHead != _pendingDownloadTail)
      {
        float delay = dispatched * 0.08f;
        if (delay > 0f)
          SendCustomEventDelayedSeconds(nameof(_DispatchNextFromQueue), delay);
        else
        {
          int index = _pendingDownloadQueue[_pendingDownloadHead];
          _pendingDownloadHead = (_pendingDownloadHead + 1) % MAX_ITEMS;
          DispatchCoverDownload(index);
        }
        dispatched++;
      }

      // All covers from cache — notify UI immediately
      if (dispatched == 0 && _pendingDownloadHead == _pendingDownloadTail)
        BroadcastEvent("AfterCoverLoaded");
    }

    public void _DispatchNextFromQueue()
    {
      if (_pendingDownloadHead == _pendingDownloadTail) return;
      int index = _pendingDownloadQueue[_pendingDownloadHead];
      _pendingDownloadHead = (_pendingDownloadHead + 1) % MAX_ITEMS;
      DispatchCoverDownload(index);
    }

    private int _dispatchIndex;

    public void _DispatchCoverAtIndex()
    {
      DispatchCoverDownload(_dispatchIndex);
    }

    private void DispatchCoverDownload(int listIndex)
    {
      _dispatchIndex = listIndex;
      int poolSize = _slotToListIndex.Length;
      for (int s = 0; s < poolSize; s++)
      {
        if (!_downloaderBusy[s])
        {
          _downloaderBusy[s] = true;
          _slotToListIndex[s] = listIndex;
          _slotAid[s] = TryGetAid(_currentList[listIndex]);
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
      int poolSize = _slotToListIndex.Length;
      int freeSlots = poolSize - _activeDownloadCount;
      if (freeSlots <= 0) return;

      // Scan queue for next item that isn't already cached or downloading
      while (_pendingDownloadHead != _pendingDownloadTail)
      {
        int index = _pendingDownloadQueue[_pendingDownloadHead];
        _pendingDownloadHead = (_pendingDownloadHead + 1) % MAX_ITEMS;

        // Re-check cache (may have been filled by an orphaned download)
        if (Utilities.IsValid(_currentList) && index < _currentList.Count)
        {
          int aid = TryGetAid(_currentList[index]);
          Texture cached = FindCachedTexture(aid);
          if (cached != null)
          {
            if (index < _coverTextures.Length)
              _coverTextures[index] = cached;
            continue;
          }
        }

        DispatchCoverDownload(index);
        return;
      }
    }
  }
}
