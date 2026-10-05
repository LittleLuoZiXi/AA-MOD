param(
    [string]$InstallerPath = (Join-Path $PSScriptRoot 'dist\剧本改稿同步 MOD  一键安装.exe'),
    [string]$ExpectedInstallerSha256,
    [string]$ExpectedPluginPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build\AzureArchive.RevisionCompare.dll'),
    [string]$ExpectedManifestPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'manifest.json'),
    [string]$ExpectedVersion = '1.1.0',
    [string]$ReferenceGameRoot = 'E:\AzureArchive_100_fix',
    [switch]$SmokeOnly
)
$ErrorActionPreference='Stop'
if($ExpectedInstallerSha256 -notmatch '^[0-9a-fA-F]{64}$'){throw 'Pass -ExpectedInstallerSha256 from the final approved build; stale hardcoded hashes are not reused.'}
$project=Split-Path $PSScriptRoot -Parent
$installer=(Resolve-Path -LiteralPath $InstallerPath).Path
$work=Join-Path $project ('evidence\protected-installer\'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($work)
$expectedPlugin=(Get-FileHash -LiteralPath $ExpectedPluginPath -Algorithm SHA256).Hash
$expectedManifest=(Get-FileHash -LiteralPath $ExpectedManifestPath -Algorithm SHA256).Hash
$expectedMetadata=[IO.File]::ReadAllText($ExpectedManifestPath)|ConvertFrom-Json
if($expectedMetadata.name -ne 'AzureArchiveRevisionCompare' -or $expectedMetadata.version_number -ne $ExpectedVersion){throw 'Expected payload manifest does not match the requested installer release.'}
$mod='mods/AzureArchiveRevisionCompare'
$receiptRelative=$mod+'/install-receipt.json'
$dllRelative=$mod+'/'+$ExpectedVersion+'/AzureArchive.RevisionCompare.dll'
$manifestRelative=$mod+'/'+$ExpectedVersion+'/manifest.json'
$script:passed=0
$script:calls=[Collections.Generic.List[object]]::new()
$script:backupPaths=[Collections.Generic.List[string]]::new()
function Assert($condition,[string]$message){if(!$condition){throw $message}}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function WriteJson([string]$path,$value){[IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 50 -Compress),[Text.UTF8Encoding]::new($false))}
function ReadJson([string]$path){[IO.File]::ReadAllText($path)|ConvertFrom-Json}
function Decode-ConsoleBytes([byte[]]$bytes){try{return [Text.UTF8Encoding]::new($false,$true).GetString($bytes)}catch [Text.DecoderFallbackException]{return [Text.Encoding]::GetEncoding(936).GetString($bytes)}}
function Snapshot([string]$root){$map=[ordered]@{};Get-ChildItem -LiteralPath $root -Recurse -File|Sort-Object FullName|ForEach-Object{$map[$_.FullName.Substring($root.Length)]=Hash $_.FullName};$map|ConvertTo-Json -Compress}
function DailyHashes {
    $map=[ordered]@{}
    $active=[IO.File]::ReadAllText((Join-Path $ReferenceGameRoot 'ActiveProfile.txt')).Trim()
    if($active -notmatch '^[^\\/:*?"<>|]+$' -or $active -in @('.','..')){throw 'Invalid reference profile name.'}
    $paths=@('ActiveProfile.txt',('profiles/'+$active+'/modconfig.json'),'mods/AzureArchiveRevisionCompare/install-receipt.json','mods/AzureArchiveRecorder/1.2.4/AzureArchive.Recorder.dll')
    $referenceReceipt=ReadJson (Join-Path $ReferenceGameRoot 'mods/AzureArchiveRevisionCompare/install-receipt.json')
    foreach($file in $referenceReceipt.Files){
        if($file.Path -notmatch '^mods/AzureArchiveRevisionCompare/[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.+-]+)?/(AzureArchive\.RevisionCompare\.dll|manifest\.json)$'){throw 'Unexpected reference receipt path.'}
        $paths+=$file.Path
    }
    foreach($relative in $paths){
        $map[$relative]=Hash (Join-Path $ReferenceGameRoot $relative)
    }
    return $map
}
function Fixture([string]$name){
    $root=Join-Path $work ($name+' AA 中文 space')
    foreach($relative in @('AzureArchive_Data','profiles\Recorder','mods\AzureArchiveRecorder\1.2.4','mods\UserOther\2.0.0')){$null=[IO.Directory]::CreateDirectory((Join-Path $root $relative))}
    [IO.File]::WriteAllText((Join-Path $root 'AzureArchive.exe'),'MZ synthetic host; never execute')
    [IO.File]::WriteAllText((Join-Path $root 'ActiveProfile.txt'),'Recorder')
    [IO.File]::WriteAllText((Join-Path $root 'mods\AzureArchiveRecorder\1.2.4\keep.txt'),'Recorder must remain byte-for-byte')
    [IO.File]::WriteAllText((Join-Path $root 'mods\UserOther\2.0.0\settings.json'),'Other mod user configuration')
    WriteJson (Join-Path $root 'profiles\Recorder\modconfig.json') @{EnabledMods=@(@{name='AzureArchiveRecorder';version='1.2.4';custom='preserve'},@{name='UserOther';version='2.0.0'});CustomRoot=@{number=42;text='中文偏好'}}
    return $root
}
function Existing([string]$name){
    $root=Fixture $name;$old='mods/AzureArchiveRevisionCompare/0.1.0';$null=[IO.Directory]::CreateDirectory((Join-Path $root $old))
    [IO.File]::WriteAllText((Join-Path $root ($old+'/AzureArchive.RevisionCompare.dll')),'MZ synthetic prior version')
    WriteJson (Join-Path $root ($old+'/manifest.json')) @{name='AzureArchiveRevisionCompare';version_number='0.1.0';dependencies=@()}
    $files=@();foreach($name in @('AzureArchive.RevisionCompare.dll','manifest.json')){$files+=@{Path=$old+'/'+$name;Sha256=(Hash (Join-Path $root ($old+'/'+$name))).ToLowerInvariant()}}
    WriteJson (Join-Path $root $receiptRelative) @{Schema=1;ProductId='azurearchive.revisioncompare';ProductName='AzureArchiveRevisionCompare';Version='0.1.0';Root=$root;Files=$files;Profiles=@(@{Profile='Recorder';Version='0.1.0'})}
    $path=Join-Path $root 'profiles\Recorder\modconfig.json';$profile=ReadJson $path;$profile.EnabledMods+=@{name='AzureArchiveRevisionCompare';version='0.1.0'};WriteJson $path $profile
    [IO.File]::WriteAllText((Join-Path $root ($old+'/user-note.txt')),'Keep unknown old-version file')
    return $root
}
function Invoke-Installer([string]$action,[string]$root,[int]$expectedExit=0){
    if($root){
        $full=[IO.Path]::GetFullPath($root)
        Assert ($full.StartsWith([IO.Path]::GetFullPath($work)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'Refusing to run installer against a non-fixture host'
    }
    $start=[Diagnostics.ProcessStartInfo]::new($installer)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.WorkingDirectory=$work
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.Arguments=$action+$(if($root){' "'+$root+'"'}else{''})
    $process=[Diagnostics.Process]::Start($start)
    $outputBuffer=[IO.MemoryStream]::new();$errorBuffer=[IO.MemoryStream]::new()
    try{
        # Capture raw bytes concurrently: Framework child output can use UTF-8 or CP936
        # depending on whether the launching PowerShell inherited a console.
        $stdout=$process.StandardOutput.BaseStream.CopyToAsync($outputBuffer);$stderr=$process.StandardError.BaseStream.CopyToAsync($errorBuffer)
        if(!$process.WaitForExit(30000)){$process.Kill();$process.WaitForExit();throw 'Protected installer CLI timed out.'}
        $null=$stdout.GetAwaiter().GetResult();$null=$stderr.GetAwaiter().GetResult()
        $output=Decode-ConsoleBytes $outputBuffer.ToArray();$errorText=Decode-ConsoleBytes $errorBuffer.ToArray()
        $record=[ordered]@{action=$action;root=$root;exitCode=$process.ExitCode;stdout=$output;stderr=$errorText}
        $script:calls.Add($record)
        Assert ($process.ExitCode -eq $expectedExit) ('Unexpected CLI exit '+$process.ExitCode+' for '+$action+'. Output: '+$output+' Error: '+$errorText)
        foreach($line in ($output -split "`r?`n")){
            if($line -match '([A-Za-z]:\\[^\r\n]*\\InstallerBackups\\[^\r\n]+)'){$script:backupPaths.Add($matches[1].Trim())}
        }
        return $record
    }finally{$process.Dispose();$outputBuffer.Dispose();$errorBuffer.Dispose()}
}
function Check-Installed([string]$root){
    Assert ((Hash (Join-Path $root $dllRelative)) -eq $expectedPlugin) 'Installed plugin differs from final embedded payload'
    Assert ((Hash (Join-Path $root $manifestRelative)) -eq $expectedManifest) 'Installed manifest differs from final embedded payload'
    $receipt=ReadJson (Join-Path $root $receiptRelative)
    Assert ($receipt.Schema -eq 1 -and $receipt.ProductId -eq 'azurearchive.revisioncompare' -and $receipt.Version -eq $ExpectedVersion -and $receipt.Root -eq $root -and $receipt.Files.Count -eq 2 -and $receipt.Profiles.Count -eq 1 -and $receipt.Profiles[0].Version -eq $ExpectedVersion) 'Protected core receipt schema/ownership is wrong'
    foreach($file in $receipt.Files){Assert ((Hash (Join-Path $root $file.Path)).ToLowerInvariant() -eq $file.Sha256) 'Receipt hash differs from disk'}
    $profile=ReadJson (Join-Path $root 'profiles\Recorder\modconfig.json')
    Assert ($profile.EnabledMods.Count -eq 3 -and $profile.EnabledMods[0].name -eq 'AzureArchiveRecorder' -and $profile.EnabledMods[0].version -eq '1.2.4' -and $profile.EnabledMods[0].custom -eq 'preserve' -and $profile.EnabledMods[1].name -eq 'UserOther' -and $profile.EnabledMods[2].version -eq $ExpectedVersion -and $profile.CustomRoot.number -eq 42 -and $profile.CustomRoot.text -eq '中文偏好') 'Profile content not preserved'
    Assert ([IO.File]::ReadAllText((Join-Path $root 'mods\AzureArchiveRecorder\1.2.4\keep.txt')) -eq 'Recorder must remain byte-for-byte') 'Recorder file changed'
    Assert ([IO.File]::ReadAllText((Join-Path $root 'mods\UserOther\2.0.0\settings.json')) -eq 'Other mod user configuration') 'Other mod configuration changed'
}
function Test([string]$name,$action){&$action;$script:passed++;Write-Output ('PASS '+$name)}
$dailyBefore=DailyHashes
$installerBefore=Hash $installer
Assert ($installerBefore -eq $ExpectedInstallerSha256) 'Final EXE hash does not match the approved artifact'
WriteJson (Join-Path $work 'daily-host-before.json') $dailyBefore
try{
    Test 'protected EXE verifies exact bundled plugin and manifest hashes' {
        $record=Invoke-Installer '--verify' ''
        $metadata=$record.stdout|ConvertFrom-Json
        Assert ($metadata.Product -eq 'AzureArchiveRevisionCompare' -and $metadata.Version -eq $ExpectedVersion -and $metadata.PluginSha256 -eq $expectedPlugin -and $metadata.ManifestSha256 -eq $expectedManifest) 'Embedded payload verification failed'
    }
    Test 'actual protected fresh install preserves Recorder, other mods and custom profile fields' {
        $script:fresh=Fixture 'fresh';$null=Invoke-Installer '--install' $script:fresh;Check-Installed $script:fresh
    }
    if (!$SmokeOnly) {
    Test 'actual protected repeated install is byte-for-byte idempotent' {
        $before=Snapshot $script:fresh;$null=Invoke-Installer '--install' $script:fresh;Assert ((Snapshot $script:fresh) -ceq $before) 'Repeat install changed host bytes'
    }
    Test 'actual protected 0.1.0 migration updates ownership and retains unknown old-version files' {
        $script:migrated=Existing 'migration';$null=Invoke-Installer '--install' $script:migrated;Check-Installed $script:migrated
        Assert (!(Test-Path -LiteralPath (Join-Path $script:migrated ($mod+'/0.1.0/AzureArchive.RevisionCompare.dll')))) 'Owned old DLL remains'
        Assert ([IO.File]::ReadAllText((Join-Path $script:migrated ($mod+'/0.1.0/user-note.txt'))) -eq 'Keep unknown old-version file') 'Unknown old file removed'
    }
    Test 'actual protected reinstall refuses a modified owned DLL without further host writes' {
        [IO.File]::AppendAllText((Join-Path $script:migrated $dllRelative),'user-modified')
        $before=Snapshot $script:migrated;$null=Invoke-Installer '--install' $script:migrated 1;Assert ((Snapshot $script:migrated) -ceq $before) 'Rejected modified-file operation changed host'
    }
    Test 'actual protected uninstall retains changed DLL and ownership receipt with exit code 2' {
        $beforeHash=Hash (Join-Path $script:migrated $dllRelative);$null=Invoke-Installer '--uninstall' $script:migrated 2
        Assert ((Hash (Join-Path $script:migrated $dllRelative)) -eq $beforeHash) 'Modified DLL deleted or changed'
        Assert ((ReadJson (Join-Path $script:migrated $receiptRelative)).Files.Count -eq 1) 'Remaining file ownership missing'
        Assert ((ReadJson (Join-Path $script:migrated 'profiles\Recorder\modconfig.json')).EnabledMods.Count -eq 2) 'Other mod entries changed during partial uninstall'
        Assert (Test-Path -LiteralPath (Join-Path $script:migrated ($mod+'/0.1.0/user-note.txt'))) 'Unknown legacy user file deleted'
    }
    }
    Test 'actual protected clean uninstall removes only owned files and keeps user extras' {
        [IO.File]::WriteAllText((Join-Path $script:fresh ($mod+'/user-settings.json')),'Do not delete user settings')
        $null=Invoke-Installer '--uninstall' $script:fresh
        Assert (!(Test-Path -LiteralPath (Join-Path $script:fresh $dllRelative)) -and !(Test-Path -LiteralPath (Join-Path $script:fresh $manifestRelative)) -and !(Test-Path -LiteralPath (Join-Path $script:fresh $receiptRelative))) 'Owned installation content remains'
        Assert ([IO.File]::ReadAllText((Join-Path $script:fresh ($mod+'/user-settings.json'))) -eq 'Do not delete user settings') 'Unknown user configuration deleted'
        $profile=ReadJson (Join-Path $script:fresh 'profiles\Recorder\modconfig.json');Assert ($profile.EnabledMods.Count -eq 2 -and $profile.EnabledMods[0].version -eq '1.2.4' -and $profile.CustomRoot.number -eq 42) 'Uninstall altered unrelated profile settings'
    }
    if (!$SmokeOnly) {
    Test 'actual protected core rejects a tampered receipt targeting Recorder' {
        $root=Existing 'tampered-receipt';$path=Join-Path $root $receiptRelative;$receipt=ReadJson $path;$receipt.Files[0].Path='mods/AzureArchiveRecorder/1.2.4/keep.txt';WriteJson $path $receipt
        $before=Snapshot $root;$null=Invoke-Installer '--uninstall' $root 1;Assert ((Snapshot $root) -ceq $before) 'Tampered receipt caused a write'
    }
    }
    Test 'real produced backups contain valid original hashes after obfuscation' {
        Assert ($script:backupPaths.Count -ge $(if($SmokeOnly){2}else{4})) 'CLI did not report expected backup directories'
        foreach($path in $script:backupPaths){
            $backup=ReadJson (Join-Path $path 'backup.json');Assert ($backup.ProductId -eq 'azurearchive.revisioncompare') 'Backup schema damaged'
            Assert ($backup.Root.StartsWith($work+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'Backup belongs to unexpected root'
            foreach($file in $backup.Files){if($file.Existed){Assert ((Hash (Join-Path $path ('files/'+$file.Path))).ToLowerInvariant() -eq $file.Sha256) 'Backup content mismatch'}}
        }
    }
    $dailyAfter=DailyHashes
    WriteJson (Join-Path $work 'daily-host-after.json') $dailyAfter
    Test 'daily AA six key files and final EXE remain unchanged' {
        Assert (($dailyBefore|ConvertTo-Json -Compress) -ceq ($dailyAfter|ConvertTo-Json -Compress)) 'Daily host changed during isolated verification'
        Assert ((Hash $installer) -eq $installerBefore) 'Final installer EXE changed during verification'
    }
    WriteJson (Join-Path $work 'results.json') ([ordered]@{mode=$(if($SmokeOnly){'SmokeOnly'}else{'Full'});passed=$script:passed;version=$ExpectedVersion;installer=$installer;installerSha256=$installerBefore;pluginSha256=$expectedPlugin;manifestSha256=$expectedManifest;fixture=$work;actualHostWritten=$false;dailyHostUnchanged=$true;backups=@($script:backupPaths);calls=@($script:calls)})
    Get-Content -LiteralPath (Join-Path $work 'results.json') -Raw
}catch{
    WriteJson (Join-Path $work 'failure.json') ([ordered]@{passed=$script:passed;error=$_.Exception.Message;fixture=$work;calls=@($script:calls);dailyBefore=$dailyBefore;dailyAfter=(DailyHashes)})
    throw
}
