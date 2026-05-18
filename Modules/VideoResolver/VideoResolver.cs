using UdonSharp;
using UnityEngine;
using VRC.SDK3.Video.Components.AVPro;
using VRC.SDKBase;

namespace Yamadev.YamaStream.Modules.VideoResolver
{
  [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
  [RequireComponent(typeof(VRCAVProVideoPlayer))]
  public class VideoResolver : YamaPlayerModule
  {
    [SerializeField] private VRCUrl[] _urls;
    [SerializeField] private int _playerId = 0;

    //———————— Callback URLs ————————//
    [SerializeField] private VRCUrl _bilibiliVideoCallbackUrl;
    [SerializeField] private VRCUrl _bilibiliLiveCallbackUrl;

    private VRCAVProVideoPlayer _signalPlayer;
    private bool _isResolving;
    private VRCUrl[] _signalQueue = new VRCUrl[0];
    private int _signalIndex;
    private float _lastSignalTime;
    private VRCUrl _pendingCallbackUrl;
    private int _callbackPhase; // 0=sending, 1=waiting, 2=done

    private const int MAX_ID_LENGTH = 19;
    private const int SIGNALS_PER_POS = 3;
    private const int TERMINATOR_POS = 20;
    private const float SIGNAL_INTERVAL = 0.2f;
    private const float CALLBACK_DELAY = 2.5f;

    private const string PLATFORM_BILIBILI_VIDEO = "bilibili_video";
    private const string PLATFORM_BILIBILI_LIVE = "bilibili_live";

    public override void Start()
    {
      base.Start();
      _signalPlayer = GetComponent<VRCAVProVideoPlayer>();
      SendCustomEventDelayedFrames(nameof(SetHooks), 1);
    }

    public void SetHooks()
    {
      if (!Utilities.IsValid(_controller)) return;
      _controller.SetTrackResolveHook(this, nameof(OnTrackResolve));
    }

    public void OnTrackResolve()
    {
      if (!Utilities.IsValid(_controller)) return;

      var track = _controller.Track;
      var vrcurl = TrackUtils.GetUrl(track);
      var checkUrl = Utilities.IsValid(vrcurl) ? vrcurl.Get() : string.Empty;

      // 非平台URL或无URL → 直接加载
      if (string.IsNullOrEmpty(checkUrl) || !TryExtractVideoId(checkUrl, out var platform, out var videoId))
      {
        _controller.DirectLoadTrack();
        return;
      }

      // 正在解析中 → 取消旧解析，开始新解析
      if (_isResolving)
      {
        PrintLog("Cancelling previous resolution for new track.");
        _isResolving = false;
      }

      // 不修改 Track.VRCUrl，直接开始信号发送
      StartSignalSending(platform, videoId);
    }


    //———————— ID 解析 ————————//
    private bool TryExtractVideoId(string url, out string platform, out string videoId)
    {
      platform = string.Empty;
      videoId = string.Empty;
      if (string.IsNullOrEmpty(url)) return false;

      // Bilibili video: https://www.bilibili.com/video/BV1xx411c7mD
      if (TryExtractBilibiliVideo(url, out videoId))
      {
        platform = PLATFORM_BILIBILI_VIDEO;
        return true;
      }

      // Bilibili live: https://live.bilibili.com/12345
      if (TryExtractBilibiliLive(url, out videoId))
      {
        platform = PLATFORM_BILIBILI_LIVE;
        return true;
      }

      return false;
    }

    //———————— 各平台 ID 解析逻辑 ————————//
    private bool TryExtractBilibiliVideo(string url, out string id)
    {
      id = string.Empty;
      var prefix = "bilibili.com/video/";
      var idx = url.IndexOf(prefix);
      if (idx < 0) return false;

      var startIdx = idx + prefix.Length;
      var endIdx = url.IndexOf("?", startIdx);
      if (endIdx < 0) endIdx = url.Length;
      id = url.Substring(startIdx, endIdx - startIdx);

      if (id.EndsWith("/")) id = id.Substring(0, id.Length - 1);
      return id.Length > 0;
    }

    private bool TryExtractBilibiliLive(string url, out string id)
    {
      id = string.Empty;
      var prefix = "live.bilibili.com/";
      var idx = url.IndexOf(prefix);
      if (idx < 0) return false;

      var startIdx = idx + prefix.Length;
      var endIdx = url.IndexOf("?", startIdx);
      if (endIdx < 0) endIdx = url.IndexOf("/", startIdx);
      if (endIdx < 0) endIdx = url.Length;
      id = url.Substring(startIdx, endIdx - startIdx);
      return id.Length > 0;
    }


    private void StartSignalSending(string platform, string videoId)
    {
      var bytes = GetUtf8Bytes(videoId);
      if (bytes.Length > MAX_ID_LENGTH)
      {
        PrintError($"Video ID too long: {bytes.Length} > {MAX_ID_LENGTH}");
        _controller.DirectLoadTrack();
        return;
      }

      var queueLength = (bytes.Length + 1) * SIGNALS_PER_POS;
      _signalQueue = new VRCUrl[queueLength];

      var urlCount = _urls.Length;
      var queueIdx = 0;

      // 为每个字符位置构建信号
      for (int pos = 0; pos < bytes.Length; pos++)
      {
        var urlIndex = pos * 256 + bytes[pos];
        if (urlIndex >= urlCount)
        {
          PrintError($"URL index out of range: {urlIndex}");
          _controller.DirectLoadTrack();
          return;
        }
        for (int s = 0; s < SIGNALS_PER_POS; s++)
        {
          _signalQueue[queueIdx] = _urls[urlIndex];
          queueIdx++;
        }
      }

      // 添加 terminator（位置20，值为ID长度）
      var terminatorIndex = TERMINATOR_POS * 256 + bytes.Length;
      if (terminatorIndex >= urlCount)
      {
        PrintError($"Terminator URL index out of range: {terminatorIndex}");
        _controller.DirectLoadTrack();
        return;
      }
      for (int s = 0; s < SIGNALS_PER_POS; s++)
      {
        _signalQueue[queueIdx] = _urls[terminatorIndex];
        queueIdx++;
      }

      // 设置回调URL
      switch (platform)
      {
        case PLATFORM_BILIBILI_VIDEO:
          _pendingCallbackUrl = _bilibiliVideoCallbackUrl;
          break;
        case PLATFORM_BILIBILI_LIVE:
          _pendingCallbackUrl = _bilibiliLiveCallbackUrl;
          break;
        default:
          _controller.DirectLoadTrack();
          return;
      }

      _signalIndex = 0;
      _lastSignalTime = 0f;
      _callbackPhase = 0;
      _isResolving = true;

      PrintLog($"Start signal sending: platform={platform} id={videoId} queueSize={queueLength}");
    }

    private byte[] GetUtf8Bytes(string str)
    {
      var bytes = new byte[str.Length];
      for (int i = 0; i < str.Length; i++)
      {
        bytes[i] = (byte)str[i];
      }
      return bytes;
    }


    private void Update()
    {
      if (!_isResolving) return;

      switch (_callbackPhase)
      {
        case 0:
          SendSignals();
          break;
        case 1:
          WaitForServer();
          break;
      }
    }


    private void SendSignals()
    {
      if (_signalIndex >= _signalQueue.Length)
      {
        _callbackPhase = 1;
        _lastSignalTime = Time.time;
        PrintLog($"Signal sending complete. Waiting {CALLBACK_DELAY}s for server processing.");
        return;
      }

      if (Time.time - _lastSignalTime < SIGNAL_INTERVAL) return;

      _lastSignalTime = Time.time;
      var url = _signalQueue[_signalIndex];
      _signalIndex++;

      if (Utilities.IsValid(_signalPlayer) && Utilities.IsValid(url))
      {
        _signalPlayer.LoadURL(url);
      }
    }

    private void WaitForServer()
    {
      if (Time.time - _lastSignalTime >= CALLBACK_DELAY)
      {
        _callbackPhase = 2;
        _isResolving = false;
        PlayCallback();
      }
    }

    private void PlayCallback()
    {
      if (!Utilities.IsValid(_pendingCallbackUrl))
      {
        PrintError("Pending callback URL is invalid.");
        return;
      }

      _controller.Handler.LoadUrl(_pendingCallbackUrl);
      PrintLog($"Playing callback URL: {_pendingCallbackUrl.Get()}");
    }
  }
}