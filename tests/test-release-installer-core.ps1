param()
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$work=Join-Path $project ('evidence\release-installer-core\'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($work)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$library=Join-Path $work 'ReleaseInstaller.Core.dll'
&$compiler /nologo /target:library /utf8output /codepage:65001 "/out:$library" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $project 'packaging\InstallerCore.cs')
if($LASTEXITCODE -ne 0){throw 'Core compilation failed.'}
[void][Reflection.Assembly]::LoadFrom($library)
$core=[AzureArchive.RevisionCompare.Installation.InstallerCore]
$backupRoot=Join-Path $work 'backups'
$plugin=[Text.Encoding]::UTF8.GetBytes('MZ synthetic release installer payload 1')
$newPlugin=[Text.Encoding]::UTF8.GetBytes('MZ synthetic release installer payload 2')
$manifest=[Text.Encoding]::UTF8.GetBytes('{"name":"AzureArchiveRevisionCompare","version_number":"1.1.0","dependencies":[]}')
$script:passed=0
function Assert($condition,[string]$message){if(!$condition){throw $message}}
function WriteJson([string]$path,$value){[IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 50 -Compress),[Text.UTF8Encoding]::new($false))}
function ReadJson([string]$path){[IO.File]::ReadAllText($path)|ConvertFrom-Json}
function Sha([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Snapshot([string]$root){$map=[ordered]@{};Get-ChildItem -LiteralPath $root -File -Recurse|Sort-Object FullName|ForEach-Object{$map[$_.FullName.Substring($root.Length)]=Sha $_.FullName};$map|ConvertTo-Json -Compress}
function Fixture([string]$name){
    $root=Join-Path $work ($name+' AA 中文 space')
    foreach($relative in @('AzureArchive_Data','profiles\Recorder','mods\AzureArchiveRecorder\1.2.4')){$null=[IO.Directory]::CreateDirectory((Join-Path $root $relative))}
    [IO.File]::WriteAllText((Join-Path $root 'AzureArchive.exe'),'Synthetic non-executable host')
    [IO.File]::WriteAllText((Join-Path $root 'ActiveProfile.txt'),'Recorder')
    [IO.File]::WriteAllText((Join-Path $root 'mods\AzureArchiveRecorder\1.2.4\keep.txt'),'Recorder preserved')
    WriteJson (Join-Path $root 'profiles\Recorder\modconfig.json') @{EnabledMods=@(@{name='AzureArchiveRecorder';version='1.2.4';custom='unchanged'});Unknown=@{number=42;text='preserve'}}
    return $root
}
function Existing([string]$name,[string]$version){
    $root=Fixture $name;$relative='mods/AzureArchiveRevisionCompare/'+$version
    $null=[IO.Directory]::CreateDirectory((Join-Path $root $relative));[IO.File]::WriteAllText((Join-Path $root ($relative+'/AzureArchive.RevisionCompare.dll')),'MZ previous installed plugin')
    WriteJson (Join-Path $root ($relative+'/manifest.json')) @{name='AzureArchiveRevisionCompare';version_number=$version;dependencies=@()}
    $files=@();foreach($name in @('AzureArchive.RevisionCompare.dll','manifest.json')){$files+=@{Path=$relative+'/'+$name;Sha256=(Sha (Join-Path $root ($relative+'/'+$name)))}}
    WriteJson (Join-Path $root $core::ReceiptRelative) @{Schema=1;ProductId=$core::ProductId;ProductName=$core::ProductName;Version=$version;Root=$root;Files=$files;Profiles=@(@{Profile='Recorder';Version=$version})}
    $profilePath=Join-Path $root 'profiles\Recorder\modconfig.json';$profile=ReadJson $profilePath;$profile.EnabledMods+=@{name=$core::ProductName;version=$version};WriteJson $profilePath $profile
    return $root
}
function Install([string]$root,[byte[]]$bytes=$plugin){$core::Install($root,$bytes,$manifest,$backupRoot)}
function Uninstall([string]$root){$core::Uninstall($root,$backupRoot)}
function Reject($action,[string]$root){$before=Snapshot $root;$caught=$false;try{&$action|Out-Null}catch{$caught=$true};Assert $caught 'Expected safe rejection';Assert ((Snapshot $root) -ceq $before) 'Rejected operation changed host files'}
function Test([string]$name,$action){&$action;$script:passed++;Write-Output ('PASS '+$name)}
function ValidateBackup([string]$directory){
    Assert (Test-Path -LiteralPath (Join-Path $directory 'backup.json')) 'Backup index missing'
    $record=ReadJson (Join-Path $directory 'backup.json');foreach($file in $record.Files){if($file.Existed){Assert ((Sha (Join-Path $directory ('files/'+$file.Path))) -eq $file.Sha256) 'Backup content hash mismatch'}}
}
Test 'fresh install, idempotent reinstall and clean uninstall preserve Recorder and unknown configuration' {
    $root=Fixture 'fresh';$originalProfile=ReadJson (Join-Path $root 'profiles\Recorder\modconfig.json')
    $result=Install $root;ValidateBackup $result.BackupDirectory
    $profile=ReadJson (Join-Path $root 'profiles\Recorder\modconfig.json');Assert ($profile.EnabledMods.Count -eq 2 -and $profile.EnabledMods[0].version -eq '1.2.4' -and $profile.EnabledMods[0].custom -eq 'unchanged' -and $profile.Unknown.number -eq 42) 'Profile preservation failed'
    $before=Snapshot $root;$repeat=Install $root;Assert ((Snapshot $root) -ceq $before -and !$repeat.BackupDirectory) 'Idempotent operation changed files or created unnecessary backup'
    $removed=Uninstall $root;ValidateBackup $removed.BackupDirectory
    Assert (!(Test-Path -LiteralPath (Join-Path $root $core::ModDirectory))) 'Owned empty directory remains'
    $profile=ReadJson (Join-Path $root 'profiles\Recorder\modconfig.json');Assert ($profile.EnabledMods.Count -eq 1 -and $profile.Unknown.number -eq 42) 'Uninstall changed unrelated profile content'
    Assert ([IO.File]::ReadAllText((Join-Path $root 'mods\AzureArchiveRecorder\1.2.4\keep.txt')) -eq 'Recorder preserved') 'Recorder file changed'
}
Test 'replace receipt-owned 1.1.0 build with updated packaged bytes and retain original backup' {
    $root=Existing 'same-version' '1.1.0';$oldHash=Sha (Join-Path $root $core::DllRelative)
    $result=Install $root $newPlugin;ValidateBackup $result.BackupDirectory
    Assert ((Sha (Join-Path $root $core::DllRelative)) -eq $core::Hash($newPlugin)) 'Same-version plugin not replaced'
    Assert ((Sha (Join-Path $result.BackupDirectory ('files/'+$core::DllRelative))) -eq $oldHash) 'Replaced plugin backup not exact'
    $receipt=ReadJson (Join-Path $root $core::ReceiptRelative);foreach($file in $receipt.Files){Assert ((Sha (Join-Path $root $file.Path)) -eq $file.Sha256) 'New receipt hash mismatch'}
}
Test 'repair a missing owned 1.1.0 DLL without duplicating or rewriting the profile entry' {
    $root=Existing 'missing-plugin' '1.1.0';$path=Join-Path $root $core::DllRelative;[IO.File]::Delete($path)
    $profilePath=Join-Path $root 'profiles\Recorder\modconfig.json';$beforeProfile=Sha $profilePath
    $null=Install $root
    Assert ((Sha $path) -eq $core::Hash($plugin) -and (Sha $profilePath) -eq $beforeProfile) 'Missing-file repair did not preserve profile bytes'
    $receipt=ReadJson (Join-Path $root $core::ReceiptRelative);foreach($file in $receipt.Files){Assert ((Sha (Join-Path $root $file.Path)) -eq $file.Sha256) 'Repaired receipt hash mismatch'}
}
Test '0.1.0 migration preserves user files, Recorder and ownership' {
    $root=Existing 'migration' '0.1.0';[IO.File]::WriteAllText((Join-Path $root 'mods\AzureArchiveRevisionCompare\0.1.0\notes.txt'),'user notes')
    $result=Install $root;ValidateBackup $result.BackupDirectory
    Assert (!(Test-Path -LiteralPath (Join-Path $root 'mods\AzureArchiveRevisionCompare\0.1.0\AzureArchive.RevisionCompare.dll'))) 'Owned old file remains'
    Assert ([IO.File]::ReadAllText((Join-Path $root 'mods\AzureArchiveRevisionCompare\0.1.0\notes.txt')) -eq 'user notes') 'Unknown old-version file removed'
    $receipt=ReadJson (Join-Path $root $core::ReceiptRelative);Assert ($receipt.Version -eq '1.1.0' -and $receipt.Profiles[0].Version -eq '1.1.0') 'Receipt migration incomplete'
}
Test 'modified owned file rejects reinstall without host mutation' {
    $root=Existing 'changed-owned' '1.1.0';[IO.File]::AppendAllText((Join-Path $root $core::DllRelative),'modified');Reject {Install $root} $root
}
Test 'unowned same-path file rejects install without adoption' {
    $root=Fixture 'collision';$null=[IO.Directory]::CreateDirectory((Join-Path $root 'mods\AzureArchiveRevisionCompare\1.1.0'));[IO.File]::WriteAllText((Join-Path $root $core::DllRelative),'manual file');Reject {Install $root} $root
}
Test 'modified payload and unknown files survive uninstall with remaining receipt' {
    $root=Existing 'uninstall-modified' '1.1.0';[IO.File]::AppendAllText((Join-Path $root $core::DllRelative),'modified');[IO.File]::WriteAllText((Join-Path $root 'mods\AzureArchiveRevisionCompare\custom.json'),'settings')
    $result=Uninstall $root;Assert ($result.Preserved.Count -eq 1) 'Modified file not reported';Assert (Test-Path -LiteralPath (Join-Path $root $core::DllRelative)) 'Modified file deleted';Assert (Test-Path -LiteralPath (Join-Path $root 'mods\AzureArchiveRevisionCompare\custom.json')) 'Unknown file deleted'
    Assert ((ReadJson (Join-Path $root $core::ReceiptRelative)).Files.Count -eq 1) 'Remaining ownership lost'
}
Test 'manually enabled profile item is never claimed or removed' {
    $root=Fixture 'manual-profile';$path=Join-Path $root 'profiles\Recorder\modconfig.json';$profile=ReadJson $path;$profile.EnabledMods+=@{name=$core::ProductName;version='1.1.0'};WriteJson $path $profile
    $null=Install $root;$null=Uninstall $root;Assert ((ReadJson $path).EnabledMods.Count -eq 2) 'Manual entry removed'
}
Test 'tampered ownership paths and missing receipt fields reject safely' {
    $root=Existing 'tamper' '1.1.0';$path=Join-Path $root $core::ReceiptRelative;$receipt=ReadJson $path;$receipt.Files[0].Path='mods/AzureArchiveRecorder/1.2.4/keep.txt';WriteJson $path $receipt;Reject {Uninstall $root} $root
    $root=Existing 'missing-field' '1.1.0';$path=Join-Path $root $core::ReceiptRelative;$receipt=ReadJson $path;$receipt.PSObject.Properties.Remove('Files');WriteJson $path $receipt;Reject {Uninstall $root} $root
}
Test 'future valid receipt can be uninstalled but cannot be downgraded' {
    $root=Existing 'future' '1.2.0';Reject {Install $root} $root;$result=Uninstall $root;ValidateBackup $result.BackupDirectory
    Assert (!(Test-Path -LiteralPath (Join-Path $root 'mods\AzureArchiveRevisionCompare\1.2.0\AzureArchive.RevisionCompare.dll'))) 'Future owned version not removed'
    Assert ((ReadJson (Join-Path $root 'profiles\Recorder\modconfig.json')).EnabledMods.Count -eq 1) 'Future profile item remains'
}
Test 'malformed profile prevents partial fresh installation' {
    $root=Fixture 'bad-profile';[IO.File]::WriteAllText((Join-Path $root 'profiles\Recorder\modconfig.json'),'{broken');Reject {Install $root} $root
}
Test 'backup failure happens before any host mutation' {
    $root=Existing 'backup-fail' '1.1.0';$blocked=Join-Path $work 'not-a-directory';[IO.File]::WriteAllText($blocked,'occupied');Reject {$core::Install($root,$plugin,$manifest,$blocked)} $root
}
Test 'late install receipt failure rolls back all plugin, old-version and profile writes' {
    $root=Existing 'rollback-install' '0.1.0';$handle=[IO.File]::Open((Join-Path $root $core::ReceiptRelative),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{Reject {Install $root} $root}finally{$handle.Dispose()}
}
Test 'late uninstall receipt failure restores payload and exact profile bytes' {
    $root=Existing 'rollback-uninstall' '1.1.0';$handle=[IO.File]::Open((Join-Path $root $core::ReceiptRelative),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{Reject {Uninstall $root} $root}finally{$handle.Dispose()}
}
Test 'junction host paths and unsafe profile components are rejected' {
    $root=Fixture 'link-target';$link=Join-Path $work 'host-link';$null=New-Item -ItemType Junction -Path $link -Target $root
    $before=Snapshot $root;$caught=$false;try{Install $link|Out-Null}catch{$caught=$true};Assert ($caught -and (Snapshot $root) -ceq $before) 'Junction host was written'
    [IO.File]::WriteAllText((Join-Path $root 'ActiveProfile.txt'),'../outside');Reject {Install $root} $root
}
Test 'changed owned profile entry is preserved during uninstall' {
    $root=Existing 'changed-profile' '1.1.0';$path=Join-Path $root 'profiles\Recorder\modconfig.json';$profile=ReadJson $path;$profile.EnabledMods[1]|Add-Member NoteProperty custom 'keep';WriteJson $path $profile
    $result=Uninstall $root;Assert ($result.Preserved.Count -eq 1 -and (ReadJson $path).EnabledMods.Count -eq 2) 'Modified profile entry removed'
}
@{passed=$script:passed;fixture=$work;actualHostWritten=$false;coreSha256=(Sha $library);sourceSha256=(Sha (Join-Path $project 'packaging\InstallerCore.cs'))}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $work 'results.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $work 'results.json')
