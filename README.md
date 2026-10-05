# AA 剧情改稿同步 MOD · V1.1

在 AA 剧情编辑器中记录修改前后的完整对话配置，播放新稿后仍能查看、重播和恢复上一版。界面沿用 AA 的字体、配色和按钮风格。

本分支为 `mods/revision-compare`，程序版本号为 `1.1.0`。V1.1 是首个提供自动检查与下载更新的正式安装包，包含源码、加壳安装器和更新文件；不附带 AA 程序或用户工程。

[下载 V1.1 一键安装包](https://raw.githubusercontent.com/LittleLuoZiXi/AA-MOD/refs/heads/mods/revision-compare/releases/revision-compare/1.1.0/剧本改稿同步%20MOD%20%20一键安装.exe) · [安装说明](releases/revision-compare/1.1.0/安装说明.txt) · [SHA-256 校验值](releases/revision-compare/1.1.0/SHA256SUMS.txt)

## 一键安装

关闭 AA 后运行 `剧本改稿同步 MOD  一键安装.exe`，检查 AA 根目录，点击“一键安装 / 修复”；卸载时重新运行同一个 EXE。界面内含功能介绍。安装器沿用内录 MOD 的蓝白风格和混淆、压缩、加密封装，包含完整插件与清单，安装过程无需联网。适用于已支持 MOD 的 AA 1.0.0-fix。

支持全新安装、收据管理的旧版迁移和同版修复；安装、卸载前备份原文件，保留其他 MOD、用户工程及未知文件。已有文件被修改或已安装更高版本时停止覆盖。备份路径会显示在操作结果中。

V1.0 是源码基线，没有正式自动更新入口。首次使用此功能请安装 V1.1。V1.1 与当前 GitHub 同版时不会提示更新；未来发布 V1.2 等更高版本后，V1.1 会在下次启动 AA 时提示。

## 功能

- 在右上角播放按钮左侧增加双页图标，悬停提示“查看对比”，按钮本身没有文字。
- 每条对话分别保留最近两次内容不同的播放版本。自动预览和手动播放均可记录，相同内容重复播放不会覆盖上一版。
- 连续输入、快速修改会合并，短暂停止编辑（约 0.9 秒）后记录最终状态。手动播放或打开对比会立即处理待记录的预览。
- 对比悬浮窗与原剧情预览保持相同画面比例，支持拖动、重新播放旧稿及关闭。
- 对比窗口打开后，再点右上角播放不会关窗：小窗定格旧稿，右侧播放新稿。点击小窗“重新播放”可切回旧稿，两侧轮流使用 AA 原生播放器。
- 打开的窗口固定保留当时的旧版本。继续编辑、播放新版本也不会改变该窗口的恢复目标。
- “恢复原来修改”仅恢复当前条目的完整配置，并支持原生撤销与重做。

## 启动检查更新

启动 AA 后，MOD 在后台读取同一仓库 `mods/revision-compare` 分支的 `manifest.json`，比较其 `version_number` 与当前加载插件的版本。正式更新入口为 [stable.json](updates/revision-compare/stable.json)，目前指向真实 V1.1 文件，没有提前宣布 V1.2。

仅在远端版本更高时，等 AA 界面准备好且没有其他模态窗口后，显示沿用内录 MOD 风格的原生弹窗，列出本地和最新版本。“更新版本”直接下载并应用更新；“暂不更新”或 Esc 关闭提示，本次启动不再提醒。关闭后，下次启动仍会重新检查。

点击“更新版本”后显示下载进度，可取消；下载、校验或准备失败会显示原因，并可重试。下载仅包含 DLL 和 manifest，须符合固定分支路径、目标版本、长度及 SHA-256。文件准备好后，受信本地助手先确认当前安装，MOD 再检查工程状态并授权退出；AA 正常退出后才替换文件、调整本 MOD 配置并重启。其他 MOD 条目保持原样，失败会保留或回滚原安装。

工程尚未保存、正在保存或内录仍在工作时不会开始更新或退出 AA。请保存后在提示中重试；“关闭”后可下次启动重新检查。后续新版本需按 [更新发布说明](updates/revision-compare/README.md) 同步发布 DLL、manifest 与校验清单；缺少可用文件时会明确提示，保留现有版本。

启动检查遇到断网、超时、同版、旧版或无效清单不会弹出错误窗口；检查不阻塞 AA 启动，不改变工程内容。每次启动只检查一次，主地址连接失败时最多尝试一次固定 GitHub API 备用地址，版本检查总等待上限为 10 秒。

后续发布时应同时提高 `src/Plugin.cs` 的 `Version` 和 `manifest.json` 的 `version_number`；构建会检查二者一致。版本按数字及语义版本优先级比较（例如 `1.10.0` 高于 `1.9.0`），`V1.0` 与 `1.0.0` 等价。

## 使用流程

1. 在编辑器中选中对话，自动预览或手动播放 A。
2. 修改为 B，再播放查看效果。
3. 点击双页图标，修改前仍显示 A。反复播放同样的 B，也不会把 A 挤掉。
4. 如需回退，点击“恢复原来修改”；一次原生撤销可返回恢复前的 B。

窗口关闭后，继续修改并播放 C，下次打开对比会显示 B。窗口保持打开时则固定显示原先的旧稿，直到关闭后重新打开。切换对话或工程会关闭旧窗口，防止恢复错对象。

## 记录与恢复范围

记录完整 `ScriptData`，包括台词、角色与表情、槽位及位置、动作、背景、转场、音乐与音效引用、配音引用、额外剧情指令等；预览同时保留所需的前条舞台上下文及有效背景音乐。

恢复只作用于当前条目，不回滚其他条目、节点连接、工程设置或外部素材文件。窗口内继续修改的内容，会作为点击恢复时的撤销前态保存。

历史保存在当前编辑会话内，关闭或重新打开工程后不会作为永久历史保留。尚未实际预览的条目暂用首次选中状态。MOD 不主动保存整个工程，自动保存仍由 AA 原设置控制。

## 适配与构建

本次验证环境：Windows、AA 1.0.0-fix、Unity 2023.2.22f1、BepInEx 6 IL2CPP。其他 AA 版本尚未验证。

使用 PowerShell 7，并准备已经生成 IL2CPP 互操作程序集的 AA MOD 环境。`build.ps1` 使用 PowerShell 自带的 Roslyn，以及所选 AA 目录中的 `dotnet`、`BepInEx/core`、`BepInEx/interop` 引用，不下载 SDK 或宿主依赖。

```powershell
./build.ps1 -GameRoot '你的 AA 目录'
```

输出为本地 `build/AzureArchive.RevisionCompare.dll` 和 `build/manifest.json`。普通用户使用一键安装器，建立后续自动更新需要的安装收据；仅手动复制文件不会自动建立收据。

开发者也可在关闭 AA 后，在 Windows PowerShell 中运行 `tools/build-local-deployment.ps1`，再运行 `tools/install-local.ps1 -GameRoot '你的 AA 目录'`，生成兼容的安装收据。遇到没有收据的同名已有文件会停止，保留用户文件。

构建加壳安装器与整理发布文件（PowerShell 7）：

```powershell
./packaging/restore-tools.ps1
./packaging/build-installer.ps1
./tools/prepare-release.ps1
./tests/test-release-feed.ps1
```

保护工具固定为 Obfuscar 2.2.50，恢复脚本验证官方 NuGet 包的固定 SHA-256。工具依赖、本地编译目录、测试宿主和测试结果不提交。发布文件位于 `releases/revision-compare/1.1.0/` 和 `updates/revision-compare/`；发布操作详见更新发布说明。

## 验证

```powershell
./tests/test-revision-store.ps1
./tests/test-update-client.ps1
```

20 项独立逻辑检查覆盖两版滚动、完整内容去重、深拷贝隔离、固定窗口恢复、工程/条目/会话隔离及过期窗口拒绝。

更新客户端和下载器的 42 项独立检查使用模拟网络响应，覆盖版本比较、清单校验、备用地址、响应大小、哈希、下载清理、超时、取消及每启动一次检查，并验证 V1.1 同版不提示、未来 V1.2 可提示和下载。模拟 V1.2 仅存在测试中，不发布到真实更新入口。

发布校验使用真实客户端检查清单和载荷：`./tests/test-release-feed.ps1` 读取待发布文件；上传后加 `-Live` 验证 GitHub 同版静默及下载校验。安装助手的合成进程测试覆盖 V1.1 → V1.2、正常退出后更新重启、取消、篡改拒绝及失败回滚。

功能基线已在隔离 AA 宿主中通过 83 项检查，包括自动记录合并、第二次播放不覆盖旧稿、新旧播放切换时保持窗口、固定旧稿恢复、一次撤销/重做、背景音乐和相邻条目保护。原生集成检查需要本地 AA、资源缓存及专用测试副本，准备要求见 [tests/README.md](tests/README.md)。

普通构建不包含集成探针。只有隔离测试使用 `-IncludeProbe`，测试构建不得部署到日常 AA。

## 代码结构

| 路径 | 内容 |
| --- | --- |
| `src/RevisionStore.cs` | 两版快照、内容去重、窗口固定与身份校验 |
| `src/NativeRevisionAdapter.cs` | 完整条目适配、自动记录合并、原生撤销恢复 |
| `src/ComparisonPreview.cs` | 原生播放器在新旧画面之间切换 |
| `src/NativeCompareUi.cs` | 原生图标与悬浮窗 |
| `src/Plugin.cs` | 插件生命周期、自动/手动播放事件 |
| `src/RevisionUpdateClient.cs` | 固定分支版本检查、语义版本比较、超时与取消 |
| `src/RevisionUpdateController.cs` | 后台结果消费及每次启动一次提示 |
| `src/RevisionUpdateDownload.cs` | 两份固定更新文件下载、进度及哈希校验 |
| `src/RevisionUpdateFlow.cs` | 下载、校验、准备及退出授权流程 |
| `src/RevisionUpdateApplySession.cs` | 启动本地助手与退出握手 |
| `src/RevisionUpdateHelper.cs.txt`、`src/RevisionUpdateHelper.ps1` | 内嵌受信助手，等待退出、事务替换、回滚及重启 |
| `src/NativeRevisionUpdateUi.cs` | 启动时可用的 AA 原生更新弹窗 |
| `tests/` | 逻辑测试、合成夹具生成与隔离宿主检查 |
| `tools/inspect-types.ps1` | 本地宿主类型检查工具 |

版本说明见 [CHANGELOG.md](CHANGELOG.md)。
