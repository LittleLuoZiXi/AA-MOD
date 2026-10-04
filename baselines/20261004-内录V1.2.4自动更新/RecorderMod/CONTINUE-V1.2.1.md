# V1.2.1 继续工作入口

更新于 2026-10-02。先读本文件，再读 [当前实现与证据](V1.2.1-NATIVE-TIMING-MENU.md)、[README](README.md)。当前完整 30/60、MENU/媒体检查及 E 盘正式安装均已完成；用户已暂时接受与参考的时长差异并准备自行测试，计时逻辑不再继续修改。

## 当前状态

- 当前保护构建：`protected-V1.2.1-20261002-183029-e93745ac`。D 盘隔离 AA 与 `E:\AzureArchive_100_1001` 均已安全安装 201 个载荷文件；D 的 7,785 个其他文件及 4 个 profile、E 的 511 个其他文件及 **7 个 profile** 字节不变，AA 主程序未改动。E 安装证据：`D:\test2\install-verification\V1.2.1-title-hotfix\install-20261002-185551-631-c178ec55\verification-result.json`；它验证实际 InstallCore 安装，不代表安装器 GUI 或安装后 E 盘启动已实测。
- 当前插件 SHA-256：`EF7C58DAFF117870A9726C1617B2FF955C15EE5EBD86256D1B4932B801B4C16F`；安装器 SHA-256：`9FC4190CC9F0E92C9B3F3D6A93BB4A314C0A17A18C891D393186AB8578F2FAD3`。载荷及保护记录以该构建的 `build-proof.json` 为准。
- 当前源码：直接模式内部统一 60 FPS 采样，在首次编码前筛到所选成品 FPS；AA 保持原生运行帧率，不能按成品 FPS 锁 AA。音频按原生源间隔采集，不变速、不裁剧情；`InputFrames` 为输入帧，`Frames` 为输出帧。
- 普通合成固定 60 Hz，首图、标题和背景等待仍真实绘制；双纹理保留最近完成的画面。MENU/AUTO 已记录的 inactive 控件树走早退，重新激活时完整扫描，合成前保持隐藏检查。
- 时长统计默认关闭，每次申请开启均确认；开启后仍完整原生预演、冻结预计值、按保存步长重放，保留 ±1 秒自然结束容差。
- 编码队列为统一 FIFO：3 张等待图像加 1 张 active 图像，PCM 独立 12 秒字节限额且包含 active 块。完成证据包含图像/PCM 队列等待、GPU 强制等待、源长帧及只读 clock-clamp 统计。

## 已验证范围与未完成项

- 真实编码回归 **186/186**、纯队列 **81/81**、清理 **34/34** 通过；编码测试覆盖 60→24/25/30/50/60 的首末帧、逐帧来源及 PCM 字节一致性。这不等于 AA 完整剧情验收通过。
- 当前保护包两场景 focused 验证通过：连续 AUTO 直接 30 FPS、普通工程预演 60 FPS。证据：`D:\test2\evidence\native-timing-final-20261002\protected-focused-20261002-183428-106-460ffe03\suite-result.json`。
- 当前同一保护 DLL 的完整直接 30/60 均自然完成、退出码 0、配置恢复且未启用增强。30 FPS：12,708 帧、423.6 秒、输入 25,416 帧、本次原生 423.5892185156 秒；60 FPS：25,502 帧、425.033333 秒、输入 25,502 帧、本次原生 425.0291892726 秒。分别比参考 419.852771 秒长 **3.747229/5.180562 秒**，两片差 1.433333 秒。
- **用户已暂时接受上述差异，等待手动验收，不再把它列为当前交付阻断。** 成片相对各自本次原生经过时间均小于一个输出帧；这不等于精确同长或 ±3 秒通过，也不能把跨运行差异全部归因于 AA 或只归因于采样。运行分别为 `20261002-183716-854-Scenario-30fps-6d4aa0`、`20261002-184526-382-Scenario-60fps-246b4d`。
- 无 ChoiceProbe 的普通剧情入口 `20261002-185403-534-catalog-entry` 已通过，配置恢复。当前完整两片 12,708/25,502 帧的 MENU 检查均为 0 命中；完整音视频解码退出码 0、错误日志 0 字节，音频非静音，180/350 秒四张抽图可读。不作全程听觉同步承诺。证据见 `D:\test2\evidence\native-timing-final-20261002\fixed60-menu` 与 `fixed60-media\fixed60-media-summary.json`。
- 历史队列包 `180102-b03a0551` 完整 30/60 为 434.766667/423.85 秒，相对参考长 14.913896/3.997229 秒；历史负结果保留，不能当作当前包结果。
- 30/60 FPS 的尾部取整小于一个输出帧；24/25/50 还有小于 1/60 秒的中间取整项，总上界小于 `1/输出FPS + 1/60` 秒。

## 换电脑需要补齐的依赖

源码 ZIP 不含宿主 DLL、大型工具二进制、`.venv`、运行输出、用户工程或媒体；**保留全部 6 张内置教程 PNG**。以下依赖需要在新电脑按本地路径补齐，不要直接套用本机 D/E 盘路径。

1. 同版本 AA 宿主及其 `dotnet`、`BepInEx/core`、`BepInEx/interop`；正式构建从这些目录读取引用和宿主身份。需要 Windows、PowerShell 7 及 Windows .NET Framework `csc.exe`。
2. **Obfuscar 2.2.50 完整工具目录**放到 `packaging/tools/obfuscar/tools`，包含 `Obfuscar.Console.exe` 和配套依赖，不能只复制一个 EXE。
3. 固定版本 `ffmpeg.exe`、`ffprobe.exe` 放到 `packaging/ffmpeg-vendor`。可从同一交付安装包安装后的 `mods/AzureArchiveRecorder/runtime/ffmpeg` 复制，保留源码附带的许可与来源资料；必须核对 `packaging/build-release.ps1` 内的固定 SHA-256，不以其他版本替换或改哈希绕过检查。
4. 可复用同一交付包安装后的整个 `mods/AzureArchiveRecorder/runtime`，作为 `-FrozenRuntimeDirectory`；构建脚本会核对冻结内容与当前源码身份。
5. 在源码根重新创建 Python **3.12** 的 `.venv`，安装 **PyInstaller 6.11.1**。这里只用于冻结运行环境的只读身份校验；无需重冻 DLSS/helper，不要安装或下载 DLSS 组件。

## 构建与验证入口

`src/AzureArchive.Recorder.csproj` 版本已从 1.1.0 更正为 1.2.1，但仅供开发。正式交付入口始终是 `packaging/build-release.ps1`，使用动态 Roslyn 编译及完整保护链；不能用开发 DLL 替代保护包。

```powershell
# 在源码根运行，三个路径均替换为新电脑的真实路径。
& .\packaging\build-release.ps1 `
  -GameRoot '<同版本 AA 根目录>' `
  -OutputDirectory '<独立交付目录>' `
  -FrozenRuntimeDirectory '<同交付包安装后的 runtime 目录>'

& .\packaging\verify-r6-payload.ps1 -BuildProof '<本次新构建的 build-proof.json>'
```

每次必须 **fresh 全量保护构建**，包括内核、loader、安装器及卸载器；不得复用旧 stage，也不使用 `-InstallerOnly`。详见 [正式构建说明](packaging/BUILD-V1.2.1.md)。

测试命令与隔离副本规则见 [tests/README.md](tests/README.md)，纯测试入口见 [修订记录的回归入口](V1.2.1-NATIVE-TIMING-MENU.md#回归入口)。搬迁后隔离标记中的绝对 root 必须匹配实际副本；不要给正式安装目录伪造测试标记。完整计时对照使用相同剧情、AUTO 和设置，不加截图/逐字日志负载，不与构建、安装或其他 AA 进程并行。拒绝与取消用例的预期非零退出码不等于测试失败；超时也不等于自然结束。

## 必须保留的约束

不上传 GitHub；不修改 AA 本体或原生等待值，不做视频裁剪/加速补偿，不修改、下载或测试 DLSS。用户已确认 152 条缺语音属于 AA 既有情况，本轮忽略，不作为 MOD 阻断。保留现有用户工程和其他 MOD；安装/卸载遵守收据归属。未经实际证据，不宣称最终完整 30/60、正式 E 盘安装或全部视觉验收通过。
