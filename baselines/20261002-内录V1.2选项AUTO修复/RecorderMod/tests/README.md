# 内录 AUTO 选项回归

`run-choice-smoke.ps1` 与 `choice-fixtures/` 可随本源码一起移动，不依赖旧桌面 ZIP。需要 PowerShell 7、AA 1.0 fix4、已安装并启用的 V1.2 内录及其私有 FFmpeg/ffprobe。将本源码放在 AA 副本的 `RecorderMod` 下。

脚本默认 AA 根目录为 `tests` 向上两级，夹具目录为同级 `choice-fixtures`。输出位于该 AA 根的 `smoke-runs`。只有显式存在 `RecorderChoiceTestRoot.json`、标记的 `root` 与规范化 GameRoot 完全相符、路径均无 reparse point 时才可运行；复制到另一位置的旧标记不能直接复用。

## 创建隔离副本与标记

先保存剧情并关闭将要复制的 AA。下面在桌面创建全新目录；仅在成功复制后的新目录写标记。不要把真实游戏安装目录（例如 `E:\AzureArchive_100_1001`）赋给 `$testGame`，不要直接给真实安装补写标记。

将 `$sourceGame` 改为已安装 V1.2 且包含本源码 `RecorderMod` 的参考 AA 根目录。命令只读参考安装，保留其原文件；若发现链接则停止。

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
foreach($required in @('AzureArchive.exe','RecorderMod\tests\run-choice-smoke.ps1','mods\AzureArchiveRecorder\1.2.0\AzureArchive.Recorder.dll')){
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

若启用的 profile 还列出其他 MOD，先在此副本的 profile 中仅启用内录 V1.2；不要改参考安装。标记只表明测试副本的用途，不替代 AA 和 MOD 的完整安装。

## 六例顺序

每条命令完成后再运行下一条。同一个副本一次只能有一个 AA 进程；脚本也会锁住 `smoke-runs/.driver.lock`。保留 `-ChoiceProbe` 可在日志中记录分支执行。没有默认项的用例放最后，以便单独检查其受控停止。

```powershell
& $runner -GameRoot $testGame -Fixture ChoiceSecondAuto -Fps 24 -ChoiceProbe
& $runner -GameRoot $testGame -Fixture ChoiceSecondAuto -Fps 60 -ChoiceProbe
& $runner -GameRoot $testGame -Fixture ChoiceConsecutiveAuto -Fps 24 -ChoiceProbe
& $runner -GameRoot $testGame -Fixture ChoiceDirectEnd -Fps 24 -ChoiceProbe
& $runner -GameRoot $testGame -Fixture PlainNoChoice -Fps 24 -ChoiceProbe
& $runner -GameRoot $testGame -Fixture ChoiceNoDefault -Fps 24 -ChoiceProbe
```

此入口只做普通录制，不传 `--aa-recorder-smoke-enhance`，不执行 DLSS helper、下载或 GPU 增强测试。脚本只使用副本 `mods/AzureArchiveRecorder/runtime/ffmpeg` 中的程序，并临时设置固定帧率、私有输出目录、MaxMinutes=1；结束时按原始字节恢复本副本配置。不要与安装、卸载或另一个回归同时操作同一副本。

## 判读证据

每次运行保存 `invocation.json`、原始/有效/运行后配置、`summary.json`、BepInEx 日志、捕获完成数据、MP4 和 ffprobe 输出。默认 120 秒的驱动超时只会结束本次启动的 AA 进程树；超时不算成功。`-DryRun` 不启动 AA，但仍会创建证据并临时改写、恢复配置。

- `ChoiceSecondAuto`：默认第二项恰好执行一次，进入 PASS 分支并自然结束；24/60 fps 都应能完成。
- `ChoiceConsecutiveAuto`：连续两组选项都执行其默认第二项，最终进入 PASS 并结束。
- `ChoiceDirectEnd`：默认第二项经一个空对白节点桥接到结束，录制完成一次。原生 choice 直接连接 exit 的原稿未建立编辑会话，未把它当作已验证夹具。
- `PlainNoChoice`：普通旁白结束，不进入选项。
- `ChoiceNoDefault`：不猜测任何分支；观察明确诊断或 MaxMinutes 限制下的受控停止，不将驱动强制超时当作修复成功。

`choice-fixtures/expectations.json` 是原始夹具预期，其 `native_validation` 字段保留创建时的 pending 文本，不代表最新运行结果。实际结果以对应运行证据为准。宿主扫描并跳过分支时也可能记录 `WRONG_*` 文本，不能用“原始日志中完全没有 WRONG”判定；应结合实际分支执行日志、结束状态和视频内容。

本交接只做脚本 AST 解析和夹具逐字节复制验证；不在准备阶段额外启动 AA。其他历史 DLSS/RTX 测试脚本不属于这六例回归。