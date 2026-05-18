# LaevaPlayer 待办事项

## VideoResolver 模块

- [ ] 在 Unity 编辑器中创建 VideoResolver 模块预制体和 ModuleDefinition asset
- [ ] 配置 `VideoResolverBuildProcess` 中的服务端地址（SIGNAL_HOST）
- [ ] 更新 Editor asmdef 中的运行时 asmdef GUID 引用（Unity 导入后自动生成）
- [ ] 端到端测试：实际 VRChat 客户端 + 服务端 Bilibili 视频解析
- [ ] 端到端测试：Bilibili 直播流解析
- [ ] 端到端测试：多客户端网络同步场景
- [ ] 端到端测试：播放中切换视频的竞态场景

## 未来扩展

- [ ] 抖音视频解析（需先解决服务端解析方法的技术问题）
- [ ] 信号发送进度 UI（显示解析进度百分比）
- [ ] 解析超时检测与用户提示
- [ ] 支持多个 VideoResolver 实例（同一世界多个播放器，不同 playerId）
- [ ] HistoryList / QueueList 同步 originalUrl（当前仅 Playlist 支持）
