using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDKBase;
using Yamadev.YamaStream.Editor;

namespace Yamadev.YamaStream.Modules.AnimeOnDemand.Editor
{
  public class AnimeOnDemandBuildProcess : IYamaPlayerBuildProcess
  {
    public int callbackOrder => -2000;

    // 与 VideoResolverBuildProcess 共享相同服务端地址
    private const string HOST = "www.laevatain.top";

    public void Process()
    {
      var modules = Object.FindObjectsByType<AnimeOnDemand>(FindObjectsInactive.Include, FindObjectsSortMode.None);
      foreach (var module in modules)
      {
        ProcessModule(module);
      }
    }

    private void ProcessModule(AnimeOnDemand module)
    {
      if (module == null) return;

      // _controller 已由 YamaPlayerModuleBuildProcess (callbackOrder -3000) 设置
      var controller = module.GetProgramVariable("_controller") as Controller;
      if (controller == null) return;
      var playerId = (int)controller.GetProgramVariable("_playerId");

      var baseUrl = $"https://{HOST}/anime/vrc";

      // 1. 更新 URL（1 个）
      var updateUrl = new VRCUrl($"{baseUrl}/update?pid={playerId}");
      module.SetProgramVariable("_updateUrl", updateUrl);

      // 2. 详情 URL（100 个）
      var detailUrls = new VRCUrl[100];
      for (int i = 0; i < 100; i++)
        detailUrls[i] = new VRCUrl($"{baseUrl}/detail?pid={playerId}&idx={i}");
      module.SetProgramVariable("_detailUrls", detailUrls);

      // 3. 封面 URL（100 个）
      var coverUrls = new VRCUrl[100];
      for (int i = 0; i < 100; i++)
        coverUrls[i] = new VRCUrl($"{baseUrl}/cover?pid={playerId}&idx={i}");
      module.SetProgramVariable("_coverUrls", coverUrls);

      // 4. 播放 URL（900 个：3 渠道 × 300 集）
      var playUrls = new VRCUrl[900];
      for (int ch = 0; ch < 3; ch++)
        for (int ep = 0; ep < 300; ep++)
          playUrls[ch * 300 + ep] = new VRCUrl($"{baseUrl}/play?pid={playerId}&ch={ch}&ep={ep}");
      module.SetProgramVariable("_playUrls", playUrls);

      // 5. 返回 URL（1 个）
      var backUrl = new VRCUrl($"{baseUrl}/back?pid={playerId}");
      module.SetProgramVariable("_backUrl", backUrl);

      // 6. 搜索基 URL（1 个）——由 UI 层运行时 SetUrl 写入 VRCUrlInputField
      var searchBaseUrl = new VRCUrl($"{baseUrl}/search?pid={playerId}&q=");
      module.SetProgramVariable("_searchBaseUrlField", searchBaseUrl);

      Debug.Log($"[AnimeOnDemand] Build process complete: playerId={playerId}, 1,104 URLs generated");
    }
  }
}
