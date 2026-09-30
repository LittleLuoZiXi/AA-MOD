param([string]$GameRoot,[string]$OutputDirectory,[string]$FrozenRuntimeDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'build-r6-support.ps1')
. (Join-Path $PSScriptRoot 'build-v1-online-support.ps1')
$context=New-R6BuildContext $GameRoot $OutputDirectory 'development-V1.0'
$mod=$context.Mod;$game=$context.Game;$stage=$context.Stage;$owned=$context.Owned;$build=$context.Build;$version=$context.Version
$frozen=if([string]::IsNullOrWhiteSpace($FrozenRuntimeDirectory)){Join-Path $PSScriptRoot 'frozen/EnhanceHost'}else{[IO.Path]::GetFullPath($FrozenRuntimeDirectory)}
Assert-FrozenRuntimeCurrent $mod $frozen (Join-Path $build 'frozen-runtime-source-check.json')
$expectedFiles=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-PayloadFile([string]$Source,[string]$Relative){Add-R6PayloadFile $Source $Relative $stage $expectedFiles}

# The generated Deployment source selects the installed runtime location. Never
# replace source, an accepted debug DLL or the current installed MOD during builds.
$plugin=Join-Path $owned "$version/AzureArchive.Recorder.dll"
$tutorialProof=@(Invoke-RecorderCompilation $context 'AzureArchive.Recorder' $plugin -Development)
$null=$expectedFiles.Add("mods/AzureArchiveRecorder/$version/AzureArchive.Recorder.dll")
$manifest=Get-Content -LiteralPath (Join-Path $mod 'manifest.json') -Raw|ConvertFrom-Json
$manifest.description='内录 V1.0 未加壳版：缺少 DLSS 组件时禁用增强并红字提示；六页新手教程、日期序号命名；保留原始符号和内嵌调试信息。'
[IO.File]::WriteAllText((Join-Path $owned "$version/manifest.json"),($manifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$null=$expectedFiles.Add("mods/AzureArchiveRecorder/$version/manifest.json")
foreach($file in Get-ChildItem -LiteralPath $frozen -Recurse -File){Add-PayloadFile $file.FullName ('mods/AzureArchiveRecorder/runtime/'+[IO.Path]::GetRelativePath($frozen,$file.FullName).Replace('\','/'))}
$ffmpegVendor=Join-Path $PSScriptRoot 'ffmpeg-vendor'
$ffmpegHashes=@{'ffmpeg.exe'='B1383F5D07470D503EDECDAEE4BDDC5891E986E916A698299B357F79CFE445FD';'ffprobe.exe'='012BDDDED3CBC5204055210D7FF4F0B3F7521BCA441A694939856D01909F5756'}
foreach($name in $ffmpegHashes.Keys){if((Get-FileHash -LiteralPath (Join-Path $ffmpegVendor $name)).Hash -ne $ffmpegHashes[$name]){throw "Bundled FFmpeg hash mismatch: $name"}}
foreach($name in @('ffmpeg.exe','ffprobe.exe','COPYING.GPLv3.txt','UPSTREAM-README.txt','VERSION.txt','BUILD-CONFIG.txt','PROVENANCE.json')){Add-PayloadFile (Join-Path $ffmpegVendor $name) ('mods/AzureArchiveRecorder/runtime/ffmpeg/'+$name)}

$installerSource=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Installer.cs') -Raw).Replace('@@HOSTHASH@@',$context.HostHash)
$generated=Join-Path $build 'Installer.generated.cs'
[IO.File]::WriteAllText($generated,$installerSource,[Text.UTF8Encoding]::new($true))
$shared=Join-Path $mod 'src/DlssComponents.cs'
if(!(Test-Path -LiteralPath $shared)){throw 'Missing shared installer/runtime DlssComponents.cs.'}
$csc=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Net.Http.dll')
$uninstaller=Join-Path $owned '卸载内录MOD.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize- /debug- /define:UNINSTALL $references "/out:$uninstaller" $generated $shared (Join-Path $PSScriptRoot 'V1AssemblyInfo.cs')
if($LASTEXITCODE -ne 0){throw 'Development uninstaller compilation failed.'}
$null=$expectedFiles.Add('mods/AzureArchiveRecorder/卸载内录MOD.exe')
$guide="内录 V1.0 未加壳版`r`nMOD DLL、安装器和两个卸载器均保留可读结构。可与保护版 1.0.0 通过覆盖确认互相切换。`r`n开发源码不在安装/卸载清单内。以下加壳说明仅适用于保护版。`r`n`r`n"+(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'USER-GUIDE.txt') -Raw)
[IO.File]::WriteAllText((Join-Path $owned '使用说明.txt'),$guide,[Text.UTF8Encoding]::new($true))
$null=$expectedFiles.Add('mods/AzureArchiveRecorder/使用说明.txt')
Add-PayloadFile (Join-Path $PSScriptRoot 'THIRD-PARTY.txt') 'mods/AzureArchiveRecorder/第三方许可.txt'
$files=@(Get-ChildItem -LiteralPath $owned -Recurse -File|ForEach-Object{
    $relative=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')
    if(!$expectedFiles.Contains($relative)){throw "Unknown file in fresh development stage: $relative"}
    @{Path=$relative;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
if($files.Count -ne $expectedFiles.Count){throw 'Development stage is incomplete.'}
$legacy=@();foreach($old in @('0.1.0','0.1.1','0.2.0','0.2.1')){foreach($name in @('AzureArchive.Recorder.dll','manifest.json')){
    $relative="mods/AzureArchiveRecorder/$old/$name";$path=Join-Path $game $relative
    if(Test-Path -LiteralPath $path){$legacy+=@{Path=$relative;Sha256=(Get-FileHash -LiteralPath $path).Hash}}
}}
$profiles=(Get-Content -LiteralPath (Join-Path $mod 'bridge/gpu_profiles.json') -Raw|ConvertFrom-Json).profiles
$generatedFiles=@(30,40,50|ForEach-Object{@{Path="mods/AzureArchiveRecorder/runtime/gpu-runtimes/rtx$_/nvngx_dlssnr.dll";Sha256=$profiles."$_".sha256.ToUpperInvariant()}})
$receipt=@{Product='AzureArchiveRecorder';Version=$version;Root='';Files=$files;LegacyFiles=$legacy;GeneratedFiles=$generatedFiles}
[IO.File]::WriteAllText((Join-Path $stage 'payload-manifest.json'),($receipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
if(@(Get-ChildItem -LiteralPath $stage -Recurse -File).Count -ne $expectedFiles.Count+1){throw 'Unknown files outside the fresh development MOD stage.'}
$payload=Join-Path $build 'install-payload-development.zip'
Assert-V1NoBundledDlss $stage
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$payload,[IO.Compression.CompressionLevel]::Optimal,$false)
$onlineUninstaller=New-V1OnlineUninstaller $context $generated $shared
$onlineSources=@('OnlineDlssInstall.cs','V1Hardware.cs','V1Installer.cs','V1AssemblyInfo.cs') | ForEach-Object {Join-Path $PSScriptRoot $_}
$fileName='AzureArchive内录V1.0-未加壳安装.exe'
$pending=Join-Path $build $fileName
& $csc /nologo /target:winexe /platform:x64 /optimize- /debug- /main:V1Program $references "/resource:$payload,installer.payload" "/resource:$onlineUninstaller,installer.dlss-uninstaller" "/out:$pending" $generated $shared $onlineSources
if($LASTEXITCODE -ne 0){throw 'Development installer compilation failed.'}
Import-R6Cecil
$module=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($plugin)
try{
    if(!($module.MainModule.Types|Where-Object FullName -eq 'AzureArchive.Recorder.RecorderPlugin') -or ($module.MainModule.Resources|Where-Object Name -eq 'recorder.payload')){throw 'Expected readable development MOD, not an encrypted loader.'}
}finally{$module.Dispose()}
foreach($executable in @($pending,$uninstaller)){
    $module=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($executable)
    try{if(!($module.MainModule.Types|Where-Object FullName -eq 'InstallCore') -or ($module.MainModule.Resources|Where-Object Name -eq 'aa.encrypted-core')){throw 'Expected readable development installer/uninstaller.'}}finally{$module.Dispose()}
}
$output=Join-Path $context.Output $fileName
Publish-R6Artifact $pending $output $build
$proof=[ordered]@{Variant='development-V1.0';Product='AzureArchiveRecorder';Version=$version;Installer=$output;InstallerSha256=(Get-FileHash -LiteralPath $output).Hash;Uninstaller=$uninstaller;UninstallerSha256=(Get-FileHash -LiteralPath $uninstaller).Hash;DlssUninstaller=$onlineUninstaller;DlssUninstallerSha256=(Get-FileHash -LiteralPath $onlineUninstaller).Hash;Plugin=$plugin;PluginSha256=(Get-FileHash -LiteralPath $plugin).Hash;TutorialResources=$tutorialProof;Payload=$payload;PayloadFiles=$files.Count;BuildDirectory=$build;Stage=$stage;RuntimeSource=$frozen;InstallerSource=$generated;SharedSourceSha256=(Get-FileHash -LiteralPath $shared).Hash;NoBundledDlssNative=$true;BuiltUtc=[DateTime]::UtcNow.ToString('o');InstalledIntoGame=$false}
[IO.File]::WriteAllText((Join-Path $build 'build-proof.json'),($proof|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Write-Host "V1.0 unprotected online installer: $output"
[pscustomobject]$proof
