# V1.2.4 开发交接

V1.2.4 已由用户完成验收，本目录是本次正式发布的源码。仓库为 LittleLuoZiXi/AA-MOD，分支为 mods/recorder，基线沿用 baselines/20261004-内录V1.2.4自动更新/RecorderMod。发布记录以 GitHub Release 和同基线的 delivery-checks.json 为准，不再以早期本地交付文档中的等待状态或旧产物摘要为准。

## 功能与结构

本版在设置记忆基础上提供自动更新：打开内录面板后台检查，同意后下载，显示进度并支持暂停、继续、终止；文件安装完成后只显示 OK，用户确认后重启 AA。帧率、DLSS 选项和输出位置继续记忆；提前统计总时长仍每次默认关闭。

主要模块：
- src/Plugin.cs：录制生命周期、工程快照、计时、相机与音频、状态恢复，以及更新期间的录制限制。
- src/NativeRecorderUi*.cs：原生风格入口、设置、进度、教程、时长确认和自动更新界面。
- src/RecorderUpdateClient.cs：固定仓库清单、版本、宿主摘要、来源和大小限制。
- src/RecorderUpdateDownload.cs：异步下载、暂停续传、终止、Range/ETag 检查与 SHA-256 校验。
- src/RecorderUpdateSession.cs：再次核对 ZIP、创建独立临时任务、启动助手、读取状态及写入用户确认。
- packaging/UpdateInstallerCore.cs：事务安装、收据和 profile 版本更新、旧文件归属与回滚。
- packaging/UpdateHelper.cs：核对原 AA 进程、执行更新、等待 OK 和正常退出，再启动同一目录的 AA。
- src/RecordingPreflight.cs、Recording*Clock.cs、*Timeline.cs：可选完整预演、时间表、视频采样和音频推进。
- src/Encoder.cs、EncoderWorkQueue.cs、FrameReadback.cs：编码队列、GPU 图像读取和 MP4 输出。
- bridge/、src/DlssComponents.cs 及相关 DLSS 文件：增强桥接与组件识别，沿用已有实现。

自动更新不改录制、采样、音频、AUTO 分支或 DLSS 算法。默认普通内录直接开始，开启提前统计后才进行完整预演。具体既有录制实现可结合历史版本交接阅读；历史文档的发布状态和摘要仅代表其记录时间。

## 更新边界

生产清单固定在 UPDATE-PROTOCOL-V1.2.4.md 记录的仓库地址。只有适用于当前宿主的更高版本才提示；用户拒绝不影响普通内录。下载和安装分别校验完整包及文件摘要，拒绝不明路径或归属。

助手只修改内录 MOD 已拥有的文件、收据及已启用 profile 的内录版本，保留 cfg、其他 MOD、用户工程和视频。新版本使用独立目录，当前占用的旧版本文件保留在归属记录中。只有匹配的 OK 确认及原进程正常退出才重启，助手不强制结束 AA。

启动更新助手以及重新启动 AA 时，分别从子进程环境中移除 DOORSTOP_DISABLE；不修改父进程、用户或系统环境，其他环境变量和工作目录继续保留。

## 构建

使用 PowerShell 7 调用 packaging/build-release.ps1，GameRoot 只用于读取兼容宿主引用，OutputDirectory 使用新的构建目录，FrozenRuntimeDirectory 可指向已校验的本 MOD runtime。构建和保护流程见 packaging/BUILD-V1.2.1.md；正式发布安装器必须来自既有保护构建流程，按 RELEASING.md 复制为公开文件名。

依赖包括 Python 3.12.14、PyInstaller 6.11.1、Obfuscar 2.2.50、固定 FFmpeg 与 AA/BepInEx 引用。大型依赖不放入本源码压缩包；不要把开发 DLL 或内部验证 EXE 当成正式发布资产。

本次验收产物摘要：
- 内录MOD1.2.4版本安装包.exe：255266304 字节；SHA-256 1269E0BF1CD63EA6C0E437EDFB517DEDF3828C5AA60B294B2D8CD7410185D9F7。
- AARecorder-V1.2.4-update.zip：255740337 字节；SHA-256 161C01C77A3CC68E7F20343F9AC143232CF421F7AC83F1ED9D1EED96354676AD。
- 插件 DLL：FC0E7D360A0A58343A45A6B804A7B27571D2A02EF645597B906AF96135DA9889。
- 更新助手：D084C3C0A8A562A188641EECF5561ACC2C59E066426F7B312AA6A7BAD2CEEC86。

## 已有验证与后续开发

已有检查覆盖下载协议、会话边界、助手和安装归属；检查结果摘要见 packaging/UPDATE-VERIFICATION-V1.2.4.json。真实隔离 AA 中的 33 项更新界面检查通过，中文面板、下载进度、暂停/继续/终止和唯一 OK 均有确认。环境传递后的实际插件加载也已核验。

这些开发检查包含内存下载响应、合成安装夹具和隔离宿主，不能单独视为一次完整线上更新。正式流程已由用户另行验收。本机无 NVIDIA 显卡，未执行 RTX/DLSS 测试，也未因本版重新测试所有长剧情。

后续版本遵循 RELEASING.md：代码、构建、必要验证、完整源码和同构建资产完成后，再最后发布 stable.json。发布时保留每版对应的安装 EXE；面向用户的名称固定为“内录MOD{version}版本安装包.exe”。
