# V1.2.1 加壳安装包构建

当前内录版本为 `1.2.1`，对外名称为 `V1.2.1`。基于已独立保存的 V1.2 开发热修复；`source-v1.2` 和既有 V1.2 安装包/证据均保留，不覆盖历史基线。

版本必须一致：`manifest.json`、`src/Plugin.cs` 的 BepInPlugin 与启动日志、`packaging/Loader.cs.in`、安装器/卸载器程序集元数据、`InstallCore.Version`。本地修订仍使用 1.2.1，必须完整重编译内核及保护安装包；不得复用上一次的 stage。

构建仍使用现有 PowerShell 7/Roslyn、.NET Framework csc 和 Obfuscar。冻结运行环境直接复用 AA 已安装的 Recorder runtime；FFmpeg 使用已备份的固定哈希版本。只读身份校验需要工作源码 `.venv` 中的 Python 3.12/PyInstaller；不需要重新冻结 helper、不下载或安装 DLSS。

正式构建需等功能源码与版本字符串确认完成，再运行：

```powershell
& 'D:\test2\work\RecorderMod\packaging\build-release.ps1' `
  -GameRoot 'E:\AzureArchive_100_1001' `
  -OutputDirectory 'D:\test2\安装交付\V1.2.1-原生计时与MENU修复' `
  -FrozenRuntimeDirectory 'D:\test2\work\mods\AzureArchiveRecorder\runtime'
```

`GameRoot` 只用于宿主/程序集引用和旧版哈希读取，源码由脚本所在的 RecorderMod 确定。所有 stage、编译输出、加壳记录和 EXE 写入显式 OutputDirectory。产物为 `AzureArchive内录V1.2.1-加壳安装.exe`；每次创建全新构建记录，不能复用旧 stage。

构建后以本次返回的 `build-proof.json` 运行 `packaging/verify-r6-payload.ps1 -BuildProof <绝对路径>`，检查载荷路径、版本与哈希。正式构建同时运行只读保护检查；这不等于已经完成 AA 正常启动与录制实测。

升级接受有效的 `0.2.1`、`1.0.0`、`1.1.0`、`1.2.0` receipt。每个旧版只允许认领它当时知道的旧文件；当前 payload 只能认领 `1.2.1` 版本目录与内录公共 runtime。清理依据原 receipt 的路径与哈希，保留修改过的文件、配置及其他 MOD。安装器与卸载器继续使用同一版本的严格归属检查；独立 DLSS 产品与逻辑保持原样。

仅验证安装器事务时，可运行下面命令。它使用新建的合成夹具，编译测试专用 EXE，不构建或覆盖共享 MOD/正式安装包，不启动 AA，不运行网络/DLSS/UI 套件：

```powershell
& 'D:\test2\work\RecorderMod\packaging\test-v1-installers.ps1' `
  -GameRoot 'D:\test2\work' -Suite Core
```

测试证据写入 `test2/V1.2.1安装测试`。清单锁使用 `Read + FileShare.Read`，并明确断言异常到达 `InstallCore.Atomic` 与 `File.Replace/InternalReplace`；卸载锁断言到达 `File.Delete/InternalDelete`，以避免把预检失败误算为事务回滚。
历史版本准备验证：6 个修改过的 PowerShell 脚本 AST 解析通过；Core 53/53 通过，证据位于 `test2/V1.2.1安装测试/test-20261002-111931-dc5f2df7`。其中包含 1.2.0 两种变体升级、取消、用户修改保留、实际原子提交失败回滚及跨版本 receipt 拒绝。该条只记录当时的准备状态；本轮实际构建和运行结果以 ../V1.2.1-NATIVE-TIMING-MENU.md 为准。
