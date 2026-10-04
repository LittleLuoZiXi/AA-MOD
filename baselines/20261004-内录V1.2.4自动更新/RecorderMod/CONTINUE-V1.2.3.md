# V1.2.3：导出设置记忆

## 需求与边界

2026-10-04 在用户验收的 V1.2.2 上开发。记住内录面板的帧率、DLSS 总开关、超分倍率、补帧倍率、神经渲染和成品输出目录。修改当下保存，关闭面板、切换故事/工程、重启 AA 均复用同一配置方案的选择。用户明确“提前统计总时长”不需要记忆，此项仍每次关闭并在开启前确认。

不改 AA 本体，不改变 V1.2.2 录制时钟、采样、音频、分支自动推进或 MENU 隐藏逻辑。不修改 DLSS 算法，不在无 NVIDIA 显卡的本机下载或执行 DLSS。安装包始终使用现有保护构建流程；保护不能保证不可逆向。当前尚未上传 GitHub。

## 工作位置

- 开发源码：D:\test2\开发\V1.2.3\RecorderMod
- 上一版只读基线：D:\test2\基线\V1.2.2\RecorderMod
- 本次隔离 AA：D:\test2\开发\V1.2.3\AA验证
- 本机正式新版 AA：E:\AzureArchive_100_fix
- 正式 AA 已从旧 Downloads 路径移动。本次独立校验原 MOD 的 201 个文件摘要后，修正该机器安装收据的 Root，再用原安装器核心更新到 V1.2.3；安装器本身的路径校验不变，未触碰旧目录。更新前插件和所有 profile 已备份于本次部署记录。

## 实现

沿用 BepInEx 当前 AA 配置方案下的 azurearchive.recorder.cfg。不同工程不会另建导出偏好。不同 MOD 配置方案的设置互相隔离。

- Recording.FrameRate：复用已有配置（24/25/30/50/60，默认 30）。
- Recording.ExportDirectory：新增成品目录；首次默认 Windows 视频文件夹。与内部素材日志用的 Recording.OutputDirectory 区分。
- DLSS.RememberedEnabled：新增保存的开关，默认 false。不读取历史遗留的 DLSS.Enabled，避免把旧残留数据误当成用户启用选择。
- DLSS.SuperResolution、FrameMultiplier、NeuralRendering：复用已有配置。
- RecorderPlugin.SavePreference：实际用户操作修改 ConfigEntry 后明确保存。磁盘写失败时提示用户“保存设置失败，下次启动可能无法保留”，本次内存值仍可使用。
- NativeRecorderUi.ToggleSettings：读取目录和用户选择，不再每次硬重置。总时长仍 ResetTimingChoice。
- ApplyFolderSelection：真实文件夹选择完成回调；取消不修改，成功规范化完整路径并立即保存。诊断的临时 OutputFolder 覆盖不持久化。
- 保存目录为空时回退 Windows 视频文件夹；格式无效时提示并临时回退，不篡改原保存值。暂时不存在的合法目录保留，到开始导出时沿用原创建/写入检测并报告实际错误。
- 不可用 GPU/组件只将本次有效 DLSS 状态关闭，不抹掉 RememberedEnabled。按钮禁用和红色原因提示继续生效，开始内录和增强前的重复校验不变。组件在已打开面板内恢复时不自动重新开启；下次主动打开面板才恢复保存选择。

## 验证范围

本次新增 PreferencesDiagnostics.cs 与 run-preferences-smoke.ps1，使用独立 AA 测试身份和合成工程，实际启动多次验证持久化；不可只用同进程内存证明重启保存。源码中 opt-in 诊断默认不运行。

不重复用用户的长剧情跑旧版速度基准，因为本次没有更改时钟/采样。DLSS 偏好在不支持机器上用隔离配置模拟历史选择，验证不丢失参数且不会启动增强；不把此结果表述为 RTX 实机 DLSS 测试。

最终验收（2026-10-04）：
- 实际运行最终加壳 DLL，两次进程分别 54、59 项，共 113 项断言通过。正常退出；进入两个独立合成工程、重启恢复 60 FPS 后改为 30 FPS并再次保存都通过。
- 保存中文路径、取消选择不覆盖、只读配置错误提示及成功重试清旧错误、总时长确认与重开关闭、旧 Enabled 残留不启用 DLSS均通过。
- 真实 Intel 显卡保留 4 倍超分、3 倍补帧、神经渲染与开启偏好，但有效 DLSS 关闭，按钮禁用、原因红字；没有伪造 GPU，没有下载/执行 DLSS。
- 测试前后 175 个正式用户文件摘要完全一致。最终 PNG 已视觉检查中文、控件状态和目录显示。
- 安装/卸载 Core 67 项回归通过；冻结桥接运行时 42 项验证通过；载荷 201 文件/202 ZIP 项全部摘要匹配；保护 IL 检查 4 项通过。
- 正式 E:\AzureArchive_100_fix 已更新同一加壳 DLL，201 个已装文件逐项匹配；13 个受保护的其他 MOD/程序文件不变，4 个 profile 文件中只有 modconfig.json 的内录版本由 1.2.2 更新为 1.2.3。原 60 FPS 设置保留，不安装 DLSS。安装前完整备份见 部署记录\formal-installation-result.json。
- 未再重复长剧情速度/音画测试；本次时间、采样、编码、分支、MENU及 bridge 文件与已验收基线保持一致。
- 加壳 DLL SHA256：2FB5E3052423E059E6DBFCFD25B324344A075327874AD28275DB5B6C0BA1FB89
- 加壳安装器 SHA256：06D81933A62A23B8DBDD71145FE16396DA67990E543371A0B7CA2F5C33B67853

证据：交付/验证记录含 runtime-preferences-summary.json、截图、formal-installation-result.json、build-proof.json、payload-verification.json 和 ACCEPTANCE-STATUS.json。完整逐阶段日志在 AA验证/smoke-runs/20261004-181310-972-preferences。开发诊断首次因直接在编辑器再次 OpenProject 停住，已改为经原生 CatalogScene 切换；最终加壳运行采用修正后的诊断，产品逻辑不受此测试流程问题影响。

## 重建与交付

源码 ZIP 包含源文件、教程素材、合成用例与脚本；不含 .venv、下载工具、AA 本体、用户工程、测试视频或未加壳构建中间文件。工作目录保留完整依赖供本机继续开发。

用 PowerShell 7 调用 packaging/build-release.ps1，参数 GameRoot 指向相同版本 AA，OutputDirectory 指向新交付目录，FrozenRuntimeDirectory 指向已安装 MOD 的 runtime。此版本必须 fresh 完整构建，禁止 InstallerOnly 复用旧stage。冻结桥接未变；构建工具 Python 3.12.14、PyInstaller 6.11.1、Obfuscar 2.2.50，来源/摘要见 packaging/DEPENDENCY-RESTORE-V1.2.3.json；Python 锁定依赖见 packaging/dependency-verification/python-build-requirements.txt。FFmpeg/ffprobe 固定版本可从安装后的私有 runtime/ffmpeg 复制回 packaging/ffmpeg-vendor，build-release 验证固定摘要。

正式环境不要运行 PreferencesDiagnostics 的命令行 probe。测试驱动要求专用标记、独立应用身份和 workspace，仅隔离 AA 修改 company/product 的等长标识以重定向 Unity 用户目录；该测试宿主改动不属于 MOD、不会打进安装包。