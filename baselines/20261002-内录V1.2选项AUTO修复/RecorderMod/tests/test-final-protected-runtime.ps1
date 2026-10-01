#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][string]$Proof,
    [Parameter(Mandatory)][string]$LocalComponentRoot
)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$workspace=Split-Path (Split-Path $mod -Parent) -Parent
$game=[IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
$allowed=[IO.Path]::GetFullPath((Join-Path $workspace 'V1实机验收')).TrimEnd('\')+'\'
if(!$game.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath (Join-Path $game 'RTX验证副本.json'))){throw 'A marked isolated V1 AA copy is required.'}
if((Split-Path $game -Leaf) -ne 'AA-UI-20260929'){throw 'This final test owns only AA-UI-20260929.'}
foreach($aa in Get-Process -Name AzureArchive -ErrorAction SilentlyContinue){if($aa.Path -eq (Join-Path $game 'AzureArchive.exe')){throw 'The isolated AA is already running.'}}
$proofData=Get-Content -LiteralPath $Proof -Raw|ConvertFrom-Json
$expected='75B8F744824AC2D4592DBC220481AE548E2FDB2B08A80FAEFE8BFFB55058D1D4'
if($proofData.PluginSha256 -ne $expected -or (Get-FileHash -LiteralPath $proofData.Plugin).Hash -ne $expected){throw 'The expected final protected MOD is not selected.'}
if((Get-FileHash -LiteralPath $proofData.Payload).Hash -ne $proofData.ReusedPayloadSha256){throw 'Immutable payload hash mismatch.'}
$evidence=Join-Path $allowed ('验证记录/final-protected-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$null=New-Item -ItemType Directory -Path $evidence
$config=Join-Path $game 'profiles/Recorder/configs/azurearchive.recorder.cfg'
$configBefore=[IO.File]::ReadAllBytes($config)
[IO.File]::WriteAllBytes((Join-Path $evidence 'original-config.cfg'),$configBefore)
$state=Join-Path (Split-Path $config -Parent) 'azurearchive.recorder-state'
$stateExisted=Test-Path -LiteralPath $state
if($stateExisted){Copy-Item -LiteralPath $state -Destination (Join-Path $evidence 'original-sequence-state') -Recurse}
$recorder=Join-Path $game 'mods/AzureArchiveRecorder'
$tool=Join-Path $game 'mods/AzureArchiveDLSS/runtime'
$summaries=[Collections.Generic.List[object]]::new()
$proc=$null
try {
    # Preserve the previous test-only deployment instead of overwriting unreceipted bytes.
    foreach($relative in @('mods/AzureArchiveRecorder','mods/AzureArchiveDLSS')) {
        $source=[IO.Path]::GetFullPath((Join-Path $game $relative))
        $destination=[IO.Path]::GetFullPath((Join-Path $evidence ('prior-'+(Split-Path $relative -Leaf))))
        if(!$source.StartsWith($game+'\',[StringComparison]::OrdinalIgnoreCase) -or !$destination.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Test backup target escaped the isolated tree.'}
        if(Test-Path -LiteralPath $source){Move-Item -LiteralPath $source -Destination $destination}
    }
    $csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $install=Join-Path $evidence 'LocalVerificationInstall-V1.exe'
    $shared=Join-Path $proofData.BuildDirectory 'DlssComponents.cs'
    & $csc /nologo /target:exe /platform:x64 /main:LocalVerificationInstall ("/out:$install") /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll $proofData.InstallerSource $shared (Join-Path $mod 'packaging/LocalVerificationInstall.cs') 2>&1|Tee-Object -FilePath (Join-Path $evidence 'harness-compile.log')
    if($LASTEXITCODE -ne 0){throw 'V1 payload deployment harness did not compile.'}
    & $install $game $proofData.Payload 2>&1|Tee-Object -FilePath (Join-Path $evidence 'payload-install.log')
    if($LASTEXITCODE -ne 0){throw 'Exact protected payload install failed.'}
    $plugin=Join-Path $recorder '1.0.0/AzureArchive.Recorder.dll'
    if((Get-FileHash -LiteralPath $plugin).Hash -ne $expected){throw 'Installed protected MOD hash mismatch.'}
    $receipt=Get-Content -LiteralPath (Join-Path $recorder 'installed-files.json') -Raw|ConvertFrom-Json
    foreach($file in $receipt.Files){if((Get-FileHash -LiteralPath (Join-Path $game $file.Path)).Hash -ne $file.Sha256){throw ('Payload deployment mismatch: '+$file.Path)}}
    $sourceComponentHashes=@{}
    foreach($mode in @('missing-ordinary','complete-sr2')) {
        $run=Join-Path $evidence $mode;$null=New-Item -ItemType Directory -Path $run
        $enhance=$mode -eq 'complete-sr2'
        if($enhance) {
            foreach($relative in @('_internal/ffmpeg.exe','_internal/ffprobe.exe','_internal/vsr_host.dll','_internal/nvngx_vsr.dll','_internal/dlssg_video_worker.exe','_internal/nvngx_dlssg.dll','_internal/dlssnr_host_v2.dll','_internal/locales/zh_CN.json','_internal/locales/en_US.json','mods/dlss/rtx50/nvngx_dlssnr.dll')) {
                $source=Join-Path $LocalComponentRoot $relative;$destination=Join-Path $tool $relative
                $sourceComponentHashes[$relative]=(Get-FileHash -LiteralPath $source).Hash
                $null=New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
                Copy-Item -LiteralPath $source -Destination $destination
                if((Get-FileHash -LiteralPath $destination).Hash -ne $sourceComponentHashes[$relative]){throw 'Local component copy mismatch.'}
            }
        }
        $lines=@('[Recording]',('OutputDirectory = '+(Join-Path $run 'jobs')),('FFmpegPath = '+(Join-Path $recorder 'runtime/ffmpeg/ffmpeg.exe')),
            'FrameRate = 30','QualityCRF = 18','HardwareEncoding = true','AsyncReadback = true','[DLSS]',('ToolDirectory = '+$tool),
            'Enabled = true',('SuperResolution = '+$(if($enhance){2}else{4})),('FrameMultiplier = '+$(if($enhance){1}else{4})),('NeuralRendering = '+$(if($enhance){'false'}else{'true'})),
            'TimeoutMinutes = 5','[Tutorial]','SeenVersion = 1')
        [IO.File]::WriteAllText($config,($lines -join "`r`n"))
        $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $game 'AzureArchive.exe'))
        $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$game
        foreach($arg in @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $run 'player.log'),
            '--aa-recorder-smoke',(Join-Path $mod 'test-fixture/RecorderSmoke.aap2'),'--aa-recorder-smoke-output',(Join-Path $run 'published'),'--aa-recorder-smoke-ui','--aa-recorder-smoke-evidence',$run)){$start.ArgumentList.Add($arg)}
        if($enhance){$start.ArgumentList.Add('--aa-recorder-smoke-enhance')}
        $proc=[Diagnostics.Process]::Start($start);$proc.Id|Set-Content -LiteralPath (Join-Path $run 'aa.pid')
        Write-Output ('Started final protected '+$mode+' PID '+$proc.Id+': '+$run)
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while(!$proc.WaitForExit(1000)){if($clock.Elapsed.TotalSeconds -gt 420){$proc.Kill($true);$proc.WaitForExit();throw 'Isolated final protected runtime test timed out.'}}
        Copy-Item -LiteralPath (Join-Path $game 'BepInEx/LogOutput.log') -Destination (Join-Path $run 'bepinex.log')
        Copy-Item -LiteralPath $config -Destination (Join-Path $run 'config-after-run.cfg')
        if($proc.ExitCode -ne 0){throw ('AA failed: '+$proc.ExitCode)}
        if(Test-Path -LiteralPath (Join-Path $run 'smoke-ui-error.txt')){throw 'Protected MOD UI diagnostic error.'}
        $checks=@(Get-Content -LiteralPath (Join-Path $run 'smoke-ui-dlss-checks.jsonl')|ForEach-Object{$_|ConvertFrom-Json})
        if(@($checks|Where-Object {!$_.passed}).Count -gt 0){throw 'Protected MOD UI assertion failed.'}
        if(!$enhance -and !@($checks|Where-Object {$_.check -like '*exact missing-component message is red'}).Count){throw 'Missing component UI proof absent.'}
        $completed=@(Get-ChildItem -LiteralPath (Join-Path $run 'jobs') -Filter completed.json -Recurse)
        if($completed.Count -ne 1){throw 'Expected exactly one completed recording.'}
        $video=Get-Content -LiteralPath $completed[0].FullName -Raw|ConvertFrom-Json
        if([bool]$video.enhanced -ne $enhance -or $video.frames -le 0 -or !(Test-Path -LiteralPath $video.output)){throw 'Recording completion does not match requested mode.'}
        if(!$enhance -and @(Get-ChildItem -LiteralPath (Join-Path $run 'jobs') -Filter enhancement-job.json -Recurse).Count -ne 0){throw 'Missing components triggered enhancement.'}
        $ffprobe=Join-Path $recorder 'runtime/ffmpeg/ffprobe.exe';$ffmpeg=Join-Path $recorder 'runtime/ffmpeg/ffmpeg.exe'
        $probe= & $ffprobe -v error -count_frames -show_streams -show_format -of json $video.output
        if($LASTEXITCODE -ne 0){throw 'Final output probe failed.'}
        $probe|Set-Content -LiteralPath (Join-Path $run 'media-probe.json') -Encoding utf8
        & $ffmpeg -nostdin -v error -xerror -i $video.output -map 0:v:0 -map 0:a:0 -f null NUL 2>&1|Tee-Object -FilePath (Join-Path $run 'full-decode.log')
        if($LASTEXITCODE -ne 0){throw 'Full audio/video decode failed.'}
        $media=$probe|ConvertFrom-Json;$v=@($media.streams|Where-Object codec_type -eq video)[0];$a=@($media.streams|Where-Object codec_type -eq audio)[0]
        if(!$v -or !$a -or $v.nb_read_frames -ne 491){throw 'Video/audio stream or expected frame count missing.'}
        if((Get-FileHash -LiteralPath $plugin).Hash -ne $expected){throw 'Protected MOD changed during run.'}
        $summary=[ordered]@{mode=$mode;aaExitCode=$proc.ExitCode;wallSeconds=$clock.Elapsed.TotalSeconds;assertions=$checks.Count;frames=$video.frames;enhanced=$video.enhanced;output=$video.output;width=$v.width;height=$v.height;videoCodec=$v.codec_name;frameRate=$v.avg_frame_rate;decodedFrames=$v.nb_read_frames;audioCodec=$a.codec_name;audioSampleRate=$a.sample_rate;videoDuration=$v.duration;audioDuration=$a.duration;fullDecodePassed=$true;pluginSha256=$expected}
        $summary|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $run 'PASS.json') -Encoding utf8
        $summaries.Add($summary);$summary|ConvertTo-Json -Compress
    }
    foreach($relative in $sourceComponentHashes.Keys){if((Get-FileHash -LiteralPath (Join-Path $LocalComponentRoot $relative)).Hash -ne $sourceComponentHashes[$relative]){throw 'Reference local components unexpectedly changed.'}}
    [ordered]@{proof=$Proof;game=$game;installerSha256=(Get-FileHash -LiteralPath $proofData.Installer).Hash;payloadSha256=(Get-FileHash -LiteralPath $proofData.Payload).Hash;pluginSha256=$expected;payloadFiles=$receipt.Files.Count;componentSource=$LocalComponentRoot;localComponentHashes=$sourceComponentHashes;networkDownloads=0;runs=$summaries}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $evidence 'final-protected-PASS.json') -Encoding utf8
} finally {
    if($proc -and !$proc.HasExited){$proc.Kill($true);$proc.WaitForExit()}
    [IO.File]::WriteAllBytes($config,$configBefore)
    $resolvedState=[IO.Path]::GetFullPath($state);$configRoot=[IO.Path]::GetFullPath((Join-Path $game 'profiles/Recorder/configs')).TrimEnd('\')+'\'
    if(!$resolvedState.StartsWith($configRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Sequence state cleanup escaped isolated profile.'}
    if(Test-Path -LiteralPath $resolvedState){Remove-Item -LiteralPath $resolvedState -Recurse -Force}
    if($stateExisted){Copy-Item -LiteralPath (Join-Path $evidence 'original-sequence-state') -Destination $state -Recurse}
    [ordered]@{configRestored=([Convert]::ToBase64String([IO.File]::ReadAllBytes($config)) -eq [Convert]::ToBase64String($configBefore));sequenceStateExisted=$stateExisted;sequenceStateExists=(Test-Path -LiteralPath $state);game=$game}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'restoration.json') -Encoding utf8
}
Write-Output ('Final protected runtime evidence: '+$evidence)
