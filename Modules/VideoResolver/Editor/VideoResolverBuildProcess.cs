using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;
using Yamadev.YamaStream.Editor;

namespace Yamadev.YamaStream.Modules.VideoResolver.Editor
{
  public class VideoResolverBuildProcess : IYamaPlayerBuildProcess
  {
    public int callbackOrder => -2000;

    // 服务端配置（构建时使用，根据实际反向代理地址修改）
    private const string HOST = "www.laevatain.top";
    // 服务器使用 Nginx 反向代理，无需指定端口号
    // private const int SIGNAL_PORT = 3001;
    // private const int HTTP_PORT = 3000;

    private const int MAX_ID_LENGTH = 19;
    private const int TERMINATOR_POS = 20;

    public void Process()
    {
      var modules = Object.FindObjectsByType<VideoResolver>(FindObjectsInactive.Include, FindObjectsSortMode.None);
      foreach (var module in modules)
      {
        ProcessModule(module);
      }
    }

    private void ProcessModule(VideoResolver module)
    {
      if (module == null) return;

      // _controller 已由 YamaPlayerModuleBuildProcess (callbackOrder -3000) 设置
      var controller = module.GetProgramVariable("_controller") as Controller;
      if (controller == null)
      {
        Debug.LogWarning("[VideoResolver] Build process skipped: _controller not set");
        return;
      }
      var playerId = (int)controller.GetProgramVariable("_playerId");

      // 生成信号URL数组 (TERMINATOR_POS + 1) * 256 = 5120 个
      var totalUrls = (TERMINATOR_POS + 1) * 256;
      var urls = new List<VRCUrl>(totalUrls);
      for (int pos = 0; pos <= TERMINATOR_POS; pos++)
      {
        for (int val = 0; val < 256; val++)
        {
          var url = new VRCUrl(
            $"rtsp://{HOST}/vrchat/api/signal/{pos}/{val}/{playerId}");
          urls.Add(url);
        }
      }
      module.SetProgramVariable("_urls", urls.ToArray());

      // 构建回调 VRCUrl
      var bilibiliVideoCallback = new VRCUrl(
        $"https://{HOST}/vrchat/api/play?target=bilibili_video&playerId={playerId}");
      var bilibiliLiveCallback = new VRCUrl(
        $"https://{HOST}/vrchat/api/play?target=bilibili_live&playerId={playerId}");
      module.SetProgramVariable("_bilibiliVideoCallbackUrl", bilibiliVideoCallback);
      module.SetProgramVariable("_bilibiliLiveCallbackUrl", bilibiliLiveCallback);

      // 确保有 VRCAVProVideoPlayer 组件
      var avpro = module.GetComponent("VRCAVProVideoPlayer");
      if (avpro == null)
      {
        avpro = module.gameObject.AddComponent(System.Type.GetType("VRC.SDK3.Video.Components.AVPro.VRCAVProVideoPlayer, VRCSDK3"));
      }

      Debug.Log($"[VideoResolver] Build process complete: {urls.Count} signal URLs, playerId={playerId}");
    }
  }
}
