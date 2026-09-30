param([string]$GameRoot)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$game=if([string]::IsNullOrWhiteSpace($GameRoot)){Split-Path $mod -Parent}else{[IO.Path]::GetFullPath($GameRoot)}
$version='0.2.1'
$stage=Join-Path $PSScriptRoot 'stage-development'
$owned=Join-Path $stage 'mods/AzureArchiveRecorder'
$build=Join-Path $PSScriptRoot 'build-development'
$sources=Join-Path $build 'sources'
$null=New-Item -ItemType Directory -Force -Path (Join-Path $owned $version),$sources
if(!(Test-Path -LiteralPath (Join-Path $game 'AzureArchive.exe'))){throw 'Set -GameRoot to the AzureArchive directory containing AzureArchive.exe.'}
$hostHash=(Get-FileHash -LiteralPath (Join-Path $game 'AzureArchive.exe')).Hash
$expectedFiles=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-PayloadFile([string]$Source,[string]$Relative) {
    $destination=Join-Path $stage $Relative
    $null=New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent)
    Copy-Item -LiteralPath $Source -Destination $destination -Force
    $null=$expectedFiles.Add($Relative.Replace('\','/'))
}

# This separate developer target deliberately emits the original plugin, with an embedded portable PDB.
# It never changes src/Deployment.cs or the protected release build, stage, payload or installed MOD.
# The protected loader normally sets AppContext.RuntimeRoot. A plain plugin has no loader, so this
# target changes exactly one fallback expression in a GENERATED Deployment.cs: the installed helper
# is resolved relative to the current AA root at mods/AzureArchiveRecorder/runtime. Re-running this
# script reproduces the adaptation while the original source stays usable by the protected target.
foreach($name in @('Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll')) {
    $path=Join-Path $PSHOME $name
    if(!(Test-Path -LiteralPath $path)){throw 'Run this developer build with PowerShell 7 (pwsh), which supplies Roslyn.'}
    $null=[Reflection.Assembly]::LoadFrom($path)
}
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new();$seen=@{}
foreach($relative in @('dotnet','BepInEx/core','BepInEx/interop')) {
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $game $relative) -Filter '*.dll') {
        if($seen.ContainsKey($file.Name)){continue}
        try {$null=[Reflection.AssemblyName]::GetAssemblyName($file.FullName);$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName));$seen[$file.Name]=$true}catch [BadImageFormatException]{}
    }
}
$trees=[Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $mod 'src') -Filter '*.cs') {
    $text=[IO.File]::ReadAllText($file.FullName);$document=$file.FullName
    if($file.Name -eq 'Deployment.cs') {
        $original='AppContext.GetData("AzureArchive.Recorder.RuntimeRoot") as string ?? Path.Combine(Paths.GameRootPath,"RecorderMod")'
        $replacement='AppContext.GetData("AzureArchive.Recorder.RuntimeRoot") as string ?? Path.Combine(Paths.GameRootPath,"mods","AzureArchiveRecorder","runtime")'
        if([regex]::Matches($text,[regex]::Escape($original)).Count -ne 1){throw 'Deployment.Root changed: expected exactly one runtime path expression; review the development-only adaptation before rebuilding.'}
        $text=$text.Replace($original,$replacement)
        $document=Join-Path $sources 'Deployment.cs'
        [IO.File]::WriteAllText($document,$text,[Text.UTF8Encoding]::new($false))
    }
    $sourceText=[Microsoft.CodeAnalysis.Text.SourceText]::From($text,[Text.UTF8Encoding]::new($false))
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($sourceText,$null,$document))
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithAllowUnsafe($true).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Debug).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('AzureArchive.Recorder',$trees,$refs,$options)
$emitOptions=[Microsoft.CodeAnalysis.Emit.EmitOptions]::new().WithDebugInformationFormat([Microsoft.CodeAnalysis.Emit.DebugInformationFormat]::Embedded)
$plugin=Join-Path $owned "$version/AzureArchive.Recorder.dll"
$stream=[IO.File]::Create($plugin)
try {$result=$compilation.Emit($stream,$null,$null,$null,$null,$emitOptions)}finally{$stream.Dispose()}
$result.Diagnostics|Where-Object {$_.Severity -in @('Error','Warning')}|ForEach-Object ToString
if(!$result.Success){throw 'Development plugin compilation failed.'}
$null=$expectedFiles.Add("mods/AzureArchiveRecorder/$version/AzureArchive.Recorder.dll")
$manifest=Get-Content -LiteralPath (Join-Path $mod 'manifest.json') -Raw|ConvertFrom-Json
$manifest.description='开发未加壳版（R5）：保留原始 C# 符号与内嵌调试信息，包含 RTX 集成、进度文件并发修复及取消/超时保护。'
$manifestPath=Join-Path $owned "$version/manifest.json"
[IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$null=$expectedFiles.Add("mods/AzureArchiveRecorder/$version/manifest.json")

$frozen=Join-Path $PSScriptRoot 'frozen/EnhanceHost'
if(!(Test-Path -LiteralPath (Join-Path $frozen 'EnhanceHost.exe'))){throw 'Missing frozen/EnhanceHost: retain this runtime from the complete source handoff, or rebuild it first.'}
foreach($file in Get-ChildItem -LiteralPath $frozen -Recurse -File) {
    Add-PayloadFile $file.FullName ("mods/AzureArchiveRecorder/runtime/"+[IO.Path]::GetRelativePath($frozen,$file.FullName).Replace('\','/'))
}
$ffmpegVendor=Join-Path $PSScriptRoot 'ffmpeg-vendor'
$ffmpegHashes=@{'ffmpeg.exe'='B1383F5D07470D503EDECDAEE4BDDC5891E986E916A698299B357F79CFE445FD';'ffprobe.exe'='012BDDDED3CBC5204055210D7FF4F0B3F7521BCA441A694939856D01909F5756'}
foreach($name in $ffmpegHashes.Keys) {if((Get-FileHash -LiteralPath (Join-Path $ffmpegVendor $name)).Hash -ne $ffmpegHashes[$name]){throw "Bundled FFmpeg hash mismatch: $name"}}
foreach($name in @('ffmpeg.exe','ffprobe.exe','COPYING.GPLv3.txt','UPSTREAM-README.txt','VERSION.txt','BUILD-CONFIG.txt','PROVENANCE.json')) {
    Add-PayloadFile (Join-Path $ffmpegVendor $name) ("mods/AzureArchiveRecorder/runtime/ffmpeg/"+$name)
}

$installerSource=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Installer.cs') -Raw).Replace('@@HOSTHASH@@',$hostHash)
$generated=Join-Path $build 'Installer.generated.cs'
[IO.File]::WriteAllText($generated,$installerSource,[Text.UTF8Encoding]::new($true))
$framework=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319'
$csc=Join-Path $framework 'csc.exe'
if(!(Test-Path -LiteralPath $csc)){throw 'The Windows .NET Framework 4 x64 C# compiler is required.'}
$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$uninstaller=Join-Path $owned '卸载内录MOD.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize- /debug- /define:UNINSTALL $references "/out:$uninstaller" $generated
if($LASTEXITCODE -ne 0){throw 'Development uninstaller compilation failed.'}
$null=$expectedFiles.Add('mods/AzureArchiveRecorder/卸载内录MOD.exe')
$guide="开发未加壳版 R5`r`n此版本的 MOD DLL、安装器和卸载器均未混淆或加密，供源码调试。`r`n实际 GPU 覆盖范围与结果见本次验证报告；不代表所有 RTX 型号均验证。`r`n可与正式 0.2.1 通过安装器的覆盖确认互相切换，不要手动替换已纳入清单的 DLL。`r`n开发源码保存在 RecorderMod，卸载不会删除它。`r`n以下通用说明中的加壳内容仅适用于正式构建。`r`n`r`n"+(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'USER-GUIDE.txt') -Raw)
[IO.File]::WriteAllText((Join-Path $owned '使用说明.txt'),$guide,[Text.UTF8Encoding]::new($true))
$null=$expectedFiles.Add('mods/AzureArchiveRecorder/使用说明.txt')
Add-PayloadFile (Join-Path $PSScriptRoot 'THIRD-PARTY.txt') 'mods/AzureArchiveRecorder/第三方许可.txt'
$files=@(Get-ChildItem -LiteralPath $owned -Recurse -File|ForEach-Object {
    $relative=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')
    if(!$expectedFiles.Contains($relative)){throw "Unknown file in development stage; retained but not packaged: $relative"}
    @{Path=$relative;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
if($files.Count -ne $expectedFiles.Count){throw 'Development stage is incomplete.'}
$legacy=@()
foreach($old in @('0.1.0','0.1.1','0.2.0')){foreach($name in @('AzureArchive.Recorder.dll','manifest.json')){
    $relative="mods/AzureArchiveRecorder/$old/$name";$path=Join-Path $game $relative
    if(Test-Path -LiteralPath $path){$legacy+=@{Path=$relative;Sha256=(Get-FileHash -LiteralPath $path).Hash}}
}}
$profiles=(Get-Content -LiteralPath (Join-Path $mod 'bridge/gpu_profiles.json') -Raw|ConvertFrom-Json).profiles
$generatedFiles=@(30,40,50|ForEach-Object {@{Path="mods/AzureArchiveRecorder/runtime/gpu-runtimes/rtx$_/nvngx_dlssnr.dll";Sha256=$profiles."$_".sha256.ToUpperInvariant()}})
$receipt=@{Product='AzureArchiveRecorder';Version=$version;Root='';Files=$files;LegacyFiles=$legacy;GeneratedFiles=$generatedFiles}
[IO.File]::WriteAllText((Join-Path $stage 'payload-manifest.json'),($receipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
if(@(Get-ChildItem -LiteralPath $stage -Recurse -File).Count -ne $expectedFiles.Count+1){throw 'Unknown files outside the development MOD stage; retained but not packaged.'}
$payload=Join-Path $PSScriptRoot 'install-payload-development.zip'
# Write to a new temporary ZIP and atomically replace only this script's own previous ZIP.
$payloadTemporary=Join-Path $build ('payload-'+[Guid]::NewGuid().ToString('N')+'.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$payloadTemporary,[IO.Compression.CompressionLevel]::Optimal,$false)
if(Test-Path -LiteralPath $payload){[IO.File]::Replace($payloadTemporary,$payload,$null)}else{[IO.File]::Move($payloadTemporary,$payload)}
$output=Join-Path $game 'AzureArchive内录MOD-0.2.1-R5-开发未加壳安装.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize- /debug- $references "/resource:$payload,installer.payload" "/out:$output" $generated
if($LASTEXITCODE -ne 0){throw 'Development installer compilation failed.'}

$null=[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'tools/obfuscar/tools/Mono.Cecil.dll'))
$module=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($plugin)
try {
    if(!($module.MainModule.Types|Where-Object FullName -eq 'AzureArchive.Recorder.RecorderPlugin') -or ($module.MainModule.Resources|Where-Object Name -eq 'recorder.payload')){throw 'Expected the readable development plugin, not an encrypted loader.'}
}finally{$module.Dispose()}
foreach($executable in @($output,$uninstaller)) {
    $module=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($executable)
    try {if(!($module.MainModule.Types|Where-Object FullName -eq 'InstallCore') -or ($module.MainModule.Resources|Where-Object Name -eq 'aa.encrypted-core')){throw 'Expected readable developer installer/uninstaller.'}}finally{$module.Dispose()}
}
Get-FileHash -LiteralPath $output,$plugin,$uninstaller
Get-Item -LiteralPath $output|Select-Object Name,Length
Write-Host "Development payload: $payload"
Write-Host "Development compiler source for tests: $generated"
