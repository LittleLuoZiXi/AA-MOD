# 内录 AUTO 选项回归

当前默认直接录制：不传 `-MeasureTotal` 时不预演。本文的两阶段验收只适用于显式开启 `-MeasureTotal`；最新实现和已执行结果以 `../V1.2.1-NATIVE-TIMING-MENU.md` 为准。完整时长对比不要加 UI 截图或逐字诊断负载。

`run-choice-smoke.ps1` 与 `choice-fixtures/` 可随本源码一起移动，不依赖旧桌面 ZIP。需要 PowerShell 7、AA 1.0 fix4、已安装并启用的 V1.2.1 内录及其私有 FFmpeg/ffprobe。将本源码放在 AA 副本的 `RecorderMod` 下。

脚本默认 AA 根目录为 `tests` 向上两级，夹具目录为同级 `choice-fixtures`。输出位于该 AA 根的 `smoke-runs`。只有显式存在 `RecorderChoiceTestRoot.json`、标记的 `root` 与规范化 GameRoot 完全相符、路径均无 reparse point 时才可运行；复制到另一位置的旧标记不能直接复用。

## 创建隔离副本与标记

先保存剧情并关闭将要复制的 AA。下面在桌面创建全新目录；仅在成功复制后的新目录写标记。不要把真实游戏安装目录（例如 `E:\AzureArchive_100_1001`）赋给 `$testGame`，不要直接给真实安装补写标记。

将 `$sourceGame` 改为已安装 V1.2.1 且包含本源码 `RecorderMod` 的参考 AA 根目录。命令只读参考安装，保留其原文件；若发现链接则停止。

```powershell
$ErrorActionPreference='Stop'
$sourceGame=[IO.Path]::GetFullPath('D:\AA-Reference')
$testGame=Join-Path ([Environment]::GetFolderPath('Desktop')) ('AA-ChoiceSmoke-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6))
$testGame=[IO.Path]::GetFullPath($testGame)
if(Test-Path -LiteralPath $testGame){throw 'Choose a new isolated destination.'}
if($testGame.Equals($sourceGame,[StringComparison]::OrdinalIgnoreCase) -or $testGame.StartsWith($sourceGame+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'The isolated destination must be outside the reference installation.'}
function Assert-NoLinks([string]$path){
    for($cursor=[IO.Path]::GetFullPath($path);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Linked path rejected: $cursor"}
    }
}
function Copy-IsolatedEntry([string]$source,[string]$destination){
    Assert-NoLinks $source
    Assert-NoLinks $destination
    $item=Get-Item -LiteralPath $source -Force
    if($item.PSIsContainer){
        $null=New-Item -ItemType Directory -Path $destination
        foreach($child in Get-ChildItem -LiteralPath $source -Force){
            if($child.Name -in @('cache','logs','__pycache__','smoke-runs')){continue}
            if(-not $child.PSIsContainer -and $child.Extension -in @('.log','.tmp')){continue}
            Copy-IsolatedEntry $child.FullName (Join-Path $destination $child.Name)
        }
    }else{Copy-Item -LiteralPath $source -Destination $destination}
}
Assert-NoLinks $sourceGame
Assert-NoLinks $testGame
foreach($required in @('AzureArchive.exe','RecorderMod\tests\run-choice-smoke.ps1','mods\AzureArchiveRecorder\1.2.1\AzureArchive.Recorder.dll')){
    if(-not(Test-Path -LiteralPath (Join-Path $sourceGame $required) -PathType Leaf)){throw "Missing required input: $required"}
}
$null=New-Item -ItemType Directory -Path $testGame
foreach($name in @('.doorstop_version','ActiveProfile.txt','AzureArchive.exe','baselib.dll','doorstop_config.ini','GameAssembly.dll','UnityCrashHandler64.exe','UnityPlayer.dll','winhttp.dll','AzureArchive_Data','D3D12','dotnet','BepInEx','profiles','RecorderMod')){
    $source=Join-Path $sourceGame $name
    if(Test-Path -LiteralPath $source){Copy-IsolatedEntry $source (Join-Path $testGame $name)}
}
$null=New-Item -ItemType Directory -Path (Join-Path $testGame 'mods')
Copy-IsolatedEntry (Join-Path $sourceGame 'mods\AzureArchiveRecorder') (Join-Path $testGame 'mods\AzureArchiveRecorder')
$marker=[ordered]@{schema_version=1;kind='RecorderChoiceSmokeIsolatedCopy';root=$testGame;source_game=$sourceGame;created_utc=[DateTime]::UtcNow.ToString('o')}
[IO.File]::WriteAllText((Join-Path $testGame 'RecorderChoiceTestRoot.json'),($marker|ConvertTo-Json)+"`n",[Text.UTF8Encoding]::new($false))
$runner=Join-Path $testGame 'RecorderMod\tests\run-choice-smoke.ps1'
```

若启用的 profile 还列出其他 MOD，先在此副本的 profile 中仅启用内录 V1.2.1；不要改参考安装。标记只表明测试副本的用途，不替代 AA 和 MOD 的完整安装。

## 普通启动、入口与固定总时长

每条命令完成后再运行下一条，同一个副本一次只能有一个 AA 进程。以下检查均不启用 ChoiceProbe：普通启动只验证无诊断参数时主插件成功加载；入口检查另行验证原生剧情页按钮。不要以旧 V1.2 的 ChoiceProbe 回归替代这两项。

```powershell
$entryRunner=Join-Path $testGame 'RecorderMod\tests\run-entry-smoke.ps1'
& $entryRunner -GameRoot $testGame -StartupOnly
& $entryRunner -GameRoot $testGame
& $runner -GameRoot $testGame -Fixture PlainNoChoice -Fps 24 -InspectProgress
& $runner -GameRoot $testGame -Fixture ChoiceNoDefault -Fps 24 -InspectProgress
```

`ChoiceNoDefault` 是预期拒绝用例：AA 和驱动退出码应为 1，未超时，零已发布 MP4；直接模式可能已启动捕获并保留临时数据，启用预演时则在启动正式编码器前拒绝；红色错误面板文字必须为“剧情中存在可选项，并且未标记 auto，故无法进行内录。”。驱动将非零退出码保留在证据中，不应将这次明确拒绝误报为导出失败，也不能把强制超时视为拒绝测试通过。

纯 C# 固定时长计划测试无需启动 AA：

```powershell
& (Join-Path $testGame 'RecorderMod\tests\test-recording-duration.ps1')
```

测试覆盖计算中状态、有效帧数/FPS、固定标签、重复冻结拒绝、Reset 后的新任务及非法输入；正式录制进度不会改写冻结的预计值。

## AUTO 路线回归

同一副本中的录制驱动会锁住 `smoke-runs/.driver.lock`。`-ChoiceProbe` 可补充分支诊断日志，但不得作为普通启动验证的前提。默认用例直接录制一次；加上 `-MeasureTotal` 才先预演、再从相同快照正式导出。启用预演时分别检查两个阶段的选择事件，不将两遍合计两次选择误判为单节点重复提交。

```powershell
& $runner -GameRoot $testGame -Fixture ChoiceSecondAuto -Fps 24 -ChoiceProbe -InspectProgress
& $runner -GameRoot $testGame -Fixture ChoiceSecondAuto -Fps 60 -ChoiceProbe -InspectProgress
& $runner -GameRoot $testGame -Fixture ChoiceConsecutiveAuto -Fps 24 -ChoiceProbe -InspectProgress
& $runner -GameRoot $testGame -Fixture ChoiceDirectEnd -Fps 24 -ChoiceProbe -InspectProgress
& $runner -GameRoot $testGame -Fixture PlainNoChoice -Fps 24 -InspectProgress
& $runner -GameRoot $testGame -Fixture ChoiceNoDefault -Fps 24 -InspectProgress
```

此入口只做普通录制，不传 `--aa-recorder-smoke-ui` 或 `--aa-recorder-smoke-enhance`，不执行 DLSS helper、下载或 GPU 增强测试。脚本只使用副本 `mods/AzureArchiveRecorder/runtime/ffmpeg` 中的程序，并临时设置成品帧率、私有输出目录、MaxMinutes（默认 1）；结束时按原始字节恢复本副本配置。不要与安装、卸载或另一个回归同时操作同一副本。

## 标题与内置背景回归

`TitleNoChoice` 是独立合成夹具，使用 AA 内置 `BG_View_Schale`（ID `2460344605`），不含用户图片或其他资源。entry 编译为 `#title;Recorder title regression;Title animation`，之后是普通旁白与结束。它覆盖背景实际渲染通知解除标题等待的路径，保留 `PlainNoChoice` 原有夹具。

```powershell
& $runner -GameRoot $testGame -Fixture TitleNoChoice -Fps 30 -MaxMinutes 1 -TimeoutSeconds 120 -InspectProgress
```

应验证直接录制越过标题并自然结束；若加 `-MeasureTotal`，还应验证预演和正式导出都自然结束；不能以超时或跳过标题为通过条件。当前本地修复机制与已执行范围见 `../V1.2.1-TITLE-HOTFIX.md`，这些命令本身不表示最终保护制品已验证。

## 使用现有 .aas 复现

`-ScenarioPath` 指定已存在的 `.aas` 时，覆盖内置 `-Fixture` 的输入选择。驱动只读源文件，复制到本次 `smoke-runs/.../input`；若存在与文件同名、不含扩展名的资源目录，也仅复制此目录。例如 `scenario.aas` 对应旁边的 `scenario/`，不复制整个资源库。扫描和复制时拒绝 reparse point。缺失的原始资源不会由驱动补造。

```powershell
$scenario='C:\MyStory\scenario.aas'
& $runner -GameRoot $testGame -ScenarioPath $scenario -Fps 30 -MaxMinutes 10 -TimeoutSeconds 1800 -InspectProgress
```

允许帧率为 24、30、60。`-MaxMinutes` 默认 1，范围 1..120，是本次录制配置的剧情视频时长上限；`-TimeoutSeconds` 默认 120，范围 60..1800，是整个 AA 测试进程的实际运行超时。二者独立，长剧情需留出实际录制与收尾时间；启用统计才额外留出完整预演时间；超过上限即停止，不能把截断结果当成自然完成。

`invocation.json` 记录 `fixture_source`、`source_sha256`、隔离副本的 `input_sha256`、同名资源源目录和逐文件 SHA，以及本次帧率、MaxMinutes、TimeoutSeconds。仍须匹配隔离标记，运行锁、路径边界、私有 FFmpeg 和配置字节还原保持不变；本入口不启用 DLSS。

真实 `.aas`、同名资源、剧情文本、图片、音频及运行产生的输入副本、截图、视频和完整日志只保留在本地隔离运行目录。不得把这些用户内容加入 `choice-fixtures`、源码归档或发布包；可共享的回归输入只使用合成夹具。文档只引用必要的本地运行目录和不含剧情内容的数值证据。

## AA 原生时序对照（本地诊断）

仅显式传入 `-NativeTiming -ScenarioPath <现有.aas>` 才启用 `src/NativeTimingSmoke.cs`。驱动复制相同输入和同名资源到隔离运行目录，使用可见的原生窗口；等待数据库就绪后打开剧情并确保 AUTO 已开启。不进入内录捕获，不创建内录进度 UI，不编码视频，不设置 `Time.captureFramerate` 或 `Time.timeScale`。仍须满足隔离标记、路径边界和运行锁要求，一次只运行一项。

```powershell
# 保留 AA 启动时的目标帧率和垂直同步设置，观察到达索引 20。
& $runner -GameRoot $testGame -ScenarioPath $scenario -NativeTiming -NativeStopRow 20 -TimeoutSeconds 120 -ChoiceProbe
# 同一输入的原生 30 fps 对照；仍不是固定捕获时钟。
& $runner -GameRoot $testGame -ScenarioPath $scenario -NativeTiming -NativeFps 30 -NativeStopRow 20 -TimeoutSeconds 120 -ChoiceProbe
```

`-NativeFps` 允许 0、30、60，默认 0 表示保持原设置，不能直接视为无限帧率；实际值见开始证据。指定 30/60 时，仅在原生 `Test.Start` 临时设置 `Application.targetFrameRate` 并关闭垂直同步，在 `Test.End` 后恢复原值，不写持久用户 settings。`-Fps`、`-MaxMinutes` 是内录参数，不控制原生模式；原生模式不临时改写录制配置，驱动结束时仍按原始字节还原配置。原生时钟的 `captureFramerate` 必须保持 0。

`-NativeStopRow N` 默认为 0（自然结束的完整路线）；正数表示到达脚本索引 `cur >= N` 后调用原生结束。此时 `partial=true`、`completed=false`，即使退出码为 0 也只表示诊断截断成功，不能作为整篇时长或完整回归结果。需要完整路线时去掉该参数，并给 `-TimeoutSeconds` 留足墙钟时间；超时不算自然结束。原生模式不能搭配 `-InspectProgress` 或 `-CancelPhase`，非原生模式不能使用非零 NativeFps/NativeStopRow。

证据包括 `native-timing-start.json`、每次索引变化和 OnReady 的 `native-timing-events.jsonl`、`native-timing-result.json`（同内容 `result.json`）及驱动 `summary.json`。结果记录墙钟、Unity 时间、真实经过帧数/平均 FPS、AUTO 延迟、文字间隔与起止 settings；缺少结果或观察到非零 captureFramerate 时，驱动判为无效基线。`-ChoiceProbe` 另外显式启用 `src/TimingDiagnostics.cs` 的逐字协程观察，在 BepInEx 日志记录 native/offline、WaitForSeconds、帧号与时间；它不改变等待值。日志含字符码点和长度，仍属于本地用户输入证据，不纳入源码包。

对照须使用相同输入 SHA、AUTO 路线、文字速度、autoDelay 和资源条件，并对齐相同脚本索引。AA 原生等待的恢复受帧调度影响；MOD 的固定 30 fps 可触发这类原生帧率依赖，因此不能把差异概括为“与 MOD 完全无关”。先比较原生默认、原生 30 fps 与离屏 30 fps 的同段事件，再区分 AA 自身行为和 MOD 计时错误；部分路线只用于定位，不能外推为完整视频结论。按当前要求，确认属于 AA 原生时序后不做 MOD 时长补偿或等待值修正。当前仅做本地诊断，GitHub 上传继续暂停。

## 判读证据

每次录制运行保存 `invocation.json`、原始/有效/运行后配置、`summary.json`、BepInEx 日志，以及实际产生的预演、导出、MP4 和 ffprobe 证据。默认 120 秒的驱动超时只会结束本次启动的 AA 进程树；超时不算成功。`-DryRun` 不启动 AA，但仍会创建证据；普通录制模式临时改写、恢复配置，原生计时模式不做该配置改写。

仅开启 `-MeasureTotal` 的成功用例应有 `preflight-start.json`、`preflight-events.jsonl` 和 `preflight.json`，其 `encoderStarted=false`、`gpuReadback=false`。预演完整执行 AUTO 路线，记录 AA 原生时间步长，原生音频照常推进但临时静音；此时不编码、不读取 GPU 图像。之后才有正式 `recording.json`、`script-events.jsonl` 和 `completed.json`。

`preflight.json` 的 frames/FPS 确定预计总时长，导出前 `RecordingDurationPlan.Freeze()` 后不可改写。开启统计时，`-InspectProgress` 保存的 `recording-progress.json` 应显示已生成时长和固定预计总时长。默认直接模式须没有总时长与数值百分比，且不存在任何预演证据文件。`completed.json` 分别记录实际 `frames`/`videoSeconds`、冻结的 `plannedFrames`/`plannedVideoSeconds` 及 `durationDifferenceSeconds`。允许首次资源加载带来小幅偏差；不能事后修改预计值、截断或填帧。预演无 AUTO、重复进入同一节点或达到配置时长/等待上限时，应在正式导出前拒绝。

- `ChoiceSecondAuto`：预演和正式录制分别只执行默认第二项一次，进入 PASS 分支并自然结束；24/60 fps 都应能完成。
- `ChoiceConsecutiveAuto`：每一阶段的连续两组选项都各执行其默认第二项一次，最终进入 PASS 并结束。
- `ChoiceDirectEnd`：默认第二项经一个空对白节点桥接到结束，两阶段均完成。原生 choice 直接连接 exit 的原稿未建立编辑会话，未把它当作已验证夹具。
- `PlainNoChoice`：普通旁白预演和导出均自然结束，不进入选项。
- `TitleNoChoice`：内置背景收到实际渲染通知，标题等待自然解除，之后普通旁白和结束在预演、正式导出两阶段均完成。
- `ChoiceNoDefault`：预演拒绝，精确错误文字如上，退出码 1、零 MP4，不等待 MaxMinutes 生成截断视频。

`choice-fixtures/expectations.json` 是原始夹具预期，其 `native_validation` 字段保留创建时的 pending 文本，不代表最新运行结果；旧版无默认项的 MaxMinutes 停止证据也不代表 V1.2.1 行为。实际结果以对应运行证据为准。宿主扫描并跳过分支时也可能记录 `WRONG_*` 文本，不能用“原始日志中完全没有 WRONG”判定；应结合分阶段的实际分支日志、结束状态和视频内容。

这些命令列出当前验证方法，不宣称所有命令已在最新制品执行。旧 V1.2 六例使用 ChoiceProbe，不能证明普通启动入口正常，也未覆盖本次预演方案。既有实际运行范围与结果记录在 `../V1.2.1-VERIFICATION.md`；本次尚未发布的标题/背景修订见 `../V1.2.1-TITLE-HOTFIX.md`，其待验证状态不能由旧结果替代。其他历史 DLSS/RTX 测试不属于本轮回归。
## 成片发布后的临时文件清理

运行 `./tests/test-encoder-cleanup.ps1`，使用本地 `packaging/ffmpeg-vendor/ffmpeg.exe` 与 `ffprobe.exe`；不启动 AA、不使用 DLSS。测试正常清理、临时视频真实文件锁、只读音频、清理日志不可写和成品路径冲突。临时文件清理失败应保留素材并返回已生成的成片；真正的成品发布冲突仍须报错且不覆盖既有文件。2026-10-02 的 5 场景 34 检查通过，4 个成功短片都通过 ffprobe 与完整解码。证据写入独立的 `test-output/encoder-cleanup-*`，不纳入源码归档。
