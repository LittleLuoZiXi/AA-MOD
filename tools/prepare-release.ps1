#requires -Version 7.0
param()
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$metadata = Get-Content -LiteralPath (Join-Path $project 'manifest.json') -Raw | ConvertFrom-Json
$version = [string]$metadata.version_number
if ($metadata.name -ne 'AzureArchiveRevisionCompare' -or $version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'A canonical stable version is required.' }
$plugin = Join-Path $project 'build/AzureArchive.RevisionCompare.dll'
$manifest = Join-Path $project 'manifest.json'
$installer = Join-Path $project 'packaging/dist/剧本改稿同步 MOD  一键安装.exe'
$cecil = Join-Path $project 'packaging/tools/obfuscar/tools/Mono.Cecil.dll'
[void][Reflection.Assembly]::LoadFrom($cecil)
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($plugin)
try {
    $types = [Collections.Generic.List[object]]::new()
    function Visit-Types($items) { foreach ($type in $items) { $types.Add($type); Visit-Types $type.NestedTypes } }
    Visit-Types $assembly.MainModule.Types
    if (@($types | Where-Object Name -match 'Probe').Count) { throw 'Do not publish a development probe.' }
    $attributes = @($types | ForEach-Object { $_.CustomAttributes } | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
    if ($attributes.Count -ne 1 -or $attributes[0].ConstructorArguments[0].Value -ne 'azurearchive.revisioncompare' -or $attributes[0].ConstructorArguments[1].Value -ne $metadata.name -or $attributes[0].ConstructorArguments[2].Value -ne $version) { throw 'Plugin and root manifest versions/identities differ.' }
} finally { $assembly.Dispose() }
$start = [Diagnostics.ProcessStartInfo]::new($installer, '--verify')
$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
try {
    if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Installer verification timed out.' }
    if ($process.ExitCode -ne 0) { throw ('Installer verification failed: ' + $stderr.GetAwaiter().GetResult()) }
    $verification = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
} finally { $process.Dispose() }
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
if ($verification.Product -ne $metadata.name -or $verification.Version -ne $version -or $verification.PluginSha256 -ne (Hash $plugin) -or $verification.ManifestSha256 -ne (Hash $manifest)) { throw 'Installer must contain this exact release plugin and manifest.' }
function Copy-ReleaseFile([string]$source, [string]$relative) {
    $target = Join-Path $project $relative
    if (Test-Path -LiteralPath $target) {
        if ((Hash $target) -ne (Hash $source)) { throw ('Versioned release files are immutable; increase the version: ' + $relative) }
    } else {
        [void][IO.Directory]::CreateDirectory((Split-Path $target -Parent))
        Copy-Item -LiteralPath $source -Destination $target
    }
    return $target
}
$raw = 'https://raw.githubusercontent.com/LittleLuoZiXi/AA-MOD/refs/heads/mods/revision-compare/'
$files = @()
foreach ($source in @($plugin, $manifest)) {
    $name = [IO.Path]::GetFileName($source)
    $relative = 'updates/revision-compare/files/' + $version + '/' + $name
    $target = Copy-ReleaseFile $source $relative
    $files += [ordered]@{ Name = $name; Url = $raw + $relative; Sha256 = Hash $target; Size = (Get-Item -LiteralPath $target).Length }
}
$releasePath = 'releases/revision-compare/' + $version + '/'
$releaseInstaller = Copy-ReleaseFile $installer ($releasePath + [IO.Path]::GetFileName($installer))
$null = Copy-ReleaseFile (Join-Path $project 'packaging/安装器说明.txt') ($releasePath + '安装说明.txt')
$descriptor = [ordered]@{ SchemaVersion = 1; Product = $metadata.name; Version = $version; Files = $files }
$descriptorPath = Join-Path $project 'updates/revision-compare/stable.json'
[IO.File]::WriteAllText($descriptorPath, ($descriptor | ConvertTo-Json -Depth 10) + "`n", [Text.UTF8Encoding]::new($false))
$sums = @((Hash $releaseInstaller) + '  ' + [IO.Path]::GetFileName($releaseInstaller))
$sums += $files | ForEach-Object { $_.Sha256 + '  ../../../updates/revision-compare/files/' + $version + '/' + $_.Name }
[IO.File]::WriteAllText((Join-Path $project ($releasePath + 'SHA256SUMS.txt')), ($sums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
'Prepared V{0}: installer, two update files, stable.json and SHA256SUMS.txt. Publish them with the root manifest in one commit.' -f $version
