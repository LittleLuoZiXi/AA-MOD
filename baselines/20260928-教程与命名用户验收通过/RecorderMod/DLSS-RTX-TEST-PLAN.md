# RTX 机器上的 DLSS 集成测试与开发计划

基线：0.2.1 R4；更新于 2026-09-28。**以下是真机待执行计划，原开发机没有 NVIDIA 显卡，尚未完成任何 RTX 推理验收。** 普通内录/安装器的既有验证见 `HANDOFF.md` 与 `packaging/RELEASE-VERIFICATION.md`。

## 准备与固定实验条件

先装好完整 AA 与本 MOD，用普通内录生成一个 5–20 秒、带声音、移动角色、中文对白/渐显、镜头切换的测试视频。测试剧情资源必须完整，不能使用已知 HTTP 404 素材来评判增强效果。每个阶段独立输出目录和文件名，不覆盖输入。

记录 Windows/驱动版本、所有 DXGI GPU 名称和厂商 ID、显存、显示器分辨率、AA EXE/MOD DLL/安装包 SHA-256、DLSS5Tool 版本和 DLL 哈希、输入视频 hash、参数、用时、峰值显存与结果。NVIDIA 显卡连接到核显输出的笔记本也应检查，不能只读 Unity 当前渲染设备。

上游基线为 [DLSS5Tool v2.3.3](https://github.com/banbanzhige/DLSS5Tool/tree/v2.3.3)，固定提交 `e53e0b4d5139126e7b31866ed6b3653bbba0f449`；源代码保存在 `bridge/vendor/dlss5tool`。GPU 系列附件来自 [上游 zip Release](https://github.com/banbanzhige/DLSS5Tool/releases/tag/zip)。这是本工程固定的来源快照；如重新查询发现上游变动，先评估并明确升级，不能自动接受新哈希或猜测参数。

准备完整 DLSS5Tool v2.3.3，设置 `[DLSS] ToolDirectory` 到它的根目录。**R4 的内置 FFmpeg 解决普通录制，增强桥接当前仍使用 `ToolDirectory/_internal/ffmpeg.EXE` 和同目录 ffprobe/GPU 库**，未自动改用 MOD 私有 FFmpeg。安装器可选依赖提示不是推理兼容性证明。

此次交接包已在 `依赖与上游/DLSS5Tool-v2.3.3-win64` 附上完整 `_internal`、主程序和许可证/说明，省去重新收集本基线运行依赖；原用户设置/任务历史没有附入。`依赖与上游/DLSS5Tool-source` 是另附的上游源码快照。将便携工具移动到稳定目录后，启动 AA 一次生成配置、关闭 AA，在活动 profile 的 `azurearchive.recorder.cfg` 写入新路径，例如：

```ini
[DLSS]
ToolDirectory = D:\DLSS5Tool-v2.3.3-win64
```

保存并重启 AA，再打开内录 UI 选择超分/补帧/神经渲染参数。当前 UI **没有工具目录选择控件**，“选择文件夹”仅设置这一次成品输出目录；不要通过那里配置 ToolDirectory。随附 `_internal/nvngx_dlssnr.dll` 对应 **40 系**，30/50 系仍需首次下载或手工放入各自哈希匹配的缓存。即使文件都随包存在，也应运行预检和实机各阶段，不能将“已附依赖”写作“GPU 已验证”。

| 功能 | 运行组件/系统依赖 |
| --- | --- |
| 普通 MP4 内录 | MOD 私有或可用外部 FFmpeg，CPU x264 或 QSV/NVENC/AMF；与 DLSS 开关独立 |
| 任何增强任务 | 完整上游 `_internal`、FFmpeg/ffprobe、所选系列 `nvngx_dlssnr.dll` 哈希匹配、桥接依赖 |
| RTX Video 超分 | `vsr_host.dll`、`nvngx_vsr.dll`、可用 NVIDIA 驱动和相关上游依赖 |
| DLSSG 补帧 | `dlssg_video_worker.exe`、`nvngx_dlssg.dll`；NVOFA/CUDA 驱动 API，源码实际加载系统 `nvcuda.dll` 与 `nvofapi64.dll` |
| 神经渲染 | `dlssnr_host_v2.dll`、匹配系列的 `nvngx_dlssnr.dll` 及其上游运行依赖 |

安装版含 Python 冻结环境，常规运行无需单独 Python。源码桥接依赖 numpy/OpenCV-headless/PyAV/imageio-ffmpeg/Pillow，不要求 Torch、CUDA Toolkit 或下载“模型包”。CUDA 驱动 API 是否可用要在 RTX 机实测，不能用“已安装 CUDA Toolkit”代替驱动/NVOFA 检查，也不要从未知站点下载散装 DLL。

完整上游工具包含原生依赖，当前交接源码中的 vendored Python 不是全部原生 DLL 的源码/安装替代品。缺文件应按明确错误补齐对应上游发行包，不能通过复用其他系列库绕过检查。

若只是 MOD 集成测试，使用随包原生库即可。若计划重编上游 C++ host/worker，额外准备 Visual Studio C++ x64 Build Tools/Windows SDK、NVIDIA DLSS SDK、RTX Video SDK 1.1；快照没有 `third_party` SDK。上游 `build_dlssg_video.bat` 还依赖历史 `tmp/.../TASK.md` 前置标记，应先修整为受控可移植构建入口并记录补丁。这些属于重编上游的准备，不是安装普通 MOD 或运行冻结桥接的必需条件。

## 显卡分类与系列配置必须一致

DXGI 枚举所有非软件适配器；仅 VendorId `0x10DE` 是 NVIDIA。型号正则识别 `RTX 20xx/30xx/40xx/50xx`（含 D、Laptop 等后缀）。无 NVIDIA、RTX 20、未识别专业/其他型号：红色“暂不支持增强”，仍允许普通 MP4。核显 + 单一 NVIDIA 系列可用；不同系列多张 NVIDIA 卡当前拒绝自动增强，以免上游自行选卡和 DLL 不对应。不要擅自将未收录型号归到“近似系列”。

| 系列 | 固定上游附件 | `nvngx_dlssnr.dll` SHA-256 |
| --- | --- | --- |
| 20 | 无适配，暂不支持 | 无；不能填 30/40/50 的值 |
| 30 | `30.-310.8.SF-v2.zip` | `6eb209e764f39872625debd6abaf45e2bb6322f6f270f781f70c059ae30b3927` |
| 40 | `40.zip` | `ceb6432f6fbdf44d886014bcd47241932bf8b67439feef9bbdd0961436662650` |
| 50 | `50.zip` | `e16bcf15e16e13f527491cdf7845b2fe6521a738d8f7c9c721866a8496e1fc8e` |

ZIP 哈希分别为：30 系 `01626f7ffe14c54928e9b2eaa09baf1886fa9200b247bbb51895f935f301886c`；40 系 `3fdeb4f3b44165bfd31d98e288a46dc16eedd40168819c8ed6f73c45fc92c7a1`；50 系 `e730e1ea95b0a4f6420b9b1bbb1c2948cbb1bc9241aebfdeb10efef8a3bc906a`。

这些值同时存在于 `src/GpuProfiles.cs` 与 `bridge/gpu_profiles.json`（自动下载 ZIP 值在 C# 内），升级时同步修改并重建冻结桥接。当前解析顺序为本 MOD `gpu-runtimes/rtx<系列>`、上游 `mods/dlss/rtx<系列>`、上游 `mods`、上游 `_internal`，任意位置只有哈希匹配才接受。

缺少库时 UI 自动从固定附件下载，验证 ZIP 和 DLL 后放入本 MOD 缓存；已有错误哈希文件不覆盖。下载后还检查选中剧情/工程未改变，避免下载期间切换剧情却录错内容。源码模式也可运行 `install-gpu-runtime.ps1 -Series 30`（按实际 30/40/50 选择）。下载客户端 15 分钟超时；不要将首次联网下载耗时计作纯推理性能。

**匹配 `nvngx_dlssnr.dll` 是系列身份校验，并非 VSR、DLSSG、神经渲染三者全兼容保证。** 当前桥接即使只做超分或补帧也要求该系列 DLL 存在并匹配，测试应记录这一限制，不能据此声称神经渲染已经执行。

## job 协议与实际调用

`bridge/enhance.py` 严格只接受以下 9 个字段，拒绝未知字段、错误类型、非 MP4、输入输出同名、已有输出和 DLL 不匹配：

```json
{
  "input": "D:/AA测试目录/Recordings/rtx-test/recording.mp4",
  "output": "D:/AA测试目录/Recordings/rtx-test/enhanced.mp4",
  "tool_directory": "D:/DLSS5Tool-v2.3.3-win64",
  "runtime": "D:/AA测试目录/mods/AzureArchiveRecorder/runtime/gpu-runtimes/rtx40/nvngx_dlssnr.dll",
  "series": 40,
  "gpu": "NVIDIA GeForce RTX 4070 SUPER",
  "scale": 2,
  "multiplier": 1,
  "neural": false
}
```

这是格式示例，GPU/路径必须换成实际机器，不能照抄 40 系冒充检测结果。每次使用新的 job 目录；上游 `dlss-diagnostics` 用 `exist_ok=False` 创建，重复使用旧目录会失败。

无 GPU 文件检查：

```powershell
& .\RecorderMod\.venv\Scripts\python.exe .\RecorderMod\bridge\enhance.py 'D:\实际任务目录\enhancement-job.json' --check
```

冻结版 `EnhanceHost.exe <job> --check` 也允许只检查；真实增强必须由 AA 启动并通过主进程/安装位置校验。不要为了独立命令行测试伪造 `AA_RECORDER_HOST_PID` 等环境变量；开发阶段需要直接推理时运行源码 Python 桥接，再在 AA 启动链复测冻结版。

桥接设置全新 `DEFAULTS` 副本，不读取/改写用户上游 GUI 设置；覆盖 `dlss_runtime`、`host_backend="v2"`、`host_auto_fallback=false`、`guidance_mode=0`、`hdr_mode=false`、`output_view=0`、`output_mix=1.0`、`output_container="mp4"`、`quality_profile="high"`、`rate_control="quality"`、`render_gpu="auto"`，然后调用：

```python
export_video(input, output, multiplier=multiplier, scale=scale,
             enhance=neural, settings=config, cancel=cancel,
             progress=progress, log_dir=job_dir / "dlss-diagnostics")
```

当前普通内录为 SDR；桥接不是 HDR 认证入口。上游标准补帧 worker 实际 argv 为：

```text
dlssg_video_worker.exe <nvngx_dlssg.dll所在目录> <诊断目录> <宽> <高> <倍率> sdr
```

上游也支持 `hdr-coded` 和显式实验 GPU 输出 worker，但当前桥接没传 `gpu_export`，不要把那套参数混入普通链。worker 使用二进制管道传帧/光流，标准握手魔数为 `0x31474746`，返回适配器 LUID；NVOFA 的 CUDA LUID 必须与 worker 一致，否则明确停止。

## 分阶段执行，不一次全部打开

| 阶段 | 参数 | 必须观察的结果 |
| --- | --- | --- |
| A 普通内录 | 关闭录制后增强 | 内录按钮/中文显示正确；仅进度屏；本机物理分辨率、有声 MP4、时间轴与窗口恢复；记录 NVENC 是否真正选中或回退 CPU |
| B 预检/不支持 | `--check` 与型号分类测试 | 运行库/依赖错误明晰，20/未识别/非 NVIDIA 红色不支持，普通录制仍可用；此阶段无推理 |
| C 仅超分 | `scale=2,multiplier=1,neural=false` | 输出宽高各 2 倍、fps 与时长保持、音频保留；不是普通 resize 冒充 RTX VSR；日志确认 VSR 初始化/执行 |
| D 仅补帧 | `scale=1,multiplier=2,neural=false` | 输出尺寸保持、fps 2 倍、总时长与音频保持；检查中间帧运动/字幕，确认 worker/NVOFA 成功且 LUID 一致 |
| E 仅神经渲染 | `scale=1,multiplier=1,neural=true` | 输出尺寸/fps/时长保持，host v2 与匹配 DLL 工作；单独观察画风、文字和人脸改变，不默认开启 |
| F 组合 | 先 `2,2,false`，再按需加 neural | 比较单项结果；无音画漂移、掉帧、泄漏；短片完成后再长片、多次连续任务 |
| G 边界/实验 | 4× 超分，3×/4× 补帧，取消/错误 | 明确资源上限与实验限制；不自动改参数掩盖失败；取消仍保留原 MP4并释放任务进程 |

同一台卡只覆盖其实际型号/系列，不对未测试 30/40/50 宣称全支持。可以做不同型号字符串的分类单元测试，不能称作硬件实测。

上游限制最终偶数宽高为 128–8192。原 3120×2080 输入 4× 会超过 8192，必须明确拒绝；不能无声改小录制分辨率，因为用户要求录制依据本机分辨率。若要测高倍上限，用独立小分辨率素材测试桥接，并在证据中区分于 AA 默认捕获。

上游会检查预计输出峰值加 15 GiB 磁盘余量，并按画面大小估计显存余量；拒绝时记录原错误。3×/4× 为上游实验路径，`nvngx_dlssg.dll` 必须匹配额外固定哈希 `135eaf0733c1e37381a8c28abcf7a862404a54132b81787c04e35d09efc5e36f`，与上表的 `nvngx_dlssnr.dll` 不是同一个库。上游警告明确其当时仅在 RTX 4070 SUPER 和固定库实验，可能有生成帧时间偏差；本 MOD 未扩展这个认证范围。

## 视频、画质、音频验收

每个成功阶段至少保存 ffprobe JSON、完整 FFmpeg 解码结果、日志和参数。输出应符合阶段预期尺寸/fps，音频存在且非全静音，正常时长不因补帧倍率而变短。首/尾帧、切镜头保持和边缘计数按上游策略检查，不凭 UI 100% 或文件存在判成功。

人工对比运动片段、静态字幕、渐显文字、遮挡边缘、面部与重复纹理；确认补帧中间帧不是全部重复输入。记录 ghosting/变形，不能把“编码正常”写为“画质无伪影”。VSR 和神经渲染应由对应执行日志与画面对比共同确认，不只用像素变化判断。

普通录制可使用 `tests/verify-runtime-output.ps1`；增强输出尺寸/帧率改变，需按增强 job 的 scale/multiplier 校验，不能原样套用普通录制断言。比较原片/增强片音频时间位置，以及对白或切镜头的实际时间。若新增测试脚本，优先验真实约束而非简单重复实现表达式。

## 取消、超时与异常要实测

- 面板“取消增强”写 `<enhancement-job.json>.cancel`；Python 每 0.25 秒观察一次，设置上游取消事件。原始 MP4 已发布，应始终保留。每次新 job 避免遗留 `.cancel` 文件立即取消下一次实验。
- 上游补帧管道单次操作默认 120 秒超时，超时/取消会终止 worker。神经 host 初始化/命令超时 120 秒；VSR 根据分辨率计算超时（通常至少 60 秒，动态上限部分为 600 秒）。这些是局部等待，不能宣称整个增强任务有统一时限。
- **当前 C# `WaitForExitAsync` 没有整体增强超时。** 若遇取消长时间无效、无进度或驱动挂起，这是优先修复项：增加明确状态/受控子进程树终止与原片保留，再验证；不要只延长超时或跳过错误。AA 退出时先写取消，等待 2 秒后尝试 `Kill(true)`，也需 RTX 实测子进程是否全部回收。
- 单独模拟缺 DLL、错误系列 hash、已存在输出、显存/磁盘不足、worker 异常退出，以及连续取消重开。仅在测试副本中临时移动测试用依赖，恢复后校验哈希；不修改用户上游 `_internal`、全局 GPU 驱动或系统 DLL。
- 下载运行库过程有 15 分钟网络超时但没有完备的 UI 取消链；如开发完善，应保持哈希、禁止覆盖错误已有 DLL、下载后剧情一致性检查，并清理本次专属 `.partial`，不扩展成全目录清理。

成功结果为 `enhancement-result.json` 中 `ok=true` 且上游 `status=complete`，并有完整通过验收的输出。失败保存 `enhancement.log`、result traceback、`dlss-diagnostics/native.log` 等；冻结版需验证 multiprocessing 子进程在导入 native host 前完成路径重定向，不能仅凭源码 Python 成功就宣称安装版成功。

## 开发闭环与完成标准

先用源码桥接缩小单项 GPU 问题，再重建 `EnhanceHost`、重新正式打包和安装，最终从 AA 原生 UI 验证。构建方法见 `HANDOFF.md`。不要直接修改 vendored 上游而不记录补丁原因和固定版本；不要修改 AA 主程序/原工程作为兼容捷径。

完成报告须给出：实际 GPU/驱动/系列及 hash、每阶段通过/失败/未测、普通录制 NVENC 状态、输出参数/用时/音画检验、已知画质问题、取消/超时和连续任务结果、最后真正验证的安装包 hash。发布继续加壳并测保护制品；未加壳包供调试。没有测过的型号或功能保留“未验证”，不将本计划表自动改成通过表。
