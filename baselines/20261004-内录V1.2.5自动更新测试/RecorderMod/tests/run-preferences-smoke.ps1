#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$GameRoot='D:\test2\开发\V1.2.3\AA验证',
    [ValidateRange(30,600)][int]$TimeoutSeconds=180,
    [switch]$PrepareOnly
)
$ErrorActionPreference='Stop'
$GameRoot=[IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
function Assert-NoReparse([string]$Path){
    $cursor=[IO.Path]::GetFullPath($Path)
    while($cursor){if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Linked path is not allowed: $cursor"};$cursor=[IO.Path]::GetDirectoryName($cursor)}
}
function Private([string]$Path){$full=[IO.Path]::GetFullPath($Path);if(!$full.StartsWith($GameRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw "Path escapes isolated host: $full"};Assert-NoReparse $full;return $full}
function Save-Json([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30)+"`n",[Text.UTF8Encoding]::new($false))}
Assert-NoReparse $GameRoot
$markerPath=Private (Join-Path $GameRoot 'RecorderChoiceTestRoot.json')
$marker=Get-Content -LiteralPath $markerPath -Raw|ConvertFrom-Json
if($marker.schema_version -ne 1 -or $marker.kind -ne 'RecorderChoiceSmokeIsolatedCopy' -or [IO.Path]::GetFullPath($marker.root).TrimEnd('\') -ne $GameRoot -or $marker.native_company -ne 'aa123test' -or $marker.native_product -ne 'AARecTest123'){throw 'A dedicated native-data-isolated preferences host is required.'}
$exe=Private (Join-Path $GameRoot 'AzureArchive.exe')
$plugin=Private (Join-Path $GameRoot 'mods\AzureArchiveRecorder\1.2.3\AzureArchive.Recorder.dll')
if(!$PrepareOnly -and !(Test-Path -LiteralPath $plugin -PathType Leaf)){throw 'Install the newly built V1.2.3 DLL into the isolated host first.'}
if(@(Get-Process -Name AzureArchive -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq $exe}).Count){throw 'The isolated AA process is already running.'}
$profile=Get-Content -LiteralPath (Private (Join-Path $GameRoot 'ActiveProfile.txt')) -Raw
if($profile.Trim() -ne 'Preferences123'){throw 'This test requires its dedicated Preferences123 profile.'}
$runs=Private (Join-Path $GameRoot 'smoke-runs');[IO.Directory]::CreateDirectory($runs)|Out-Null
$lock=[IO.File]::Open((Private (Join-Path $runs '.driver.lock')),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$run=Private (Join-Path $runs ((Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-preferences'))
[IO.Directory]::CreateDirectory($run)|Out-Null
$config=Private (Join-Path $GameRoot 'profiles\Preferences123\configs\azurearchive.recorder.cfg')
$folder=Private (Join-Path $run '中文成品 目录')
[IO.Directory]::CreateDirectory($folder)|Out-Null
$first=Private (Join-Path $run 'PreferencesOne.aap2');$second=Private (Join-Path $run 'PreferencesTwo.aap2')
$fixture=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'choice-fixtures\PlainNoChoice.aap2') -Raw
$encoding=[Text.UTF8Encoding]::new($false)
foreach($pair in @(@($first,'PreferencesOne'),@($second,'PreferencesTwo'))){$text=$fixture.Replace('PlainNoChoice',$pair[1]).Replace('302a8b23-f2e5-495e-bf19-f8abd68c599c',[Guid]::NewGuid().ToString());[IO.File]::WriteAllText($pair[0],$text,$encoding)}
# Hash real settings and story files read-only, both before and after the run.
$realNative=Join-Path $env:USERPROFILE 'AppData\LocalLow\foxxlight\AzureArchive\data'
$protectedRoots=@((Join-Path $marker.source_game 'profiles'),(Join-Path $realNative 'settings'),(Join-Path $realNative 'projects'),(Join-Path $realNative 'saves'))
function Protected-Snapshot {
    $result=[ordered]@{}
    foreach($root in $protectedRoots){
        if(Test-Path -LiteralPath $root){
            foreach($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force){Assert-NoReparse $file.FullName;$result[$file.FullName]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
        }
    }
    return $result
}
$before=Protected-Snapshot;Save-Json (Join-Path $run 'protected-before.json') $before
$results=[Collections.Generic.List[object]]::new();$failure=$null;$passed=$false
try{
    if(Test-Path -LiteralPath $config){Copy-Item -LiteralPath $config -Destination (Join-Path $run 'isolated-config.before.cfg')}
    # Legacy Enabled is deliberately present; the new RememberedEnabled must start off.
    $seed="[Recording]`nOutputDirectory = $(Join-Path $run 'internal-captures')`n`n[DLSS]`nEnabled = true`n`n[Tutorial]`nSeenVersion = 1`n"
    [IO.File]::WriteAllText($config,$seed,$encoding)
    if(!$PrepareOnly){
        foreach($phase in @('write','restart')){
            $phaseRoot=Private (Join-Path $run $phase);[IO.Directory]::CreateDirectory($phaseRoot)|Out-Null
            $args=@('--aa-recorder-preferences-probe','--aa-recorder-preferences-phase',$phase,'--aa-recorder-preferences-first',$first,'--aa-recorder-preferences-second',$second,'--aa-recorder-preferences-folder',$folder,'--aa-recorder-smoke-evidence',$phaseRoot,'-logFile',(Join-Path $phaseRoot 'Unity-player.log'))
            $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=$exe;$psi.WorkingDirectory=$GameRoot;$psi.UseShellExecute=$false;$psi.WindowStyle='Hidden';$psi.CreateNoWindow=$true
            foreach($arg in $args){$psi.ArgumentList.Add($arg)}
            Save-Json (Join-Path $phaseRoot 'invocation.json') ([ordered]@{executable=$exe;arguments=$args;plugin_sha256=(Get-FileHash -LiteralPath $plugin).Hash;native_identity='aa123test/AARecTest123';legacy_enabled_seed=($phase -eq 'write')})
            $process=$null;$timeout=$false;$exitCode=$null
            try{
                $process=[Diagnostics.Process]::Start($psi);$watch=[Diagnostics.Stopwatch]::StartNew()
                Write-Output "Preferences $phase started, PID $($process.Id); evidence $phaseRoot"
                while(!$process.WaitForExit(250)){if($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds){$timeout=$true;break}}
                if($timeout){$process.Kill($true);$process.WaitForExit(10000)|Out-Null;throw "Preferences phase $phase timed out."}
                $exitCode=$process.ExitCode
            }finally{
                if($process){if(!$process.HasExited){$process.Kill($true);$process.WaitForExit(10000)|Out-Null};$process.Dispose()}
                $bep=Join-Path $GameRoot 'BepInEx\LogOutput.log';if(Test-Path -LiteralPath $bep){Copy-Item -LiteralPath $bep -Destination (Join-Path $phaseRoot 'BepInEx.log')}
                Copy-Item -LiteralPath $config -Destination (Join-Path $phaseRoot 'config.after.cfg')
            }
            $resultPath=Join-Path $phaseRoot 'preferences-result.json'
            if($exitCode -ne 0 -or !(Test-Path -LiteralPath $resultPath)){throw "Preferences phase $phase failed (exit $exitCode); see evidence logs."}
            $result=Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json
            if(!$result.passed -or $result.folder -ne $folder -or $result.effectiveDlss -or !$result.rememberedDlss -or $result.fps -ne $(if($phase -eq 'write'){60}else{30})){throw 'Preferences result did not meet the expected state.'}
            $results.Add($result)
            $ffmpeg=Private (Join-Path $GameRoot 'mods\AzureArchiveRecorder\runtime\ffmpeg\ffmpeg.exe')
            foreach($ppm in Get-ChildItem -LiteralPath $phaseRoot -Filter '*.ppm'){
                $png=[IO.Path]::ChangeExtension($ppm.FullName,'.png')
                & $ffmpeg -hide_banner -loglevel error -y -i $ppm.FullName -vf vflip -frames:v 1 $png
                if($LASTEXITCODE -ne 0){throw 'Screenshot conversion failed.'}
            }
        }
        $passed=$true
    }
}catch{$failure=$_.Exception.ToString()}finally{
    $after=Protected-Snapshot;Save-Json (Join-Path $run 'protected-after.json') $after
    $unchanged=($before|ConvertTo-Json -Compress) -eq ($after|ConvertTo-Json -Compress)
    if(!$unchanged){$passed=$false;$failure+=' Original settings or story files changed during verification.'}
    $summary=[ordered]@{passed=$passed;prepared_only=[bool]$PrepareOnly;run_directory=$run;phases=$results.ToArray();error=$failure;original_user_files_unchanged=$unchanged;protected_file_count=$before.Count;native_identity_is_test_host_only=$true;gpu_spoof_used=$false;dlss_download_or_execution=$false}
    Save-Json (Join-Path $run 'summary.json') $summary
    $lock.Dispose()
}
[ordered]@{passed=$passed;prepared_only=[bool]$PrepareOnly;run_directory=$run;phase_checks=@($results|ForEach-Object {[ordered]@{phase=$_.phase;checks=$_.checks.Count;fps=$_.fps}});error=$failure;original_user_files_unchanged=$unchanged;protected_file_count=$before.Count}|ConvertTo-Json -Depth 5
if(!$PrepareOnly -and !$passed){exit 1}