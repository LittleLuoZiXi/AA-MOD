# 剧情改稿同步 MOD 的更新发布格式

V1.1 / `1.1.0` 是首个支持自动更新的正式包。目前入口指向真实 V1.1，同版不会提示更新。之后 V1.2 / `1.2.0` 发布时，已安装 V1.1 的用户在下次启动 AA 时才会收到提示。

## 固定入口

所有文件保存在 `LittleLuoZiXi/AA-MOD` 的 `mods/revision-compare` 分支：

| 路径 | 用途 |
| --- | --- |
| 根目录 `manifest.json` | 启动时读取 `version_number`，决定是否提示 |
| `updates/revision-compare/stable.json` | 点击“更新版本”后获取下载地址、长度和 SHA-256 |
| `updates/revision-compare/files/<版本>/AzureArchive.RevisionCompare.dll` | 对应版本插件 |
| `updates/revision-compare/files/<版本>/manifest.json` | 对应版本清单 |
| `releases/revision-compare/<版本>/剧本改稿同步 MOD  一键安装.exe` | 首次安装、修复或卸载使用 |

[查看当前真实下载清单](stable.json)。其 `SchemaVersion` 固定为 `1`，`Product` 固定为 `AzureArchiveRevisionCompare`，`Version` 为规范三段版本号。`Files` 必须且只能含上述 DLL 和 manifest，各项字段为 `Name`、`Url`、`Sha256`、`Size`。更新过程不下载或运行远端 EXE、ZIP 或脚本。

`Url` 必须使用 `https://raw.githubusercontent.com/LittleLuoZiXi/AA-MOD/refs/heads/mods/revision-compare/updates/revision-compare/files/<版本>/<文件名>`。客户端校验完整路径，不能改为 Release 附件、其他仓库、短链或 CDN。GitHub API 备用地址也固定为同一仓库、分支、版本目录。

## 以后发布 V1.2

1. 将 `src/Plugin.cs` 的 `Version` 与根目录 manifest 的 `version_number` 同时改为 `1.2.0`；同步安装器、部署工具的产品版本及说明。不能仅改 GitHub 清单却继续发旧 DLL。
2. 使用 PowerShell 7 普通构建，不带开发探针：

   ```powershell
   ./build.ps1 -GameRoot '你的 AA 目录'
   ./packaging/restore-tools.ps1
   ./packaging/build-installer.ps1
   ./tools/prepare-release.ps1
   ./tests/test-update-client.ps1
   ./tests/test-release-feed.ps1
   ```

3. `prepare-release.ps1` 校验插件版本、无探针及安装器内嵌载荷一致，再生成新版本目录、真实哈希/长度、`stable.json` 和安装器校验文件。同一版本目录内的已生成文件不允许换内容，应提高版本再发布。
4. 将源码、根 manifest、stable 清单、新版本的两个更新文件及安装器**放在同一次提交中**，最后更新 `mods/revision-compare` 分支。不要先发布版本号再补文件。保留旧版本文件；不修改主分支和内录 MOD 分支。
5. 上传后运行 `./tests/test-release-feed.ps1 -Live`，验证 GitHub 入口、同版静默、两个文件的实际下载及哈希。客户端/原生界面的模拟测试可验证旧版识别新版，不要把测试用的虚构版本写入真实 GitHub 入口。
6. 同步 README 的安装包下载链接与 CHANGELOG。上述入口与 schema 保持兼容，V1.1 用户无需重新安装更新器。

## 应用更新

更新助手来自当前已安装插件的内嵌源码。它校验下载 DLL 的插件身份和版本、拒绝探针，核对安装收据、原文件哈希及当前 profile。只迁移收据拥有的本 MOD 条目，保留其他 MOD 和未知文件。当前支持收据中仅有一个、且与当前 profile 对应的安装；配置被人工改动或拥有多个 profile 时停止覆盖。

一键安装器会建立兼容收据。直接复制 DLL 不会建立收据，因此不能保证自动接管此类手动安装。V1.0 为源码基线，首次使用自动更新请安装 V1.1。

更新先复制到独立临时任务目录，等待 AA 授权并正常退出。状态和备份位于系统临时目录下的 `AzureArchiveRevisionCompareApply/<任务ID>/`。失败原因和回滚备份保留供恢复检查。内录忙碌或工程未保存时，不发出退出请求。清单版本若在检查后变动，需下次启动重新检查。
