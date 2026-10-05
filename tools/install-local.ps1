param(
    [string]$GameRoot = 'E:\AzureArchive_100_fix',
    [string]$PluginPath,
    [string]$ManifestPath,
    [string]$CorePath,
    [string]$MetadataLibraryPath,
    [string]$BackupRoot
)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
if (!$PluginPath) { $PluginPath = Join-Path $modRoot 'build\AzureArchive.RevisionCompare.dll' }
if (!$ManifestPath) { $ManifestPath = Join-Path $modRoot 'manifest.json' }
if (!$CorePath) { $CorePath = Join-Path $modRoot 'build\LocalDeployment.Core.dll' }
if (!$MetadataLibraryPath) { $MetadataLibraryPath = Join-Path $GameRoot 'BepInEx\core\Mono.Cecil.dll' }
$plugin = (Resolve-Path -LiteralPath $PluginPath).Path
$manifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$core = (Resolve-Path -LiteralPath $CorePath).Path
$metadataLibrary = (Resolve-Path -LiteralPath $MetadataLibraryPath).Path
$null = [Reflection.Assembly]::LoadFrom($core)
$deployment = [AzureArchive.RevisionCompare.LocalDeployment.DeploymentCore]
$GameRoot = $deployment::NormalizeRoot($GameRoot)
$deployment::ValidateGame($GameRoot)
$pluginBytes = [IO.File]::ReadAllBytes($plugin)
$manifestBytes = [IO.File]::ReadAllBytes($manifest)
$metadata = [Text.Encoding]::UTF8.GetString($manifestBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
$version = [string]$metadata.version_number
if ($metadata.name -ne $deployment::ProductName -or $version -ne $deployment::Version) { throw ('Manifest does not match local deployment core ' + $deployment::Version + '. Rebuild tools/build-local-deployment.ps1 if the core is stale.') }

# Inspect metadata without loading or running the Unity plugin. Release source contains
# reflection strings naming probes, so inspect actual type definitions rather than strings.
$null = [Reflection.Assembly]::LoadFrom($metadataLibrary)
$assemblyStream = [IO.MemoryStream]::new($pluginBytes, $false)
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($assemblyStream)
try {
    if ($assembly.Name.Name -ne 'AzureArchive.RevisionCompare') { throw 'Unexpected plugin assembly name.' }
    $types = [Collections.Generic.List[object]]::new()
    function Add-Types($items) { foreach ($item in $items) { $types.Add($item); Add-Types $item.NestedTypes } }
    Add-Types $assembly.MainModule.Types
    $probes = @($types | Where-Object { $_.Name -match 'Probe' })
    if ($probes.Count -ne 0) { throw ('Refusing development build with probe types: ' + (($probes | ForEach-Object FullName) -join ', ')) }
    $pluginAttributes = @($types | ForEach-Object { $_.CustomAttributes } | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
    if ($pluginAttributes.Count -ne 1) { throw 'Expected exactly one BepInPlugin metadata attribute.' }
    $attribute = $pluginAttributes[0]
    if ($attribute.ConstructorArguments.Count -ne 3 -or $attribute.ConstructorArguments[0].Value -ne $deployment::ProductId -or $attribute.ConstructorArguments[1].Value -ne $deployment::ProductName -or $attribute.ConstructorArguments[2].Value -ne $version) { throw 'BepInPlugin identity/version does not match manifest.' }
} finally { $assembly.Dispose(); $assemblyStream.Dispose() }

$active = [IO.File]::ReadAllText($deployment::Within($GameRoot, 'ActiveProfile.txt')).Trim()
if ($active -notmatch '^[^\\/:*?"<>|]+$' -or $active -in @('.', '..') -or $active.TrimEnd(' ', '.') -ne $active) { throw 'Invalid active profile.' }
$profile = $deployment::Within($GameRoot, ('profiles/' + $active + '/modconfig.json'))
$receipt = $deployment::Within($GameRoot, $deployment::ReceiptRelative)
$original = [IO.File]::ReadAllText($profile) | ConvertFrom-Json
$beforeNonMod = @($original.EnabledMods | Where-Object { $_.name -ne $deployment::ProductName }) | ConvertTo-Json -Depth 100 -Compress
$beforeOtherProperties = $original | Select-Object -Property * -ExcludeProperty EnabledMods | ConvertTo-Json -Depth 100 -Compress
$previousReceipt = $deployment::ReadReceipt($GameRoot)
if (!$BackupRoot) { $BackupRoot = Join-Path $modRoot 'evidence\daily-install' }
$backup = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$null = [IO.Directory]::CreateDirectory($backup)
Copy-Item -LiteralPath $profile -Destination (Join-Path $backup 'modconfig.before.json')
if (Test-Path -LiteralPath $receipt) { Copy-Item -LiteralPath $receipt -Destination (Join-Path $backup 'install-receipt.before.json') }
if ($null -ne $previousReceipt) {
    foreach ($entry in $previousReceipt.Files) {
        $oldFile = $deployment::Within($GameRoot, $entry.Path)
        if ([IO.File]::Exists($oldFile)) { Copy-Item -LiteralPath $oldFile -Destination (Join-Path $backup ($previousReceipt.Version + '-' + [IO.Path]::GetFileName($oldFile))) }
    }
}
$result = $deployment::Install($GameRoot, $pluginBytes, $manifestBytes)
$installed = $deployment::Within($GameRoot, $deployment::DllRelative)
$installedManifest = $deployment::Within($GameRoot, $deployment::ManifestRelative)
$after = [IO.File]::ReadAllText($profile) | ConvertFrom-Json
$afterNonMod = @($after.EnabledMods | Where-Object { $_.name -ne $deployment::ProductName }) | ConvertTo-Json -Depth 100 -Compress
$afterOtherProperties = $after | Select-Object -Property * -ExcludeProperty EnabledMods | ConvertTo-Json -Depth 100 -Compress
if ($beforeNonMod -cne $afterNonMod -or $beforeOtherProperties -cne $afterOtherProperties) { throw 'An unrelated MOD or profile setting changed.' }
$hash = $deployment::Hash($pluginBytes)
$manifestHash = $deployment::Hash($manifestBytes)
if ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash -or (Get-FileHash -LiteralPath $installedManifest -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifestHash) { throw 'Installed payload hashes differ from verified inputs.' }
$own = @($after.EnabledMods | Where-Object { $_.name -eq $deployment::ProductName })
if ($own.Count -ne 1 -or $own[0].version -ne $version) { throw 'MOD is not enabled exactly once at the target version.' }
$installedReceipt = [IO.File]::ReadAllText($receipt) | ConvertFrom-Json
if ($installedReceipt.Version -ne $version -or $installedReceipt.Files.Count -ne 2) { throw 'Receipt version/file count is incorrect.' }
foreach ($entry in $installedReceipt.Files) {
    $path = $deployment::Within($GameRoot, $entry.Path)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Sha256) { throw 'Receipt hash does not match installed file.' }
}
if (!@($installedReceipt.Profiles | Where-Object { $_.Profile -eq $active -and $_.Version -eq $version }).Count) { throw 'Receipt does not own the active profile entry.' }
[ordered]@{ installed=$installed; version=$version; activeProfile=$active; sha256=$hash; manifestSha256=$manifestHash; receipt=$receipt; existingModsPreserved=$true; probeTypesPresent=$false; message=$result.Message; backup=$backup } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'installation-result.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $backup 'installation-result.json')
