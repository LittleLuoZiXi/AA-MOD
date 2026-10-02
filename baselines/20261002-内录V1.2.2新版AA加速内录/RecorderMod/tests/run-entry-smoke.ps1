#requires -Version 7.0
[CmdletBinding()]
param([string]$GameRoot=(Join-Path $PSScriptRoot '..\..'),[switch]$StartupOnly)
$ErrorActionPreference='Stop'
$GameRoot=[IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
$marker=[IO.File]::ReadAllText((Join-Path $GameRoot 'RecorderChoiceTestRoot.json'))|ConvertFrom-Json
if($marker.kind -ne 'RecorderChoiceSmokeIsolatedCopy' -or [IO.Path]::GetFullPath($marker.root).TrimEnd('\') -ne $GameRoot){throw 'This test requires the dedicated isolated-copy marker.'}
$cursor=$GameRoot
while($cursor){if((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked test root is not allowed.'};$cursor=[IO.Path]::GetDirectoryName($cursor)}
$exe=Join-Path $GameRoot 'AzureArchive.exe'
if(@(Get-Process -Name AzureArchive -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq $exe}).Count){throw 'Isolated AA is already running.'}
$mode=if($StartupOnly){'normal-startup'}else{'catalog-entry'}
$run=Join-Path $GameRoot ('smoke-runs/'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-'+$mode)
[IO.Directory]::CreateDirectory($run)|Out-Null
$profile=([IO.File]::ReadAllText((Join-Path $GameRoot 'ActiveProfile.txt'))).Trim()
if($profile -notmatch '^[A-Za-z0-9_-]+$'){throw 'Unexpected test profile name.'}
$config=Join-Path $GameRoot ('profiles/'+$profile+'/configs/azurearchive.recorder.cfg')
$before=[IO.File]::ReadAllBytes($config)
[IO.File]::WriteAllBytes((Join-Path $run 'config.before.cfg'),$before)
$process=$null;$ok=$false;$errorMessage=$null;$timeout=$false
try{
    if(!$StartupOnly){$text=[Text.Encoding]::UTF8.GetString($before);$text=[regex]::Replace($text,'(?m)^SeenVersion\s*=.*$','SeenVersion = 1');[IO.File]::WriteAllText($config,$text,[Text.UTF8Encoding]::new($false))}
    $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=$exe;$psi.WorkingDirectory=$GameRoot;$psi.UseShellExecute=$false;$psi.WindowStyle='Hidden'
    $psi.ArgumentList.Add('-logFile');$psi.ArgumentList.Add((Join-Path $run 'Unity-player.log'))
    if(!$StartupOnly){foreach($arg in @('--aa-recorder-entry-smoke','--aa-recorder-smoke-evidence',$run)){$psi.ArgumentList.Add($arg)}}
    $psi.ArgumentList|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'arguments.json')
    $process=[Diagnostics.Process]::Start($psi);$watch=[Diagnostics.Stopwatch]::StartNew()
    Write-Output "AA $mode test PID $($process.Id); evidence $run"
    while(!$process.WaitForExit(250)){
        $log=Join-Path $GameRoot 'BepInEx/LogOutput.log'
        if($StartupOnly -and $watch.Elapsed.TotalSeconds -ge 18 -and (Test-Path -LiteralPath $log)){
            $stream=[IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite);$reader=[IO.StreamReader]::new($stream);try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
            if($text.Contains('Recorder ready (1.2.2;') -and $text.Contains('Published 1 successfully loaded mod')){$ok=$true;break}
        }
        if($watch.Elapsed.TotalSeconds -ge 70){$timeout=$true;break}
    }
    if(!$StartupOnly){$result=Join-Path $run 'catalog-entry-result.json';$ok=(!$timeout -and $process.HasExited -and $process.ExitCode -eq 0 -and (Test-Path -LiteralPath $result))}
    if(!$ok){throw 'AA startup/catalog smoke check did not pass.'}
}catch{$errorMessage=$_.Exception.ToString()}finally{
    if($process){if(!$process.HasExited){$process.Kill($true);$process.WaitForExit(10000)|Out-Null};$process.Dispose()}
    [IO.File]::WriteAllBytes($config,$before)
    Copy-Item -LiteralPath (Join-Path $GameRoot 'BepInEx/LogOutput.log') -Destination (Join-Path $run 'BepInEx.log')
}
$logText=[IO.File]::ReadAllText((Join-Path $run 'BepInEx.log'))
if($logText -match 'Error loading \[AzureArchiveRecorder|Undefined target method|HarmonyException'){$ok=$false;$errorMessage+=' Plugin load exception detected.'}
$summary=[ordered]@{passed=$ok;mode=$mode;choice_probe=$false;timed_out=$timeout;error=$errorMessage;evidence=$run;config_restored=[Convert]::ToBase64String([IO.File]::ReadAllBytes($config)) -eq [Convert]::ToBase64String($before)}
$summary|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'summary.json')
$summary|ConvertTo-Json
if(!$ok){exit 1}
