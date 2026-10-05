param(
    # Used only to locate the read-only Mono.Cecil dependency. No host data is copied.
    [string]$ReferenceGameRoot='E:\AzureArchive_100_fix',
    [string]$MetadataLibraryPath
)
$ErrorActionPreference='Stop'
$modRoot=Split-Path $PSScriptRoot -Parent
$work=Join-Path $modRoot ('evidence\local-migration\'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($work)
$core=Join-Path $work 'LocalDeployment.Core.dll'
& (Join-Path $PSScriptRoot 'build-local-deployment.ps1') -OutputPath $core | Out-Null
$null=[Reflection.Assembly]::LoadFrom($core)
$deployment=[AzureArchive.RevisionCompare.LocalDeployment.DeploymentCore]
$targetVersion=$deployment::Version
$oldVersion=$deployment::PreviousVersion
$legacyVersion=$deployment::LegacyVersion
$parsedVersion=[Version]::Parse($targetVersion)
$futureVersion='{0}.{1}.0' -f $parsedVersion.Major,($parsedVersion.Minor+1)
$script:passed=0
$script:checks=[Collections.Generic.List[string]]::new()
$utf8=[Text.UTF8Encoding]::new($false)
function Assert($condition,[string]$message) { if(!$condition){throw $message} }
function Write-Json([string]$path,$value){[IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 100 -Compress),$utf8)}
function Read-Json([string]$path){[IO.File]::ReadAllText($path)|ConvertFrom-Json}
function Payload-Path([string]$version,[string]$name) { $deployment::ModDirectory+'/'+$version+'/'+$name }
function Manifest-Bytes([string]$version) {
    $utf8.GetBytes((@{name=$deployment::ProductName;version_number=$version;dependencies=@()}|ConvertTo-Json -Compress))
}
function Snapshot([string]$root) {
    $map=[ordered]@{}
    Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName | ForEach-Object {$map[$_.FullName.Substring($root.Length)]=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    $map|ConvertTo-Json -Compress
}
function New-Fixture([string]$name,[string]$version=$oldVersion) {
    $root=Join-Path $work $name
    foreach($relative in @('AzureArchive_Data','profiles\Recorder','mods\AzureArchiveRecorder\1.2.4',$deployment::ModDirectory)) {$null=[IO.Directory]::CreateDirectory((Join-Path $root $relative))}
    [IO.File]::WriteAllText((Join-Path $root 'AzureArchive.exe'),'Synthetic host - not executable')
    [IO.File]::WriteAllText((Join-Path $root 'ActiveProfile.txt'),'Recorder')
    [IO.File]::WriteAllText((Join-Path $root 'mods\AzureArchiveRecorder\1.2.4\keep.txt'),'Recorder untouched')
    $enabled=@(@{name='AzureArchiveRecorder';version='1.2.4'})
    if($version) {
        $enabled+=@{name=$deployment::ProductName;version=$version}
        $null=[IO.Directory]::CreateDirectory((Join-Path $root ($deployment::ModDirectory+'/'+$version)))
        $dllRelative=Payload-Path $version 'AzureArchive.RevisionCompare.dll'
        $manifestRelative=Payload-Path $version 'manifest.json'
        $oldPlugin=$utf8.GetBytes('MZ synthetic installed version '+$version)
        $oldManifest=Manifest-Bytes $version
        [IO.File]::WriteAllBytes((Join-Path $root $dllRelative),$oldPlugin)
        [IO.File]::WriteAllBytes((Join-Path $root $manifestRelative),$oldManifest)
        Write-Json (Join-Path $root $deployment::ReceiptRelative) ([ordered]@{
            Schema=1;ProductId=$deployment::ProductId;ProductName=$deployment::ProductName;Version=$version;Root=$root
            Files=@(@{Path=$dllRelative;Sha256=$deployment::Hash($oldPlugin)},@{Path=$manifestRelative;Sha256=$deployment::Hash($oldManifest)})
            Profiles=@(@{Profile='Recorder';Version=$version})
        })
        [IO.File]::WriteAllText((Join-Path $root ($deployment::ModDirectory+'/'+$version+'/user-note.txt')),'preserve unknown version file')
    }
    Write-Json (Join-Path $root 'profiles\Recorder\modconfig.json') ([ordered]@{EnabledMods=$enabled;UnknownPreferences=@{value='preserve';number=42}})
    [IO.File]::WriteAllText((Join-Path $root ($deployment::ModDirectory+'/settings.json')),'preserve user settings')
    return $root
}
function Expect-Rejected($action,[string]$root) {
    $before=Snapshot $root;$caught=$false
    try { & $action | Out-Null } catch {$caught=$true}
    Assert $caught 'Expected rejection'
    Assert ((Snapshot $root) -ceq $before) 'Rejected operation changed synthetic host files'
}
function Test([string]$name,$action){&$action;$script:passed++;$script:checks.Add($name);Write-Output ('PASS '+$name)}
function Assert-Installed([string]$root) {
    $profile=Read-Json (Join-Path $root 'profiles\Recorder\modconfig.json')
    Assert ($profile.EnabledMods.Count -eq 2 -and $profile.EnabledMods[0].name -eq 'AzureArchiveRecorder' -and $profile.EnabledMods[0].version -eq '1.2.4' -and $profile.EnabledMods[1].version -eq $targetVersion) 'Profile version or unrelated MOD changed'
    Assert ($profile.UnknownPreferences.value -eq 'preserve' -and $profile.UnknownPreferences.number -eq 42) 'Unknown profile fields changed'
    $receipt=Read-Json (Join-Path $root $deployment::ReceiptRelative)
    Assert ($receipt.Schema -eq 1 -and $receipt.Version -eq $targetVersion -and $receipt.Profiles.Count -eq 1 -and $receipt.Profiles[0].Version -eq $targetVersion -and $receipt.Files.Count -eq 2) 'Receipt schema, ownership or version incorrect'
    foreach($entry in $receipt.Files) {
        Assert ($entry.Path -in @($deployment::DllRelative,$deployment::ManifestRelative)) 'Receipt payload path does not use target version'
        Assert ((Get-FileHash -LiteralPath (Join-Path $root $entry.Path) -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $entry.Sha256) 'Receipt hash mismatch'
    }
}
$manifestBytes=Manifest-Bytes $targetVersion
$payload=$utf8.GetBytes('MZ synthetic new version '+$targetVersion)
Test 'version-derived output paths and fresh installation' {
    Assert ($deployment::DllRelative -ceq (Payload-Path $targetVersion 'AzureArchive.RevisionCompare.dll')) 'DLL path version drift'
    Assert ($deployment::ManifestRelative -ceq (Payload-Path $targetVersion 'manifest.json')) 'Manifest path version drift'
    $root=New-Fixture 'fresh' ''
    $result=$deployment::Install($root,$payload,$manifestBytes)
    Assert ($result.Message.Contains($targetVersion)) 'Success message has stale version'
    Assert-Installed $root
}
foreach($sourceVersion in @($legacyVersion,$oldVersion)) {
    Test ('upgrade '+$sourceVersion+' to '+$targetVersion+', idempotence and safe uninstall') {
        $root=New-Fixture ('migrate-'+$sourceVersion) $sourceVersion
        $null=$deployment::Install($root,$payload,$manifestBytes)
        Assert-Installed $root
        foreach($name in @('AzureArchive.RevisionCompare.dll','manifest.json')) {Assert (!(Test-Path -LiteralPath (Join-Path $root (Payload-Path $sourceVersion $name)))) ('Owned old payload remains: '+$name)}
        $before=Snapshot $root;$null=$deployment::Install($root,$payload,$manifestBytes);Assert ((Snapshot $root) -ceq $before) 'Repeat deployment changed files'
        $null=$deployment::Uninstall($root)
        Assert ((Read-Json (Join-Path $root 'profiles\Recorder\modconfig.json')).EnabledMods.Count -eq 1) 'Uninstall did not remove only our item'
        Assert (!(Test-Path -LiteralPath (Join-Path $root $deployment::ReceiptRelative))) 'Clean uninstall retained ownership receipt'
        foreach($relative in @('mods/AzureArchiveRecorder/1.2.4/keep.txt',($deployment::ModDirectory+'/settings.json'),($deployment::ModDirectory+'/'+$sourceVersion+'/user-note.txt'))) {Assert (Test-Path -LiteralPath (Join-Path $root $relative)) ('Unknown file lost: '+$relative)}
    }
}
Test 'same-version owned installation can be repaired' {
    $root=New-Fixture 'repair' $targetVersion
    $null=$deployment::Install($root,$payload,$manifestBytes);Assert-Installed $root
    Assert ($deployment::Hash([IO.File]::ReadAllBytes((Join-Path $root $deployment::DllRelative))) -eq $deployment::Hash($payload)) 'Repair did not write the current payload'
}
Test 'changed old DLL rejects with exact host preservation' {
    $root=New-Fixture 'modified-old';[IO.File]::AppendAllText((Join-Path $root (Payload-Path $oldVersion 'AzureArchive.RevisionCompare.dll')),'modified')
    Expect-Rejected {$deployment::Install($root,$payload,$manifestBytes)} $root
}
Test 'modified profile entry rejects with exact host preservation' {
    $root=New-Fixture 'modified-profile';$path=Join-Path $root 'profiles\Recorder\modconfig.json';$profile=Read-Json $path;$profile.EnabledMods[1]|Add-Member NoteProperty UserField 'keep';Write-Json $path $profile
    Expect-Rejected {$deployment::Install($root,$payload,$manifestBytes)} $root
}
Test 'unowned target-version payload collision rejects' {
    $root=New-Fixture 'collision';$null=[IO.Directory]::CreateDirectory((Join-Path $root ($deployment::ModDirectory+'/'+$targetVersion)));[IO.File]::WriteAllText((Join-Path $root $deployment::DllRelative),'manual installation')
    Expect-Rejected {$deployment::Install($root,$payload,$manifestBytes)} $root
}
Test 'tampered receipt cannot authorize another MOD deletion' {
    $root=New-Fixture 'tampered';$path=Join-Path $root $deployment::ReceiptRelative;$receipt=Read-Json $path;$receipt.Files[0].Path='mods/AzureArchiveRecorder/1.2.4/keep.txt';Write-Json $path $receipt
    Expect-Rejected {$deployment::Install($root,$payload,$manifestBytes)} $root
}
Test 'additional profile ownership does not silently expand migration' {
    $root=New-Fixture 'extra-profile';$path=Join-Path $root $deployment::ReceiptRelative;$receipt=Read-Json $path;$receipt.Profiles+=@{Profile='Another';Version=$oldVersion};Write-Json $path $receipt
    Expect-Rejected {$deployment::Install($root,$payload,$manifestBytes)} $root
}
Test 'receipt replacement failure rolls back payload and exact profile bytes' {
    $root=New-Fixture 'rollback';$path=Join-Path $root $deployment::ReceiptRelative
    $handle=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {Expect-Rejected {$deployment::Install($root,$payload,$manifestBytes)} $root} finally {$handle.Dispose()}
}
Test 'changed newly installed DLL is retained by uninstall with receipt' {
    $root=New-Fixture 'modified-new';$null=$deployment::Install($root,$payload,$manifestBytes)
    [IO.File]::AppendAllText((Join-Path $root $deployment::DllRelative),'user edit');$result=$deployment::Uninstall($root)
    Assert ($result.Preserved.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $root $deployment::ReceiptRelative))) 'Modified installed file ownership was lost'
}
Test 'future automatic-update version rejects local downgrade and stale uninstall' {
    $root=New-Fixture 'future' $futureVersion
    Expect-Rejected {$deployment::Install($root,$payload,$manifestBytes)} $root
    Expect-Rejected {$deployment::Uninstall($root)} $root
}
Test 'wrong target manifest rejects before changing files' {
    $root=New-Fixture 'wrong-manifest'
    Expect-Rejected {$deployment::Install($root,$payload,(Manifest-Bytes $oldVersion))} $root
}

# A different managed thread owns the same named mutex as the automatic update helper.
$lockSource = @"
using System;
using System.Threading;
public sealed class SyntheticUpdateLock : IDisposable {
    readonly ManualResetEvent ready = new ManualResetEvent(false), stop = new ManualResetEvent(false);
    readonly Thread thread;
    Exception error;
    public SyntheticUpdateLock(string name) {
        thread = new Thread(delegate() {
            try { using (var mutex = new Mutex(false, name)) { mutex.WaitOne(); try { ready.Set(); stop.WaitOne(); } finally { mutex.ReleaseMutex(); } } }
            catch(Exception ex) { error = ex; ready.Set(); }
        });
        thread.IsBackground = true; thread.Start();
        if(!ready.WaitOne(5000)) throw new TimeoutException("Mutex fixture did not start.");
        if(error != null) throw error;
    }
    public void Dispose() { stop.Set(); if(!thread.Join(5000)) throw new TimeoutException("Mutex fixture did not stop."); ready.Dispose(); stop.Dispose(); }
}
"@
Add-Type -TypeDefinition $lockSource
Test 'automatic update helper mutex blocks local install and uninstall without mutation' {
    $root=New-Fixture 'helper-lock'
    $mutexName='Local\AARevisionCompareUpdate-'+$deployment::Hash($utf8.GetBytes($deployment::NormalizeRoot($root).ToUpperInvariant()))
    $held=[SyntheticUpdateLock]::new($mutexName)
    try {
        Expect-Rejected {$deployment::Install($root,$payload,$manifestBytes)} $root
        Expect-Rejected {$deployment::Uninstall($root)} $root
    } finally {$held.Dispose()}
    $null=$deployment::Install($root,$payload,$manifestBytes);Assert-Installed $root
}

# Compile only synthetic plugin DLLs; no real AA process or Unity plugin is loaded.
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'}
$stub = @"
using System;
namespace BepInEx { public sealed class BepInPlugin:Attribute { public BepInPlugin(string id,string name,string version){} } }
namespace AzureArchive.RevisionCompare { [BepInEx.BepInPlugin("azurearchive.revisioncompare","AzureArchiveRevisionCompare","__VERSION__")] public class Plugin {} }
"@
$stub=$stub.Replace('__VERSION__',$targetVersion)
$valid=Join-Path $work 'valid';$probe=Join-Path $work 'probe';$null=[IO.Directory]::CreateDirectory($valid);$null=[IO.Directory]::CreateDirectory($probe)
$validSource=Join-Path $valid 'SyntheticPlugin.cs';$probeSource=Join-Path $probe 'SyntheticPlugin.cs'
[IO.File]::WriteAllText($validSource,$stub);[IO.File]::WriteAllText($probeSource,$stub+' namespace AzureArchive.RevisionCompare { internal class IntegrationProbe {} }')
$validPlugin=Join-Path $valid 'AzureArchive.RevisionCompare.dll';$probePlugin=Join-Path $probe 'AzureArchive.RevisionCompare.dll'
&$compiler /nologo /target:library "/out:$validPlugin" $validSource;if($LASTEXITCODE -ne 0){throw 'Stub compilation failed'}
&$compiler /nologo /target:library "/out:$probePlugin" $probeSource;if($LASTEXITCODE -ne 0){throw 'Probe stub compilation failed'}
$newManifest=Join-Path $work 'manifest.json';[IO.File]::WriteAllBytes($newManifest,$manifestBytes)
if (!$MetadataLibraryPath) {$MetadataLibraryPath=Join-Path $ReferenceGameRoot 'BepInEx\core\Mono.Cecil.dll'}
$cecil=(Resolve-Path -LiteralPath $MetadataLibraryPath).Path
$entrypointArgs=@{PluginPath=$validPlugin;ManifestPath=$newManifest;CorePath=$core;MetadataLibraryPath=$cecil;BackupRoot=(Join-Path $work 'backups')}
Test 'local entrypoint reports current version, versioned paths and verified backups' {
    $root=New-Fixture 'entrypoint'
    $result=& (Join-Path $PSScriptRoot 'install-local.ps1') -GameRoot $root @entrypointArgs | ConvertFrom-Json
    Assert-Installed $root
    Assert ($result.version -eq $targetVersion -and $result.installed -eq (Join-Path $root $deployment::DllRelative) -and $result.receipt -eq (Join-Path $root $deployment::ReceiptRelative)) 'Entrypoint output version or path is stale'
    Assert ((Read-Json (Join-Path $result.backup 'install-receipt.before.json')).Version -eq $oldVersion) 'Backup receipt version incorrect'
    foreach($name in @('AzureArchive.RevisionCompare.dll','manifest.json')) {Assert (Test-Path -LiteralPath (Join-Path $result.backup ($oldVersion+'-'+$name))) 'Owned previous payload not backed up'}
    $before=Snapshot $root
    $repeat=& (Join-Path $PSScriptRoot 'install-local.ps1') -GameRoot $root @entrypointArgs | ConvertFrom-Json
    Assert ((Snapshot $root) -ceq $before -and (Test-Path -LiteralPath (Join-Path $repeat.backup ($targetVersion+'-AzureArchive.RevisionCompare.dll')))) 'Same-version entrypoint or current-version backup failed'
}
Test 'entrypoint rejects actual probe type before any host mutation' {
    $root=New-Fixture 'probe-rejected'
    $probeArgs=$entrypointArgs.Clone();$probeArgs.PluginPath=$probePlugin
    Expect-Rejected {& (Join-Path $PSScriptRoot 'install-local.ps1') -GameRoot $root @probeArgs} $root
}

# Compile the existing helper as a library and call only its read-only preflight.
# Run (which can wait, write or restart) is never called.
$helper=Join-Path $work 'RevisionUpdate.Helper.ReadOnly.dll'
$helperSource=Join-Path $modRoot 'src\RevisionUpdateHelper.cs.txt'
$netstandard=Join-Path ([IO.Path]::GetDirectoryName($compiler)) 'netstandard.dll'
if(!(Test-Path -LiteralPath $netstandard)) {$netstandard=Join-Path ([IO.Path]::GetDirectoryName($compiler)) 'Facades\netstandard.dll'}
if(!(Test-Path -LiteralPath $netstandard)) {throw 'The local .NET Framework netstandard facade is missing.'}
&$compiler /nologo /target:library /utf8output /codepage:65001 "/out:$helper" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll "/reference:$netstandard" "/reference:$cecil" $helperSource
if($LASTEXITCODE -ne 0){throw 'Update helper read-only fixture compilation failed'}
$null=[Reflection.Assembly]::LoadFrom($cecil)
$helperAssembly=[Reflection.Assembly]::LoadFrom($helper)
Test 'new local receipt is accepted by the actual automatic update helper preflight' {
    $root=New-Fixture 'helper-receipt'
    $null=$deployment::Install($root,$payload,$manifestBytes)
    $helperCore=[AzureArchive.RevisionCompare.UpdateApplication.DeploymentCore]
    $helperCore::Version=$futureVersion
    $job=[AzureArchive.RevisionCompare.UpdateApplication.UpdateJob]::new()
    $job.Root=$root;$job.CurrentVersion=$targetVersion;$job.TargetVersion=$futureVersion
    $before=Snapshot $root
    $validate=$helperAssembly.GetType('AzureArchive.RevisionCompare.UpdateApplication.UpdateApplyHelper').GetMethod('ValidateInstallation',([Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic))
    Assert ($null -ne $validate) 'Actual update helper receipt preflight was not found'
    $accepted=$validate.Invoke($null,@($job))
    Assert ($accepted.Version -eq $targetVersion -and $accepted.Files.Count -eq 2 -and $accepted.Profiles.Count -eq 1) 'Helper did not accept current local receipt'
    Assert ((Snapshot $root) -ceq $before) 'Read-only helper compatibility check changed files'
}
[ordered]@{passed=$script:passed;version=$targetVersion;previousVersions=@($legacyVersion,$oldVersion);nextUpdateVersion=$futureVersion;fixture=$work;hostWritten=$false;networkUsed=$false;realHostDataRead=$false;metadataLibrary=$cecil;checks=$script:checks;coreSha256=(Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $work 'results.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $work 'results.json')
