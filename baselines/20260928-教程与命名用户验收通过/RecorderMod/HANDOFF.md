# 给下一台电脑上 Codex 的交接说明

> 当前本机状态已更新：用户于 2026-09-28 验收通过 DLSS 总开关、六页教程和日期序号命名，最新快照为 `../../基线/20260928-教程与命名用户验收通过`。先读 `DEVELOPMENT.md`；未获用户明确要求不得打包或加壳。下面保留原 R4 交接历史，旧测试原始数据已按用户要求清理。

更新日期：2026-09-28。基线为 **AzureArchiveRecorder 0.2.1 R4**，含 R3 加载兼容补丁、R4 内置 FFmpeg 与安装/卸载 EXE 保护。此文记录当前实现，不表示 NVIDIA 推理已经验证。

## 先做这些

1. 阅读本文件和 `DLSS-RTX-TEST-PLAN.md`，再读 `packaging/RELEASE-VERIFICATION.md` 的 R4 部分。后面的 R3/R2 是历史证据，不能当作最新制品哈希。
2. 在 RTX 电脑准备完整 **AzureArchive 1.0 fix4 Windows x64**、BepInEx 6 IL2CPP、ModTheAzureArchive、AA 自带 `dotnet` 与生成完毕的 `BepInEx/interop`。本包是 MOD 开发交接包，不是 AA 本体安装包。
3. 完整解压交接包。把其中源码目录 `RecorderMod` 放在测试用 AA 根目录，形成 `AzureArchive.exe` 与 `RecorderMod` 同级结构；不要直接在 ZIP 中运行脚本。源码构建依赖这个相对位置。
4. 关闭 AA，将保护版安装 EXE 放到 AA 根目录并安装。重复安装按提示确认；缺少有效 FFmpeg 时确认安装内置版本。其他必需框架缺失即使选择继续，也仍需补齐才能加载 MOD。
5. 在当前 MOD profile 启用 `AzureArchiveRecorder 0.2.1`，重启 AA。日志应出现 `Recorder ready (0.2.1 R4 bundled FFmpeg; includes R3 compatibility fix)`。先导出一个普通有声 MP4，再按 RTX 测试计划分阶段开启增强。
6. 用单独的 AA 测试副本做开发安装和卸载实验。不要在有用户唯一剧情、其他 MOD 工作成果的目录运行破坏性测试。普通安装不修改 AA 本体；开发也继续保持这个边界。

本次 `AA录制MOD.zip` 另附：

- `依赖与上游/DLSS5Tool-v2.3.3-win64`：完整 `_internal`、`DLSS5Tool.exe`、说明与许可证；不含原机器的用户设置、队列或任务历史。可以移动到新机器任意稳定目录。
- `依赖与上游/DLSS5Tool-source`：上游源码快照，不含 `.git` 和缓存。它与 MOD 中固定 vendored Python 的作用不同：前者供查阅/继续开发上游，后者才是当前桥接实际导入的代码。

先启动一次 AA 使 MOD 生成配置，再关闭 AA，编辑 `profiles/<当前 profile>/configs/azurearchive.recorder.cfg` 的 `[DLSS] ToolDirectory` 为上述便携工具**在新机器上的实际绝对目录**，然后重启。从“剧情 → 内录”面板选择倍率/增强开关进行测试。当前面板只有成品输出文件夹选择器，**没有 DLSS 工具目录选择器**，不能把“本次输出文件夹”当成 `ToolDirectory`。随附 `_internal/nvngx_dlssnr.dll` 是匹配 40 系的版本；30/50 系仍按自己的 ZIP/DLL 哈希下载安装，不能直接复用 40 系库。

源码、未加壳安装器及保护安装器是分别交接的用途：

| 交接包 `安装程序` 中的文件 | 保护范围与用途 |
| --- | --- |
| `AzureArchive内录MOD-0.2.1-R4-内置FFmpeg安装.exe` | 正式版：MOD 核心及安装/卸载 EXE 均有保护，供最终用户和发布回归 |
| `AzureArchive内录MOD-0.2.1-R4-开发未加壳安装.exe` | 开发版：明文 Debug MOD DLL（嵌入 PDB）、明文安装/卸载 EXE，供本次定位和开发；冻结增强程序及第三方工具仍按各自构建方式交付 |

两版使用同一 `0.2.1` 产品位置和 receipt 文件集合，可互相覆盖；一次只安装/启用一版，不需要把两份都装入 MOD 管理器。以交接包根目录文件清单中的 SHA-256 核对最终制品。正式对外发布继续使用保护流程，未加壳版本是用户为本次交接明确要求的开发制品。

## 当前验证边界

- 原开发机为 Intel Arc B390，无 NVIDIA GPU。R4 的 37 项本机检查通过：8 项安装/卸载安全、13 项真实弹框交互、12 项 FFmpeg 集成、4 项保护结构检查。详细断言及日志索引见 `packaging/RELEASE-VERIFICATION.md`。
- 实际受保护 MOD 以原生 UI 导出 3120×2080、60 fps、1152 帧、19.2 秒 H.264/AAC MP4；检查了有声音、完整解码、输出文件一致、窗口恢复。该次渲染约 21.35 秒，总任务约 25.11 秒，所以不能承诺任意剧情一定快于实时。
- 已在真实 AA 中模拟缺少 `Test.OnDestroy` 和原 FFmpeg 路径失效，仍能显示入口并录制。远端原问题机器尚需更新后复测。
- 更早的正常速度/加速对照验证事件顺序和时间轴一致。比较时必须使用同一剧情、同一帧率、同一资源，不能拿用户后来修改的剧情与旧帧数比较。
- **RTX 超分、DLSSG 补帧、神经渲染、NVIDIA NVENC 和 AMD AMF 均没有在原开发机实测成功记录。** NVIDIA 上游对其自己软件的测试，不等于本 MOD 集成测试通过。`--check` 输出 `gpu_executed: false` 也不代表推理通过。
- 原测试剧情存在缺失资源和 HTTP 404。未修改用户剧情来隐藏这些错误；视频格式验证通过不代表缺失素材已恢复。RTX 画质验收应使用资源完整的自有测试剧情。
- 本次交接会清理原机器的历史大文件、隔离测试副本和测试数据。某些历史报告中的原始路径可能不再存在；保留报告是历史验收记录，不要冒称这些路径仍可回放。

## 目录与文件职责

| 文件或目录 | 职责 |
| --- | --- |
| `src/Plugin.cs` | BepInEx 插件入口；配置；录制状态机；播放器补丁；离屏画面和 Unity 内部音频；结束封装、发布成品、发起增强与取消 |
| `src/NativeRecorderUi.cs` | 在剧情“入场”旁加入“内录”；复用 AA 设置面板风格；中文字体；选项面板；屏幕专用录制进度 UI |
| `src/Encoder.cs` | 探测普通 H.264 编码器；有界帧队列；原始画面与浮点音频写入、FFmpeg 封装；异常保留诊断 |
| `src/FrameReadback.cs` | 异步 GPU 回读，回调中复制数据，保证提交给编码器的帧顺序；停止时刷新 |
| `src/MonitorResolution.cs` | AA 窗口所在显示器物理像素尺寸；不按窗口客户区/DPI 缩放后的逻辑尺寸录制 |
| `src/FolderPicker.cs` | Windows 文件夹选择框；读取可能已重定向的系统“视频”目录 |
| `src/GpuProfiles.cs` | DXGI 枚举 GPU；NVIDIA 系列分类；对应运行库查找、固定附件下载及 SHA-256 校验 |
| `src/Deployment.cs` | 开发/安装运行目录切换；FFmpeg 路径回退；选择冻结增强程序或 Python 源码桥接 |
| `src/OptionalHooks.cs` | 安全解析可选的零参数回调，缺失只警告，避免旧互操作缓存导致整个插件启动失败 |
| `src/UiDiagnostics.cs` | 明确 CLI 标志控制的 UI/录制冒烟验证，不应在普通运行时自动启动 |
| `src/NullableAttributes.cs` | 编译兼容属性；无需改动业务逻辑 |
| `bridge/enhance.py` | 严格验证增强 job；重定向上游路径；调用上游 `export_video`；进度/结果/取消 |
| `bridge/gpu_profiles.json` | 上游 20/30/40/50 支持状态、ZIP/DLL 固定哈希；须与 C# 常量同步 |
| `bridge/vendor/dlss5tool` | 固定的上游 v2.3.3 Python 实现；不是用普通缩放或重复帧伪装 DLSS |
| `packaging/enhance_entry.py` | 冻结增强 EXE 入口、AA 主进程/安装位置检查、multiprocessing 初始化 |
| `packaging/build-enhancer.ps1` | 用 PyInstaller 构建带 Python 依赖的 `EnhanceHost` |
| `packaging/Installer.cs` | 安装、重复安装、依赖/FFmpeg 确认、事务回滚、清单卸载、错误弹框与非零退出 |
| `packaging/Loader.cs.in` | 加密 MOD 核心加载器；校验 AA 进程及本体哈希；设置安装运行目录 |
| `packaging/ExecutableShell.cs.in`、`protect-executable.ps1` | 安装/卸载 EXE 混淆、压缩、加密及内存加载外壳 |
| `packaging/build-release.ps1` | 正式保护发布，生成 DLL/运行时/FFmpeg/卸载器/清单/安装器 |
| `packaging/tools`、`frozen`、`ffmpeg-vendor` | 构建工具、已冻结增强运行时和已验证第三方 FFmpeg，保留用于离线复用 |
| `tests`、`packaging/*Tests.cs` | 配置/编码/实际导出/时间轴，以及真实安装卸载交互与保护结构测试源码 |

安装后目录为 `mods/AzureArchiveRecorder/0.2.1`（DLL/manifest）与 `mods/AzureArchiveRecorder/runtime`（增强程序、FFmpeg、下载的 GPU 配置）。开发模式 `Deployment.Root` 默认为 AA 下 `RecorderMod`；保护加载器将其设为当前安装目录的 `runtime`。因此 GPU 运行库缓存、增强入口的实际路径随运行方式变化，**不要硬编码原机器 E: 盘**。

## 录制状态机与不能破坏的细节

从剧情列表选中文件，打开内录面板，选成品目录和帧率，点击开始；MOD 自动调用 AA 编译/工作台和原生播放器进入剧情。MCP 现有接口用于创作、保存和编译，没有录制命令；捕获依靠本地 MOD API，不改 MCP 服务或剧情源码。

`Armed` 表示等候播放器（最长 120 秒），`Capturing` 表示逐帧捕获，结束后后台封装与可选增强期间仍保持忙碌。循环剧情按 `MaxMinutes` 限制视频时长。默认分支沿用原生 AUTO 和默认选项，不会自动导出每条分支。

1. **固定时间步长。** 开始保存并暂时修改 `Time.captureFramerate`、`timeScale`、VSync、目标帧率和后台运行设置。每一帧仍代表 `1/fps` 秒，尽快渲染而非录快放视频再降速。录制期间还要使 NGUI `RealTime`、`Time.unscaledDeltaTime` 和跟踪到的 `WaitForSecondsRealtime` 使用视频时间；结束恢复原状态。删除这些补丁会使对白/动画/音频时间轴错位。
2. **零音频采样是有效帧。** Unity 混音按 DSP 块产生样本。在 60 fps 下某一视频帧可能得到 0 个样本；仍必须调用 `AudioRenderer.Internal_AudioRenderer_Render`，传有效哑指针与长度 0，让内部余数推进。不能跳过 Render、拒绝 0，或强塞 800 个样本。持续检查 DSP 时钟漂移，写出真实获取的样本。结束要恢复 AudioRenderer/时钟；录制期间扬声器暂时静音属于此离线混音方式。
3. **相机只渲染一次。** 剧情相机直接输出到离屏 `RenderTexture`，在帧尾读取；独立进度相机只输出屏幕，使用独立图层并从录制相机排除。菜单/AUTO 隐藏，角色、字幕、选项仍进入视频。不要把屏幕进度捕获进去，也不要重复 Camera.Render。
4. **GPU 回读生命周期。** `AsyncGPUReadback` 返回数据可能在下一帧失效，必须在完成回调内复制；最多三帧在途，按顺序交给容量受限的编码队列。停止前 `Flush`，不要按回调到达顺序写帧。驱动不支持时可用同步读取。
5. **显示尺寸与恢复。** 使用 AA 所在屏幕当前物理像素，开始调整窗口以匹配剧情布局；奇数边长向下取偶数以适配 H.264。录制期间切换显示模式会报错并保留临时数据。成功、取消、失败都恢复原窗口和全屏模式。
6. **原生 UI。** 界面使用 AA NGUI 组件和中文字体；入口 Widget 深度曾因原按钮抢点击而修为 22。维护时检查实际射线/点击，不只看截图。F8/F9 为备用入口，F10 为停止保存。
7. **兼容性。** `Test.OnDestroy`、`OnReady` 可缺省；生成的延迟协程类型按 `_DelayedAdvance_d__` 前缀解析，不绑定编号 81。轮询实例失效补充销毁检测。必需补丁失败会撤销本 MOD 已装补丁并报告错误；不能为了“显示 UI”静默跳过必要时钟补丁。

## 输出和配置

配置位于 `profiles/<当前 profile>/configs/azurearchive.recorder.cfg`。活动配置来自 **AA 根目录 `ActiveProfile.txt`**；该文件不存在时安装器使用 `Recorder`。不要将原机器固定 `Recorder` 当作每台机器的唯一配置。

| 配置 | 作用/当前默认 |
| --- | --- |
| `[Recording] OutputDirectory` | 内部任务素材/日志目录，默认 AA 下 `Recordings`；不是面板每次选择的成品目录 |
| `FFmpegPath` | 安装器配置；运行时旧路径失效则尝试当前 MOD 私有 FFmpeg |
| `FrameRate` | 24/25/30/50/60，源码默认 30；历史机器配置为 60，不要写死迁移 |
| `QualityCRF` | 默认 18；硬件编码用对应质量参数，不保证不同编码器数值同画质；0 强制 CPU |
| `HardwareEncoding` / `AsyncReadback` | 默认 true；排查可分别关闭，普通 NVENC 编码与 DLSS 支持独立 |
| `MaxMinutes` | 默认 120，范围 1–600，用于循环剧情保护 |
| `[DLSS] ToolDirectory` | 完整 DLSS5Tool v2.3.3 的根目录，仍需原生 GPU DLL/worker |
| `PythonPath` | 源码模式的 Python；安装版优先 `runtime/EnhanceHost.exe`，无需另装 Python |
| `SuperResolution` / `FrameMultiplier` | 默认 2 / 2，分别允许 1/2/4 与 1/2/3/4 |
| `NeuralRendering` | 默认 false，可能改变画风；与超分、补帧分别验证 |

每次打开面板重置成品目录为 Windows“视频”目录，支持重定向。最终复制到 `<剧情名_时间_唯一编号>.mp4`；增强结果另存 `_enhanced.mp4`，原 MP4 始终保留。发布通过唯一临时文件后移动，不覆盖现有成品。工作目录保留原始素材和日志，失败不冒充完成。

## FFmpeg、依赖与安装安全

R4 已内置 Gyan FFmpeg/ffprobe 7.1.1 full，第三方二进制保持原版、保留许可证和来源说明。README 中历史“NVIDIA DLL 与 FFmpeg 不打包”的描述，**只对旧版本成立；当前 FFmpeg 已内置，NVIDIA 组件仍外部提供**。

安装器有界检查配置、PATH、AA 附近和常见目录，并做 CPU libx264/AAC 试编码，避免只看文件存在。无有效 FFmpeg 时弹框询问是否安装内置版本，默认取消；取消整个安装不写文件。已有有效工具则自动配置且不重复询问，私有后备始终保留，不改系统 PATH、不覆盖外部 FFmpeg。BepInEx/MOD 管理器/启动入口/.NET 与可选增强组件另有缺失提示，不能把 DLSS 缺失误诊为按钮加载失败。

安装、覆盖和卸载基于专属目录白名单、清单及 SHA-256。拒绝路径穿越、NTFS ADS、符号链接/目录联接和未知冲突；安装事务失败回滚并弹框后错误退出。卸载逐项删除匹配文件，只删空目录；用户改动/未知文件保留。只处理 profile 中精确的本 MOD 项目，保留其他配置。卸载器复制为临时辅助程序处理自身，临时自身登记重启清理，不承诺立即删尽所有临时文件。

**不要将 `RecorderMod` 源码、其他 MOD、AA 本体、用户剧情和导出视频加入删除清单。不要对整个 `mods` 或用户视频目录递归删除。** 若修改版本号，必须一并处理 manifest、BepInPlugin、加载器、安装器版本/允许路径/旧清单迁移、文档和验证；当前 receipt 有严格版本/路径约束，不能只改 DLL 的版本字符串。

## 可复现构建

在 Windows x64 的 **PowerShell 7** 执行，源码已位于 AA 根目录：

```powershell
Set-Location 'D:\AA测试目录'
pwsh -File .\RecorderMod\build.ps1
pwsh -File .\RecorderMod\tests\test-optional-hooks.ps1
pwsh -File .\RecorderMod\tests\verify-cpu.ps1 -FFmpeg '.\RecorderMod\packaging\ffmpeg-vendor\ffmpeg.exe'
```

`build.ps1` 使用 PowerShell 7 自带 Roslyn，引用当前 AA `dotnet` / `BepInEx/core` / `BepInEx/interop`，不需要系统 .NET SDK；替代方案为 .NET 6 SDK 编译 `.csproj`。安装器使用 Windows `.NET Framework64/v4.0.30319/csc.exe`。普通 Windows PowerShell 5.1 不能代替源码构建所需 PowerShell 7。

调试可用 `build.ps1 -Install` 或 `install.ps1`，二者是明文开发 DLL 流程，不是正式保护发布。开发覆盖保护 DLL 会使已安装 receipt 的哈希不再匹配，卸载会按设计保留被改文件；因此优先用独立测试 AA，不把“卸载保留修改文件”当作缺陷。

交接用完整开发安装器由新增独立脚本 `packaging/build-development-installer.ps1` 生成。它在 `build-development/sources` 生成开发构建副本，将无加载器时 `Deployment.Root` 的 fallback 调整为当前 AA 的 `mods/AzureArchiveRecorder/runtime`，以使完整开发安装包复用同一运行时布局；不改共享 `src/Deployment.cs`。Debug DLL 的 PDB 嵌入 DLL，不额外安装 PDB 文件，以避免回切正式版留下陌生文件。直接 `build.ps1` 的源码开发布局仍是 `RecorderMod`，不要混淆两种入口。

```powershell
pwsh -File .\RecorderMod\packaging\build-development-installer.ps1
# 源码暂不放在 AA 下时，仅这个完整开发构建入口支持显式指定引用/输出的 AA：
pwsh -File 'D:\交接源码\RecorderMod\packaging\build-development-installer.ps1' -GameRoot 'D:\AA测试目录'
```

开发构建仍需 `packaging/tools/obfuscar/tools/Mono.Cecil.dll` 做制品结构检查，因此不能因“未加壳”删去整个工具目录。必须带齐 `frozen/EnhanceHost` 的所有文件、`ffmpeg-vendor` 两个 EXE 及五个许可/版本/来源文件、安装器源码和说明文件。若 stage 有上一次遗留的未知文件，脚本明确拒绝打包而不删除；按报错检查该专属构建目录，不要改成无边界清理。

未改 Python 桥接时，可离线复用包内 `packaging/frozen/EnhanceHost`、工具和 `ffmpeg-vendor`，直接正式构建：

```powershell
pwsh -File .\RecorderMod\packaging\build-release.ps1
```

这会重新编译、混淆与封装 MOD 核心，保护安装和卸载 EXE，并生成当前安装负载。脚本会按当前 AA 哈希生成加载/安装限制；先确认是目标 AA 版本，不能通过随意换 hash 掩盖 API 不兼容。不要把 stale `stage` 中未知文件带入正式包；清理只能针对确认的构建目录，保留源码。

`-InstallerOnly` 仅可复用已验证的保护 DLL，不能用于修改 C# 后的正式构建；它仍会保护安装/卸载 EXE。每次加密使用新随机材料，重新构建 EXE 哈希变化是正常现象，应记录新哈希、不能套用历史报告。

修改桥接或 vendored Python 后必须重建冻结程序，不能只改源码却继续分发旧 `frozen`。虚拟环境不可直接跨电脑复制：

```powershell
pwsh -File .\RecorderMod\setup-dlss.ps1 -Python 'C:\实际Python目录\python.exe'
& .\RecorderMod\.venv\Scripts\python.exe -m pip install 'PyInstaller==6.22.0'
& .\RecorderMod\.venv\Scripts\python.exe -m unittest discover -s .\RecorderMod\tests -p test_profiles.py
pwsh -File .\RecorderMod\packaging\build-enhancer.ps1
pwsh -File .\RecorderMod\packaging\build-release.ps1
```

原冻结环境为 Python 3.12.14；`setup-dlss.ps1` 要求 3.11+，优先复用 3.12 x64 降低变量。Python 依赖版本在 `bridge/requirements.txt`；重建 `.venv`/下载依赖需要网络或另备对应 wheels，交接包的冻结程序可直接用于常规运行，不等于包含完整离线 pip 镜像。不要盲装 Torch、CUDA Toolkit 或大型模型来满足这个桥接；实际驱动依赖见测试计划。

`obfuscar.xml` 和 `EnhanceHost.spec` 可能记录原机器 E: 路径：正式 `build-release.ps1` 会重写 Obfuscar 的路径，`build-enhancer.ps1` 会重新生成 spec；迁移后应运行这些入口，不能直接照旧绝对路径调用工具。当前正式 EXE 构建脚本的 .NET Framework 编译器路径仍写为 `C:\Windows`；如新机器 Windows 安装在其他盘，需要按该机器系统目录调整这处构建参数。新的开发安装构建已动态读取 Windows 目录。

本 MOD 的完整构建不要求重编上游原生库。若后续修改随附 `DLSS5Tool-source` 中的 C++ host/worker，还需要 Visual Studio C++ x64 Build Tools、Windows SDK、NVIDIA DLSS SDK，以及编译 VSR 所需 RTX Video SDK 1.1（`NV_RTX_VIDEO_SDK`）；当前上游快照没有 `third_party` SDK 目录。上游 `scripts/build_dlssg_video.bat` 还要求历史 `tmp/dlssg-entry-20260913/TASK.md` 才继续，不能把它直接视为可移植的一键构建入口；应先整理其临时输出/前置检查。仅修改 C# 或 Python 桥接时不需要处理这些额外原生 SDK。

保护使用 Obfuscar 2.2.50、字符串/符号混淆、GZip/AES、完整性校验和内存加载，不是不可破解保证。安装器未商业签名。源码交接包本身包括维护所需源码/工具，不能当作闭源终端分发包。第三方开源/二进制许可继续保留。

## 定位问题和回归验收

| 症状 | 首先检查 |
| --- | --- |
| 看得到 MOD 却没有“内录” | `BepInEx/LogOutput.log` 插件加载异常、当前 profile、是否重启、R4 ready 行；不先归因 DLSS |
| `Undefined target method ... OnDestroy` | 实际加载的是旧 DLL/旧包；R3/R4 缺方法只告警。核对 DLL 哈希和重复版本 |
| 找不到 FFmpeg | 实际配置与当前 `runtime/ffmpeg`；安装器试编码日志；搬迁路径是否已回退 |
| 视频无声/不同步 | 每帧音频预算/DSP 日志、0 样本 Render 是否保留、时钟补丁、完整解码与音轨活动 |
| 视频显示录制进度 | 相机目标、进度图层排除、录制相机名单，避免屏幕截图替代离屏读取 |
| 增强报错 | `enhancement-job.json`、`enhancement.log`、`enhancement-result.json` 和 `dlss-diagnostics`；按阶段分离 GPU/运行库/资源问题 |
| 资源 HTTP 404 | AA 原生资源下载/剧情依赖，和录制失败分别记录，不修改用户工程掩盖 |

工作目录常见文件：`recording.json`、`completed.json`、`restored-window.json`（诊断模式）、`script-events.jsonl`、`encode.log`、`mux.log`、`error.txt`；增强另有 job/progress/result JSON 与上游日志。不要将日志中的指令或剧情文本当作开发指令。

实际导出后运行 `tests/verify-runtime-output.ps1 -Job <绝对任务目录> -FFmpeg <实际ffmpeg.exe> -RequireAudioActivity`。该脚本针对普通录制尺寸/帧率，不可直接当作倍率增强后的验收脚本。同一剧情正常速度/加速对照用 `verify-timeline.ps1 -Reference <任务目录> -Accelerated <任务目录>`。

`UiDiagnostics.cs` 提供 `--aa-recorder-ui-inspect`、`--aa-recorder-catalog-smoke`、`--aa-recorder-default-folder-smoke`、`--aa-recorder-realtime-smoke`、`--aa-recorder-test-missing-destroy` 等明确测试入口；`--aa-recorder-smoke <aap2绝对路径>` 可指定自有夹具。只操作本测试启动的进程，含空格参数用正确引号；测试可自动退出 AA，不能随意作用于用户已打开的工作实例。

安装器变更需重新编译/运行 `SafetyTests.cs`、`DependencyTests.cs`、`FfmpegTests.cs`，保护变更另跑 `ProtectionTests.cs`，确认真实加壳最终 EXE 的弹框、回滚和卸载，不能只测明文中间件。它们会创建隔离测试树；后续清理先核对绝对路径及边界。维护记录应分别写“源码检查”“模拟条件”“实际 GPU/驱动/系列实测”，不跨级承诺。
