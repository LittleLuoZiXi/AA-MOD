#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][string]$Proof
)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$workspace=Split-Path (Split-Path $mod -Parent) -Parent
$game=[IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
$allowed=[IO.Path]::GetFullPath((Join-Path $workspace 'V1实机验收')).TrimEnd('\')+'\'
if(!$game.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $game -Leaf) -ne 'AA-UI-20260929' -or !(Test-Path -LiteralPath (Join-Path $game 'RTX验证副本.json'))){throw 'Only the owned AA-UI-20260929 acceptance copy is permitted.'}
if((Get-Content -LiteralPath (Join-Path $game 'ActiveProfile.txt') -Raw).Trim() -ne 'Recorder'){throw 'The isolated Recorder profile is required.'}
foreach($aa in Get-Process -Name AzureArchive -ErrorAction SilentlyContinue){if($aa.Path -eq (Join-Path $game 'AzureArchive.exe')){throw 'The isolated AA is already running.'}}
$release=Get-Content -LiteralPath $Proof -Raw|ConvertFrom-Json
$expectedPlugin='659FB1830C49C107E7CBD9B2AF1A2060DA0E0F40C20EDDCCA51A2C70B1926691'
$expectedInstaller='BA9214C3C01C302536EE6A731460249EE75750228FF8C68DA866EBE8388927B0'
if($release.Version -ne '1.1.0' -or $release.PayloadFiles -ne 201 -or $release.PluginSha256 -ne $expectedPlugin -or (Get-FileHash -LiteralPath $release.Plugin).Hash -ne $expectedPlugin){throw 'Unexpected final V1.1 protected payload.'}
if($release.InstallerSha256 -ne $expectedInstaller){throw 'The originating V1.1 build proof identity differs.'}
$payloadHash=(Get-FileHash -LiteralPath $release.Payload).Hash
$shared=Join-Path $mod 'src/DlssComponents.cs'
if((Get-FileHash -LiteralPath $shared).Hash -ne $release.SharedSourceSha256){throw 'Shared installation helper differs from the release proof.'}
$dailyPlugin=Join-Path $workspace 'AA/mods/AzureArchiveRecorder/0.2.1/AzureArchive.Recorder.dll'
$dailyHash=(Get-FileHash -LiteralPath $dailyPlugin).Hash
$evidence=Join-Path $allowed ('验证记录/final-protected-v1.1-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$null=New-Item -ItemType Directory -Path $evidence
$config=Join-Path $game 'profiles/Recorder/configs/azurearchive.recorder.cfg'
$configBefore=[IO.File]::ReadAllBytes($config)
[IO.File]::WriteAllBytes((Join-Path $evidence 'original-config.cfg'),$configBefore)
$state=Join-Path (Split-Path $config -Parent) 'azurearchive.recorder-state'
$stateExisted=Test-Path -LiteralPath $state
if($stateExisted){Copy-Item -LiteralPath $state -Destination (Join-Path $evidence 'original-sequence-state') -Recurse}
$proc=$null
try {
    foreach($relative in @('mods/AzureArchiveRecorder','mods/AzureArchiveDLSS')) {
        $source=[IO.Path]::GetFullPath((Join-Path $game $relative))
        $destination=[IO.Path]::GetFullPath((Join-Path $evidence ('prior-'+(Split-Path $relative -Leaf))))
        if(!$source.StartsWith($game+'\',[StringComparison]::OrdinalIgnoreCase) -or !$destination.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Test backup paths escaped the owned isolation tree.'}
        if(Test-Path -LiteralPath $destination){throw 'A prior deployment backup already exists.'}
        if(Test-Path -LiteralPath $source){Move-Item -LiteralPath $source -Destination $destination}
    }
    $generated=Join-Path $evidence 'Installer.generated.cs';Copy-Item -LiteralPath $release.InstallerSource -Destination $generated
    $sharedCopy=Join-Path $evidence 'DlssComponents.cs';Copy-Item -LiteralPath $shared -Destination $sharedCopy
    $entry=Join-Path $evidence 'LocalVerificationInstall.cs';Copy-Item -LiteralPath (Join-Path $mod 'packaging/LocalVerificationInstall.cs') -Destination $entry
    $csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $install=Join-Path $evidence 'LocalVerificationInstall-V1.1.exe'
    & $csc /nologo /target:exe /platform:x64 /main:LocalVerificationInstall ("/out:$install") /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll $generated $sharedCopy $entry 2>&1|Tee-Object -FilePath (Join-Path $evidence 'harness-compile.log')
    if($LASTEXITCODE -ne 0){throw 'The V1.1 local payload deployment harness did not compile.'}
    & $install $game $release.Payload 2>&1|Tee-Object -FilePath (Join-Path $evidence 'payload-install.log')
    if($LASTEXITCODE -ne 0){throw 'Exact V1.1 payload installation failed.'}
    $recorder=Join-Path $game 'mods/AzureArchiveRecorder'
    $plugin=Join-Path $recorder '1.1.0/AzureArchive.Recorder.dll'
    if((Get-FileHash -LiteralPath $plugin).Hash -ne $expectedPlugin){throw 'Installed protected MOD SHA-256 mismatch.'}
    $receipt=Get-Content -LiteralPath (Join-Path $recorder 'installed-files.json') -Raw|ConvertFrom-Json
    if($receipt.Version -ne '1.1.0' -or $receipt.Files.Count -ne 201){throw 'Final installed receipt version/file count mismatch.'}
    $fileChecks=@(foreach($file in $receipt.Files) {
        $actual=(Get-FileHash -LiteralPath (Join-Path $game $file.Path)).Hash
        if($actual -ne $file.Sha256){throw ('Installed payload file mismatch: '+$file.Path)}
        [ordered]@{Path=$file.Path;Sha256=$actual;Passed=$true}
    })
    $fileChecks|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $evidence 'payload-201-file-hashes.json') -Encoding utf8
    $tool=Join-Path $game 'mods/AzureArchiveDLSS/runtime'
    if(Test-Path -LiteralPath $tool){throw 'This run must have no installed DLSS components.'}
    $lines=@('[Recording]',('OutputDirectory = '+(Join-Path $evidence 'jobs')),('FFmpegPath = '+(Join-Path $recorder 'runtime/ffmpeg/ffmpeg.exe')),
        'FrameRate = 30','QualityCRF = 18','HardwareEncoding = true','AsyncReadback = true','[DLSS]',('ToolDirectory = '+$tool),
        'Enabled = true','SuperResolution = 4','FrameMultiplier = 4','NeuralRendering = true','TimeoutMinutes = 2','[Tutorial]','SeenVersion = 1')
    [IO.File]::WriteAllText($config,($lines -join [Environment]::NewLine))
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $game 'AzureArchive.exe'))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$game
    foreach($arg in @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $evidence 'player.log'),
        '--aa-recorder-smoke',(Join-Path $mod 'test-fixture/RecorderSmoke.aap2'),'--aa-recorder-smoke-output',(Join-Path $evidence 'published'),'--aa-recorder-smoke-ui','--aa-recorder-smoke-evidence',$evidence)){$start.ArgumentList.Add($arg)}
    $proc=[Diagnostics.Process]::Start($start);$proc.Id|Set-Content -LiteralPath (Join-Path $evidence 'aa.pid')
    Write-Output ('Final protected V1.1 ordinary run PID '+$proc.Id+': '+$evidence)
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while(!$proc.WaitForExit(1000)){if($clock.Elapsed.TotalSeconds -gt 240){$proc.Kill($true);$proc.WaitForExit();throw 'The isolated V1.1 ordinary smoke test timed out.'}}
    Copy-Item -LiteralPath (Join-Path $game 'BepInEx/LogOutput.log') -Destination (Join-Path $evidence 'bepinex.log')
    Copy-Item -LiteralPath $config -Destination (Join-Path $evidence 'config-after-run.cfg')
    if($proc.ExitCode -ne 0 -or (Test-Path -LiteralPath (Join-Path $evidence 'smoke-ui-error.txt'))){throw 'V1.1 protected AA smoke failed.'}
    $checks=@(Get-Content -LiteralPath (Join-Path $evidence 'smoke-ui-dlss-checks.jsonl')|ForEach-Object {$_|ConvertFrom-Json})
    if($checks.Count -ne 25 -or @($checks|Where-Object {!$_.passed}).Count -gt 0){throw 'V1.1 missing-DLSS UI assertions failed.'}
    if(!@($checks|Where-Object {$_.check -like '*exact missing-component message is red'}).Count){throw 'Exact red missing-component message proof is missing.'}
    $completed=@(Get-ChildItem -LiteralPath (Join-Path $evidence 'jobs') -Filter completed.json -Recurse)
    if($completed.Count -ne 1){throw 'Expected exactly one completed ordinary recording.'}
    $video=Get-Content -LiteralPath $completed[0].FullName -Raw|ConvertFrom-Json
    if($video.enhanced -or $video.frames -ne 491 -or !(Test-Path -LiteralPath $video.output)){throw 'Unexpected ordinary recording result.'}
    if(@(Get-ChildItem -LiteralPath (Join-Path $evidence 'jobs') -Filter enhancement-job.json -Recurse).Count -ne 0 -or (Test-Path -LiteralPath $tool)){throw 'Missing DLSS triggered enhancement or component installation.'}
    $log=Get-Content -LiteralPath (Join-Path $evidence 'bepinex.log')
    if(!($log -match 'Protected recorder 1.1.0 loaded inside AzureArchive.') -or !($log -match 'Loading \[AzureArchiveRecorder 1.1.0\]')){throw 'Final V1.1 protected loading log is missing.'}
    $errors=@($log|Where-Object {$_ -match '^\[Error\s*:AzureArchiveRecorder\]'})
    if($errors.Count -ne 1 -or $errors[0] -notmatch 'System.InvalidOperationException: 缺少DLSS组件，相关DLSS功能不可用。$'){throw 'Unexpected plugin error, or missing negative-test rejection proof.'}
    $ffprobe=Join-Path $recorder 'runtime/ffmpeg/ffprobe.exe';$ffmpeg=Join-Path $recorder 'runtime/ffmpeg/ffmpeg.exe'
    $probe= & $ffprobe -v error -count_frames -show_streams -show_format -of json $video.output
    if($LASTEXITCODE -ne 0){throw 'Media probe failed.'}
    $probe|Set-Content -LiteralPath (Join-Path $evidence 'media-probe.json') -Encoding utf8
    & $ffmpeg -nostdin -v error -xerror -i $video.output -map 0:v:0 -map 0:a:0 -f null NUL 2>&1|Tee-Object -FilePath (Join-Path $evidence 'full-decode.log')
    if($LASTEXITCODE -ne 0){throw 'Full audio/video decode failed.'}
    $media=$probe|ConvertFrom-Json;$v=@($media.streams|Where-Object codec_type -eq video)[0];$a=@($media.streams|Where-Object codec_type -eq audio)[0]
    if($v.codec_name -ne 'h264' -or $v.nb_read_frames -ne 491 -or $v.avg_frame_rate -ne '30/1' -or $a.codec_name -ne 'aac' -or $a.channels -ne 2){throw 'Unexpected ordinary video/audio stream properties.'}
    foreach($name in @('smoke-ui-settings','smoke-ui-dlss-missing')) {
        & $ffmpeg -nostdin -v error -i (Join-Path $evidence ($name+'.ppm')) -vf vflip -frames:v 1 (Join-Path $evidence ($name+'.png'))
        if($LASTEXITCODE -ne 0){throw 'PPM format/orientation conversion failed.'}
    }
    if((Get-FileHash -LiteralPath $plugin).Hash -ne $expectedPlugin -or (Get-FileHash -LiteralPath $release.Payload).Hash -ne $payloadHash -or (Get-FileHash -LiteralPath $dailyPlugin).Hash -ne $dailyHash){throw 'A protected or reference artifact unexpectedly changed.'}
    $summary=[ordered]@{version='1.1.0';proof=$Proof;game=$game;sourceInstallerSha256=$expectedInstaller;installerExecuted=$false;payloadSha256=$payloadHash;payloadFileChecks=$fileChecks.Count;pluginSha256=$expectedPlugin;
        aaExitCode=$proc.ExitCode;wallSeconds=$clock.Elapsed.TotalSeconds;assertions=$checks.Count;protectedLoaderConfirmed=$true;expectedNegativeTestErrors=1;unexpectedPluginErrors=0;networkDownloads=0;enhanced=$false;
        output=$video.output;outputSha256=(Get-FileHash -LiteralPath $video.output).Hash;frames=$video.frames;width=$v.width;height=$v.height;frameRate=$v.avg_frame_rate;videoCodec=$v.codec_name;audioCodec=$a.codec_name;audioSampleRate=$a.sample_rate;audioChannels=$a.channels;
        videoDuration=$v.duration;audioDuration=$a.duration;fullDecodePassed=$true;dailyModSha256=$dailyHash}
    $summary|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $evidence 'PASS.json') -Encoding utf8
    $summary|ConvertTo-Json -Compress
} finally {
    if($proc -and !$proc.HasExited){$proc.Kill($true);$proc.WaitForExit()}
    [IO.File]::WriteAllBytes($config,$configBefore)
    $resolvedState=[IO.Path]::GetFullPath($state);$configRoot=[IO.Path]::GetFullPath((Join-Path $game 'profiles/Recorder/configs')).TrimEnd('\')+'\'
    if(!$resolvedState.StartsWith($configRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Refuse sequence-state cleanup outside the isolated profile.'}
    if(Test-Path -LiteralPath $resolvedState){Remove-Item -LiteralPath $resolvedState -Recurse -Force}
    if($stateExisted){Copy-Item -LiteralPath (Join-Path $evidence 'original-sequence-state') -Destination $state -Recurse}
    [ordered]@{configRestored=([Convert]::ToBase64String([IO.File]::ReadAllBytes($config)) -eq [Convert]::ToBase64String($configBefore));sequenceStateExisted=$stateExisted;sequenceStateExists=(Test-Path -LiteralPath $state);game=$game}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'restoration.json') -Encoding utf8
}
Write-Output ('Final V1.1 protected runtime evidence: '+$evidence)
