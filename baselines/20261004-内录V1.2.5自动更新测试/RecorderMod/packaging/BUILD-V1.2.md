# V1.2 加壳安装包构建

构建在桌面 `test2/work` 隔离 AA 副本中进行。`source-v1.1` 为固定 GitHub 基线，保持只读。

当前已准备：
- PowerShell 7.6.5 自带 Roslyn；系统 .NET Framework 4.0 编译器。
- `RecorderMod/.venv`：Python 3.12.14、PyInstaller 6.11.1，仅用于只读冻结身份校验。
- `packaging/tools/obfuscar`：从原桌面 `AA录制MOD.zip` 精确提取。
- `packaging/ffmpeg-vendor`：元数据来自基线，两程序来自已安装 AA 的副本，SHA-256 与基线脚本固定值一致。
- 冻结 helper 使用 `work/mods/AzureArchiveRecorder/runtime`。无需运行 `build-enhancer.ps1`，不重建或执行增强组件。

源码完成后，在 PowerShell 7 运行：

```powershell
& 'C:\Users\Luo Tian Xing\Desktop\test2\work\RecorderMod\packaging\build-release.ps1' `
  -GameRoot 'C:\Users\Luo Tian Xing\Desktop\test2\work' `
  -OutputDirectory 'C:\Users\Luo Tian Xing\Desktop\test2\安装交付\V1.2' `
  -FrozenRuntimeDirectory 'C:\Users\Luo Tian Xing\Desktop\test2\work\mods\AzureArchiveRecorder\runtime'
```

脚本创建全新构建记录，不复用旧 stage，产物为 `AzureArchive内录V1.2-加壳安装.exe`。构建后对返回的 `build-proof.json` 运行：

```powershell
& 'C:\Users\Luo Tian Xing\Desktop\test2\work\RecorderMod\packaging\verify-r6-payload.ps1' -BuildProof '<本次构建记录中的 build-proof.json 绝对路径>'
```

打包准备验证：
- V1.2 Core 43 项通过，含 V1.1 加壳/未加壳升级、取消、回滚、用户修改保留和跨版本文件归属拒绝。
- 证据：`test2/V1.2安装测试/test-20261002-000625-753237f0`。
- 冻结身份只读校验 42 项通过，证据：`test2/build-dependencies/frozen-runtime-source-check-v12.json`。
- 未运行 Network、Artifacts、DLSS、UI 测试套件，未启动游戏。
- 独立 DLSS 产品身份、安装逻辑和源文件保持基线状态；此构建不捆绑 DLSS 原生资源。