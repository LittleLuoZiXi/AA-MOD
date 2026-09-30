#requires -Version 7.2
<#
.SYNOPSIS
Save the accepted V1.0 delivery into a new, immutable-by-convention baseline.
.DESCRIPTION
This script never builds, downloads, installs, deletes, or overwrites a baseline.
Run only after final acceptance. It requires the final audit to have passed=true,
version=1.0.0, exactly the two final EXE hashes, and pinned build-proof/report files.

Audit format (paths are relative to DeliveryDirectory unless absolute):
{
  "passed": true, "version": "1.0.0",
  "artifacts": [
    {"path":"AzureArchive内录V1.0-未加壳安装.exe","sha256":"64 hex characters",
     "buildProof":"构建记录/development-V1.0-.../build-proof.json"},
    {"path":"AzureArchive内录V1.0-加壳安装.exe","sha256":"64 hex characters",
     "buildProof":"构建记录/protected-V1.0-.../build-proof.json"}
  ],
  "reports": [
    {"path":"测试汇总.md","sha256":"64 hex characters"},
    {"path":"安装与卸载说明.md","sha256":"64 hex characters"}
  ]
}
Reports may also name explicitly selected small evidence files under RTX开发测试.
The two matching build proofs pin the MOD DLL and both independent uninstallers.
External rebuild environments are inventoried with hashes, not copied.

Copying and verification use a new pending directory. The final name is published
only after verification; the final success seal is written last. On failure a new
pending/incomplete directory may remain with a failure record, never a success
seal. No existing baseline is removed, renamed, or overwritten to retry.
.EXAMPLE
pwsh -File .\packaging\save-v1-baseline.ps1 -DeliveryDirectory 安装交付/V1.0 -AuditPath 最终封板审核.json
#>
[CmdletBinding()]
param(
    [string]$DeliveryDirectory = '安装交付/V1.0',
    [Parameter(Mandatory = $true)]
    [Alias('FinalAuditJson')]
    [string]$AuditPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gameRoot = Split-Path $projectRoot -Parent
$developmentRoot = Split-Path $gameRoot -Parent
$baselineParent = Join-Path $developmentRoot '基线'
$baselineName = '20260929-内录V1.0正式封板'
$destination = Join-Path $baselineParent $baselineName
$startedUtc = [DateTime]::UtcNow.ToString('o')
$utf8 = [Text.UTF8Encoding]::new($false)
$plan = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$dependencies = [Collections.Generic.List[object]]::new()
$dependencyKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$buildSummaries = [Collections.Generic.List[object]]::new()
$skippedNames = @('.git','.venv','__pycache__','.pytest_cache','node_modules','bin','obj','test-output','test-fixture')
$textExtensions = @('.cs','.csproj','.sln','.props','.targets','.ps1','.py','.md','.txt','.json','.xml','.in','.spec','.resx','.yml','.yaml','.toml','.ini','.cfg')
$evidenceExtensions = @('.json','.jsonl','.md','.txt','.log','.csv','.html','.xml','.png','.jpg','.jpeg','.webp','.pdf')

function Get-FullPath([string]$Base, [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'An empty path is not permitted.' }
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $Base $Path))
}

function Assert-Within([string]$Root, [string]$Path) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside its permitted root: $full" }
}

function Assert-NoLinks([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while (![string]::IsNullOrEmpty($cursor)) {
        $item = $null
        try { $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] { }
        if ($null -ne $item) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Links/junctions are not permitted: $cursor" }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Assert-File([string]$Path) {
    Assert-NoLinks $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" }
}

function Get-Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Assert-Hash([string]$Path, [string]$Expected) {
    if ($Expected -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid SHA-256 for: $Path" }
    Assert-File $Path
    if ((Get-Hash $Path) -ne $Expected) { throw "SHA-256 mismatch: $Path" }
}

function Get-Relative([string]$Root, [string]$Path) {
    Assert-Within $Root $Path
    return [IO.Path]::GetRelativePath($Root, $Path).Replace('\','/')
}

function Assert-Relative([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path) -or $Path.Contains(':') -or
        @($Path.Replace('\','/').Split('/') | Where-Object { $_ -in @('','.','..') }).Count -gt 0) {
        throw "Unsafe relative destination: $Path"
    }
}

function Get-SafeFiles([string]$Root, [bool]$Recurse = $true, [string[]]$ExcludeNames = @()) {
    Assert-NoLinks $Root
    if (!(Test-Path -LiteralPath $Root -PathType Container)) { throw "Required directory is missing: $Root" }
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Root)
    while ($queue.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
            if ($item.Name -in $ExcludeNames) { continue }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Link in source tree: $($item.FullName)" }
            if ($item.PSIsContainer) {
                if ($Recurse) { $queue.Enqueue($item.FullName) }
            } else { $item }
        }
    }
}

function Add-PlannedFile([string]$Source, [string]$Relative, [string]$Expected = '') {
    $sourcePath = [IO.Path]::GetFullPath($Source)
    Assert-File $sourcePath
    Assert-Relative $Relative
    $relativePath = $Relative.Replace('\','/')
    $digest = Get-Hash $sourcePath
    if ($Expected -ne '' -and ($Expected -notmatch '^[0-9a-fA-F]{64}$' -or $digest -ne $Expected)) {
        throw "Source differs from its approved hash: $sourcePath"
    }
    if ($plan.ContainsKey($relativePath)) {
        $previous = $plan[$relativePath]
        if ($previous.Source -ne $sourcePath -or $previous.Sha256 -ne $digest) { throw "Conflicting snapshot destination: $relativePath" }
        return
    }
    $plan.Add($relativePath, [pscustomobject][ordered]@{
        Path = $relativePath; Source = $sourcePath; Bytes = (Get-Item -LiteralPath $sourcePath).Length; Sha256 = $digest
    })
}

function Add-SourceTree([string]$Relative, [bool]$Recurse = $true, [bool]$Images = $false) {
    $root = Join-Path $projectRoot $Relative
    foreach ($item in Get-SafeFiles $root $Recurse $skippedNames) {
        $isText = $item.Extension.ToLowerInvariant() -in $textExtensions -or $item.Name -match '(?i)(^LICENSE|[-_]LICENSE$|^COPYING)'
        $isImage = $Images -and $item.Extension.ToLowerInvariant() -in @('.png','.jpg','.jpeg','.webp','.svg')
        if ($isText -or $isImage) {
            if ($item.Length -gt 64MB) { throw "Unexpectedly large source/document: $($item.FullName)" }
            Add-PlannedFile $item.FullName ('项目源码/RecorderMod/' + (Get-Relative $projectRoot $item.FullName))
        }
    }
}

function Add-DependencyGroup([string]$Role, [string]$Root, [IO.FileInfo[]]$Files, [string]$Note = '') {
    $key = $Role + '|' + [IO.Path]::GetFullPath($Root)
    if (!$dependencyKeys.Add($key)) { return }
    Assert-NoLinks $Root
    if ($Files.Count -eq 0) { throw "No files found for required dependency group: $Role" }
    $entries = @(foreach ($file in $Files | Sort-Object FullName) {
        Assert-File $file.FullName
        [ordered]@{Path = (Get-Relative $Root $file.FullName); AbsolutePath = $file.FullName; Bytes = $file.Length; Sha256 = (Get-Hash $file.FullName)}
    })
    $dependencies.Add([ordered]@{Role = $Role; Root = [IO.Path]::GetFullPath($Root); Note = $Note; Files = $entries})
}

function Write-NewBytes([string]$Path, [byte[]]$Bytes) {
    Assert-NoLinks $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

function Write-NewJson([string]$Path, [object]$Value) { Write-NewBytes $Path ($utf8.GetBytes(($Value | ConvertTo-Json -Depth 16))) }

function Assert-Snapshot([string]$Root, [object[]]$Entries, [string]$ManifestHash, [string]$ChecksumHash) {
    Assert-NoLinks $Root
    $actual = @(Get-SafeFiles $Root)
    if ($actual.Count -ne $Entries.Count + 2) { throw 'Unexpected or missing files in the pending baseline.' }
    foreach ($entry in $Entries) {
        $file = Join-Path $Root $entry.Path
        Assert-Within $Root $file
        Assert-Hash $file $entry.Sha256
        if ((Get-Item -LiteralPath $file).Length -ne $entry.Bytes) { throw "Length mismatch: $file" }
    }
    Assert-Hash (Join-Path $Root '文件SHA256.json') $ManifestHash
    Assert-Hash (Join-Path $Root '文件SHA256.sha256') $ChecksumHash
}

# Everything above this point is just function/parameter definition. All gates,
# inventories and hashes below are read-only until a fresh pending dir is created.
$delivery = Get-FullPath $developmentRoot $DeliveryDirectory
Assert-Within $developmentRoot $delivery
Assert-NoLinks $delivery
if (!(Test-Path -LiteralPath $delivery -PathType Container)) { throw 'Delivery directory does not exist.' }
Assert-NoLinks $baselineParent
if (!(Test-Path -LiteralPath $baselineParent -PathType Container)) { throw 'The baseline parent must already exist.' }
if (Test-Path -LiteralPath $destination) { throw "Baseline already exists; refusing to overwrite or reuse it: $destination" }
$auditFile = Get-FullPath $delivery $AuditPath
Assert-Within $delivery $auditFile
Assert-File $auditFile
$audit = Get-Content -LiteralPath $auditFile -Raw | ConvertFrom-Json
if ($audit.passed -isnot [bool] -or !$audit.passed -or $audit.version -ne '1.0.0') { throw 'Final acceptance audit has not passed for version 1.0.0.' }
$artifacts = @($audit.artifacts)
if ($artifacts.Count -ne 2) { throw 'Final audit must identify exactly two V1.0 installer EXEs.' }
$expectedNames = @('AzureArchive内录V1.0-未加壳安装.exe','AzureArchive内录V1.0-加壳安装.exe')
$seenArtifacts = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$buildRoot = Join-Path $delivery '构建记录'
foreach ($artifact in $artifacts) {
    if ($artifact.path -notin $expectedNames -or !$seenArtifacts.Add([string]$artifact.path)) { throw 'The audit has an unknown or duplicated installer.' }
    $installer = Join-Path $delivery $artifact.path
    Add-PlannedFile $installer ('正式交付/' + $artifact.path) $artifact.sha256
    $proofFile = Get-FullPath $delivery $artifact.buildProof
    Assert-Within $buildRoot $proofFile
    Assert-File $proofFile
    $proof = Get-Content -LiteralPath $proofFile -Raw | ConvertFrom-Json
    $buildDirectory = Split-Path $proofFile -Parent
    $variant = if ($artifact.path -eq $expectedNames[0]) { 'development-V1.0' } else { 'protected-V1.0' }
    if ($proof.Product -ne 'AzureArchiveRecorder' -or $proof.Version -ne '1.0.0' -or $proof.Variant -ne $variant -or
        $proof.NoBundledDlssNative -isnot [bool] -or !$proof.NoBundledDlssNative -or
        [IO.Path]::GetFullPath($proof.Installer) -ne $installer -or $proof.InstallerSha256 -ne $artifact.sha256 -or
        [IO.Path]::GetFullPath($proof.BuildDirectory) -ne $buildDirectory) { throw "Build proof is not for the approved artifact: $proofFile" }
    Add-PlannedFile $proofFile ("构建证据/$variant/build-proof.json")
    foreach ($pair in @(@('Plugin','PluginSha256','AzureArchive.Recorder.dll'), @('Uninstaller','UninstallerSha256','卸载内录MOD.exe'), @('DlssUninstaller','DlssUninstallerSha256','卸载DLSS补充MOD.exe'))) {
        $binary = Get-FullPath $buildDirectory ([string]$proof.($pair[0]))
        # A GUI-only rebuild may deliberately reuse a previously accepted stage.
        # Follow the proof's explicit paths, never assume a stage in this build.
        Assert-Within $buildRoot $binary
        if ((Get-Item -LiteralPath $binary).Length -gt 64MB) { throw "Unexpectedly large optional compiled file: $binary" }
        Add-PlannedFile $binary ("编译制品/$variant/" + $pair[2]) ([string]$proof.($pair[1]))
    }
    foreach ($name in @('frozen-runtime-source-check.json','payload-verification.json','protection-tests.log','Installer.generated.cs','Deployment.generated.cs','Loader.generated.cs','obfuscar.generated.xml')) {
        $file = Join-Path $buildDirectory $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { Add-PlannedFile $file ("构建证据/$variant/$name") }
    }
    if ($proof.PSObject.Properties.Name -contains 'SourceFiles') {
        foreach ($sourceProof in @($proof.SourceFiles)) {
            $sourceFile = Get-FullPath $buildDirectory ([string]$sourceProof.Path)
            Assert-Within $buildDirectory $sourceFile
            if ([IO.Path]::GetExtension($sourceFile).ToLowerInvariant() -notin $textExtensions) { throw 'Unexpected file in the final installer source proof.' }
            Add-PlannedFile $sourceFile ("构建证据/$variant/final-installer-sources/" + (Get-Relative $buildDirectory $sourceFile)) ([string]$sourceProof.Sha256)
            $sourceName = [IO.Path]::GetFileName($sourceFile)
            if ($sourceName -eq 'DlssComponents.cs') {
                Assert-Hash (Join-Path $projectRoot 'src/DlssComponents.cs') ([string]$sourceProof.Sha256)
            } elseif ($sourceName -in @('OnlineDlssInstall.cs','V1Hardware.cs','V1Installer.cs','V1AssemblyInfo.cs')) {
                Assert-Hash (Join-Path $PSScriptRoot $sourceName) ([string]$sourceProof.Sha256)
            }
        }
    }
    $stage = Get-FullPath $buildDirectory ([string]$proof.Stage)
    Assert-Within $buildRoot $stage
    $payloadManifest = Join-Path $stage 'payload-manifest.json'
    Add-PlannedFile $payloadManifest ("构建证据/$variant/payload-manifest.json")
    $payload = Get-FullPath $buildDirectory ([string]$proof.Payload)
    Assert-Within $buildRoot $payload
    Assert-File $payload
    $payloadHash = Get-Hash $payload
    if ($proof.PSObject.Properties.Name -contains 'PayloadSha256') { Assert-Hash $payload ([string]$proof.PayloadSha256) }
    if ($proof.PSObject.Properties.Name -contains 'ReusedPayloadSha256') { Assert-Hash $payload ([string]$proof.ReusedPayloadSha256) }
    if ($proof.PSObject.Properties.Name -contains 'ReusedPayloadFrom' -and ![string]::IsNullOrWhiteSpace([string]$proof.ReusedPayloadFrom)) {
        $priorProofPath = Get-FullPath $buildDirectory ([string]$proof.ReusedPayloadFrom)
        Assert-Within $buildRoot $priorProofPath
        Assert-NoLinks $priorProofPath
        if (Test-Path -LiteralPath $priorProofPath -PathType Container) { $priorProofPath = Join-Path $priorProofPath 'build-proof.json' }
        Assert-File $priorProofPath
        $priorProof = Get-Content -LiteralPath $priorProofPath -Raw | ConvertFrom-Json
        if ($priorProof.Version -ne '1.0.0' -or $priorProof.Product -ne 'AzureArchiveRecorder' -or
            [IO.Path]::GetFullPath($priorProof.Stage) -ne $stage -or [IO.Path]::GetFullPath($priorProof.Payload) -ne $payload -or
            $priorProof.PluginSha256 -ne $proof.PluginSha256 -or $priorProof.UninstallerSha256 -ne $proof.UninstallerSha256 -or
            $priorProof.DlssUninstallerSha256 -ne $proof.DlssUninstallerSha256) { throw 'Reused payload does not match its accepted build ancestry.' }
        Add-PlannedFile $priorProofPath ("构建证据/$variant/reused-payload-build-proof.json")
        $priorBuild = Split-Path $priorProofPath -Parent
        foreach ($name in @('frozen-runtime-source-check.json','payload-verification.json','protection-tests.log')) {
            $file = Join-Path $priorBuild $name
            if (Test-Path -LiteralPath $file -PathType Leaf) { Add-PlannedFile $file ("构建证据/$variant/reused-payload/$name") }
        }
    }
    Assert-Hash (Join-Path $projectRoot 'src/DlssComponents.cs') ([string]$proof.SharedSourceSha256)
    if (@($proof.TutorialResources).Count -ne 6) { throw 'Build proof must identify all six tutorial illustrations.' }
    foreach ($page in 1..6) {
        $resourceName = 'recorder.tutorial.arona.page-{0:00}.png' -f $page
        $matches = @($proof.TutorialResources | Where-Object Resource -eq $resourceName)
        if ($matches.Count -ne 1) { throw "Missing tutorial resource proof: $resourceName" }
        $artName = if ($page -eq 1) { 'arona-guide.png' } else { 'arona-page-{0:00}.png' -f $page }
        Assert-Hash (Join-Path $projectRoot ('assets/tutorial/' + $artName)) $matches[0].Sha256
    }
    $runtime = [IO.Path]::GetFullPath($proof.RuntimeSource)
    Assert-Within $developmentRoot $runtime
    $payloadReceipt = Get-Content -LiteralPath $payloadManifest -Raw | ConvertFrom-Json
    if ($payloadReceipt.Product -ne 'AzureArchiveRecorder' -or $payloadReceipt.Version -ne '1.0.0') { throw 'Payload manifest is not V1.0.' }
    $runtimeHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $payloadReceipt.Files) {
        if (!$runtimeHashes.TryAdd([string]$file.Path, [string]$file.Sha256)) { throw 'Duplicate payload manifest path.' }
    }
    $frozenFiles = @(Get-SafeFiles $runtime)
    $expectedFrozen = @($runtimeHashes.Keys | Where-Object { $_.StartsWith('mods/AzureArchiveRecorder/runtime/', [StringComparison]::OrdinalIgnoreCase) -and !$_.StartsWith('mods/AzureArchiveRecorder/runtime/ffmpeg/', [StringComparison]::OrdinalIgnoreCase) })
    if ($frozenFiles.Count -ne $expectedFrozen.Count) { throw 'External frozen runtime does not have the approved complete file set.' }
    foreach ($file in $frozenFiles) {
        $relative = 'mods/AzureArchiveRecorder/runtime/' + (Get-Relative $runtime $file.FullName)
        if (!$runtimeHashes.ContainsKey($relative)) { throw "External frozen runtime contains an unapproved file: $relative" }
        Assert-Hash $file.FullName $runtimeHashes[$relative]
    }
    $ffmpegFiles = @(Get-SafeFiles (Join-Path $PSScriptRoot 'ffmpeg-vendor') $false)
    $expectedFfmpeg = @($runtimeHashes.Keys | Where-Object { $_.StartsWith('mods/AzureArchiveRecorder/runtime/ffmpeg/', [StringComparison]::OrdinalIgnoreCase) })
    if ($ffmpegFiles.Count -ne $expectedFfmpeg.Count) { throw 'FFmpeg vendor does not have the approved complete file set.' }
    foreach ($file in $ffmpegFiles) {
        $relative = 'mods/AzureArchiveRecorder/runtime/ffmpeg/' + $file.Name
        if (!$runtimeHashes.ContainsKey($relative)) { throw "FFmpeg vendor contains an unapproved file: $relative" }
        Assert-Hash $file.FullName $runtimeHashes[$relative]
    }
    Add-DependencyGroup 'FrozenRuntimeDirectory' $runtime @(Get-SafeFiles $runtime) 'Exact accepted frozen runtime; pass this directory to both build scripts. Files are not copied into the baseline.'
    $buildSummaries.Add([ordered]@{Variant=$variant; InstallerSha256=$artifact.sha256; BuildProof=$proofFile; PluginSha256=$proof.PluginSha256; UninstallerSha256=$proof.UninstallerSha256; DlssUninstallerSha256=$proof.DlssUninstallerSha256; RuntimeSource=$runtime; Payload=$payload; PayloadSha256=$payloadHash; Stage=$stage})
}
if (@(Get-ChildItem -LiteralPath $delivery -File -Filter '*.exe').Count -ne 2) { throw 'Unexpected additional EXE at the root of the formal delivery.' }

$reportSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($report in @($audit.reports)) {
    $source = Get-FullPath $delivery $report.path
    Assert-Within $developmentRoot $source
    Assert-File $source
    if ([IO.Path]::GetExtension($source).ToLowerInvariant() -notin $evidenceExtensions -or (Get-Item -LiteralPath $source).Length -gt 64MB) { throw "Unsupported/oversized evidence file: $source" }
    if (!$reportSources.Add($source)) { throw "Duplicate final audit report: $source" }
    Add-PlannedFile $source ('验收证据/' + (Get-Relative $developmentRoot $source)) $report.sha256
}
foreach ($name in @('测试汇总.md','安装与卸载说明.md')) {
    if (!$reportSources.Contains((Join-Path $delivery $name))) { throw "Final audit must pin the formal document: $name" }
}
# Only formal root documents are swept; build histories/stages/log trees are not.
foreach ($item in Get-SafeFiles $delivery $false) {
    if ($item.Extension.ToLowerInvariant() -in @('.md','.txt','.json','.csv','.html','.pdf')) {
        Add-PlannedFile $item.FullName ('正式交付/' + $item.Name)
    }
}
Add-PlannedFile $auditFile '验收证据/最终封板审核.json'

Add-SourceTree '.' $false
foreach ($directory in @('src','bridge','tests')) { Add-SourceTree $directory }
Add-SourceTree 'assets/tutorial' $true $true
Add-SourceTree 'packaging' $false
if (Test-Path -LiteralPath (Join-Path $projectRoot 'packaging/templates') -PathType Container) { Add-SourceTree 'packaging/templates' }
Add-SourceTree 'packaging/ffmpeg-vendor' $false
$toolLicense = Join-Path $PSScriptRoot 'tools/Obfuscar-LICENSE.txt'
Add-PlannedFile $toolLicense '项目源码/RecorderMod/packaging/tools/Obfuscar-LICENSE.txt'
Add-PlannedFile (Join-Path $developmentRoot 'AGENTS.md') '项目源码/开发约束-AGENTS.md'

foreach ($item in @(@('FFmpeg vendor','ffmpeg-vendor'), @('Obfuscar and Mono.Cecil','tools/obfuscar'))) {
    $root = Join-Path $PSScriptRoot $item[1]
    Add-DependencyGroup $item[0] $root @(Get-SafeFiles $root) 'Read-only rebuild dependency inventory; native/tool files are not duplicated.'
}
$roslynNames = @('pwsh.exe','System.Management.Automation.dll','Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll','pwsh.deps.json','pwsh.runtimeconfig.json')
$roslynFiles = @(foreach ($name in $roslynNames) { $file = Join-Path $PSHOME $name; Assert-File $file; Get-Item -LiteralPath $file })
Add-DependencyGroup 'PowerShell and Roslyn' $PSHOME $roslynFiles ('PowerShell ' + $PSVersionTable.PSVersion.ToString())
$framework = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319'
$frameworkFiles = @(foreach ($name in @('csc.exe','csc.exe.config','mscorlib.dll','System.dll','System.Core.dll','System.Windows.Forms.dll','System.Drawing.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Net.Http.dll')) {
    $file = Join-Path $framework $name; Assert-File $file; Get-Item -LiteralPath $file
})
Add-DependencyGroup '.NET Framework compiler and references' $framework $frameworkFiles ('Compiler file version: ' + (Get-Item -LiteralPath (Join-Path $framework 'csc.exe')).VersionInfo.FileVersion)
foreach ($relative in @('dotnet','BepInEx/core','BepInEx/interop')) {
    $root = Join-Path $gameRoot $relative
    $files = @(Get-SafeFiles $root $false | Where-Object Extension -eq '.dll')
    Add-DependencyGroup ('AA compiler references: ' + $relative) $root $files 'The build reads these actual AA references; the full AA game is not copied.'
}
Add-DependencyGroup 'AA host executable' $gameRoot @((Get-Item -LiteralPath (Join-Path $gameRoot 'AzureArchive.exe'))) 'Host compatibility identity used by installer generation.'

# Only key Python files and relevant distribution metadata are fingerprinted;
# never crawl/copy the entire virtual environment or its unrelated packages.
$venv = Join-Path $projectRoot '.venv'
$pythonFiles = @(foreach ($relative in @('Scripts/python.exe','pyvenv.cfg')) {
    $file = Join-Path $venv $relative; Assert-File $file; Get-Item -LiteralPath $file
})
$packageMetadata = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $venv 'Lib/site-packages') -Directory -Filter '*.dist-info') {
    if ($directory.Name -match '^(?i:pyinstaller|pyinstaller_hooks_contrib|numpy|opencv_python_headless|av|imageio_ffmpeg|pillow|altgraph|packaging|pefile|pywin32_ctypes|setuptools)-') {
        $file = Join-Path $directory.FullName 'METADATA'; Assert-File $file; $packageMetadata.Add((Get-Item -LiteralPath $file))
    }
}
Add-DependencyGroup 'Python freeze environment identity' $venv @($pythonFiles + $packageMetadata.ToArray()) 'Only interpreter/config and required package version metadata. bridge/requirements.txt plus the retained exact frozen runtime are authoritative rebuild inputs; this is not a full venv backup.'

$sourcePlan = @($plan.Values | Sort-Object Path)
if ($sourcePlan.Count -lt 50) { throw 'Source snapshot unexpectedly small; refusing to publish an incomplete baseline.' }
$snapshotId = [Guid]::NewGuid().ToString('N')
$pending = Join-Path $baselineParent ('.pending-' + $baselineName + '-' + $snapshotId)
Assert-Within $baselineParent $pending
Assert-Within $baselineParent $destination
if (Test-Path -LiteralPath $pending) { throw 'New pending directory unexpectedly already exists.' }
if (Test-Path -LiteralPath $destination) { throw 'Final baseline appeared during preflight; refusing to touch it.' }
$ownedRoot = $null
try {
    $null = New-Item -ItemType Directory -Path $pending
    $ownedRoot = $pending
    foreach ($entry in $sourcePlan) {
        $target = Join-Path $pending $entry.Path
        Assert-Within $pending $target
        Assert-NoLinks $target
        $null = [IO.Directory]::CreateDirectory((Split-Path $target -Parent))
        Assert-File $entry.Source
        $inputStream = [IO.File]::Open($entry.Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
        Assert-Hash $target $entry.Sha256
        if ((Get-Item -LiteralPath $target).Length -ne $entry.Bytes) { throw "Copy length mismatch: $target" }
    }
    Write-NewJson (Join-Path $pending '外部重建依赖.json') ([ordered]@{
        Version='1.0.0'; CapturedUtc=$startedUtc; GameRoot=$gameRoot; ProjectRoot=$projectRoot
        PowerShellVersion=$PSVersionTable.PSVersion.ToString(); OS=[Environment]::OSVersion.VersionString
        Note='Locations and SHA-256 fingerprints only. Large environments/native dependencies are deliberately not copied.'; Groups=$dependencies.ToArray()
    })
    Write-NewJson (Join-Path $pending '封板来源.json') ([ordered]@{
        SnapshotId=$snapshotId; Version='1.0.0'; CapturedUtc=$startedUtc; Audit=$auditFile; AuditSha256=(Get-Hash $auditFile)
        DeliveryDirectory=$delivery; ProjectRoot=$projectRoot; Builds=$buildSummaries.ToArray(); Files=$sourcePlan
        Excluded=@('venv/cache','historical build and test-output trees','download ZIP files','third-party native DLL/EXE trees','duplicate frozen runtime','AA game data')
    })
    $readme = @'
# 内录 V1.0 正式封板

正式交付内含通过最终审核的加壳和未加壳两版安装 EXE；编译制品另存对应 MOD DLL、内录卸载器及 DLSS 卸载器。项目源码保留六页教程图片、bridge/vendor、模板、清单、构建和测试脚本及文档。

大体积重建依赖没有重复复制。重建前应核对“外部重建依赖.json”中的实际目录及 SHA-256；缺失时先恢复同版本依赖。冻结运行时以两份 build-proof.json 的 RuntimeSource 为准，不能误用历史 packaging/frozen。构建脚本使用 PowerShell 7 / Roslyn 和所列 AA 引用。受保护构建含随机密钥，不承诺重新构建产生字节相同的 EXE。

“文件SHA256.json”列出所有封板内容文件；“文件SHA256.sha256”单独固定该清单。“封板核验.json”是两轮完整复核之后最后原子发布的成功标记，记录两份清单的摘要。清单、清单校验文件及最终标记不作自引用哈希；除此之外不得有未登记文件。封板核验缺失、任何哈希不符或存在“封板失败.json”时，均不得视为有效封板。

这是只追加的新快照。不得向此目录回写后续修复，不得覆盖旧基线。保存失败留下的 pending 目录不代表通过，也不会被脚本自动删除或重用。测试覆盖范围以正式交付的“测试汇总.md”为准；保存成功本身不扩展测试结论。
'@
    Write-NewBytes (Join-Path $pending '封板说明.md') ($utf8.GetBytes($readme))
    # Detect source drift after the copy rather than blessing a mixed source tree.
    foreach ($entry in $sourcePlan) { Assert-Hash $entry.Source $entry.Sha256 }
    foreach ($group in $dependencies) { foreach ($file in $group.Files) { Assert-Hash $file.AbsolutePath $file.Sha256 } }
    $inventory = @(foreach ($file in Get-SafeFiles $pending | Sort-Object FullName) {
        [ordered]@{Path=(Get-Relative $pending $file.FullName); Bytes=$file.Length; Sha256=(Get-Hash $file.FullName)}
    })
    Write-NewJson (Join-Path $pending '文件SHA256.json') ([ordered]@{
        SchemaVersion=1; Algorithm='SHA256'; Version='1.0.0'; SnapshotId=$snapshotId; FileCount=$inventory.Count
        ControlFilesExcluded=@('文件SHA256.json','文件SHA256.sha256','封板核验.json'); Files=$inventory
    })
    $manifestHash = Get-Hash (Join-Path $pending '文件SHA256.json')
    Write-NewBytes (Join-Path $pending '文件SHA256.sha256') ($utf8.GetBytes($manifestHash + '  文件SHA256.json' + "`r`n"))
    $checksumHash = Get-Hash (Join-Path $pending '文件SHA256.sha256')
    Assert-Snapshot $pending $inventory $manifestHash $checksumHash
    # Atomic same-volume publication; Directory.Move never merges/overwrites.
    Assert-NoLinks $baselineParent
    if (Test-Path -LiteralPath $destination) { throw 'Final baseline appeared before publication; it will not be overwritten.' }
    [IO.Directory]::Move($pending, $destination)
    $ownedRoot = $destination
    Assert-Snapshot $destination $inventory $manifestHash $checksumHash
    $seal = [ordered]@{
        passed=$true; version='1.0.0'; SnapshotId=$snapshotId; CompletedUtc=[DateTime]::UtcNow.ToString('o')
        Baseline=$destination; VerifiedContentFiles=$inventory.Count; VerificationPasses=2
        Manifest='文件SHA256.json'; ManifestSha256=$manifestHash; ChecksumFileSha256=$checksumHash
        FinalAuditSha256=(Get-Hash (Join-Path $destination '验收证据/最终封板审核.json'))
        Note='Content copy hashes checked; complete manifest verified before and after atomic directory publication. Does not broaden the final audit test scope.'
    }
    $sealBytes = $utf8.GetBytes(($seal | ConvertTo-Json -Depth 8))
    $sealHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sealBytes))
    $sealPending = Join-Path $destination '.seal.pending'
    Write-NewBytes $sealPending $sealBytes
    Assert-Hash $sealPending $sealHash
    [IO.File]::Move($sealPending, (Join-Path $destination '封板核验.json'))
    [pscustomobject]@{passed=$true; Baseline=$destination; Files=$inventory.Count; ManifestSha256=$manifestHash; SealSha256=$sealHash}
} catch {
    $failure = $_
    if ($null -ne $ownedRoot) {
        try {
            Assert-Within $baselineParent $ownedRoot
            Assert-NoLinks $ownedRoot
            Write-NewJson (Join-Path $ownedRoot '封板失败.json') ([ordered]@{passed=$false; SnapshotId=$snapshotId; FailedUtc=[DateTime]::UtcNow.ToString('o'); Error=$failure.Exception.Message; Note='No existing baseline was overwritten or removed. This incomplete snapshot must not be treated as accepted.'})
        } catch { Write-Warning 'Could not save the failure record; absence of the final success seal still means the snapshot is incomplete.' }
    }
    throw $failure
}
