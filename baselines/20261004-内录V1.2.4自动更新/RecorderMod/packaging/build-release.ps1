param([string]$GameRoot,[string]$OutputDirectory,[string]$FrozenRuntimeDirectory,[switch]$InstallerOnly)
$ErrorActionPreference='Stop'
if($InstallerOnly){throw 'V1.2.4 requires a fresh complete protected build; reusing an old stage is forbidden.'}
. (Join-Path $PSScriptRoot 'build-r6-support.ps1')
. (Join-Path $PSScriptRoot 'build-v1-online-support.ps1')
$context=New-R6BuildContext $GameRoot $OutputDirectory 'protected-V1.2.4'
$mod=$context.Mod;$game=$context.Game;$stage=$context.Stage;$owned=$context.Owned;$build=$context.Build;$version=$context.Version
$frozen=if([string]::IsNullOrWhiteSpace($FrozenRuntimeDirectory)){Join-Path $PSScriptRoot 'frozen/EnhanceHost'}else{[IO.Path]::GetFullPath($FrozenRuntimeDirectory)}
Assert-FrozenRuntimeCurrent $mod $frozen (Join-Path $build 'frozen-runtime-source-check.json')
$expectedFiles=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-PayloadFile([string]$Source,[string]$Relative){Add-R6PayloadFile $Source $Relative $stage $expectedFiles}

# Compile and protect exclusively in this build directory, preserving build/ and
# the accepted installed DLL. Use a generated Obfuscar XML, never its template.
$coreInput=Join-Path $build 'core-input/AzureArchive.Recorder.Core.dll'
$tutorialBefore=@(Invoke-RecorderCompilation $context 'AzureArchive.Recorder.Core' $coreInput)
$obfuscated=Join-Path $build 'core-obfuscated'
$null=New-Item -ItemType Directory -Force -Path $obfuscated
[xml]$config=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'obfuscar.xml') -Raw
($config.Obfuscator.Var|Where-Object name -eq 'InPath').value=Split-Path $coreInput -Parent
($config.Obfuscator.Var|Where-Object name -eq 'OutPath').value=$obfuscated
$searchPaths=@('dotnet','BepInEx/core','BepInEx/interop')
for($i=0;$i -lt $searchPaths.Count;$i++){$config.Obfuscator.AssemblySearchPath[$i].path=Join-Path $game $searchPaths[$i]}
$config.Obfuscator.Module.file=$coreInput
$obfuscationConfig=Join-Path $build 'obfuscar.generated.xml'
$config.Save($obfuscationConfig)
& (Join-Path $PSScriptRoot 'tools/obfuscar/tools/Obfuscar.Console.exe') $obfuscationConfig
if($LASTEXITCODE -ne 0){throw 'Recorder core obfuscation failed.'}
$core=Join-Path $obfuscated 'AzureArchive.Recorder.Core.dll'
$tutorialAfter=@(Assert-TutorialResources $core $mod)
$image=[IO.File]::ReadAllBytes($core)
$coreHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($image))
$compressed=[IO.MemoryStream]::new()
$zipper=[IO.Compression.GZipStream]::new($compressed,[IO.Compression.CompressionLevel]::Optimal,$true)
$zipper.Write($image,0,$image.Length);$zipper.Dispose()
$aes=[Security.Cryptography.Aes]::Create();$aes.GenerateKey();$aes.GenerateIV()
try{
    $encrypted=$aes.CreateEncryptor().TransformFinalBlock($compressed.ToArray(),0,[int]$compressed.Length)
    $resource=Join-Path $build 'recorder.payload'
    [IO.File]::WriteAllBytes($resource,$encrypted)
    $loader=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Loader.cs.in') -Raw).Replace('@@HOSTHASH@@',$context.HostHash).Replace('@@COREHASH@@',$coreHash).Replace('@@KEY@@',[Convert]::ToBase64String($aes.Key)).Replace('@@IV@@',[Convert]::ToBase64String($aes.IV))
}finally{$aes.Dispose();$compressed.Dispose()}
[IO.File]::WriteAllText((Join-Path $build 'Loader.generated.cs'),$loader,[Text.UTF8Encoding]::new($false))
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new();$seen=@{}
foreach($dir in @('dotnet','BepInEx/core','BepInEx/interop')){foreach($file in Get-ChildItem -LiteralPath (Join-Path $game $dir) -Filter '*.dll'){
    if($seen.ContainsKey($file.Name)){continue}
    try{$null=[Reflection.AssemblyName]::GetAssemblyName($file.FullName);$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName));$seen[$file.Name]=$true}catch [BadImageFormatException]{}
}}
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($loader)
$attributes=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $mod 'src/NullableAttributes.cs')))
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('AzureArchive.Recorder',[Microsoft.CodeAnalysis.SyntaxTree[]]@($tree,$attributes),$refs,$options)
$plugin=Join-Path $owned "$version/AzureArchive.Recorder.dll"
$stream=[IO.File]::Create($plugin)
$provider=[Func[IO.Stream]]{[IO.File]::OpenRead($resource)}.GetNewClosure()
$embedded=[Microsoft.CodeAnalysis.ResourceDescription]::new('recorder.payload',$provider,$false)
try{$emit=$compilation.Emit($stream,$null,$null,$null,[Microsoft.CodeAnalysis.ResourceDescription[]]@($embedded))}finally{$stream.Dispose()}
if(!$emit.Success){$emit.Diagnostics|ForEach-Object ToString;throw 'Protected loader compilation failed.'}
$null=$expectedFiles.Add("mods/AzureArchiveRecorder/$version/AzureArchive.Recorder.dll")
Import-R6Cecil
$recorderAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($plugin)
try{
    $resources=@($recorderAssembly.MainModule.Resources)
    if($resources.Count -ne 1 -or $resources[0].Name -ne 'recorder.payload' -or !($recorderAssembly.MainModule.Types|Where-Object FullName -eq 'RecorderLoader') -or ($recorderAssembly.MainModule.Types|Where-Object FullName -eq 'AzureArchive.Recorder.RecorderPlugin')){throw 'Recorder DLL is not the protected loader.'}
    $rs=$resources[0].GetResourceStream()
    try{if($rs.Length -lt 16 -or $rs.Length%16 -ne 0){throw 'Recorder encrypted resource length is invalid.'}}finally{$rs.Dispose()}
}finally{$recorderAssembly.Dispose()}
Add-PayloadFile (Join-Path $mod 'manifest.json') "mods/AzureArchiveRecorder/$version/manifest.json"
foreach($file in Get-ChildItem -LiteralPath $frozen -Recurse -File){Add-PayloadFile $file.FullName ('mods/AzureArchiveRecorder/runtime/'+[IO.Path]::GetRelativePath($frozen,$file.FullName).Replace('\','/'))}
$ffmpegVendor=Join-Path $PSScriptRoot 'ffmpeg-vendor'
$ffmpegHashes=@{'ffmpeg.exe'='B1383F5D07470D503EDECDAEE4BDDC5891E986E916A698299B357F79CFE445FD';'ffprobe.exe'='012BDDDED3CBC5204055210D7FF4F0B3F7521BCA441A694939856D01909F5756'}
foreach($name in $ffmpegHashes.Keys){if((Get-FileHash -LiteralPath (Join-Path $ffmpegVendor $name)).Hash -ne $ffmpegHashes[$name]){throw "Bundled FFmpeg binary hash mismatch: $name"}}
foreach($name in @('ffmpeg.exe','ffprobe.exe','COPYING.GPLv3.txt','UPSTREAM-README.txt','VERSION.txt','BUILD-CONFIG.txt','PROVENANCE.json')){Add-PayloadFile (Join-Path $ffmpegVendor $name) ('mods/AzureArchiveRecorder/runtime/ffmpeg/'+$name)}

$installerSource=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Installer.cs') -Raw).Replace('@@HOSTHASH@@',$context.HostHash)
$generated=Join-Path $build 'Installer.generated.cs'
[IO.File]::WriteAllText($generated,$installerSource,[Text.UTF8Encoding]::new($true))
$shared=Join-Path $mod 'src/DlssComponents.cs'
if(!(Test-Path -LiteralPath $shared)){throw 'Missing shared installer/runtime DlssComponents.cs.'}
$csc=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Net.Http.dll')
$updaterCore=Join-Path $build 'AARecorder.Update.Core.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /debug- /main:UpdateHelperProgram $references "/out:$updaterCore" $generated $shared (Join-Path $PSScriptRoot 'UpdateInstallerCore.cs') (Join-Path $PSScriptRoot 'UpdateHelper.cs') (Join-Path $PSScriptRoot 'V1AssemblyInfo.cs')
if($LASTEXITCODE -ne 0){throw 'Protected updater core compilation failed.'}
$updater=Join-Path $owned "$version/更新内录MOD.exe"
& (Join-Path $PSScriptRoot 'protect-executable.ps1') -InputPath $updaterCore -OutputPath $updater -Role updater -BuildDirectory (Join-Path $build 'protection/updater')
$null=$expectedFiles.Add("mods/AzureArchiveRecorder/$version/更新内录MOD.exe")
$uninstallerCore=Join-Path $build 'AARecorder.Uninstall.Core.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /debug- /define:UNINSTALL $references "/out:$uninstallerCore" $generated $shared (Join-Path $PSScriptRoot 'V1AssemblyInfo.cs')
if($LASTEXITCODE -ne 0){throw 'Protected uninstaller core compilation failed.'}
$uninstaller=Join-Path $owned '卸载内录MOD.exe'
& (Join-Path $PSScriptRoot 'protect-executable.ps1') -InputPath $uninstallerCore -OutputPath $uninstaller -Role uninstaller -BuildDirectory (Join-Path $build 'protection/uninstaller')
$null=$expectedFiles.Add('mods/AzureArchiveRecorder/卸载内录MOD.exe')
Add-PayloadFile (Join-Path $PSScriptRoot 'USER-GUIDE.txt') 'mods/AzureArchiveRecorder/使用说明.txt'
Add-PayloadFile (Join-Path $PSScriptRoot 'THIRD-PARTY.txt') 'mods/AzureArchiveRecorder/第三方许可.txt'
$files=@(Get-ChildItem -LiteralPath $owned -Recurse -File|ForEach-Object{
    $relative=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')
    if(!$expectedFiles.Contains($relative)){throw "Unknown file in fresh protected stage: $relative"}
    @{Path=$relative;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
if($files.Count -ne $expectedFiles.Count){throw 'Protected stage is incomplete.'}
if($files.Path|Where-Object{$_ -match '\.(cs|pdb|ps1|spec)$' -or $_ -match 'enhance(_entry)?\.py$|mapping\.txt$'}){throw 'Development source/debug files found in protected payload.'}
$legacy=@();foreach($old in @('0.1.0','0.1.1','0.2.0','0.2.1','1.0.0','1.1.0','1.2.0','1.2.1','1.2.2','1.2.3')){foreach($name in @('AzureArchive.Recorder.dll','manifest.json')){
    $relative="mods/AzureArchiveRecorder/$old/$name";$path=Join-Path $game $relative
    if(Test-Path -LiteralPath $path){$legacy+=@{Path=$relative;Sha256=(Get-FileHash -LiteralPath $path).Hash}}
}}
$profiles=(Get-Content -LiteralPath (Join-Path $mod 'bridge/gpu_profiles.json') -Raw|ConvertFrom-Json).profiles
$generatedFiles=@(30,40,50|ForEach-Object{@{Path="mods/AzureArchiveRecorder/runtime/gpu-runtimes/rtx$_/nvngx_dlssnr.dll";Sha256=$profiles."$_".sha256.ToUpperInvariant()}})
$receipt=@{Product='AzureArchiveRecorder';Version=$version;Root='';Files=$files;LegacyFiles=$legacy;GeneratedFiles=$generatedFiles}
[IO.File]::WriteAllText((Join-Path $stage 'payload-manifest.json'),($receipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
if(@(Get-ChildItem -LiteralPath $stage -Recurse -File).Count -ne $expectedFiles.Count+1){throw 'Unknown files outside the protected MOD stage.'}
$payload=Join-Path $build 'install-payload.zip'
Assert-V1NoBundledDlss $stage
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$payload,[IO.Compression.CompressionLevel]::Optimal,$false)
$onlineUninstaller=New-V1OnlineUninstaller $context $generated $shared -Protected
$onlineSources=@('OnlineDlssInstall.cs','V1Hardware.cs','V1Installer.cs','V1AssemblyInfo.cs') | ForEach-Object {Join-Path $PSScriptRoot $_}
$installerCore=Join-Path $build 'AARecorder.Install.Core.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /debug- /main:V1Program $references "/resource:$payload,installer.payload" "/resource:$onlineUninstaller,installer.dlss-uninstaller" "/out:$installerCore" $generated $shared $onlineSources
if($LASTEXITCODE -ne 0){throw 'Protected installer core compilation failed.'}
$fileName='AzureArchive内录V1.2.4-加壳安装.exe'
$pending=Join-Path $build $fileName
& (Join-Path $PSScriptRoot 'protect-executable.ps1') -InputPath $installerCore -OutputPath $pending -Role installer -BuildDirectory (Join-Path $build 'protection/installer')

# Existing read-only IL tests prove AES/GZip/memory loading of both EXE shells.
$protectionTest=Join-Path $build 'ProtectionTests.exe'
& $csc /nologo /target:exe /platform:x64 /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/out:$protectionTest" (Join-Path $PSScriptRoot 'ProtectionTests.cs')
if($LASTEXITCODE -ne 0){throw 'Protection inspection test compilation failed.'}
& $protectionTest $pending $uninstaller $updater *> (Join-Path $build 'protection-tests.log')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $build 'protection-tests.log');throw 'Protection IL inspection failed.'}
$output=Join-Path $context.Output $fileName
Publish-R6Artifact $pending $output $build
$proof=[ordered]@{Variant='protected-V1.2.4';Product='AzureArchiveRecorder';Version=$version;Installer=$output;InstallerSha256=(Get-FileHash -LiteralPath $output).Hash;Uninstaller=$uninstaller;UninstallerSha256=(Get-FileHash -LiteralPath $uninstaller).Hash;DlssUninstaller=$onlineUninstaller;DlssUninstallerSha256=(Get-FileHash -LiteralPath $onlineUninstaller).Hash;Plugin=$plugin;PluginSha256=(Get-FileHash -LiteralPath $plugin).Hash;CoreSha256=$coreHash;TutorialResourcesBeforeObfuscation=$tutorialBefore;TutorialResources=$tutorialAfter;Protection='Obfuscar + AES-256-CBC + GZip + in-memory loading; installer, both uninstallers and updater also protected';Updater=$updater;UpdaterSha256=(Get-FileHash -LiteralPath $updater).Hash;Payload=$payload;PayloadFiles=$files.Count;BuildDirectory=$build;Stage=$stage;RuntimeSource=$frozen;InstallerSource=$generated;SharedSourceSha256=(Get-FileHash -LiteralPath $shared).Hash;NoBundledDlssNative=$true;BuiltUtc=[DateTime]::UtcNow.ToString('o');InstalledIntoGame=$false}
[IO.File]::WriteAllText((Join-Path $build 'build-proof.json'),($proof|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Write-Host "V1.2.4 protected optional-DLSS installer: $output"
[pscustomobject]$proof
