using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDK3.Data;
using VRC.SDKBase;
using VRC.Udon.Common.Enums;
using Yamadev.YamaStream.UI;

namespace Yamadev.YamaStream.Modules.AnimeOnDemand
{
  [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
  public class AnimeOnDemandUI : YamaPlayerListener
  {
    private const int MAX_ITEMS = 100;
    private const int MAX_CHANNELS = 3;
    private const int MAX_EPISODES = 300;

    [Header("逻辑层引用")]
    [SerializeField] private AnimeOnDemand _animeOnDemand;

    [Header("视图容器")]
    [SerializeField] private GameObject _animeListView;
    [SerializeField] private GameObject _detailView;

    [Header("搜索栏")]
    [SerializeField, RegisterEvent(nameof(VRCUrlInputField.onEndEdit), nameof(OnSearchSubmit))]
    private VRCUrlInputField _searchInputField;

    [Header("列表视图")]
    [SerializeField] private Text _listTitleText;
    [SerializeField] private Transform _coverGrid;
    [SerializeField] private GameObject _coverItemTemplate;
    [SerializeField] private Texture2D _placeholderTexture;

    [Header("详情视图")]
    [SerializeField] private RawImage _detailCoverImage;
    [SerializeField] private Text _detailTitleText;
    [SerializeField] private Text _detailDescText;
    [SerializeField] private Transform _episodeList;
    [SerializeField] private GameObject _episodeButtonTemplate;
    [SerializeField] private ToggleGroup _channelToggleGroup;
    [SerializeField, RegisterEvent(nameof(Toggle.onValueChanged), nameof(OnChannel1Changed))]
    private Toggle _channel1Toggle;
    [SerializeField, RegisterEvent(nameof(Toggle.onValueChanged), nameof(OnChannel2Changed))]
    private Toggle _channel2Toggle;
    [SerializeField, RegisterEvent(nameof(Toggle.onValueChanged), nameof(OnChannel3Changed))]
    private Toggle _channel3Toggle;

    [Header("按钮")]
    [SerializeField, RegisterEvent(nameof(Button.onClick), nameof(OnRefreshClick))]
    private Button _refreshButton;
    [SerializeField, RegisterEvent(nameof(Button.onClick), nameof(OnClearSearchClick))]
    private Button _clearSearchButton;
    [SerializeField, RegisterEvent(nameof(Button.onClick), nameof(OnBackClick))]
    private Button _backButton;

    // ── Object pools ──
    private GameObject[] _coverItemPool;
    private int _coverPoolSize;
    private GameObject[] _episodePool;
    private bool[] _channelLoaded;
    private int _currentChannel;

    private UIController _uiController;
    private VRCUrl _searchBaseUrl;
    private bool _suppressSearchSubmit;

    // ═══════════════════════════════════════════════
    //  Lifecycle
    // ═══════════════════════════════════════════════

    private void Start()
    {
      _uiController = GetComponentInParent<UIController>();

      if (!Utilities.IsValid(_animeOnDemand))
      {
        Debug.LogError("[AnimeOnDemandUI] AnimeOnDemand reference not set");
        return;
      }

      _animeOnDemand.AddListener(this);
      _uiController.AddListener(this);

      _coverItemPool = new GameObject[MAX_ITEMS];
      _coverPoolSize = 0;

      _episodePool = new GameObject[MAX_CHANNELS * MAX_EPISODES];
      _channelLoaded = new bool[MAX_CHANNELS];
      _currentChannel = 0;

      if (Utilities.IsValid(_coverItemTemplate))
        _coverItemTemplate.SetActive(false);
      if (Utilities.IsValid(_episodeButtonTemplate))
        _episodeButtonTemplate.SetActive(false);

      // 从逻辑层获取搜索 URL 前缀，SetUrl 写入 VRCUrlInputField
      if (Utilities.IsValid(_searchInputField) && Utilities.IsValid(_animeOnDemand))
      {
        var baseUrl = _animeOnDemand.GetSearchBaseUrl();
        if (baseUrl.IsValidUrl())
        {
          _searchInputField.SetUrl(baseUrl);
          _searchBaseUrl = baseUrl;
          Debug.Log("[AnimeOnDemandUI] SearchBaseUrl set: " + baseUrl.Get());
        }
        else
        {
          Debug.LogWarning("[AnimeOnDemandUI] SearchBaseUrl is invalid or empty");
        }
      }

      UpdateTranslation();
    }

    // ═══════════════════════════════════════════════
    //  Event handlers
    // ═══════════════════════════════════════════════

    public void AfterAnimeStateChanged()
    {
      Debug.Log("[AnimeOnDemandUI] AfterAnimeStateChanged");
      SendCustomEventDelayedFrames(nameof(UpdateUI), 0, EventTiming.LateUpdate);
    }

    public void AfterCoverLoaded()
    {
      Debug.Log("[AnimeOnDemandUI] AfterCoverLoaded");
      SendCustomEventDelayedFrames(nameof(UpdateCoverGridTextures), 0, EventTiming.LateUpdate);
    }

    public void AfterLanguageChanged()
    {
      UpdateTranslation();
    }

    // ═══════════════════════════════════════════════
    //  Main UI update
    // ═══════════════════════════════════════════════

    public void UpdateUI()
    {
      if (!Utilities.IsValid(_animeOnDemand)) return;

      int state = _animeOnDemand.GetState();
      Debug.Log("[AnimeOnDemandUI] UpdateUI state=" + state);
      bool isLoading = (state == 1 || state == 2 || state == 3);
      bool isListView = (state == 4 || state == 5);
      bool isDetail = (state == 6);

      if (Utilities.IsValid(_animeListView))
        _animeListView.SetActive(isListView || isLoading);
      if (Utilities.IsValid(_detailView))
        _detailView.SetActive(isDetail);

      if (isListView)
        UpdateListView();
      else if (isDetail)
        UpdateDetailView();
    }

    // ═══════════════════════════════════════════════
    //  List view
    // ═══════════════════════════════════════════════

    private void UpdateListView()
    {
      if (!Utilities.IsValid(_animeOnDemand)) return;

      DataList list = _animeOnDemand.GetCurrentList();
      int count = Utilities.IsValid(list) ? list.Count : 0;
      int state = _animeOnDemand.GetState();
      int context = _animeOnDemand.GetContext();

      if (Utilities.IsValid(_listTitleText))
      {
        if (state == 1 || state == 2) // LOADING_UPDATE or LOADING_SEARCH
        {
          _listTitleText.text = GetLocalized("module.animeOnDemand.loading");
        }
        else if (count > 0)
        {
          string prefix = (context == 0)
            ? GetLocalized("module.animeOnDemand.recentUpdate")
            : GetLocalized("module.animeOnDemand.searchResults");
          _listTitleText.text = $"{prefix} ({count})";
        }
        else
        {
          _listTitleText.text = GetLocalized("module.animeOnDemand.noData");
        }
      }

      if (Utilities.IsValid(_coverItemTemplate) && Utilities.IsValid(_coverGrid))
      {
        for (int i = count; i < _coverPoolSize; i++)
        {
          if (Utilities.IsValid(_coverItemPool[i]))
            _coverItemPool[i].SetActive(false);
        }

        for (int i = 0; i < count && i < MAX_ITEMS; i++)
        {
          if (i < _coverPoolSize && Utilities.IsValid(_coverItemPool[i]))
          {
            _coverItemPool[i].SetActive(true);
          }
          else
          {
            GameObject item = Instantiate(_coverItemTemplate);
            item.transform.SetParent(_coverGrid, false);
            item.SetActive(true);
            _coverItemPool[i] = item;
            if (i >= _coverPoolSize) _coverPoolSize = i + 1;

            var indexTrigger = item.GetComponent<IndexTrigger>();
            if (Utilities.IsValid(indexTrigger))
              indexTrigger.SetProgramVariable("_variableObject", i);
          }
        }

        UpdateCoverGridTextures();
      }
    }

    public void UpdateCoverGridTextures()
    {
      if (!Utilities.IsValid(_animeOnDemand)) return;

      DataList list = _animeOnDemand.GetCurrentList();
      int count = Utilities.IsValid(list) ? list.Count : 0;

      for (int i = 0; i < count && i < _coverPoolSize; i++)
      {
        if (!Utilities.IsValid(_coverItemPool[i])) continue;

        var rawImage = _coverItemPool[i].GetComponentInChildren<RawImage>();
        if (!Utilities.IsValid(rawImage)) continue;

        Texture tex = _animeOnDemand.GetCoverTexture(i);
        rawImage.texture = (tex != null) ? tex : _placeholderTexture;
      }

      int detailIdx = _animeOnDemand.GetCurrentDetailIndex();
      if (detailIdx >= 0 && Utilities.IsValid(_detailCoverImage))
      {
        Texture tex = _animeOnDemand.GetCoverTexture(detailIdx);
        _detailCoverImage.texture = (tex != null) ? tex : _placeholderTexture;
      }
    }

    // ═══════════════════════════════════════════════
    //  Detail view
    // ═══════════════════════════════════════════════

    private void UpdateDetailView()
    {
      if (!Utilities.IsValid(_animeOnDemand)) return;

      DataDictionary detail = _animeOnDemand.GetCurrentDetail();
      int state = _animeOnDemand.GetState();
      bool isLoading = (state == 3);

      if (Utilities.IsValid(_detailTitleText))
      {
        if (isLoading)
        {
          _detailTitleText.text = "--";
        }
        else if (Utilities.IsValid(detail) && detail.TryGetValue("title", out DataToken t) && t.TokenType == TokenType.String)
        {
          _detailTitleText.text = t.String;
        }
        else
        {
          _detailTitleText.text = "--";
        }
      }

      if (Utilities.IsValid(_detailDescText))
      {
        if (isLoading)
        {
          _detailDescText.text = "--";
        }
        else if (Utilities.IsValid(detail) && detail.TryGetValue("description", out DataToken d) && d.TokenType == TokenType.String)
        {
          _detailDescText.text = d.String;
        }
        else
        {
          _detailDescText.text = "--";
        }
      }

      int detailIdx = _animeOnDemand.GetCurrentDetailIndex();
      if (Utilities.IsValid(_detailCoverImage))
      {
        Texture tex = _animeOnDemand.GetCoverTexture(detailIdx);
        _detailCoverImage.texture = (tex != null) ? tex : _placeholderTexture;
      }

      if (!Utilities.IsValid(detail)) return;

      if (detail.TryGetValue("channels", out DataToken channelsToken) && channelsToken.TokenType == TokenType.DataList)
      {
        DataList channels = channelsToken.DataList;
        int channelCount = channels.Count;
        if (channelCount > MAX_CHANNELS) channelCount = MAX_CHANNELS;

        if (Utilities.IsValid(_channel1Toggle))
          _channel1Toggle.gameObject.SetActive(channelCount >= 1);
        if (Utilities.IsValid(_channel2Toggle))
          _channel2Toggle.gameObject.SetActive(channelCount >= 2);
        if (Utilities.IsValid(_channel3Toggle))
          _channel3Toggle.gameObject.SetActive(channelCount >= 3);

        for (int ch = 0; ch < MAX_CHANNELS; ch++)
          _channelLoaded[ch] = false;

        _currentChannel = 0;
        if (Utilities.IsValid(_channel1Toggle))
          _channel1Toggle.SetIsOnWithoutNotify(true);
        if (Utilities.IsValid(_channel2Toggle))
          _channel2Toggle.SetIsOnWithoutNotify(false);
        if (Utilities.IsValid(_channel3Toggle))
          _channel3Toggle.SetIsOnWithoutNotify(false);

        _animeOnDemand.SetProgramVariable("_episodeChannelIndex", 0);
        ShowEpisodesForChannel(0, channels);
      }
    }

    private void ShowEpisodesForChannel(int chIndex, DataList channels)
    {
      if (chIndex < 0 || chIndex >= channels.Count) return;

      HideAllEpisodes();
      _animeOnDemand.SetProgramVariable("_episodeChannelIndex", chIndex);

      if (!_channelLoaded[chIndex])
        LoadEpisodesForChannel(chIndex, channels);
      else
        ShowEpisodesForChannelRange(chIndex);
    }

    private void LoadEpisodesForChannel(int chIndex, DataList channels)
    {
      if (!Utilities.IsValid(_episodeButtonTemplate) || !Utilities.IsValid(_episodeList)) return;

      DataToken channelToken = channels[chIndex];
      if (channelToken.TokenType != TokenType.DataDictionary) return;
      DataDictionary channelData = channelToken.DataDictionary;

      if (!channelData.TryGetValue("episodes", out DataToken episodesToken)) return;
      if (episodesToken.TokenType != TokenType.DataList) return;

      DataList episodes = episodesToken.DataList;
      int epCount = episodes.Count;
      if (epCount > MAX_EPISODES) epCount = MAX_EPISODES;

      int baseIdx = chIndex * MAX_EPISODES;
      for (int ep = 0; ep < epCount; ep++)
      {
        GameObject btn = Instantiate(_episodeButtonTemplate);
        btn.transform.SetParent(_episodeList, false);
        btn.SetActive(true);
        _episodePool[baseIdx + ep] = btn;

        DataToken epToken = episodes[ep];
        string label = epToken.TokenType == TokenType.String ? epToken.String : $"第{ep + 1}集";
        var btnText = btn.GetComponentInChildren<Text>();
        if (Utilities.IsValid(btnText)) btnText.text = label;

        var indexTrigger = btn.GetComponent<IndexTrigger>();
        if (Utilities.IsValid(indexTrigger))
          indexTrigger.SetProgramVariable("_variableObject", ep);
      }

      _channelLoaded[chIndex] = true;
    }

    private void ShowEpisodesForChannelRange(int chIndex)
    {
      int baseIdx = chIndex * MAX_EPISODES;
      for (int ep = 0; ep < MAX_EPISODES; ep++)
      {
        GameObject btn = _episodePool[baseIdx + ep];
        if (!Utilities.IsValid(btn)) break;
        btn.SetActive(true);
      }
    }

    private void HideAllEpisodes()
    {
      for (int i = 0; i < _episodePool.Length; i++)
      {
        if (Utilities.IsValid(_episodePool[i]))
          _episodePool[i].SetActive(false);
      }
    }

    private void ResetEpisodePool()
    {
      HideAllEpisodes();
      for (int ch = 0; ch < MAX_CHANNELS; ch++)
        _channelLoaded[ch] = false;
    }

    // ═══════════════════════════════════════════════
    //  Button handlers
    // ═══════════════════════════════════════════════

    public void OnRefreshClick()
    {
      if (!Utilities.IsValid(_animeOnDemand)) return;
      ResetEpisodePool();
      int context = _animeOnDemand.GetContext();
      if (context == 1 && Utilities.IsValid(_searchInputField)) // CONTEXT_SEARCH
      {
        var url = _searchInputField.GetUrl();
        if (url.IsValidUrl())
          _animeOnDemand.OnSearchSubmit(url);
        else
          _animeOnDemand.FetchUpdateList();
      }
      else
      {
        _animeOnDemand.FetchUpdateList();
      }
    }

    public void OnClearSearchClick()
    {
      if (!Utilities.IsValid(_animeOnDemand)) return;
      // 恢复搜索 URL 前缀，抑制 SetUrl 触发的 onEndEdit
      _suppressSearchSubmit = true;
      if (Utilities.IsValid(_searchInputField))
      {
        var baseUrl = _animeOnDemand.GetSearchBaseUrl();
        if (baseUrl.IsValidUrl())
          _searchInputField.SetUrl(baseUrl);
      }
      SendCustomEventDelayedFrames(nameof(ClearSearchSuppressFlag), 0);
      _animeOnDemand.ClearSearch();
    }

    public void ClearSearchSuppressFlag()
    {
      _suppressSearchSubmit = false;
    }

    public void OnBackClick()
    {
      if (!Utilities.IsValid(_animeOnDemand)) return;
      ResetEpisodePool();
      _animeOnDemand.BackToUpdate();
    }

    public void OnChannel1Changed()
    {
      if (!Utilities.IsValid(_channel1Toggle) || !_channel1Toggle.isOn) return;
      OnChannelSelected(0);
    }

    public void OnChannel2Changed()
    {
      if (!Utilities.IsValid(_channel2Toggle) || !_channel2Toggle.isOn) return;
      OnChannelSelected(1);
    }

    public void OnChannel3Changed()
    {
      if (!Utilities.IsValid(_channel3Toggle) || !_channel3Toggle.isOn) return;
      OnChannelSelected(2);
    }

    private void OnChannelSelected(int chIndex)
    {
      if (!Utilities.IsValid(_animeOnDemand)) return;
      _currentChannel = chIndex;

      DataDictionary detail = _animeOnDemand.GetCurrentDetail();
      if (!Utilities.IsValid(detail)) return;

      if (detail.TryGetValue("channels", out DataToken channelsToken) && channelsToken.TokenType == TokenType.DataList)
        ShowEpisodesForChannel(chIndex, channelsToken.DataList);
    }

    public void OnSearchSubmit()
    {
      if (_suppressSearchSubmit) return;
      if (!Utilities.IsValid(_animeOnDemand) || !Utilities.IsValid(_searchInputField)) return;
      var url = _searchInputField.GetUrl();
      if (!url.IsValidUrl()) return;
      _animeOnDemand.OnSearchSubmit(url);
    }

    // ═══════════════════════════════════════════════
    //  Localization
    // ═══════════════════════════════════════════════

    private string GetLocalized(string key)
    {
      if (Utilities.IsValid(_uiController))
      {
        string translation = _uiController.GetTranslation(key);
        if (!string.IsNullOrEmpty(translation)) return translation;
      }
      return key;
    }

    private void UpdateTranslation()
    {
      if (!Utilities.IsValid(_uiController)) return;
      int state = Utilities.IsValid(_animeOnDemand) ? _animeOnDemand.GetState() : -1;
      if (state == 4 || state == 5) UpdateListView();
    }
  }
}
