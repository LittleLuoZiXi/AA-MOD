#requires -Version 7.0
param([string]$GameRoot='',[int]$TimeoutSeconds=210,[switch]$CoreOnly)
$ErrorActionPreference='Stop'
$modRoot=Split-Path $PSScriptRoot -Parent
if(!$GameRoot){$GameRoot=Join-Path $modRoot 'test-host'}
$marker=Get-Content -LiteralPath (Join-Path $GameRoot 'RevisionTestHost.json') -Raw|ConvertFrom-Json
if($marker.kind -ne 'RevisionCompareIsolatedHost' -or [IO.Path]::GetFullPath($marker.root) -ne [IO.Path]::GetFullPath($GameRoot)){throw 'Isolated test marker mismatch.'}
$exe=Join-Path $GameRoot 'AzureArchive.exe'
if(@(Get-Process -Name AzureArchive -ErrorAction SilentlyContinue | Where-Object Path -EQ $exe).Count){throw 'Test host is already running.'}
$manifest=Get-Content -LiteralPath (Join-Path $modRoot 'manifest.json') -Raw|ConvertFrom-Json
$activeProfile=[IO.File]::ReadAllText((Join-Path $GameRoot 'ActiveProfile.txt')).Trim()
if($activeProfile -ne 'RevisionTest'){throw 'The isolated host must use its dedicated RevisionTest profile.'}
$config=Get-Content -LiteralPath (Join-Path $GameRoot 'profiles\RevisionTest\modconfig.json') -Raw|ConvertFrom-Json
if(!@($config.EnabledMods|Where-Object {$_.name -eq $manifest.name -and $_.version -eq $manifest.version_number}).Count){throw 'Test profile MOD version differs from manifest.json; prepare a matching isolated host first.'}
$dest=Join-Path $GameRoot ('mods\AzureArchiveRevisionCompare\'+$manifest.version_number)
[IO.Directory]::CreateDirectory($dest)|Out-Null
Copy-Item -LiteralPath (Join-Path $modRoot 'build\AzureArchive.RevisionCompare.dll'),(Join-Path $modRoot 'manifest.json') -Destination $dest -Force
$evidence=Join-Path $GameRoot 'evidence'
[IO.Directory]::CreateDirectory($evidence)|Out-Null
if(Test-Path -LiteralPath (Join-Path $evidence 'result.json')) {
    $archive=Join-Path $evidence ('runs\'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    [IO.Directory]::CreateDirectory($archive)|Out-Null
    foreach($name in @('result.json','progress.txt','comparison.png','comparison.ppm','failure.ppm','Unity-player.log','ui-hierarchy.json','user-data-preserved.json')) {
        $item=Join-Path $evidence $name
        if(Test-Path -LiteralPath $item){Copy-Item -LiteralPath $item -Destination $archive}
    }
    Remove-Item -LiteralPath (Join-Path $evidence 'result.json')
}
[IO.File]::WriteAllText((Join-Path $evidence 'progress.txt'),'')
function Snapshot-UserData {
    $map=[ordered]@{}
    $userRoot=Join-Path $env:USERPROFILE 'AppData\LocalLow\foxxlight\AzureArchive\data'
    foreach($dir in @('settings','projects','saves')) {
        Get-ChildItem -LiteralPath (Join-Path $userRoot $dir) -File -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object {
            $map[$_.FullName]=(Get-FileHash -LiteralPath $_.FullName).Hash
        }
    }
    Get-ChildItem -LiteralPath (Join-Path $marker.source 'profiles') -Recurse -File | Sort-Object FullName | ForEach-Object {$map[$_.FullName]=(Get-FileHash -LiteralPath $_.FullName).Hash}
    return $map
}
$before=Snapshot-UserData
$before|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $evidence 'protected-before.json') -Encoding utf8
$psi=[Diagnostics.ProcessStartInfo]::new()
$psi.FileName=$exe;$psi.WorkingDirectory=$GameRoot;$psi.UseShellExecute=$false;$psi.WindowStyle='Hidden';$psi.CreateNoWindow=$true
$null=$psi.Environment.Remove('DOORSTOP_DISABLE')
foreach($arg in @('--revision-compare-probe','-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $evidence 'Unity-player.log'))){$psi.ArgumentList.Add($arg)}
if($CoreOnly){$psi.ArgumentList.Add('--revision-core-only')}
$process=[Diagnostics.Process]::Start($psi);$clock=[Diagnostics.Stopwatch]::StartNew()
"Started isolated AA verification (PID $($process.Id))."
try {
    while(!$process.WaitForExit(500)) {
        if($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds){$process.Kill($true);$process.WaitForExit(10000)|Out-Null;throw 'Isolated integration verification timed out.'}
    }
    $exitCode=$process.ExitCode
} finally {
    if(!$process.HasExited){$process.Kill($true);$process.WaitForExit(10000)|Out-Null}
    $process.Dispose()
    $after=Snapshot-UserData
    $unchanged=($before|ConvertTo-Json -Compress) -eq ($after|ConvertTo-Json -Compress)
    [ordered]@{unchanged=$unchanged;count=$before.Count}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'user-data-preserved.json') -Encoding utf8
    if(!$unchanged){throw 'Protected user data changed.'}
}
if(Test-Path -LiteralPath (Join-Path $evidence 'result.json')){Get-Content -LiteralPath (Join-Path $evidence 'result.json')}
"Native process exit: $exitCode"
if($exitCode -ne 0){exit 1}
