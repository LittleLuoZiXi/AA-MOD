# 测试说明

## 独立逻辑测试

在 PowerShell 7 中运行 `./tests/test-revision-store.ps1`。不需要启动 AA，结果写入本地 `evidence/store-tests/`。

## 原生集成检查

集成检查针对 AA 1.0.0-fix 的专用副本，不允许将测试探针加载到日常 AA。源码不附带 AA 程序、资源缓存或用户工程。

1. 执行 `./tests/prepare-host.ps1 -Source '本地 AA 目录'`，生成源码目录内的 `test-host/`。脚本默认拒绝覆盖已有目录，并更换副本的 Unity 公司/产品标识，写入 `RevisionTestHost.json`。
2. 执行 `./tests/create-fixture.ps1 -TestHost './test-host'`，生成两条合成对白的 `evidence/fixture.aap2`。夹具使用原生白子、办公室背景及 BGM 37，不读取个人工程。
3. 为副本准备可用的 AA 资源缓存与 Addressables catalog，并在副本 `UserData/data/settings/user_settings.json` 中配置独立工作目录。使用 `test-host/workspace/`；关闭自动保存、自动编译及更新检查。集成探针会将持久化目录改到副本的 `UserData/`。缓存未准备好时，资源就绪检查会超时。
4. 执行 `./build.ps1 -GameRoot './test-host' -IncludeProbe`，再执行 `./tests/run-integration.ps1 -GameRoot './test-host'`。

检查脚本仅启动带标记的副本，自动载入合成工程，验证原生播放事件、图标点击、悬浮窗、音乐、单条恢复和撤销，并把结果写入副本 `evidence/`。脚本会核对日常 AA 的相关设置、工程及 MOD 配置是否变化，不主动写入这些原文件。

`prepare-host.ps1` 只准备程序副本，不自动下载资源，也不复制用户设置和个人工程。上述缓存与独立设置仍需准备；单独运行准备脚本不代表原生测试环境已完整。若更新源码版本，请重建专用副本或同步其 `RevisionTest` 配置中的 MOD 版本。

完成测试后，重新执行不带 `-IncludeProbe` 的普通构建再用于日常环境。任何测试宿主、测试结果、编译产物及本地缓存均不纳入本分支。
