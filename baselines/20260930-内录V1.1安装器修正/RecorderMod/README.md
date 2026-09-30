# AzureArchive 内部录制 MOD

> 当前本机开发版已通过用户验收：DLSS 总开关、六页阿罗娜教程、按日期递增命名。新基线为 `../../基线/20260928-教程与命名用户验收通过`，详情见 `DEVELOPMENT.md`。直接运行当前 AA 即可；用户再次明确要求前，不得打包或加壳。以下 R4 安装器内容为原始交接历史。

用于本机 AzureArchive 1.0 fix4 / BepInEx 6 IL2CPP，当前保护安装版为 **0.2.1 R4 内置 FFmpeg 版**。

当前使用 `AzureArchive内录MOD-0.2.1-R4-内置FFmpeg安装.exe`。安装 EXE 已包含经过本机编码验证的 FFmpeg/ffprobe 便携版，安装时无需联网。安装器优先验证当前配置、PATH、AA 附近和常见安装目录里的工具（有界搜索，不遍历全盘），使用 CPU H.264/AAC 试编码验证可用性。

没有找到可用 FFmpeg 时会弹出 **“安装内置 FFmpeg”** 确认框；选择安装后部署并配置，取消则整个本次安装结束且不写入文件。找到有效版本则不弹这个框。内置后备位于 `mods/AzureArchiveRecorder/runtime/ffmpeg`，不修改系统 PATH，不覆盖外部工具。只修正需要更新的 `[Recording] FFmpegPath`，其他配置内容保留。运行时发现原路径消失，会回退当前 AA 目录内的私有版本，适应搬迁电脑。

内置 FFmpeg 纳入安装/卸载哈希清单，卸载仅清理此 MOD 私有工具，用户原有的外部 FFmpeg 保留。第三方 FFmpeg 保持原始可执行文件及许可，不受本 MOD 混淆加壳保护；原包依赖版本、构建配置及来源附在同目录。

R3 修复部分电脑缺少 `Test.OnDestroy` 导致 Harmony 中止 MOD 加载、内录按钮不出现的问题。`OnDestroy` / `OnReady` 按实际存在情况挂接；延时协程按名称前缀查找，不再绑定生成编号 81。播放器销毁时还会检测实例失效并停止保存。必需补丁出错会撤销本 MOD 已挂接的补丁，再报告加载失败，避免部分加载状态。

R4 已包含 R3 的修复，迁移电脑时关闭 AA 后覆盖重新安装即可，无需先卸载。加载成功的日志包含 `Recorder ready (0.2.1 R4 bundled FFmpeg; includes R3 compatibility fix)`。缺少 DLSS 组件不影响按钮或内置 FFmpeg 的普通 MP4 内录；增强仍需对应上游组件。

## EXE 安装与卸载

AA 根目录的 `AzureArchive内录MOD-0.2.1-安装.exe` 是当前发布包。关闭 AA 后双击，自动识别同目录的软件，也可浏览指定其他 AA 1.0 fix4 安装目录；文件安装到 `mods/AzureArchiveRecorder`，为当前 profile 启用。安装失败会弹框，回滚本次文件更改，关闭弹框后强制以错误码退出。

安装器检测 BepInEx / AA MOD 管理器、启动入口、内置 .NET 和可选 DLSS5Tool 组件。缺失时列出影响并询问是否继续；这些缺失组件仍需自行补齐。FFmpeg 单独检测并在需要时询问安装内置版本，不要求另装已包含的 Python。FFmpeg 探测只做 CPU 试编码，不执行 NVIDIA 增强推理。

已有内录 MOD 时，会列出已安装版本，询问“是否覆盖重新安装”；确认后更新运行文件，保留其他录制配置，仅按检测结果更新 FFmpegPath。**依赖缺失、重复安装和内置 FFmpeg 确认均默认取消，取消不写入文件。** AA 目录或版本错误、未知文件冲突、写入错误仍会报错终止，不能以“继续安装”绕过。

卸载时关闭 AA，双击 `mods/AzureArchiveRecorder/卸载内录MOD.exe`。按文件清单与哈希清理运行文件、已识别的旧版内录文件、内录配置及启用项。未知或被用户修改的文件保留；越界路径、符号链接和目录联接会使操作停止。**`RecorderMod` 下的源码、构建脚本、工具、历史包及验证记录全部保留，不会被卸载。其他 MOD、原剧情及已导出视频也不删除。**

保护安装包不包含本 MOD 源码、调试符号或混淆映射；核心采用 Obfuscar 符号/字符串混淆与压缩加密封装，启动时校验 AA 进程和此版本主程序。安装 EXE 和卸载 EXE 同样先混淆、再以 AES 加密压缩核心，仅在内存中校验并加载执行；外层不直接暴露安装 ZIP 资源。正式构建每次必须保护两份 EXE，`-InstallerOnly` 仅复用已有保护 MOD 核心，不跳过 EXE 加壳。增强辅助程序已编译并包含 Python 环境，无需另装 Python。开源依赖的许可与必要文件保留。**这些措施提高逆向成本，不能保证本地程序不可破解。**

维护保护版本使用 `packaging/build-enhancer.ps1`、`packaging/build-release.ps1`；开发工具和混淆映射留在本机 `packaging` 内。`install.ps1` / `build.ps1 -Install` 是源码开发安装流程，会安装未加壳的调试开发版本，不是保护发布流程；`package.ps1` 是旧版源码 ZIP 打包流程，不应当作保护版发布包分发。安装和卸载验证详见 `packaging/RELEASE-VERIFICATION.md`。

0.2.0 已完成本机离屏内录验证：自动使用显示器分辨率、内部混音、加速生成 MP4，录制期间仅显示进度。

## 使用

当前 `Recorder` 配置已启用 0.2.1 保护版。**重新启动 AA 后生效**。

历史源码 ZIP 仅供开发维护；普通用户使用根目录中的 R4 或更新版 EXE 安装包。FFmpeg 可在安装时检测并配置，不再要求同级 DLSS5Tool 目录。

1. 在“剧情”页面选择已编译的剧情文件。
2. 点击 **“入场”旁边的“内录”**，打开独立选项面板。面板复用 AA 设置的背景、标题装饰、关闭按钮和蓝色控件，使用 Noto Sans SC 中文字体。**F8 / F9** 也可打开面板。
3. 可点击 **选择文件夹**，为这次内录指定成品目录。不选择时使用 **Windows“视频”文件夹**（读取系统已重定向的位置），每次打开面板重新使用此默认值。
4. 点击 **开始内录**。界面仅显示进度，不展示剧情播放。MOD 在离屏目标中自动执行剧情；**F10 / 停止并保存**可提前结束。
5. 成品直接保存为所选目录内 `<剧情名_时间_唯一编号>.mp4`。工作素材和日志另存于 AA 的 `Recordings/<任务名>/`，不会覆盖已有视频。
6. 需要增强时，开启 **录制后增强**。原始 MP4 始终保留，增强另存 `<任务名>_enhanced.mp4`。

点击帧率按钮循环选择 24 / 25 / 30 / 50 / 60 fps；超分、补帧和神经渲染也在设置区调整。其他参数在 `profiles/<配置名>/configs/azurearchive.recorder.cfg` 中调整。

不支持增强时，显卡状态、增强的“暂不支持”文字及点击后的提示均显示为**红色**；普通 MP4 录制仍可用。核显与受支持的 NVIDIA 独显共存时，按选中的 NVIDIA 显卡判断支持状态。

**分辨率自动采用 AA 窗口所在显示器的当前物理像素尺寸**，不受 DPI 缩放或窗口大小影响。本机检测为 **3120 × 2080**。开始时切到显示器尺寸，让剧情相机和字幕布局匹配宽高比，屏幕仅显示独立进度界面；结束、取消或报错后恢复原窗口尺寸和显示模式。为兼容 H.264，奇数边长向下取偶数，最多差 1 像素。对白、角色名和选项保留在视频中；进度、菜单和 AUTO 控件不会进入视频。

有分支的剧情沿用原生 AUTO 与默认选项；需要特定路线时先在工程中设置默认选项。当前不是一次导出所有分支。循环剧情受 `MaxMinutes` 限制，默认 120 分钟。

## 工作方式

- MCP 已检查：`2025-11-25`，现有工具覆盖编辑、保存、编译，未提供播放/录制命令。
- MOD 调用 AA 的 `AuthoringCompiler`、`AuthoringWorkbench` 与原生播放器；不修改 MCP 服务器或剧情文件。
- 通过 Unity 离屏相机渲染、固定帧率时钟与 `AudioRenderer` 获取画面和混音，不录桌面、麦克风或其他应用的声音。
- FFmpeg 输出 H.264 + AAC MP4，优先探测本机可用的 Intel QSV / NVIDIA NVENC / AMD AMF 普通硬件编码；探测失败回退 CPU `libx264`。普通录制不要求 NVIDIA，也不依赖 DLSS。本机实测为 Intel QSV，NVENC / AMF 尚未实机覆盖。
- 内录期间 Unity 的扬声器输出可能静音，这是 `AudioRenderer` 的行为；结束后恢复。
- 使用固定视频时间步长，取消实时帧率等待，每一视频帧仍对应 1/fps 秒。录制期间统一 NGUI 字幕/动画、非缩放帧时间和实时等待的计时；结束后恢复。按处理能力尽快推进，导出保持正常时间轴，不靠事后拉慢快放视频。
- 音频由 Unity 内部离线混音获取。混音块可能大于单个视频帧，因此允许零采样帧，仍调用混音接口推进余数；持续校验 DSP 时钟和最终音视频时长差。
- 剧情相机直接渲染到离屏目标，只渲染一次；屏幕进度相机单独渲染。异步 GPU 回读在完成回调内复制数据，最多保留三帧请求，并按帧顺序交给有界编码队列。无法异步回读时使用同步路径。
- `HardwareEncoding=false` 可强制 CPU 编码；`AsyncReadback=false` 可强制同步读取，供驱动兼容性排查。`QualityCRF` 控制编码质量（硬件编码采用各编码器的相应质量参数，数值不代表与 x264 完全相同的画质；0 强制 CPU）。
- 加速程度取决于剧情、分辨率和机器性能，不能保证任意剧情都快于实时。进度条按剧情指令估算，不是精确的剩余时间；复杂跳转可能提前到达较高进度。
- 本机 Intel Arc B390 的 3120×2080 / 60 fps 短篇实测：16.28 秒成片，录制约 10.91 秒（1.49 倍），含准备和封装约 13.13 秒；正常速度对照同为 977 帧，各剧情指令时间点完全一致。此数据不代表所有剧情和机器。
- 有界编码等待，保持逐帧输出；异常时保留临时音视频与错误日志，不把失败结果冒充成品。

## DLSS5Tool 对接与显卡适配

代码基于 [DLSS5Tool v2.3.3](https://github.com/banbanzhige/DLSS5Tool/tree/v2.3.3)，提交 `e53e0b4d5139126e7b31866ed6b3653bbba0f449`。使用上游真正的 `frame_generation.export_video` 处理链：RTX Video 超分、DLSSG 插帧、可选 DLSS5 神经渲染。并非将普通缩放/重复帧包装成 DLSS。

| 检测结果 | 行为 |
| --- | --- |
| RTX 30 系 | 选择上游 `310.8.SF-v2` 适配库 |
| RTX 40 系 | 选择上游 40 系库，可复用 DLSS5Tool 默认库 |
| RTX 50 系 | 选择上游 50 系库 |
| RTX 20 系、未收录的 NVIDIA 型号、非 NVIDIA 显卡 | 显示暂不支持增强；仍可录制普通 MP4 |
| 不同系列的多张 NVIDIA 卡 | 暂不自动增强，避免上游独立选卡导致 DLL 与实际显卡不匹配 |

通过 DXGI 枚举所有适配器，因此核显连接显示器时仍能识别 NVIDIA 独显。按 **DLL SHA-256** 匹配型号，不将任意 `mods/nvngx_dlssnr.dll` 当作正确版本。缺少对应运行库时，点击增强录制会从上游固定 Release 自动下载该系列附件，校验 ZIP 和 DLL，然后在当前所选剧情/已保存工程仍未变化时继续录制。已有不匹配文件不会被覆盖。

运行库独立放入 `RecorderMod/gpu-runtimes/rtx30|rtx40|rtx50`；不会覆盖 DLSS5Tool 的 `_internal`、用户配置或队列。也可手动执行 `./RecorderMod/install-gpu-runtime.ps1 -Series 30`。

[上游附件](https://github.com/banbanzhige/DLSS5Tool/releases/tag/zip)目前只有 30 / 40 / 50 系，没有 20 系。系列识别代表选择正确配置，**不等于实机兼容性认证**。3× / 4× 补帧是上游实验模式；画面含字幕时可能出现伪影。神经渲染会改变画风，因此默认关闭，可单独勾选。

本机已在 `RecorderMod/.venv` 准备独立 Python 桥接环境。修改 Python 桥接时，在其他电脑准备 Python 3.12，运行 `setup-dlss.ps1`；配置 `ToolDirectory` 为完整 DLSS5Tool v2.3.3 目录，源码运行时的 `PythonPath` 指向该环境。安装版自带冻结 Python，无需额外安装 Python、Torch 或 CUDA 模型包。R4 普通录制自带私有 FFmpeg；增强仍使用 DLSS5Tool 的 `_internal/ffmpeg.EXE`。

## 开发与诊断

- `src/`：完整 C# 源码及 `.csproj`。
- `build.ps1 -Install`：使用 PowerShell 7 自带 Roslyn 编译并复制当前版本文件，无需系统 .NET SDK；不会切换 profile。首次安装或切换启用版本使用 `install.ps1`。也可用 .NET 6 SDK 编译 `.csproj`。
- `install.ps1`：保留已有 MOD 项目，为当前配置加入或更新录制插件版本；修改前备份配置，首次安装创建 `Recorder` 配置。
- `src/NativeRecorderUi.cs`：原生设置风格弹窗、屏幕进度相机；`src/FolderPicker.cs`：Windows 文件夹选择器和系统视频目录；`src/MonitorResolution.cs`：显示器物理像素检测。
- `tests/verify-cpu.ps1`：显卡型号分类、H.264/AAC 合成测试、帧数/时长及完整解码检查。
- `tests/test_profiles.py`：不支持型号、错误 DLL、输出覆盖保护等纯配置测试，无 GPU 推理。
- `tests/verify-folder-picker.ps1`：真实 Windows 目录选择器的路径与取消检查；`verify-runtime-output.ps1`：真实内录的分辨率、音视频时长、完整解码与输出文件校验；`verify-timeline.ps1`：对比正常帧率与加速导出的剧情事件时间。
- `bridge/enhance.py <job.json> --check`：只检查文件、参数、运行库身份、Python 依赖，不初始化 GPU。
- `BepInEx/LogOutput.log` 与每次输出目录内的 `recording.json`、`completed.json`、`encode.log`、`mux.log`、`error.txt`：录制诊断。
- 增强任务另外保存 `enhancement-job.json`、`enhancement-progress.json`、`enhancement-result.json`、`enhancement.log` 及上游诊断。

按用户要求，本机不执行 NVIDIA 超分、补帧、神经渲染测试。已执行的验证和局限见 `VERIFICATION.md`。

## 第三方代码

`bridge/vendor/dlss5tool` 保留 v2.3.3 的 Python 源码，不修改其实现；许可证为 `bridge/vendor/DLSS5Tool-LICENSE`。桥接仅将运行库与可写状态目录重定向到指定安装和本次任务。R4 安装器包含普通录制所用的私有 FFmpeg/ffprobe 及来源、许可说明。NVIDIA 增强组件不包含在 MOD 安装器中，复用完整 DLSS5Tool 或下载上游对应附件；开发交接包另外附带现有工具和许可，仍须为实际显卡选择匹配运行库。
