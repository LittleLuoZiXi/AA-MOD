param([string]$ReferenceGameRoot='E:\AzureArchive_100_fix')
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$work=Join-Path $project ('evidence\update-apply\'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($work)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$powershell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:passed=0
function Assert($condition,[string]$message){if(!$condition){throw $message}}
function WriteJson([string]$path,$value){[IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 50 -Compress),[Text.UTF8Encoding]::new($false))}
function Sha([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Snapshot([string]$root){$map=[ordered]@{};Get-ChildItem -LiteralPath (Join-Path $root 'mods') -File -Recurse|Sort-Object FullName|ForEach-Object{$map[$_.FullName.Substring($root.Length)]=Sha $_.FullName};$map['profile']=Sha (Join-Path $root 'profiles\Recorder\modconfig.json');$map|ConvertTo-Json -Compress}
$hostSource=Join-Path $work 'SyntheticHost.cs'
$hostCode=@'
using System;
using System.IO;
using System.Threading;
class SyntheticHost {
  public static int Main(string[] args) {
    string root=AppDomain.CurrentDomain.BaseDirectory;
    if(args.Length==0){File.WriteAllText(Path.Combine(root,"restarted.marker"),Environment.GetEnvironmentVariable("DOORSTOP_DISABLE")??"removed");return 0;}
    File.WriteAllText(Path.Combine(root,"parent.ready"),"ready");
    for(int i=0;i<600;i++){string path=Path.Combine(root,"exit.code");if(File.Exists(path))return Int32.Parse(File.ReadAllText(path));Thread.Sleep(100);}
    return 9;
  }
}
'@
[IO.File]::WriteAllText($hostSource,$hostCode)
$hostExe=Join-Path $work 'SyntheticHost.exe'
&$compiler /nologo /target:winexe "/out:$hostExe" $hostSource
if($LASTEXITCODE -ne 0){throw 'Synthetic host compilation failed.'}
function Compile-Payload([string]$version,[string]$suffix=''){
    $folder=Join-Path $work ('payload-'+$version+$suffix);$null=[IO.Directory]::CreateDirectory($folder)
    $source=Join-Path $folder 'Plugin.cs'
    $text='using System; namespace BepInEx { public class BepInPlugin:Attribute { public BepInPlugin(string id,string name,string version){} } } namespace AzureArchive.RevisionCompare { [BepInEx.BepInPlugin("azurearchive.revisioncompare","AzureArchiveRevisionCompare","'+$version+'")] public class Plugin {} '+$(if($suffix -eq '-probe'){'internal class IntegrationProbe {}'}else{''})+' }'
    [IO.File]::WriteAllText($source,$text);$dll=Join-Path $folder 'AzureArchive.RevisionCompare.dll'
    &$compiler /nologo /target:library "/out:$dll" $source;if($LASTEXITCODE -ne 0){throw 'Synthetic plugin compilation failed.'}
    WriteJson (Join-Path $folder 'manifest.json') @{name='AzureArchiveRevisionCompare';version_number=$version;dependencies=@()}
    return $folder
}
$payload=Compile-Payload '1.2.0';$probePayload=Compile-Payload '1.2.0' '-probe'
function New-Fixture([string]$name,[string]$payloadDirectory=$payload){
    $root=Join-Path $work ($name+' AA 中文 space');$task=Join-Path $work ([Guid]::NewGuid().ToString('N'))
    foreach($path in @($root,$task,(Join-Path $root 'AzureArchive_Data'),(Join-Path $root 'profiles\Recorder'),(Join-Path $root 'mods\AzureArchiveRevisionCompare\1.1.0'),(Join-Path $root 'mods\AzureArchiveRecorder\1.2.4'))){$null=[IO.Directory]::CreateDirectory($path)}
    Copy-Item -LiteralPath $hostExe -Destination (Join-Path $root 'AzureArchive.exe')
    [IO.File]::WriteAllText((Join-Path $root 'ActiveProfile.txt'),'Recorder')
    WriteJson (Join-Path $root 'profiles\Recorder\modconfig.json') @{EnabledMods=@(@{name='AzureArchiveRecorder';version='1.2.4';custom='keep'},@{name='AzureArchiveRevisionCompare';version='1.1.0'});UnknownPreference=@{number=42}}
    [IO.File]::WriteAllText((Join-Path $root 'mods\AzureArchiveRecorder\1.2.4\keep.txt'),'Recorder content')
    [IO.File]::WriteAllText((Join-Path $root 'mods\AzureArchiveRevisionCompare\user.json'),'user settings')
    $oldDll=Join-Path $root 'mods\AzureArchiveRevisionCompare\1.1.0\AzureArchive.RevisionCompare.dll';[IO.File]::WriteAllText($oldDll,'MZ old plugin')
    $oldManifest=Join-Path $root 'mods\AzureArchiveRevisionCompare\1.1.0\manifest.json';WriteJson $oldManifest @{name='AzureArchiveRevisionCompare';version_number='1.1.0'}
    WriteJson (Join-Path $root 'mods\AzureArchiveRevisionCompare\install-receipt.json') @{Schema=1;ProductId='azurearchive.revisioncompare';ProductName='AzureArchiveRevisionCompare';Version='1.1.0';Root=$root;Files=@(@{Path='mods/AzureArchiveRevisionCompare/1.1.0/AzureArchive.RevisionCompare.dll';Sha256=(Sha $oldDll)},@{Path='mods/AzureArchiveRevisionCompare/1.1.0/manifest.json';Sha256=(Sha $oldManifest)});Profiles=@(@{Profile='Recorder';Version='1.1.0'})}
    Copy-Item -LiteralPath (Join-Path $payloadDirectory 'AzureArchive.RevisionCompare.dll'),(Join-Path $payloadDirectory 'manifest.json') -Destination $task
    Copy-Item -LiteralPath (Join-Path $project 'src\RevisionUpdateHelper.ps1') -Destination (Join-Path $task 'UpdateHelper.ps1')
    Copy-Item -LiteralPath (Join-Path $project 'src\RevisionUpdateHelper.cs.txt') -Destination (Join-Path $task 'UpdateHelper.cs')
    Copy-Item -LiteralPath (Join-Path $ReferenceGameRoot 'BepInEx\core\Mono.Cecil.dll') -Destination $task
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'AzureArchive.exe'),'--hold');$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.WorkingDirectory=$root
    $parent=[Diagnostics.Process]::Start($start)
    for($i=0;$i -lt 100 -and !(Test-Path -LiteralPath (Join-Path $root 'parent.ready'));$i++){Start-Sleep -Milliseconds 20}
    Assert (Test-Path -LiteralPath (Join-Path $root 'parent.ready')) 'Synthetic parent did not start'
    $job=@{Root=$root;CurrentVersion='1.1.0';TargetVersion='1.2.0';Token=('a'*64);ParentPid=$parent.Id;ParentStartUtcTicks=$parent.StartTime.ToUniversalTime().Ticks;AssemblySha256=(Sha (Join-Path $task 'AzureArchive.RevisionCompare.dll'));AssemblySize=(Get-Item -LiteralPath (Join-Path $task 'AzureArchive.RevisionCompare.dll')).Length;ManifestSha256=(Sha (Join-Path $task 'manifest.json'));ManifestSize=(Get-Item -LiteralPath (Join-Path $task 'manifest.json')).Length;HostSha256=(Sha (Join-Path $root 'AzureArchive.exe'))}
    $jobPath=Join-Path $task 'job.json';WriteJson $jobPath $job
    return [pscustomobject]@{Root=$root;Task=$task;Job=$job;JobPath=$jobPath;Parent=$parent;Helper=$null}
}
function Start-Helper($fixture){
    $arguments='-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $fixture.Task 'UpdateHelper.ps1')+'" -JobPath "'+$fixture.JobPath+'" -JobSha256 '+(Sha $fixture.JobPath)
    $start=[Diagnostics.ProcessStartInfo]::new($powershell,$arguments);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.WorkingDirectory=$fixture.Task
    $start.EnvironmentVariables['DOORSTOP_DISABLE']='inherited-disabled'
    $fixture.Helper=[Diagnostics.Process]::Start($start)
}
function Wait-State($fixture,[string]$state){
    $path=Join-Path $fixture.Task 'status.json'
    for($i=0;$i -lt 400;$i++){
        if(Test-Path -LiteralPath $path){$status=[IO.File]::ReadAllText($path)|ConvertFrom-Json;if($status.State -eq $state){return $status};if($status.State -in @('Failed','Cancelled','Completed')){throw ('Unexpected status '+($status|ConvertTo-Json -Compress))}}
        if($fixture.Helper.HasExited){throw ('Helper exited before '+$state)}
        Start-Sleep -Milliseconds 50
    }
    throw ('Timeout waiting for '+$state)
}
function Authorize($fixture){[IO.File]::WriteAllText((Join-Path $fixture.Task 'authorize'),$fixture.Job.Token);Wait-State $fixture 'WaitingForExit'|Out-Null}
function Close-Parent($fixture,[int]$code=0){[IO.File]::WriteAllText((Join-Path $fixture.Root 'exit.code'),[string]$code);Assert ($fixture.Parent.WaitForExit(5000)) 'Parent failed to exit'}
function Wait-Restart($fixture){$marker=Join-Path $fixture.Root 'restarted.marker';for($i=0;$i -lt 100 -and !(Test-Path -LiteralPath $marker);$i++){Start-Sleep -Milliseconds 20};Assert (Test-Path -LiteralPath $marker) 'Restart process did not write its marker';return [IO.File]::ReadAllText($marker)}
function Cleanup($fixture){if($fixture.Parent -and !$fixture.Parent.HasExited){$fixture.Parent.Kill();$fixture.Parent.WaitForExit()};if($fixture.Helper -and !$fixture.Helper.HasExited){[IO.File]::WriteAllText((Join-Path $fixture.Task 'cancel'),$fixture.Job.Token);if(!$fixture.Helper.WaitForExit(3000)){$fixture.Helper.Kill();$fixture.Helper.WaitForExit()}}}
function Test([string]$name,$action){&$action;$script:passed++;Write-Output ('PASS '+$name)}
Test 'Unicode/spaced host, wait-before-write, authorized update, receipts and clean restart environment' {
    $f=New-Fixture 'success';try{
        $before=Snapshot $f.Root;Start-Helper $f;Wait-State $f 'AwaitingApproval'|Out-Null;Assert ((Snapshot $f.Root) -ceq $before) 'Writes before authorization'
        Authorize $f;Assert ((Snapshot $f.Root) -ceq $before) 'Writes while AA running';Close-Parent $f;Wait-State $f 'Completed'|Out-Null
        $receipt=[IO.File]::ReadAllText((Join-Path $f.Root 'mods\AzureArchiveRevisionCompare\install-receipt.json'))|ConvertFrom-Json
        Assert ($receipt.Version -eq '1.2.0' -and $receipt.Profiles[0].Version -eq '1.2.0') 'Receipt not updated';foreach($file in $receipt.Files){Assert ((Sha (Join-Path $f.Root $file.Path)) -eq $file.Sha256) 'Receipt hash mismatch'}
        $profile=[IO.File]::ReadAllText((Join-Path $f.Root 'profiles\Recorder\modconfig.json'))|ConvertFrom-Json
        Assert ($profile.EnabledMods[0].version -eq '1.2.4' -and $profile.EnabledMods[0].custom -eq 'keep' -and $profile.UnknownPreference.number -eq 42) 'Recorder/profile changed'
        Assert ([IO.File]::ReadAllText((Join-Path $f.Root 'mods\AzureArchiveRevisionCompare\user.json')) -eq 'user settings') 'User config lost'
        Assert ((Wait-Restart $f) -eq 'removed') 'Restart inherited DOORSTOP_DISABLE'
    }finally{Cleanup $f}
}
Test 'parent exits without authorization: zero host writes' {
    $f=New-Fixture 'no-authorization';try{$before=Snapshot $f.Root;Start-Helper $f;Wait-State $f 'AwaitingApproval'|Out-Null;Close-Parent $f;Wait-State $f 'Cancelled'|Out-Null;Assert ((Snapshot $f.Root) -ceq $before) 'Unauthorised write'}finally{Cleanup $f}
}
Test 'explicit cancellation after authorization revokes install' {
    $f=New-Fixture 'cancel-authorized';try{$before=Snapshot $f.Root;Start-Helper $f;Wait-State $f 'AwaitingApproval'|Out-Null;Authorize $f;[IO.File]::WriteAllText((Join-Path $f.Task 'cancel'),$f.Job.Token);Wait-State $f 'Cancelled'|Out-Null;Close-Parent $f;Assert ((Snapshot $f.Root) -ceq $before) 'Cancelled authorization wrote host'}finally{Cleanup $f}
}
Test 'authorized abnormal AA exit never installs' {
    $f=New-Fixture 'crash';try{$before=Snapshot $f.Root;Start-Helper $f;Wait-State $f 'AwaitingApproval'|Out-Null;Authorize $f;Close-Parent $f 7;Wait-State $f 'Failed'|Out-Null;Assert ((Snapshot $f.Root) -ceq $before) 'Crash path modified installation'}finally{Cleanup $f}
}
Test 'payload hash tampering rejected before ready' {
    $f=New-Fixture 'tamper';try{$before=Snapshot $f.Root;[IO.File]::AppendAllText((Join-Path $f.Task 'AzureArchive.RevisionCompare.dll'),'tampered');Start-Helper $f;Wait-State $f 'Failed'|Out-Null;Assert ((Snapshot $f.Root) -ceq $before) 'Tampered payload applied'}finally{Cleanup $f}
}
Test 'actual development probe type rejected before ready' {
    $f=New-Fixture 'probe' $probePayload;try{$before=Snapshot $f.Root;Start-Helper $f;Wait-State $f 'Failed'|Out-Null;Assert ((Snapshot $f.Root) -ceq $before) 'Probe payload applied'}finally{Cleanup $f}
}
Test 'receipt write failure restores deleted old files, profile and receipt, restarts old AA' {
    $f=New-Fixture 'rollback';$handle=$null;try{
        $before=Snapshot $f.Root;Start-Helper $f;Wait-State $f 'AwaitingApproval'|Out-Null;Authorize $f
        $handle=[IO.File]::Open((Join-Path $f.Root 'mods\AzureArchiveRevisionCompare\install-receipt.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        Close-Parent $f;Wait-State $f 'Failed'|Out-Null;Assert ((Snapshot $f.Root) -ceq $before) 'Rollback did not restore exact old files'
        $null=Wait-Restart $f
        Assert (Test-Path -LiteralPath (Join-Path $f.Task 'backup\mods\AzureArchiveRevisionCompare\install-receipt.json')) 'Backup missing'
    }finally{if($handle){$handle.Dispose()};Cleanup $f}
}
Test 'generic semver including multi-digit/prerelease and downgrade ordering' {
    $task=Get-ChildItem -LiteralPath $work -Directory|Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'RevisionCompare.ApplyCore.dll')}|Select-Object -First 1
    [void][Reflection.Assembly]::LoadFrom((Join-Path $task.FullName 'Mono.Cecil.dll'));[void][Reflection.Assembly]::LoadFrom((Join-Path $task.FullName 'RevisionCompare.ApplyCore.dll'))
    $v=[AzureArchive.RevisionCompare.UpdateApplication.UpdateVersion]
    Assert ($v::Compare('1.10.0','1.9.0') -gt 0) 'Numeric order incorrect';Assert ($v::Compare('2.0.0','2.0.0-rc.1') -gt 0) 'Prerelease order incorrect';Assert ($v::Compare('2.0.0+new','2.0.0+old') -eq 0) 'Build metadata changes precedence';Assert (!$v::IsValid('../2.0.0')) 'Unsafe version accepted'
}
@{passed=$script:passed;fixture=$work;actualHostWritten=$false;helperSourceSha256=(Sha (Join-Path $project 'src\RevisionUpdateHelper.cs.txt'))}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $work 'results.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $work 'results.json')
