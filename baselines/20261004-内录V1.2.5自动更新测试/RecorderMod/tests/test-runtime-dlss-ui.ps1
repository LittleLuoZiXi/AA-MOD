#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$GameRoot,
    [ValidateSet('Missing','WrongGeneration','ComponentLoss','Complete','Unsupported')][string]$Mode='Missing',
    [switch]$Tutorial,
    [switch]$SkipBuild,
    [ValidateRange(30,900)][int]$TimeoutSeconds=300
)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$workspace=Split-Path (Split-Path $mod -Parent) -Parent
$game=[IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
$allowed=[IO.Path]::GetFullPath((Join-Path $workspace 'V1实机验收')).TrimEnd('\')+'\'
if(!$game.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath (Join-Path $game 'RTX验证副本.json'))){throw 'Only the explicitly marked V1 UI verification copy may be used.'}
if((Get-Content -LiteralPath (Join-Path $game 'ActiveProfile.txt') -Raw).Trim() -ne 'Recorder'){throw 'This isolated test requires the Recorder profile.'}
foreach($aa in Get-Process -Name AzureArchive -ErrorAction SilentlyContinue){if($aa.Path -eq (Join-Path $game 'AzureArchive.exe')){throw 'The isolated AA is already running.'}}
$evidence=Join-Path (Split-Path $game -Parent) ('验证记录/runtime-gate-'+$Mode+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$null=New-Item -ItemType Directory -Path $evidence
$version='1.0.0'
$installedMod=Join-Path $game ('mods/AzureArchiveRecorder/'+$version)
$null=New-Item -ItemType Directory -Force -Path $installedMod
if(!$SkipBuild) {
    . (Join-Path $mod 'packaging/build-r6-support.ps1')
    $context=[pscustomobject]@{Mod=$mod;Game=$game;Build=$evidence}
    Invoke-RecorderCompilation $context 'AzureArchive.Recorder' (Join-Path $installedMod 'AzureArchive.Recorder.dll') -Development | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'tutorial-resource-proof.json')
}
$manifest=Get-Content -LiteralPath (Join-Path $mod 'manifest.json') -Raw|ConvertFrom-Json
$manifest.version_number=$version
$manifest|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $installedMod 'manifest.json') -Encoding utf8
$runtime=Join-Path $game 'mods/AzureArchiveRecorder/runtime'
$referenceRuntime=Join-Path $workspace '安装交付/20260929/构建记录/development-R6-20260929-212103-292b4dd3/stage/mods/AzureArchiveRecorder/runtime'
if(!(Test-Path -LiteralPath (Join-Path $runtime 'EnhanceHost.exe'))){Copy-Item -LiteralPath $referenceRuntime -Destination $runtime -Recurse}
$tool=Join-Path $game 'mods/AzureArchiveDLSS/runtime'
$referenceTool=Join-Path $mod 'packaging/build-dlss-supplement/stage/mods/AzureArchiveDLSS/runtime'
foreach($relative in @('_internal/ffmpeg.exe','_internal/ffprobe.exe','_internal/vsr_host.dll','_internal/nvngx_vsr.dll','_internal/dlssg_video_worker.exe','_internal/nvngx_dlssg.dll','_internal/dlssnr_host_v2.dll','_internal/locales/zh_CN.json','_internal/locales/en_US.json','mods/dlss/rtx50/nvngx_dlssnr.dll')) {
    $destination=Join-Path $tool $relative;$source=Join-Path $referenceTool $relative
    $null=New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent)
    if(Test-Path -LiteralPath $destination){if((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $source).Hash){throw ('Isolated component was modified; refuse to overwrite: '+$destination)}}else{Copy-Item -LiteralPath $source -Destination $destination}
}
$config=Join-Path $game 'profiles/Recorder/configs/azurearchive.recorder.cfg'
$before=[IO.File]::ReadAllBytes($config)
$state=Join-Path (Split-Path $config -Parent) 'azurearchive.recorder-state'
$stateExisted=Test-Path -LiteralPath $state
$stateBackup=Join-Path $evidence 'original-sequence-state'
if($stateExisted){Copy-Item -LiteralPath $state -Destination $stateBackup -Recurse}
$moved=$null;$backup=$null;$proc=$null;$lossDone=$false;$restored=$false
function Move-TestComponent([string]$File) {
    $script:moved=[IO.Path]::GetFullPath($File);$script:backup=Join-Path $evidence ('held-'+[IO.Path]::GetFileName($File))
    if(!$script:moved.StartsWith($game+'\',[StringComparison]::OrdinalIgnoreCase) -or !$script:backup.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Component move escaped the explicit V1 test area.'}
    if(Test-Path -LiteralPath $script:backup){throw 'A held component already exists.'}
    Move-Item -LiteralPath $script:moved -Destination $script:backup
}
function Restore-TestComponent {
    if($script:backup -and (Test-Path -LiteralPath $script:backup)) {
        if(Test-Path -LiteralPath $script:moved){Remove-Item -LiteralPath $script:moved}
        Move-Item -LiteralPath $script:backup -Destination $script:moved
    }
}
try {
    if($Mode -eq 'Missing'){Move-TestComponent (Join-Path $tool '_internal/vsr_host.dll')}
    if($Mode -eq 'WrongGeneration'){
        Move-TestComponent (Join-Path $tool 'mods/dlss/rtx50/nvngx_dlssnr.dll')
        Copy-Item -LiteralPath (Join-Path $referenceTool 'mods/dlss/rtx40/nvngx_dlssnr.dll') -Destination $moved
    }
    $lines=@('[Recording]',('OutputDirectory = '+(Join-Path $evidence 'jobs')),('FFmpegPath = '+(Join-Path $runtime 'ffmpeg/ffmpeg.exe')),
        'FrameRate = 30','QualityCRF = 18','HardwareEncoding = true','AsyncReadback = true','[DLSS]',('ToolDirectory = '+$tool),
        'Enabled = true','SuperResolution = 4','FrameMultiplier = 4','NeuralRendering = true','TimeoutMinutes = 2','[Tutorial]',('SeenVersion = '+$(if($Tutorial){0}else{1})))
    [IO.File]::WriteAllText($config,($lines -join "`r`n"))
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $game 'AzureArchive.exe'))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$game
    foreach($arg in @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $evidence 'player.log'),
        '--aa-recorder-smoke',(Join-Path $mod 'test-fixture/RecorderSmoke.aap2'),'--aa-recorder-smoke-output',(Join-Path $evidence 'published'),'--aa-recorder-smoke-ui','--aa-recorder-smoke-evidence',$evidence)){$start.ArgumentList.Add($arg)}
    if($Tutorial){$start.ArgumentList.Add('--aa-recorder-tutorial-smoke')}
    if($Mode -eq 'Unsupported'){$start.ArgumentList.Add('--aa-recorder-test-unsupported-gpu')}
    if($Mode -eq 'ComponentLoss'){$start.ArgumentList.Add('--aa-recorder-test-component-loss')}
    $proc=[Diagnostics.Process]::Start($start)
    $proc.Id|Set-Content -LiteralPath (Join-Path $evidence 'aa.pid')
    Write-Output ('V1 UI test PID '+$proc.Id+': '+$evidence)
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while(!$proc.WaitForExit(500)) {
        if($clock.Elapsed.TotalSeconds -gt $TimeoutSeconds){$proc.Kill($true);$proc.WaitForExit();throw 'Isolated UI verification timed out.'}
        if($Mode -eq 'ComponentLoss') {
            if(!$lossDone -and (Test-Path -LiteralPath (Join-Path $evidence 'component-loss-ready.json'))){Move-TestComponent (Join-Path $tool '_internal/vsr_host.dll');$lossDone=$true}
            if($lossDone -and !$restored -and (Test-Path -LiteralPath (Join-Path $evidence 'component-restore-ready.json'))){Restore-TestComponent;$restored=$true}
        }
    }
    Copy-Item -LiteralPath (Join-Path $game 'BepInEx/LogOutput.log') -Destination (Join-Path $evidence 'bepinex.log')
    Copy-Item -LiteralPath $config -Destination (Join-Path $evidence 'config-after-run.cfg')
    if($proc.ExitCode -ne 0){throw ('AA test returned '+$proc.ExitCode)}
    $checks=@(Get-Content -LiteralPath (Join-Path $evidence 'smoke-ui-dlss-checks.jsonl') | ForEach-Object {$_|ConvertFrom-Json})
    if(@($checks|Where-Object {!$_.passed}).Count -gt 0){throw 'UI assertions failed.'}
    if($Mode -in @('Missing','WrongGeneration','ComponentLoss') -and !@($checks|Where-Object {$_.check -like '*exact missing-component message is red'}).Count){throw 'Missing exact red-message proof.'}
    if($Mode -eq 'ComponentLoss' -and (!$lossDone -or !$restored)){throw 'Component-loss exercise did not finish.'}
    $completed=@(Get-ChildItem -LiteralPath (Join-Path $evidence 'jobs') -Filter completed.json -Recurse)
    if($completed.Count -ne 1){throw 'Expected one completed ordinary recording.'}
    $video=Get-Content -LiteralPath $completed[0].FullName -Raw|ConvertFrom-Json
    if($video.enhanced -or $video.frames -le 0 -or !(Test-Path -LiteralPath $video.output)){throw 'Saved enhancement settings affected the ordinary recording.'}
    if(@(Get-ChildItem -LiteralPath (Join-Path $evidence 'jobs') -Filter enhancement-job.json -Recurse).Count -ne 0){throw 'Ordinary recording invoked the enhancement bridge.'}
    $summary=[ordered]@{mode=$Mode;game=$game;aaExitCode=$proc.ExitCode;wallSeconds=$clock.Elapsed.TotalSeconds;assertions=$checks.Count;tutorial=$Tutorial.IsPresent;componentRemoved=$lossDone;componentRestored=$restored;ordinaryVideo=$video.output;frames=$video.frames;enhanced=$video.enhanced;modSha256=(Get-FileHash -LiteralPath (Join-Path $installedMod 'AzureArchive.Recorder.dll')).Hash}
    $summary|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'PASS.json') -Encoding utf8
    $summary|ConvertTo-Json
} finally {
    if($proc -and !$proc.HasExited){$proc.Kill($true);$proc.WaitForExit()}
    Restore-TestComponent
    [IO.File]::WriteAllBytes($config,$before)
    $resolvedState=[IO.Path]::GetFullPath($state)
    $configRoot=[IO.Path]::GetFullPath((Join-Path $game 'profiles/Recorder/configs')).TrimEnd('\')+'\'
    if(!$resolvedState.StartsWith($configRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Refuse sequence-state cleanup outside the isolated profile.'}
    if(Test-Path -LiteralPath $resolvedState){Remove-Item -LiteralPath $resolvedState -Recurse -Force}
    if($stateExisted){Copy-Item -LiteralPath $stateBackup -Destination $state -Recurse}
}
