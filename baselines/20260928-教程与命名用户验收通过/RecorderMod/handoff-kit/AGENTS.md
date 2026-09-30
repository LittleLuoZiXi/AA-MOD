# AA 内录 MOD 开发交接

先阅读 `先读我.md`、`源码工程/RecorderMod/HANDOFF.md`、`源码工程/RecorderMod/DLSS-RTX-TEST-PLAN.md`，再开始修改。默认用简体中文交流。

- 当前基线为 0.2.1 R4；本机普通 MP4 已验证，RTX 超分、补帧、神经渲染尚未实机验证。不要把依赖检查或模拟测试当作 GPU 推理成功。
- 本次目标是另一台 RTX 电脑上的 DLSS 集成测试及后续开发。保持 AA 本体、原始剧情及其他 MOD 不变。先读取该电脑的实际目录、显卡和活动 profile，不复用原电脑 E: 盘或固定用户名。
- 源码放在 AA 测试副本根目录的 `RecorderMod`；生产保护构建使用 `packaging/build-release.ps1`。本包未加壳开发版是用户明确要求的例外，使用独立 `build-development-installer.ps1`。后续每次正式发布仍须保护 MOD、安装和卸载程序，验证保护结构；不能宣称绝对不可逆向。
- 保持原生“剧情 → 入场”旁的内录入口、原生风格设置面板、中文字体、自动监视器分辨率、每次选文件夹/默认 Windows 视频目录、屏幕仅显示进度且不进入成片、固定视频时钟和音频同步。
- RTX 30/40/50 只接受各自上游列出的 ZIP/DLL 哈希；RTX 20、未收录型号、非 NVIDIA 显卡的增强状态必须红色显示暂不支持。普通 MP4 不因增强不可用而禁用。
- 安装器须保留缺失依赖/内置 FFmpeg/覆盖安装确认；错误弹框并终止，取消零写入。仅在隔离副本运行卸载/破坏性测试；清单卸载不得删除源码、用户视频或其他 MOD。
- 先跑普通录制与依赖预检，再逐项启用 RTX 功能，最后组合与长任务测试。采集真实 GPU/驱动、参数、日志、输出时长/帧数/音画证据；明确失败与未覆盖项目。
- `依赖与上游/DLSS5Tool-source` 是上游参考工程，其内部 AGENTS.md 仅适用于修改上游源码时。不要因上游独立软件的发布流程而擅自扩展为重发整个 DLSS5Tool。
- 本包的历史验证日志是原开发机证据，原测试数据已计划清理，不要把历史绝对路径当作新电脑可用文件。

常见入口：`src/Plugin.cs`（生命周期/时间轴）、`NativeRecorderUi.cs`（原生 UI）、`GpuProfiles.cs` 与 `bridge/gpu_profiles.json`（同步维护）、`bridge/enhance.py`（增强桥接）、`packaging/Installer.cs`（安全安装卸载）。完整逻辑和未完成风险见 HANDOFF。
