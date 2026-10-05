param(
    [string]$PluginPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build\AzureArchive.RevisionCompare.dll'),
    [string]$ManifestPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'manifest.json'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$plugin=(Resolve-Path -LiteralPath $PluginPath).Path
$manifest=(Resolve-Path -LiteralPath $ManifestPath).Path
$pluginBytes=[IO.File]::ReadAllBytes($plugin)
$manifestBytes=[IO.File]::ReadAllBytes($manifest)
$metadata=[Text.Encoding]::UTF8.GetString($manifestBytes).TrimStart([char]0xFEFF)|ConvertFrom-Json
if($metadata.name -ne 'AzureArchiveRevisionCompare' -or $metadata.version_number -ne '1.1.0'){throw 'Installer payload must be AzureArchiveRevisionCompare 1.1.0.'}
$cecil=Join-Path $PSScriptRoot 'tools/obfuscar/tools/Mono.Cecil.dll'
[void][Reflection.Assembly]::LoadFrom($cecil)
$memory=[IO.MemoryStream]::new($pluginBytes,$false)
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($memory)
try {
    if($assembly.Name.Name -ne 'AzureArchive.RevisionCompare'){throw 'Unexpected plugin assembly name.'}
    $types=[Collections.Generic.List[object]]::new()
    function Add-Types($items){foreach($item in $items){$types.Add($item);Add-Types $item.NestedTypes}}
    Add-Types $assembly.MainModule.Types
    if(@($types|Where-Object Name -match 'Probe').Count){throw 'Refusing to package a development probe.'}
    $attrs=@($types|ForEach-Object {$_.CustomAttributes}|Where-Object {$_.AttributeType.FullName -eq 'BepInEx.BepInPlugin'})
    if($attrs.Count -ne 1 -or $attrs[0].ConstructorArguments[0].Value -ne 'azurearchive.revisioncompare' -or $attrs[0].ConstructorArguments[1].Value -ne $metadata.name -or $attrs[0].ConstructorArguments[2].Value -ne $metadata.version_number){throw 'Plugin identity/version differs from manifest.'}
    $resources=@($assembly.MainModule.Resources|ForEach-Object Name)
    if($resources -notcontains 'RevisionCompare.UpdateHelper.ps1' -or $resources -notcontains 'RevisionCompare.UpdateHelper.cs'){throw 'Latest direct-update helper resources are missing.'}
} finally {$assembly.Dispose();$memory.Dispose()}
$work=Join-Path $project ('evidence\installer-release\'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$null=[IO.Directory]::CreateDirectory($work)
$payload=Join-Path $work 'payload'
$null=[IO.Directory]::CreateDirectory($payload)
$lockedPlugin=Join-Path $payload 'AzureArchive.RevisionCompare.dll'
$lockedManifest=Join-Path $payload 'manifest.json'
[IO.File]::WriteAllBytes($lockedPlugin,$pluginBytes)
[IO.File]::WriteAllBytes($lockedManifest,$manifestBytes)
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
if(!(Test-Path -LiteralPath $compiler)){throw 'Windows .NET Framework compiler is missing.'}
$core=Join-Path $work 'RevisionCompare.Installer.Core.exe'
$source=@('InstallerCore.cs','InstallerProgram.cs','InstallerWindow.cs')|ForEach-Object {Join-Path $PSScriptRoot $_}
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /debug- /utf8output /codepage:65001 "/out:$core" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/resource:$lockedPlugin,revisioncompare.plugin" "/resource:$lockedManifest,revisioncompare.manifest" $source
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
$pending=Join-Path $work 'Protected.pending.exe'
& (Join-Path $PSScriptRoot 'protect-executable.ps1') -InputPath $core -OutputPath $pending -Role installer -BuildDirectory (Join-Path $work 'protection')
$inspection=Join-Path $work 'ProtectionTests.exe'
& $compiler /nologo /target:exe /platform:x64 /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/out:$inspection" (Join-Path $PSScriptRoot 'ProtectionTests.cs')
if($LASTEXITCODE -ne 0){throw 'Protection inspection compilation failed.'}
& $inspection $pending | Tee-Object -FilePath (Join-Path $work 'protection-tests.log')
if($LASTEXITCODE -ne 0){throw 'Protection inspection failed.'}
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=$pending;$start.Arguments='--verify';$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($start)
$stdoutTask=$process.StandardOutput.ReadToEndAsync();$stderrTask=$process.StandardError.ReadToEndAsync()
if(!$process.WaitForExit(30000)){$process.Kill();throw 'Protected payload verification timed out.'}
$stdout=$stdoutTask.GetAwaiter().GetResult();$stderr=$stderrTask.GetAwaiter().GetResult()
try {if($process.ExitCode -ne 0){throw ('Protected payload verification failed: '+$stderr)}}finally{$process.Dispose()}
$verification=$stdout|ConvertFrom-Json
$pluginHash=(Get-FileHash -LiteralPath $lockedPlugin -Algorithm SHA256).Hash
$manifestHash=(Get-FileHash -LiteralPath $lockedManifest -Algorithm SHA256).Hash
if($verification.Product -ne $metadata.name -or $verification.Version -ne $metadata.version_number -or $verification.PluginSha256 -ne $pluginHash -or $verification.ManifestSha256 -ne $manifestHash){throw 'Protected shell did not verify the exact input payload.'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
$null=[IO.Directory]::CreateDirectory($output)
$exe=Join-Path $output '剧本改稿同步 MOD  一键安装.exe'
if(Test-Path -LiteralPath $exe){Copy-Item -LiteralPath $exe -Destination (Join-Path $work 'previous-installer.exe')}
Move-Item -LiteralPath $pending -Destination $exe -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '安装器说明.txt') -Destination (Join-Path $output '安装说明.txt') -Force
[ordered]@{
    Installer=$exe;Name='剧本改稿同步 MOD  一键安装';Version=$metadata.version_number;Bytes=(Get-Item -LiteralPath $exe).Length
    Sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;PluginSha256=$pluginHash;ManifestSha256=$manifestHash
    Protection='Obfuscar 2.2.50 + AES-256-CBC + GZip + in-memory installer loading'
    IncludesDirectUpdater=$true;ContainsProbe=$false;InstalledIntoGame=$false;UploadedToGitHub=$false
    BuildDirectory=$work;Core=$core;BuiltUtc=[DateTime]::UtcNow.ToString('o')
}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $work 'installer-build.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $work 'installer-build.json')
