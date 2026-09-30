# DLSS 补充组件审计与构建记录

2026-09-29，产品 `AzureArchiveDLSSSupplement 1.0.0`。本记录针对这次获得明确授权的本地安装包构建，不修改已保存基线、上游工具或系统驱动。

## 文件来源与固定身份

读取了本工作区 `RTX开发测试/AGENTS.md`、当前 `RecorderMod/HANDOFF.md` 和 `DLSS-RTX-TEST-PLAN.md`，并以当前源码及 `DEVELOPMENT.md` 的新状态为准，不把 R4 历史限制或历史实测范围当成当前全部情况。

来源为 DLSS5Tool v2.3.3，Python 源码提交 `e53e0b4d5139126e7b31866ed6b3653bbba0f449`。本补充包不复制其 GUI、Python/Tk 环境、配置、队列或导出历史。

GPU 附件来自 <https://github.com/banbanzhige/DLSS5Tool/releases/tag/zip>。公开元数据 <https://github.com/banbanzhige/DLSS5Tool/releases/expanded_assets/zip> 所列 RTX30 ZIP 摘要与工程预存值一致。获得用户针对固定链接“允许下载并校验”的明确答复后，下载到专属 sources 目录，验证 ZIP 后只提取唯一命名 DLL，再验证 DLL；没有直接执行新下载的 RTX30 DLL。

| 系列 | 本次来源 | DLL 字节数 | DLL SHA-256 |
| --- | --- | ---: | --- |
| 30 | `dlss-supplement-sources/30.-310.8.SF-v2.zip` | 165830144 | `6eb209e764f39872625debd6abaf45e2bb6322f6f270f781f70c059ae30b3927` |
| 40 | 原有工具 `_internal/nvngx_dlssnr.dll` | 165840496 | `ceb6432f6fbdf44d886014bcd47241932bf8b67439feef9bbdd0961436662650` |
| 50 | 原有工具 `mods/nvngx_dlssnr.dll` | 165840496 | `e16bcf15e16e13f527491cdf7845b2fe6521a738d8f7c9c721866a8496e1fc8e` |

RTX30 原 ZIP 为 117898662 字节，SHA-256 `01626f7ffe14c54928e9b2eaa09baf1886fa9200b247bbb51895f935f301886c`。40/50 原 ZIP 本地不存在；本次不将 DLL 校验写成其原 ZIP 已重新下载验证。

全部原生组件摘要、具体来源、许可范围和排除内容由构建脚本生成 `组件来源.json`。原始第三方文件按字节复制，不打补丁、不加壳。

## 实际运行闭包

公共组件为 `ffmpeg.exe`、`ffprobe.exe`、`vsr_host.dll`、`nvngx_vsr.dll`、`dlssg_video_worker.exe`、`nvngx_dlssg.dll`、`dlssnr_host_v2.dll`，另附中英 locales 与三系运行库。

桥接固定使用 SDR、host v2、禁用 host 自动回退和 guidance，因此当前入口不使用 legacy host、Torch/RAFT、GUI 或实验 GPU-export。静态 PE 导入检查显示，VSR/神经 host 与补帧 worker 的外部原生导入为 Windows D3D12/DXGI/系统库和 NVIDIA 驱动 API；NVOFA 代码从系统加载 `nvcuda.dll` 与 `nvofapi64.dll`。这些驱动/系统 DLL 不随包复制。

NumPy、OpenCV、PyAV、imageio-ffmpeg 等 Python 依赖继续由内录 MOD 的 `EnhanceHost` 冻结运行时提供。补充包的文件预检不宣称代替 GPU 执行测试；尤其 30/40 系没有对应实体显卡实测。

## 安装与卸载边界

安装专属根目录为 `mods/AzureArchiveDLSS`，运行根为 `runtime`。三系各自在 `runtime/mods/dlss/rtx30`、`rtx40`、`rtx50`，不会互相覆盖。

清单为专属 `installed-files.json`；白名单严格枚举 27 个文件。安装前验证全部 ZIP 项、文件 SHA、固定原生摘要、AA 本体、运行中状态、重解析点、已有文件所有权。覆盖须确认，取消不写安装文件。事务错误回滚。内录配置和 profile 一律不改。

独立未加壳卸载器位于 `mods/AzureArchiveDLSS/卸载DLSS补充MOD.exe`。仅删除本包清单中且摘要未变的文件；额外/修改文件保留，目录仅为空时删除。不会调用内录安装/卸载入口。卸载自身通过身份校验的临时辅助副本完成。

自动连接由共享 `src/DlssComponents.cs` 和内录主实现负责；补充包不会为了连接而写绝对路径进配置。双方安装顺序可独立，组件缺失不会禁用普通 MP4。

## 构建检查

- .NET Framework 4 x64 安装与卸载入口均编译通过，主入口为 `DlssProgram`。
- 构建脚本语法通过。脚本自身不联网，RTX30 缺失或任何固定摘要不匹配时明确中止。
- 三系 DLL 与七个公共原生组件的固定摘要均通过。
- EXE 元数据入口、嵌入资源 `dlss-supplement.payload`、未包含加密外壳资源均通过。
- 初次构建安装负载 27 文件，821817958 字节（783.75 MiB）；ZIP 加清单共 28 项。
- 最终交付 EXE 摘要及大小以 `安装交付/20260929/DLSS补充组件清单.json` 为准。联合安装/卸载安全与真正 GPU 测试由独立测试记录报告，不把本节静态检查当作该测试已通过。

## 第三方许可资料

保留 DLSS5Tool MIT 和原始第三方清单、NVIDIA RTX SDK 文本与 RTX Video SDK PDF、Optical Flow 头文件 BSD 条款、RTX40MFG temporal adapter MIT 条款，以及 FFmpeg GPLv3、原始构建说明、版本和来源记录。NVIDIA 与 FFmpeg 的原始条款未被重新许可为 MIT。

上游原有来源记录没有把公开再分发许可审查标记为完成，本记录也不作此断言。FFmpeg 保留原始源码链接和构建资料，但这些资料不等于随附了整个对应构建的完整源码归档。此处仅记录来源文件状态；本次没有上传或公开发布安装包。
