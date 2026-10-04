#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$GameRoot='D:\test2\开发\V1.2.3\AA验证',
    [ValidateRange(30,600)][int]$TimeoutSeconds=180,
    [switch]$PrepareOnly
)
$ErrorActionPreference='Stop'
$GameRoot=[IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
function Assert-NoReparse([string]$Path) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while($cursor) {
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw "Linked path is forbidden: $cursor"}
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
}
function Private([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    if(!$full.StartsWith($GameRoot+'\',[StringComparison]::OrdinalIgnoreCase)) {throw "Path escapes isolated host: $full"}
    Assert-NoReparse $full
    return $full
}
function Save-Json([string]$Path,$Value) {
    [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30)+"`n",[Text.UTF8Encoding]::new($false))
}
# Refuse before creating output, rewriting settings or starting a process.
Assert-NoReparse $GameRoot
$markerPath=Private (Join-Path $GameRoot 'RecorderChoiceTestRoot.json')
$marker=Get-Content -LiteralPath $markerPath -Raw|ConvertFrom-Json
if($marker.schema_version -ne 1 -or $marker.kind -ne 'RecorderChoiceSmokeIsolatedCopy' -or
    [IO.Path]::GetFullPath($marker.root).TrimEnd('\') -ne $GameRoot -or
    [IO.Path]::GetFullPath($marker.source_game).TrimEnd('\') -eq $GameRoot -or
    $marker.native_company -ne 'aa123test' -or $marker.native_product -ne 'AARecTest123') {throw 'A dedicated aa123test/AARecTest123 host is required.'}
$profilePath=Private (Join-Path $GameRoot 'ActiveProfile.txt')
if((Get-Content -LiteralPath $profilePath -Raw).Trim() -ne 'Update124') {throw 'This test requires the dedicated Update124 profile; this driver does not switch profiles.'}
$exe=Private (Join-Path $GameRoot 'AzureArchive.exe')
$plugin=Private (Join-Path $GameRoot 'mods\AzureArchiveRecorder\1.2.4\AzureArchive.Recorder.dll')
$manifestPath=Private (Join-Path $GameRoot 'mods\AzureArchiveRecorder\1.2.4\manifest.json')
if(!$PrepareOnly) {
    if(!(Test-Path -LiteralPath $plugin -PathType Leaf)) {throw 'Install the newly built V1.2.4 DLL in the isolated host first.'}
    $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
    if($manifest.version_number -ne '1.2.4') {throw 'The isolated profile requires the V1.2.4 manifest.'}
}
if(@(Get-Process -Name AzureArchive -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq $exe}).Count) {throw 'The isolated AA process is already running.'}
$runs=Private (Join-Path $GameRoot 'smoke-runs')
[IO.Directory]::CreateDirectory($runs)|Out-Null
$lock=[IO.File]::Open((Private (Join-Path $runs '.update-driver.lock')),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$run=Private (Join-Path $runs ((Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-update-v124'))
[IO.Directory]::CreateDirectory($run)|Out-Null
$config=Private (Join-Path $GameRoot 'profiles\Update124\configs\azurearchive.recorder.cfg')
$project=Private (Join-Path $run 'UpdateUiOnly.aap2')
$fixture=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'choice-fixtures\PlainNoChoice.aap2') -Raw
$fixture=$fixture.Replace('PlainNoChoice','UpdateUiOnly').Replace('302a8b23-f2e5-495e-bf19-f8abd68c599c',[Guid]::NewGuid().ToString())
[IO.File]::WriteAllText($project,$fixture,[Text.UTF8Encoding]::new($false))
$realNative=Join-Path $env:USERPROFILE 'AppData\LocalLow\foxxlight\AzureArchive\data'
$protectedRoots=@((Join-Path $marker.source_game 'profiles'),(Join-Path $realNative 'settings'),(Join-Path $realNative 'projects'),(Join-Path $realNative 'saves'))
foreach($profile in Get-ChildItem -LiteralPath (Join-Path $GameRoot 'profiles') -Directory) {
    if($profile.Name -ne 'Update124') {$protectedRoots+=$profile.FullName}
}
$protectedExact=@((Join-Path $marker.source_game 'AzureArchive.exe'),$profilePath,(Join-Path $GameRoot 'mods\AzureArchiveRecorder\1.2.3\AzureArchive.Recorder.dll'))
function Protected-Snapshot {
    $snapshot=[ordered]@{}
    foreach($root in $protectedRoots) {
        if(Test-Path -LiteralPath $root) {
            Assert-NoReparse $root
            foreach($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force|Sort-Object FullName) {
                Assert-NoReparse $file.FullName
                $snapshot[$file.FullName]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            }
        }
    }
    foreach($path in $protectedExact) {
        if(Test-Path -LiteralPath $path) {Assert-NoReparse $path;$snapshot[$path]=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    }
    return $snapshot
}
$before=Protected-Snapshot
Save-Json (Join-Path $run 'protected-before.json') $before
$configExisted=Test-Path -LiteralPath $config -PathType Leaf
$configBefore=if($configExisted){[IO.File]::ReadAllBytes($config)}else{$null}
$passed=$false;$failure=$null;$result=$null;$exitCode=$null;$process=$null;$configSeeded=$false
try {
    if(!$PrepareOnly) {
        [IO.Directory]::CreateDirectory((Split-Path $config -Parent))|Out-Null
        if($configExisted) {[IO.File]::WriteAllBytes((Join-Path $run 'isolated-config.before.cfg'),$configBefore)}
        $seed="[Recording]`nOutputDirectory = $(Join-Path $run 'must-not-record')`n`n[DLSS]`nRememberedEnabled = false`n`n[Tutorial]`nSeenVersion = 1`n"
        [IO.File]::WriteAllText($config,$seed,[Text.UTF8Encoding]::new($false));$configSeeded=$true
        $arguments=@('--aa-recorder-update-probe','--aa-recorder-update-project',$project,'--aa-recorder-smoke-evidence',$run,'-logFile',(Join-Path $run 'Unity-player.log'))
        $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=$exe;$psi.WorkingDirectory=$GameRoot;$psi.UseShellExecute=$false;$psi.WindowStyle='Hidden';$psi.CreateNoWindow=$true
        foreach($argument in $arguments) {$psi.ArgumentList.Add($argument)}
        Save-Json (Join-Path $run 'invocation.json') ([ordered]@{
            executable=$exe;arguments=$arguments;plugin_sha256=(Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash
            native_identity='aa123test/AARecTest123';profile='Update124';real_network=$false;real_install=$false;real_restart=$false
            simulated_check=$true;memory_handler_for_real_downloader=$true;simulated_install_and_restart=$true
        })
        $process=[Diagnostics.Process]::Start($psi);$watch=[Diagnostics.Stopwatch]::StartNew()
        Write-Output "Update UI probe started, PID $($process.Id); evidence $run"
        while(!$process.WaitForExit(250)) {
            if($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $process.Kill($true);$process.WaitForExit(10000)|Out-Null
                throw 'Update UI probe timed out.'
            }
        }
        $exitCode=$process.ExitCode
        $resultPath=Join-Path $run 'update-ui-result.json'
        if($exitCode -ne 0 -or !(Test-Path -LiteralPath $resultPath)) {throw "Update UI probe failed (exit $exitCode); inspect its evidence logs."}
        $result=Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json
        if(!$result.passed -or !$result.nativeUiCallbacks -or !$result.nativeRaycasts -or !$result.simulatedInstall -or !$result.interceptedRestart -or
            $result.realNetworkAccess -or $result.realInstallation -or $result.realRestart -or $result.recordingExecuted -or $result.dlssDownloadOrExecution) {throw 'Probe result did not match the explicitly isolated simulation contract.'}
        $ffmpeg=Private (Join-Path $GameRoot 'mods\AzureArchiveRecorder\runtime\ffmpeg\ffmpeg.exe')
        foreach($name in @('update-offer','update-declined','update-downloading','update-paused','update-resumed','update-terminated','update-completed')) {
            $ppm=Private (Join-Path $run ($name+'.ppm'));$png=Private (Join-Path $run ($name+'.png'))
            if(!(Test-Path -LiteralPath $ppm -PathType Leaf)) {throw "Expected UI frame is absent: $name"}
            & $ffmpeg -hide_banner -loglevel error -y -i $ppm -vf vflip -frames:v 1 $png
            if($LASTEXITCODE -ne 0) {throw "Screenshot conversion failed: $name"}
        }
        $passed=$true
    }
} catch {$failure=$_.Exception.ToString()} finally {
    if($process) {
        if(!$process.HasExited) {$process.Kill($true);$process.WaitForExit(10000)|Out-Null}
        $process.Dispose()
    }
    $bep=Join-Path $GameRoot 'BepInEx\LogOutput.log'
    if(!$PrepareOnly -and (Test-Path -LiteralPath $bep)) {Copy-Item -LiteralPath $bep -Destination (Join-Path $run 'BepInEx.log')}
    if($configSeeded) {
        if(Test-Path -LiteralPath $config) {Copy-Item -LiteralPath $config -Destination (Join-Path $run 'isolated-config.after.cfg')}
        if($configExisted) {[IO.File]::WriteAllBytes($config,$configBefore)}
        elseif(Test-Path -LiteralPath $config) {Remove-Item -LiteralPath $config -Force}
    }
    $after=Protected-Snapshot;Save-Json (Join-Path $run 'protected-after.json') $after
    $unchanged=($before|ConvertTo-Json -Compress) -eq ($after|ConvertTo-Json -Compress)
    if(!$unchanged) {$passed=$false;$failure+=' Protected production or existing profile files changed.'}
    $summary=[ordered]@{
        passed=$passed;prepared_only=[bool]$PrepareOnly;run_directory=$run;exit_code=$exitCode;checks=if($result){$result.checks.Count}else{0};error=$failure
        original_user_files_unchanged=$unchanged;protected_file_count=$before.Count;isolated_config_restored=$configSeeded
        simulated_update_check=$true;real_downloader_with_memory_handler=$true;simulated_install_and_restart=$true
        real_network_access=$false;real_installation=$false;real_restart=$false;recording_executed=$false;dlss_download_or_execution=$false
    }
    Save-Json (Join-Path $run 'summary.json') $summary
    $lock.Dispose()
}
$summary|ConvertTo-Json -Depth 5
if(!$PrepareOnly -and !$passed) {exit 1}
