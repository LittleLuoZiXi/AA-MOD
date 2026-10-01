# AzureArchive 内录 MOD V1.2

当前版本为 **1.2.0**，基于仓库 `mods/recorder` 分支的 V1.1（提交 `5002fc8463b4c4b6a6063a8de9938ad2385d415c`）。本次修复工程中 AUTO 选项倒计时结束后仍停在选项节点、持续录制的问题。没有修改 AA 主程序，也没有修改 DLSS 模块逻辑。

## 使用

保存剧情并关闭 AA，运行 V1.2 加壳安装 EXE，选择 AA 根目录。已有内录时确认覆盖升级；支持有效的 0.2.1、1.0.0、1.1.0 安装。默认不勾选 DLSS，此时仅安装内录，不检查显卡、不下载 DLSS 组件。FFmpeg 会自动检测，找不到时提示是否部署随包私有版本。

进入“剧情”，点击“入场”旁的“内录”，或按 F8/F9。选择本次输出目录（留空使用 Windows 视频文件夹），开始后只显示录制进度。录制按本机显示器分辨率、固定视频帧率输出带内部混音的 MP4；F10 可停止并保存。

有分支的工程先标记 AUTO 默认选项。V1.2 保留 AA 的默认选项、原生倒计时、退出动画和分支回调；不会替用户猜测未标记 AUTO 的路线。未设置默认项或存在循环的剧情仍受 MaxMinutes 限制。

安装和卸载仅处理本 MOD 收据中归属明确的文件。其他 MOD、源码工程、剧情、成品视频及用户修改过的文件保留。卸载入口为 `mods/AzureArchiveRecorder/卸载内录MOD.exe`。

## 实现与维护

- `src/Plugin.cs`：录制生命周期、工程快照编译、固定视频时钟、离屏相机、内部音频及停止恢复。
- `src/RecordingChoices.cs`：在原生 AUTO 倒计时触发默认按钮时，用 `SelectionElement.OnSelect()` 代替依赖鼠标上下文的模拟按压，避免离屏模式下的 `MXButton.OnPress` 空引用；并保留倒计时完成后的受限补提交检查。AA 负责禁用按钮、退场动画和分支推进。
- `src/ChoiceProbe.cs`：仅 `--aa-recorder-choice-probe` 启用的诊断日志；不替代选项执行逻辑。
- `src/NativeRecorderUi*.cs`：原生风格设置、进度与教程；`Encoder.cs`：FFmpeg；`DailyExportNaming.cs`：日期序号。
- `bridge/` 及 DLSS 相关源码继承 V1.1，本次未改；本机不具备 RTX，未做 DLSS 测试。
- `packaging/build-release.ps1`：Obfuscar 混淆与压缩加密封装 MOD、安装器、卸载器；不保证不可逆向。构建依赖与复用冻结 runtime 见 `packaging/BUILD-V1.2.md`。

开发编译使用 PowerShell 7 执行 `build.ps1`，`-Install` 安装开发 DLL。正式交付必须使用保护构建脚本；不要将开发 DLL 当成加壳版本发布。

旧 R4/V1.0/V1.1 文档及测试作为历史参考保留；当前版本行为与验证以 `V1.2-CHANGELOG.md`、`V1.2-VERIFICATION.md` 和本 README 为准。GitHub 的每个版本保存为独立 baseline，保留历史目录，不覆盖旧基线，不合并到 main。