# V1.2.2 新版 AA 加速内录交接

2026-10-02。先读本文件。V1.2.1 的交接与旧视频对照仅为历史记录；当前源码在 D:\test2\newhost-20261002\RecorderMod，宿主来自 C:\Users\Luo Tian Xing\Downloads\AzureArchive_100_fix。不修改 AA 本体，不做 DLSS 修改、安装、下载或测试，用户手动验证前不上传 GitHub。

## 当前实现

普通内录不完整预演，默认在首张真实完整画面后以 120 Hz 的虚拟剧情时钟处理，Time.captureDeltaTime=1/120。AA 的 Application.targetFrameRate、vSync 和 timeScale 保持其原设置。30/60 输出帧率独立于剧情步长。RealTime.time/deltaTime、unscaledDeltaTime 与 WaitForSecondsRealtime 在本次录制期间映射到同一时钟，结束、失败或取消均恢复。不会修改 AA 等待常量，也不会裁剪/拉伸成片来匹配参考。

RecordingFastClock.cs 冻结本次 AcceleratedRecording 配置（默认 true）；false 回退 V1.2.1 原生实时模式。诊断 --aa-recorder-fast-hz 0 强制实时，100..2000 可覆盖。开始后改配置不改变当前任务。RecordingNativeClock/LiveClock 保留双纹理，按时间戳取最近已完成画面；每个剧情步调用 AudioRenderer，包括零样本步，PCM 与重复画面分开送入有界 FIFO。

高速模式禁用已接管相机的自动渲染，以约 60 Hz 手动合成，背景/标题等待仍触发真实 Render。关闭前先由 NativeRecorderUi.ExcludeFromCapture 登记并移除进度 UI 层，避免禁用相机遗漏 Camera.allCameras 导致 UI 入镜；新相机、相机重新启用、恢复均处理。MENU/AUTO 隐藏逻辑沿用已验证实现。

“开始前统计总时长”仍是原生状态按钮，每次打开默认关闭。申请开启必须确认：“统计总时长需要完整的预演一次，会极大的拖慢内录的耗时时长，是否开启？”开启后强制 fastSimulationHz=0，完整原生预演，冻结计划，再按保存的原生步长回放。关闭时不生成计划，进度不显示总时长。未设置 AUTO 的选项显示红色错误并终止，连续 AUTO 按标记路线选择。

DiagnosticHostReady.cs 只约束自动测试启动：AreDbsLoaded 早于资源索引就绪，故还检查 manifest 和 CharacterManager.characterHashDict.ByteBuffer；原先过早开始的失败记录不算有效基线。NativeTimingSmoke 增加 120 秒开始超时。

## 新宿主和完整剧情证据

新 GameAssembly.dll SHA256：2B8C36F681A3932071D4E609BB034034B087D4D87DFA88A7FFC1A4A206169528。宿主 CHANGELOG 提到 AUTO 等待被减半的修复。新版本实际时长可不同于旧视频；不得再以旧程曦18.mp4判定通过。

本次同一私有剧情 SHA256：0025BE46EC2A8D865E6392955F51E3AF5A31F8997ECEE3FE579437BDBA32C295。AUTO 等待 2 秒、文字 0.04 秒、换行 0.2 秒、原生无限帧率。测试均在独立 AA 中运行。私有工程、资源、视频不随源码发布。

- 新原生基线：420.1272344 秒，155 节点，自然结束；run 20261002-192725-134-Scenario-native-default-c8f587。
- 加速直接 30 FPS：421.333333 秒，12,640 帧；render 205.353409 秒；含准备和封装总 226.241198 秒，成片/总耗时 1.8623 倍，比原生长 1.2061 秒；run 20261002-193522-007-Scenario-30fps-d93368。
- 加速直接 60 FPS：421.316667 秒，25,279 帧；render 337.778111 秒；总 364.670522 秒，1.1553 倍，比原生长 1.1894 秒；run 20261002-194001-462-Scenario-60fps-a2e181。
- 两片均 3120×2080，音频 48k 双声道，20,221,952 个采样；完整自然结束，增强关闭，配置恢复。固定源步长约 0.0083333338 秒，无超过 100ms 的源步。
- 用户在得知两档实际速度后明确接受，不再优化 60 FPS；不能宣称两档都达 1.6–2 倍。
- 上述完整性能数据来自最终算法的开发 DLL（显式 --aa-recorder-fast-hz 120）；随后增加默认配置冻结/版本和诊断信息。最终加壳 DLL 的回归见交付验证记录，不冒称完整性能跑在保护 DLL 上。

## 构建与后续测试

沿用 packaging/build-release.ps1 fresh 全量加壳链；禁止 InstallerOnly 复用旧 stage。Obfuscar 2.2.50 完整 tools、固定哈希 FFmpeg/ffprobe、Python3.12 .venv 和 PyInstaller6.11.1 按 CONTINUE-V1.2.1.md 落位。FrozenRuntimeDirectory 可使用已有安装的 recorder/runtime，桥接和 DLSS 逻辑本轮未改。用户端不勾选 DLSS，普通 MP4 不依赖它。

源码 ZIP 使用文件白名单、逐条 SHA256 和六张教程 PNG，不含 AA 宿主、工具二进制、虚拟环境、用户工程或视频。tests/benchmark-video-pipe.ps1 为停止优化前未执行的草稿，不纳入发行源码索引。所有测试结果须区分开发 DLL 与加壳 DLL；不做全程听感同步承诺。

旧正式源 D:\test2\work\RecorderMod、旧 E:\AzureArchive_100_1001 和已接受的 V1.2.1 包保留。后续在 RTX 机器测试 DLSS 时，先建立新隔离副本，不绕过 GPU 系列支持表和固定来源哈希，不影响其他 MOD 或源码工程。
## 最终保护包回归补充

构建 protected-V1.2.2-20261002-195141-f00af549；插件 SHA256 C3AED2EA7B7FD2C30D612F140BD0DE674D072964DE68406E246F2457136DD762；安装器 SHA256 C34C74BD34EAD02CE205599BB7A288114AE7B54DADCFAB04EB8893C33E6D0275。201 个载荷文件校验通过，冻结 helper 42 项源码一致性检查通过，包内无 DLSS 原生资源。

最终保护 DLL 的六场景回归全部通过：默认直接加速的连续两次 AUTO；开启总时长后的原生预演/回放；未标 auto 的直接与预演两种入口均红字拒绝；预演过程中与正式渲染之前取消。测试通过真实原生 UI 回调检查默认关闭、确认/取消/重开状态、进度字段、MENU 隐藏和恢复。默认直接模式元数据显示 accelerated/120Hz/totalWasMeasured=false；计时模式显示 native-preflight-replay/0Hz，预计 10.666667 秒、完整成片 10.65 秒（相差一帧，预计值未改）。记录位于 verification/protected-focused-20261002-195247-267-b373e027/suite-result.json。

同生产安装源码的合成安装/卸载事务回归 60/60 通过，包括 1.2.1 升级、其他 MOD/源码/用户文件保留、路径归属检查和真实文件操作失败回滚。该测试不是加壳 EXE GUI 端到端测试；实际安装采用同一已校验载荷与 InstallCore，分别保留日志。

两份完整影片音视频全量解码无错误；MENU 扫描 12,640/25,279 帧，命中均为 0；音视频时长差均小于 1ms，音轨平均 -12.1dB、峰值 0dB。210/350 秒四张预览可读且对齐。未全片人工听辨。

时钟相关纯回归通过：LiveCaptureTimeline 35,078；CompositeSchedule 27,630；CaptureTimeline 27,519；DurationPlan 20 项。测试需各自独立 PowerShell 进程，避免 Add-Type 同名类型重复注册。
最终额外验证：无 ChoiceProbe 的 catalog-entry 通过（20261002-195728-225）；显式 fast-hz 0 的 TitleNoChoice 30 FPS 实时回退自然完成，11.633333 秒，模式 native-realtime/0Hz，配置恢复。已将同一保护载荷安装到用户下载的新 AA 目录，201 项载荷哈希通过，490 个原有文件逐字节不变，创建并启用 Recorder profile，无 DLSS 安装。本轮未在该实际安装目录启动 AA，启动/UI 验证在同宿主隔离副本完成。记录：verification/new-aa-install/result.json 与 final-startup-fallback.json。
