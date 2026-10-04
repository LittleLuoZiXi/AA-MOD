# V1.2.5 自动更新验证版交接

本版以完整 V1.2.4 的 269 文件源码索引逐文件复制为起点，仅修改版本号、发行显示和版本兼容元数据，用于正式 AA 从 V1.2.4 自动更新至 V1.2.5 的用户验收。没有录制、预演、进度、设置记忆、自动更新流程或 DLSS 功能变更。

当前源码：D:\test2\开发\V1.2.5\RecorderMod。交付目录位于上一级的交付文件夹。旧版 CONTINUE-V1.2.4.md、UPDATE-VERIFICATION-V1.2.4.json 和其他历史文档保留原样，其中状态、路径和测试次数属于历史快照。

版本变更涉及插件常量和日志、BepInEx manifest、程序集版本、加载器和 EXE 外壳版本、安装器版本显示、HTTP User-Agent、构建产物命名，以及构建/验证脚本接受本版版本号。Installer 的旧版 1.2.4 兼容项与最早更新助手版本 1.2.4 保留；旧版目录检测和保护构建的 LegacyFiles 枚举新增 1.2.4。版本专用测试只改变目标版本预期，不改生产算法。

保护构建入口：packaging/build-release.ps1。使用 PowerShell 7，传入正式 AA 引用目录、单独交付目录与已验证冻结 runtime；离线复制的 .venv、packaging/tools、packaging/ffmpeg-vendor 仅供本机构建，排除于源码索引和源码 ZIP。没有下载或安装 DLSS。构建会验证冻结桥接源码、六页教程资源、保护 DLL、全部 EXE 保护封装和 payload 文件清单。

发布时必须先完整上传 source 和更新 ZIP，以及加壳安装器；核实真实下载的大小与 SHA256 后，最后发布 updates/recorder/stable.json。feed 的 Version 为 1.2.5、MinimumVersion 为 1.2.4，使用真实基线路径和 GitHub Release 资产 URL，不能使用安装 EXE 代替 update ZIP。具体协议见 UPDATE-PROTOCOL-V1.2.4.md。

正式 AA 保持 V1.2.4 留给用户测试：保存剧情，打开内录面板，在后台发现新版本后确认更新；可检查下载暂停/继续/终止。下载验证并替换完成后，单击“内录MOD更新完成，AA即将重启”的 OK，AA 正常退出后由助手重新启动；重启后应为 V1.2.5。未由本次构建流程代替用户执行此正式更新。

本版验证与产物摘要记录在交付/交付校验记录.json、源码文件索引.json、源码变更清单.json 和验证记录目录。已有 V1.2.4 结果仅作未改动功能的历史依据，本版不重跑长剧情或 NVIDIA 功能。