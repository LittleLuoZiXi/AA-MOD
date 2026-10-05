# 测试说明

## 独立逻辑测试

在 PowerShell 7 中运行 `./tests/test-revision-store.ps1`。不需要启动 AA，结果写入本地 `evidence/store-tests/`。

启动更新检查的独立测试运行 `./tests/test-update-client.ps1`，42 项结果写入 `evidence/update-client-tests/`，包含 V1.1 同版静默及未来 V1.2 提示、下载。全部 HTTP 响应在本地模拟，不访问真实 GitHub，不修改分支版本。

发布前运行 `./tools/prepare-release.ps1` 后，再运行 `./tests/test-release-feed.ps1`，用真实客户端校验待发布清单和实际载荷。发布后加 `-Live` 读取真实 GitHub，确认同版不提示、清单与本地一致、两份更新文件能下载且通过哈希。此检查不安装、不启动 AA，也不发布测试版本。

Windows PowerShell 中执行 `./tests/test-release-installer-core.ps1`，以及 `./packaging/test-protected-installer.ps1 -ExpectedInstallerSha256 '<本次构建记录的 SHA-256>'`，在合成目录内检查安装、修复、卸载及文件保护；不部署到日常 AA。

## 原生集成检查

集成检查针对 AA 1.0.0-fix 的专用副本，不允许将测试探针加载到日常 AA。源码不附带 AA 程序、资源缓存或用户工程。

1. 执行 `./tests/prepare-host.ps1 -Source '本地 AA 目录'`，生成源码目录内的 `test-host/`。脚本默认拒绝覆盖已有目录，并更换副本的 Unity 公司/产品标识，写入 `RevisionTestHost.json`。
2. 执行 `./tests/create-fixture.ps1 -TestHost './test-host'`，生成两条合成对白的 `evidence/fixture.aap2`。夹具使用原生白子、办公室背景及 BGM 37，不读取个人工程。
3. 为副本准备可用的 AA 资源缓存与 Addressables catalog，并在副本 `UserData/data/settings/user_settings.json` 中配置独立工作目录。使用 `test-host/workspace/`；关闭自动保存、自动编译及更新检查。集成探针会将持久化目录改到副本的 `UserData/`。缓存未准备好时，资源就绪检查会超时。
4. 执行 `./build.ps1 -GameRoot './test-host' -IncludeProbe`，再执行 `./tests/run-integration.ps1 -GameRoot './test-host'`。

检查脚本仅启动带标记的副本，自动载入合成工程，验证原生播放事件、图标点击、悬浮窗、音乐、单条恢复和撤销，并把结果写入副本 `evidence/`。脚本会核对日常 AA 的相关设置、工程及 MOD 配置是否变化，不主动写入这些原文件。

`prepare-host.ps1` 只准备程序副本，不自动下载资源，也不复制用户设置和个人工程。上述缓存与独立设置仍需准备；单独运行准备脚本不代表原生测试环境已完整。若更新源码版本，请重建专用副本或同步其 `RevisionTest` 配置中的 MOD 版本。

完成测试后，重新执行不带 `-IncludeProbe` 的普通构建再用于日常环境。测试宿主、测试结果、本地编译目录及缓存均不提交；仅显式整理到 releases/ 和 updates/ 的正式载荷随发布上传。

## 启动更新提示检查

使用相同隔离宿主与 `-IncludeProbe` 构建，执行：

```powershell
./tests/run-integration.ps1 -UpdateOnly
```

此模式停留在主页，不打开工程，用可控的后台结果模拟有新版、无新版、离线和取消，并检查下载进度、取消、失败重试、未保存保护，以及助手确认后才请求退出。共 61 项检查。模拟新版本只存在测试进程内，不修改 GitHub 或日常 AA；下载、助手授权及退出均由测试回调控制，不实际下载或重启。截图为 `test-host/evidence/update-prompt.ppm` 和 `update-downloading.ppm`，检查结果仍写入 `result.json`。

安装助手的进程测试：在 Windows PowerShell 中运行 `./tests/test-update-apply-helper.ps1 -ReferenceGameRoot '本地 AA 目录'`。它仅从参考宿主读取元数据校验依赖，在 `evidence/update-apply/` 生成临时合成宿主与插件，不使用 Unity 或个人工程。8 项检查覆盖更新与重启、未授权退出、授权后取消、异常退出、文件篡改、开发探针拒绝、回滚及版本顺序。合成宿主程序只用于进程生命周期测试，不是发布的安装包。
