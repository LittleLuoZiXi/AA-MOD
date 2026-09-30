# V1.0 DLSS 运行时禁用与原生 UI 验证

日期：2026-09-29。开发源码：`RTX开发测试/AA/RecorderMod`。唯一运行与写入 MOD 的验收副本：`RTX开发测试/V1实机验收/AA-UI-20260929`。未更新日常 AA 的 DLL、旧基线或 `安装交付/20260929` 制品。

## 运行行为

- 原生设置按实际选中显卡系列检查共同组件，以及匹配固定 SHA-256 的代际运行库。共同组件缺失、空文件、实际显卡代际库缺失或校验失败时，DLSS 总开关与超分、补帧、神经渲染四个按钮都灰色禁用。
- 支持的显卡缺组件时，红字精确显示：`缺少DLSS组件，相关DLSS功能不可用。`。不支持显卡保留具体原因，普通内录保持可用。
- 设置打开时、DLSS 开关和选项点击时、开始增强录制时、实际启动增强进程前都检查可用性；录制和增强预检重新计算 DLL 哈希。界面每秒检查文件元数据变化，避免每帧读取大运行库。
- 检测到组件消失会清除本次 DLSS 开启状态。组件恢复后仍需再次手动开启；旧设置中的倍率、神经渲染开启值以及 `Enabled = true` 不会隐式启用增强。
- 录制路径删除了 `EnsureRuntimeAsync` 调用和下载后自动恢复录制的流程，缺组件直接拒绝增强请求。下载由安装器负责；原有普通导出、六张教程素材与日期序号命名保留。
- 教程 DLSS 页仅调整两行说明，提醒通过安装器补齐组件，并保留实验补帧和神经渲染风格变化的说明。

## 文件改动

运行代码：`src/Plugin.cs`、`src/NativeRecorderUi.cs`、`src/NativeRecorderTutorial.cs`、`src/DlssComponents.cs`。

`DlssComponents` 只增加运行时检查 API、文件状态指纹和提示常量。原有 `ResolveTool`、`HasRuntime`、`MissingForInstall` 语义保持；运行时只要求实际显卡代际，原安装完整性 API 对官方三系目录仍要求三系齐全。

诊断与测试：`src/UiDiagnostics.cs`、`tests/DlssComponentsTests.cs`、`tests/test-dlss-components.ps1`、`tests/test-runtime-dlss-ui.ps1`。新的原生 UI 诊断仅在显式测试参数启用时执行；组件移出和恢复由外部验收脚本负责，不会由正常 MOD 操作文件。

插件属性与日志按主任务授权更新为 `1.0.0`；安装器、主 manifest、构建与加载器版本由主任务负责。

## 自动检查

共享帮助类通过 .NET Framework 4 编译；整个 MOD 通过 AA 的 .NET 6 / IL2CPP 引用编译。组件 API 共 7 组检查通过：

1. 只有共同文件时不放行缺失代际库。
2. 真实 RTX 50 代际哈希放行；旧安装完整性 API 仍要求其他两系。
3. RTX 20、未知、非 NVIDIA 编号及错误支持代际均不放行。
4. 七个共同组件逐个删除、置零均关闭可用状态。
5. 保持 DLL 长度、修改时间与创建时间的字节篡改，仍被全量预检哈希拒绝。
6. 旧工具位置和 profile 缓存仅在真实代际哈希匹配时可用。
7. 缺件检查不创建目录、不下载文件，并保留精确红字文本。

原始结果：`RecorderMod/test-output/dlss-components-20260929-215749/PASS.txt`。

## 实际 AA 验证

物理显卡为 RTX 5080。所有用例通过真实原生控件，均保留日志、逐条断言、截图与视频。证据根：`RTX开发测试/V1实机验收/验证记录`。

| 场景 | 界面断言 | 记录目录 |
| --- | ---: | --- |
| 启动前移除共同组件 | 25 | `runtime-gate-Missing-20260929-220136` |
| 主动开启后移除组件，再恢复 | 53 | `runtime-gate-ComponentLoss-20260929-220245` |
| 在 50 系位置放入真实 40 系 DLL | 25 | `runtime-gate-WrongGeneration-20260929-220420` |
| 组件完整、手动开关及六页教程回归 | 148 | `runtime-gate-Complete-20260929-220535` |
| 明确标记的不支持显卡模拟 | 13 | `runtime-gate-Unsupported-20260929-220705` |

合计 **264 条界面断言通过**。缺件场景同时直接请求增强录制，确认任务开始前被拒绝；绕过禁用外观调用原生点击监听也不能更改增强设置。动态恢复场景确认主开关恢复可点击，但仍为关闭。

五个场景都保留先前配置的 4 倍超分、4 倍补帧和神经渲染开启值，最终均正常导出普通 H.264/AAC 视频：2560×1440、30 fps、491 帧、16.366667 秒，`enhanced=false`，无增强任务 JSON。五个文件均通过完整解码，独立媒体信息和 SHA-256 在 `V1-runtime-gate-video-verification.json`。文件命名均为各自新输出目录中的 `20260929-1.mp4`。

教程回归覆盖首次弹出、上一页/下一页、点击高亮进入下一页、背景输入屏蔽、跳过始终高亮可用、回看不改设置和六张不同阿罗娜图片。已人工查看新的第 4 页截图，文字完整显示。

最终 UI 验证 DLL SHA-256：`20E25087B41AE7FC4159C31DBB4559A42FF1A2354FAA688D8BA8AAD7BB7F44E6`。前两轮使用的 `CFB3D28B...BB4AB13E` 与最终版仅差随后收敛的配置说明、无使用字段及教程两行文案；缺件门控实现相同。每轮实际 DLL 哈希均保存在各自 `PASS.json`。

## 还原与范围

- 每轮都在 `finally` 恢复临时移开的组件、配置文件及导出序号状态；最终副本配置恢复原始 2 字节空白内容，原来不存在的序号目录仍不存在，测试 AA 已退出。
- 日常 `RTX开发测试/AA/mods/AzureArchiveRecorder/0.2.1/AzureArchive.Recorder.dll` 仍为 `855425D9AAF87ACF0CD3F5E76903DB19DEBB76268912EE8095AF8175591339E7`。
- 本轮不支持显卡界面用例是明确标记的模拟，不能声称在 RTX 20 物理显卡运行过；组件错代与损坏检查使用真实文件。本轮未新增 GPU 增强画质推理结论，最终两种安装制品由主任务另行验收。

## 最终加壳制品实机验收（22:45，已通过）

本节是上面开发版验证之后的最终制品复核。使用 `tests/test-final-protected-runtime.ps1`，在同一个专用 `AA-UI-20260929` 副本中移存旧开发部署，再通过 V1 安装核心部署正式不可变 payload 的全部 201 个文件；逐文件核对 receipt SHA-256。只编译测试用部署入口，**未重新编译或修改 MOD、安装 payload、正式源码**。

制品来源：`安装交付/V1.0/构建记录/protected-V1.0-ui-20260929-223603-19af9de2/build-proof.json`。对应不可变 payload 位于 `protected-V1.0-20260929-221838-4a2eea77/install-payload.zip`；GUI 修订没有改变此 payload。

| 制品 | SHA-256 |
| --- | --- |
| 最终加壳安装 EXE | `DFD1115A5E2BEF60711609C18949594E3644A1ECDC11A113F905F062FBF22ECA` |
| 正式 payload ZIP | `88E6238B4AAF6DFF975612634FCA7534C5E8FE4C1B83DCD0B5185E656A98AEA7` |
| 实际运行的加壳 MOD DLL | `75B8F744824AC2D4592DBC220481AE548E2FDB2B08A80FAEFE8BFFB55058D1D4` |

证据根：`RTX开发测试/V1实机验收/验证记录/final-protected-20260929-224502`。汇总为 `final-protected-PASS.json`，日志与媒体审计为 `final-log-media-audit.json`，每轮保留独立 `PASS.json`、`bepinex.log`、`player.log`、`media-probe.json`、`full-decode.log` 与原生界面截图。

| 场景 | 原生界面断言 | 实际视频 | 结果 |
| --- | ---: | --- | --- |
| DLSS 目录完全不存在，已有开启值与 4 倍设置 | 25 | `missing-ordinary/published/20260929-1.mp4`；H.264，2560×1440 | 总开关及三个选项灰色不可操作，精确红字正确；普通内录成功，`enhanced=false`，没有增强任务 |
| 本地共同组件与仅 RTX 50 运行库，手动启用 SR2 | 29 | `complete-sr2/published/20260929-2.mp4`；HEVC，5120×2880 | 实际物理 RTX 5080 运行 2 倍超分，`scale=2`、`multiplier=1`、`neural=false`，进度到 1、`enhanced=true` |

两轮 AA 均退出码 0，最终视频均为 **30 fps、491 帧、AAC 48 kHz 双声道**。视频时长 16.366667 秒，音频时长 16.366000 秒，时长差约 0.667 毫秒。均通过映射完整视频流与音频流的 `ffmpeg -xerror` 全片解码，日志无解码错误。普通片 SHA-256：`2B6597EE14B24352A750292B3959B20D8D8E9BA1D1E618354ECFFF716D1CA89B`；SR2 片 SHA-256：`CE4879C2262ADC7E12BD97A79E2288B4CA2C22D4DA64DBC0FF1E8CD6332869A6`。

两轮日志均出现 `Protected recorder 1.0.0 loaded inside AzureArchive.`，证明实际通过保护加载器。SR2 没有插件错误。缺组件轮有且仅有一条测试主动绕过界面、直接请求增强时产生的预期 `InvalidOperationException: 缺少DLSS组件，相关DLSS功能不可用。`；其对应拒绝断言通过，并且没有创建增强任务；没有其他意外插件错误。SR2 整轮耗时约 111.5 秒，包含 AA 启动、界面断言、原始录制及增强，不能当作单独超分耗时。

本轮网络下载次数为 **0**。组件从主任务已经联网验收过的 `AA-Package-20260929/mods/AzureArchiveDLSS/runtime` 只读复制：七个共同文件、两个 locales 文件与 RTX 50 DLL；RTX 30/40 未复制。每个来源文件与复制件均核对哈希，测试结束再次确认来源文件没有改变。这里只验证相同组件内容的实际运行；最终 EXE 的联网流程由主任务单独验收。

原始 PPM 截图按捕获格式的底部朝上排列恢复显示方向后转换为 PNG，没有更改界面内容：`missing-ordinary/smoke-ui-settings.png`、`missing-ordinary/smoke-ui-dlss-missing.png`、`complete-sr2/smoke-ui-dlss-on.png`。

`restoration.json` 确认 cfg 恢复到本轮前的原始字节，原先不存在的 `azurearchive.recorder-state` 仍不存在；AA 与增强子进程均已结束。隔离副本保留正式保护 DLL 与本地 50 系组件以便追溯，旧开发部署保存在证据目录的 `prior-*` 中。日常 AA DLL 仍是 `855425D9AAF87ACF0CD3F5E76903DB19DEBB76268912EE8095AF8175591339E7`，本轮未修改旧基线、R6 制品或其他 AA 副本。
