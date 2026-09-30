#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$ToolDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Label,
    [switch]$Enhance,[switch]$Cancel,[switch]$UnsupportedGpu,
    [switch]$TutorialTest,
    [ValidateSet(0,1)][int]$TutorialSeenVersion=1,
    [ValidateSet(1,2,4)][int]$Scale=1,
    [ValidateSet(1,2,3,4)][int]$Multiplier=1,
    [switch]$Neural,
    [ValidateRange(1,1440)][int]$TimeoutMinutes=120,
    [ValidateRange(30,7200)][int]$HarnessTimeoutSeconds=900
)
$ErrorActionPreference='Stop'
$game=[IO.Path]::GetFullPath($GameRoot)
if(!(Test-Path -LiteralPath (Join-Path $game 'RTX验证副本.json'))){throw '仅允许在有 RTX验证副本.json 标记的独立验收副本执行。'}
if((Get-Content -LiteralPath (Join-Path $game 'ActiveProfile.txt') -Raw).Trim() -ne 'Recorder'){throw '此测试需要独立 Recorder profile。'}
$evidence=Join-Path (Split-Path $game -Parent) ('验证记录/AA-'+$Label+'-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
$null=New-Item -ItemType Directory -Path $evidence
$config=Join-Path $game 'profiles/Recorder/configs/azurearchive.recorder.cfg'
$before=[IO.File]::ReadAllBytes($config)
$proc=$null
try {
    $lines=@('[Recording]',('OutputDirectory = '+(Join-Path $evidence 'jobs')),('FFmpegPath = '+(Join-Path $game 'mods/AzureArchiveRecorder/runtime/ffmpeg/ffmpeg.exe')),
        'FrameRate = 30','QualityCRF = 18','HardwareEncoding = true','AsyncReadback = true','[DLSS]',('ToolDirectory = '+[IO.Path]::GetFullPath($ToolDirectory)),
        ('SuperResolution = '+$Scale),('FrameMultiplier = '+$Multiplier),('NeuralRendering = '+$Neural.IsPresent.ToString().ToLowerInvariant()),('TimeoutMinutes = '+$TimeoutMinutes),
        '[Tutorial]',('SeenVersion = '+$TutorialSeenVersion))
    [IO.File]::WriteAllText($config,($lines -join "`r`n"))
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $game 'AzureArchive.exe'))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$game
    foreach($arg in @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $evidence 'player.log'),
        '--aa-recorder-smoke',[IO.Path]::GetFullPath($Source),'--aa-recorder-smoke-output',(Join-Path $evidence 'published'),'--aa-recorder-smoke-ui','--aa-recorder-smoke-evidence',$evidence)){$start.ArgumentList.Add($arg)}
    if($Enhance){$start.ArgumentList.Add('--aa-recorder-smoke-enhance')}
    if($Cancel){$start.ArgumentList.Add('--aa-recorder-smoke-cancel')}
    if($UnsupportedGpu){$start.ArgumentList.Add('--aa-recorder-test-unsupported-gpu')}
    if($TutorialTest){$start.ArgumentList.Add('--aa-recorder-tutorial-smoke')}
    $proc=[Diagnostics.Process]::Start($start)
    $proc.Id | Set-Content -LiteralPath (Join-Path $evidence 'aa.pid')
    Write-Output "AA smoke PID $($proc.Id): $evidence"
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $gpuLog=Join-Path $evidence 'gpu-samples.csv'
    'seconds,memory_used_mib,gpu_utilization_percent' | Set-Content -LiteralPath $gpuLog
    $peak=0
    while(!$proc.WaitForExit(1000)){
        if($watch.Elapsed.TotalSeconds -gt $HarnessTimeoutSeconds){$proc.Kill($true);$proc.WaitForExit();throw '验收脚本超时；已结束本测试启动的 AA 进程树。'}
        $raw= & nvidia-smi --query-gpu=memory.used,utilization.gpu --format=csv,noheader,nounits
        if($LASTEXITCODE -eq 0){
            $parts=($raw -split ',');$peak=[Math]::Max($peak,[int]$parts[0].Trim())
            ('{0:F3},{1}' -f $watch.Elapsed.TotalSeconds,$raw.Trim()) | Add-Content -LiteralPath $gpuLog
        }
    }
    Copy-Item -LiteralPath (Join-Path $game 'BepInEx/LogOutput.log') -Destination (Join-Path $evidence 'bepinex.log')
    Copy-Item -LiteralPath $config -Destination (Join-Path $evidence 'config-after-run.cfg')
    $sourceBridge=Join-Path $game 'RecorderMod/bridge/enhance.py'
    $summary=[ordered]@{label=$Label;game=$game;source=[IO.Path]::GetFullPath($Source);enhance=$Enhance.IsPresent;scale=$Scale;multiplier=$Multiplier;neural=$Neural.IsPresent;
        cancelledTest=$Cancel.IsPresent;unsupportedGpuSimulation=$UnsupportedGpu.IsPresent;tutorialTest=$TutorialTest.IsPresent;tutorialSeenVersionBefore=$TutorialSeenVersion;
        aaExitCode=$proc.ExitCode;wallSeconds=$watch.Elapsed.TotalSeconds;peakDeviceMemoryMiB=$peak;
        modSha256=(Get-FileHash -LiteralPath (Join-Path $game 'mods/AzureArchiveRecorder/0.2.1/AzureArchive.Recorder.dll')).Hash;
        bridgeMode=if(Test-Path -LiteralPath $sourceBridge){'development source'}else{'installed frozen runtime'};
        bridgeSha256=if(Test-Path -LiteralPath $sourceBridge){(Get-FileHash -LiteralPath $sourceBridge).Hash}else{(Get-FileHash -LiteralPath (Join-Path $game 'mods/AzureArchiveRecorder/runtime/EnhanceHost.exe')).Hash}}
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'run-summary.json') -Encoding utf8
    $summary | ConvertTo-Json -Compress
} finally {
    if($proc -and !$proc.HasExited){$proc.Kill($true);$proc.WaitForExit()}
    [IO.File]::WriteAllBytes($config,$before)
}
