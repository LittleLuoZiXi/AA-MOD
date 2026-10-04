# V1.0 联网 DLSS 后端

实现为 `OnlineDlssInstall.cs`，只在内录本体安装完成后由向导调用。内录安装 EXE 不嵌入 NVIDIA/DLSS 原生组件。内嵌的小型独立卸载程序不是推理运行库。

RTX 20 在此阶段报告“暂时不支持20系”，不创建 DLSS 目标文件；已经安装的内录本体、配置与原有数据保留。其他不支持显卡由向导禁用增强。代际专属附件只下载本机选择的 30、40 或 50 系这一份。共同 ZIP 本身带有默认 40 系 DLL，但后端不会提取它，只安装所选专属附件中的运行库；旧目录中其他代际的已登记文件保留。

## 固定上游

共同组件：

- URL：<https://github.com/banbanzhige/DLSS5Tool/releases/download/v2.3.3/DLSS5Tool-v2.3.3-win64.zip>
- ZIP：579761793 字节。
- SHA-256：`d724f986143a45293e50faf7e2799eabad8a35f98bfa30c3d6f9836702b056b1`。
- 公开摘要：<https://github.com/banbanzhige/DLSS5Tool/releases/expanded_assets/v2.3.3>。

本次已在新建 `online-dlss-work/source-audit` 保存并验证该普通便携 ZIP；没有下载 full/addon 多卷包。七个共同 native 的独立 SHA-256 与 R6 已固定值逐项一致，审计见 `source-audit/common-source-audit.json`。ZIP 内各原始许可、两种 locales 和 NVIDIA RTX Video PDF 均已核对存在。

代际运行库使用固定 release：<https://github.com/banbanzhige/DLSS5Tool/releases/expanded_assets/zip>。

| 系列 | 附件 | ZIP SHA-256 |
| --- | --- | --- |
| 30 | `30.-310.8.SF-v2.zip`，117898662 字节 | `01626f7ffe14c54928e9b2eaa09baf1886fa9200b247bbb51895f935f301886c` |
| 40 | `40.zip`，106991529 字节 | `3fdeb4f3b44165bfd31d98e288a46dc16eedd40168819c8ed6f73c45fc92c7a1` |
| 50 | `50.zip`，109425424 字节 | `e730e1ea95b0a4f6420b9b1bbb1c2948cbb1bc9241aebfdeb10efef8a3bc906a` |

40/50 的精确长度及相同摘要由父线程从固定公开 GitHub API <https://api.github.com/repos/banbanzhige/DLSS5Tool/releases/tags/zip> 核验。下载前已知总量：30 系 697660455 字节；40 系 686753322 字节；50 系 689187217 字节，因此总进度无需在中途补入预计长度。

三系 DLL 固定摘要继续取共享 `DlssComponents.Hashes`，与 C# 原系列规则及 `bridge/gpu_profiles.json` 一致。生产入口没有 URL、hash 或代际覆盖环境变量；代际由向导的真实 GPU 检测决定。

## 共享接口

`RunAsync(gameRoot, series, uninstallerBytes, progress, control)` 返回 `Task<OnlineDlssResult>`。只有完整事务提交后 `Completed=true`；已安装且固定共同/本机代际摘要、receipt、locales 与卸载器全部有效时，返回 `AlreadyInstalled=true`，跳过下载且不改现有文件。

`IsInstalled(root,series)` 与 `MissingComponents(root,series)` 是相同规则的只读检查。R6 补充包的真实身份是 `AzureArchiveDLSSSupplement 1.0.0`，本后端兼容该 receipt；`0.2.1` 是内录 MOD 的版本，不是补充包版本。

`OnlineDlssControl.Pause/Resume/Cancel` 控制下载，取消抛出 `OperationCanceledException`。Pause 中止当前 HTTP 请求并保留本次暂存；Resume 使用 Range/If-Range 继续。服务器忽略 Range 时从头下载，ETag 变化时从头下载，错误 Content-Range 拒绝接收。完整文件再执行 SHA-256 校验。

进度字段为 `BytesReceived`、`TotalBytes`、`Phase`、`Asset`、`Message`。阶段包含 Checking、Downloading、Paused、Verifying、Verified、Extracting、Installing、Completed。暂停与取消也在校验、提取和事务阶段的检查点生效。界面应在 Task 收尾后再 Dispose 控制器。

独立卸载器构建入口为 `/main:OnlineDlssUninstallerProgram`，编译时包含本文件、生成过 HostHash 的基础 `Installer.cs` 与共享 `DlssComponents.cs`。它兼容原保护外壳的 `AARecorder.InstallerHost`。将生成的卸载 EXE 作为小资源传给 RunAsync；主安装器选择 `/main:V1Program`。

## 安装边界

下载及提取先在独立随机临时目录完成，验证期间不写任何 MOD 目标文件。仅提取七个共同 native、两种 locales、本机代际 DLL 和原始许可；GUI、Python/Tk 重复环境、历史/配置、其余代际、驱动与实验 GPU-export 均不提取。

目标为 `mods/AzureArchiveDLSS/runtime`，receipt 和卸载器只归此产品。已有未知或改动过的目标文件在下载前就拒绝覆盖，提交前再次检查。所有新文件与 receipt 通过共享事务写入；取消或故障回滚，不删除内录本体，不更改 profile，也不删除旧组件的其他代际或未知文件。

失败和取消清理本次创建的暂存 `.part`、已下载归档与临时提取文件。重试开始新会话。卸载仅删除 receipt 范围内且摘要仍一致的文件，其他文件保留，仅删除空目录。

## 验证边界

同程序集测试可以调用 `DownloadVerifiedAsset` 对本地 HTTP 夹具执行真实传输，以及 `CommitPrepared` 对真实已验证的临时文件集合测试事务故障/取消回滚。生产入口没有测试清单或本地 URL 参数。

独立测试已通过 23 项检查：

- 17 项真实本地 HTTP 传输测试：成功、未知长度、暂停期间停止写入、Range 续传、忽略 Range、ETag 变化、非法 Content-Range、断线重试、离线/404、错误 SHA-256、下载/暂停时取消、既有文件与竞态保护、回调失败清理、非本地明文 HTTP 拒绝。结果：`RTX开发测试/V1安装测试/test-20260929-220937-0ea2e78f/fixtures/results.json`。
- 6 项组件事务测试：只安装所选代际、有效 R6 补充清单直接复用、取消回滚、写入异常回滚、改动文件保留与独立卸载、RTX 20 拒绝后保留已安装内录。结果：`RTX开发测试/V1安装测试/test-20260929-221314-fc2226c1/fixtures/results.json`。
- 已验证公开共同 ZIP 与 30 系固定 ZIP 的真实白名单提取，生成 25 项单系负载，未混入 40/50 系运行库。记录：`online-dlss-work/prepare-30-20260929-220811/prepare-report.json`。

上述检查覆盖后端传输与文件事务，没有启动 AA 或运行显卡推理；它们不能替代最终安装 EXE 界面、内嵌资源和真实卸载器的独立验证。
