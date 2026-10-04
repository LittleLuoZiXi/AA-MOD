# V1.2.4 自动更新协议与发布

本版在已验证并发布的 V1.2.3 上增加自动更新。V1.2.3 的完整源码已保存在 mods/recorder 分支，提交 2ebd5f1dfcafdaba842c08149a660f31d37b580e。不要改写该快照。

## 用户流程

1. 打开内录面板后，在后台检查固定的 GitHub 更新信息；不阻塞普通录制。教程和总时长确认显示期间暂缓更新提示。
2. 有适用于当前 AA 的更高版本时，弹出“内录MOD有新版本，是否进行更新？”。拒绝可继续普通内录。
3. 同意后显示下载进度，可暂停、继续、终止。暂停保留已经下载的有效数据；继续时核对 Range、ETag、大小，服务器不支持范围请求则从头重下。全部数据核对 SHA-256 后才能进入安装阶段。
4. 安装阶段禁止开始录制，不能终止正在提交的安装事务。新版本放入独立版本目录，未变化的运行库不替换；当前加载的旧 DLL 继续保留到卸载/下次安装按归属清理。
5. 更新助手完成文件、配置方案版本及收据提交后，才显示“内录MOD更新完成，AA即将重启”，仅有 OK。点击 OK 后 AA 正常退出，助手再启动同一目录的 AA。未点击 OK 不自动重启。
6. 工程有未保存更改、正在保存或应用编辑时拒绝安装。重启前再次核查。无法确认安装结果时保持禁录；用户可返回保存工程，然后手动重启/使用安装包修复。

## 固定来源

更新入口固定为：
https://raw.githubusercontent.com/LittleLuoZiXi/AA-MOD/refs/heads/mods/recorder/updates/recorder/stable.json

下载地址仅允许 LittleLuoZiXi/AA-MOD 的 HTTPS Release 资产及 GitHub 官方 Release 资产跳转，使用系统 TLS 证书验证。生产配置不能覆盖更新服务器地址。测试只注入内存 HTTP handler，不提供任意生产下载端点。

信息结构：

    {
      "SchemaVersion": 1,
      "Product": "AzureArchiveRecorder",
      "Version": "1.2.4",
      "MinimumVersion": "1.2.3",
      "HostSha256": "<AzureArchive.exe 的 SHA-256>",
      "BaselinePath": "baselines/<已上传的源码基线>",
      "DownloadUrl": "https://github.com/LittleLuoZiXi/AA-MOD/releases/download/<真实标签>/<真实更新ZIP名>",
      "Sha256": "<更新ZIP的 SHA-256>",
      "Size": 0
    }

以上为结构示意，尖括号和 Size=0 不能用于发布。必须使用实测文件值。版本为三段或四段数字；不接受前缀、预发布后缀、前导零。包大小上限 256 MiB。更新只适用于相同宿主摘要且当前版本达到 MinimumVersion 的机器。没有兼容新版本时不弹框。

## 载荷与安装

载荷是 build-release.ps1 生成的 install-payload.zip，不是源码 ZIP 或安装 EXE。它包含 payload-manifest.json，以及收据列出的完整 MOD 文件；不含 DLSS 本机组件。版本目录中必须包括 AzureArchive.Recorder.dll、manifest.json 和加壳 更新内录MOD.exe。

MOD 再次校验已下载包，限制条目、展开大小和路径，逐项检查哈希及内部 manifest 的产品与版本。将已验证 ZIP 复制到本次独立 Temp/AzureArchiveRecorderApply/<GUID-N>/payload.zip，提取经验证的更新助手，再启动助手。整个过程不执行 AA 目录外的任意指定命令。

助手接收 --job 同目录/job.json，字段为 Root、ParentPid、ParentStartUtcTicks、Version、PayloadPath、PayloadSha256、PayloadSize、Token。它重新验证宿主、父进程身份、自身与载荷摘要、当前安装收据和所有目标文件归属。只修改内录 MOD 已拥有的文件及配置方案中的内录版本，不改 cfg、其他 MOD、用户故事、源码和视频；冲突或失败走事务回滚。

status.json 使用 State / Error / Message / InstallationUncertain；状态为 Applying、Completed、Failed、Restarting。未能确认完整回滚时 InstallationUncertain=true，MOD 保持禁录。只有 OK 回调写入含匹配 Token 的 restart.json，助手才等原进程正常退出后重启。助手不会强制结束 AA。

保护使用既有 Obfuscar + AES/GZip 内存装载；安装器、卸载器和新增更新助手都走保护构建。保护不保证不可逆向，仓库公开源码属于用户已授权的决定。

## 公开资产名称与版本说明

每个正式版本均上传既有保护构建生成的安装 EXE，公开名称固定为“内录MOD{version}版本安装包.exe”；文件名不注明保护。源码包使用 AARecorder-V{version}-Source.zip，更新包使用 AARecorder-V{version}-update.zip。发布重命名只复制文件，不重新封装或改变二进制字节。

本次 V1.2.4 沿用已存在的基线目录 baselines/20261004-内录V1.2.4自动更新，正式说明仅介绍功能。该说明方式只适用于本次，后续版本的长期规则见 RELEASING.md。发布状态以 Release、同基线的 delivery-checks.json 和生产清单为准。

## 发布顺序

- 先完成代码、安装器、下载协议、回滚、真实 UI 与最终加壳版本验证。
- 将完整源码和构建/验证摘要保存到对应版本 baseline，并记下提交号；本次按用户授权更新既有 V1.2.4 基线，后续版本默认创建独立基线。
- 为同一构建创建或更新 Release，上传对应安装 EXE、完整源码 ZIP 和更新 ZIP。安装包采用“内录MOD{version}版本安装包.exe”公开名称；必须来自既有保护构建。
- 核验 Release 资产下载到的大小和 SHA-256 与本地一致。
- 最后才提交 stable.json，填写已经存在的 baseline、真实 Release URL、大小、哈希及宿主限制。更新清单是启用分发的最后一步；不能先指向不存在或未验收的文件。
- 当前版本的用户不会反复安装同一版本；下一版本发布时沿用此流程。

未发布 stable.json 或网络不可达时，面板给出可继续内录的检查失败信息。不要将隔离测试的模拟新版本、下载地址或虚拟数据上传为生产更新。

## 验证入口

- tests/test-recorder-update.ps1：纯 .NET 6 下载与更新检查测试，内存网络，不访问生产更新。
- tests/test-update-session.ps1：ZIP/助手启动边界、明确确认及不确定状态保护，合成文件与模拟进程。
- packaging/test-update-installer.ps1：真实加壳助手/文件锁/安装回滚/父进程退出与确认握手，独立临时 AA 夹具。
- tests/run-update-ui-smoke.ps1：真实 Unity/NGUI 面板，专用 AA 测试身份；下载器是真实现+内存数据，安装和重启为明确标记的诊断模拟。此测试不能替代助手真实安装测试。
- packaging/test-v1-installers.ps1：既有安装卸载归属和旧版本迁移回归。

开发过程和最终结果另见 CONTINUE-V1.2.4.md；未完成的测试不要宣称通过。禁止在正式用户宿主传入诊断开关。
