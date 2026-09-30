param([switch]$InstallerOnly)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$game=Split-Path $mod -Parent
$stage=Join-Path $PSScriptRoot 'stage'
$owned=Join-Path $stage 'mods/AzureArchiveRecorder'
$version='0.2.1'
$null=New-Item -ItemType Directory -Force -Path (Join-Path $owned $version)
$hostHash=(Get-FileHash (Join-Path $game 'AzureArchive.exe')).Hash
if(!$InstallerOnly){
    & (Join-Path $mod 'build.ps1') -AssemblyName AzureArchive.Recorder.Core
    [xml]$config=Get-Content (Join-Path $PSScriptRoot 'obfuscar.xml') -Raw
    ($config.Obfuscator.Var|Where-Object name -eq 'InPath').value=Join-Path $mod 'build'
    ($config.Obfuscator.Var|Where-Object name -eq 'OutPath').value=Join-Path $PSScriptRoot 'obfuscated'
    $searchPaths=@('dotnet','BepInEx/core','BepInEx/interop')
    for($i=0;$i -lt $searchPaths.Count;$i++){$config.Obfuscator.AssemblySearchPath[$i].path=Join-Path $game $searchPaths[$i]}
    $config.Obfuscator.Module.file=Join-Path $mod 'build/AzureArchive.Recorder.Core.dll'
    $config.Save((Join-Path $PSScriptRoot 'obfuscar.xml'))
    & (Join-Path $PSScriptRoot 'tools/obfuscar/tools/Obfuscar.Console.exe') (Join-Path $PSScriptRoot 'obfuscar.xml')
    if($LASTEXITCODE -ne 0){throw 'Obfuscation failed'}
$image=[IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'obfuscated/AzureArchive.Recorder.Core.dll'))
$coreHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($image))
$compressed=[IO.MemoryStream]::new()
$zipper=[IO.Compression.GZipStream]::new($compressed,[IO.Compression.CompressionLevel]::Optimal,$true)
$zipper.Write($image,0,$image.Length);$zipper.Dispose()
$aes=[Security.Cryptography.Aes]::Create();$aes.GenerateKey();$aes.GenerateIV()
$encrypted=$aes.CreateEncryptor().TransformFinalBlock($compressed.ToArray(),0,[int]$compressed.Length)
$resource=Join-Path $PSScriptRoot 'recorder.payload'
[IO.File]::WriteAllBytes($resource,$encrypted)
$loader=(Get-Content (Join-Path $PSScriptRoot 'Loader.cs.in') -Raw).Replace('@@HOSTHASH@@',$hostHash).Replace('@@COREHASH@@',$coreHash).Replace('@@KEY@@',[Convert]::ToBase64String($aes.Key)).Replace('@@IV@@',[Convert]::ToBase64String($aes.IV))
$aes.Dispose();$compressed.Dispose()
foreach($name in @('Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll')){$null=[Reflection.Assembly]::LoadFrom((Join-Path $PSHOME $name))}
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new();$seen=@{}
foreach($dir in @('dotnet','BepInEx/core','BepInEx/interop')){foreach($file in Get-ChildItem (Join-Path $game $dir) -Filter '*.dll'){
    if($seen.ContainsKey($file.Name)){continue};try{$null=[Reflection.AssemblyName]::GetAssemblyName($file.FullName);$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName));$seen[$file.Name]=$true}catch [BadImageFormatException]{}
}}
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($loader)
$attributes=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $mod 'src/NullableAttributes.cs')))
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('AzureArchive.Recorder',[Microsoft.CodeAnalysis.SyntaxTree[]]@($tree,$attributes),$refs,$options)
$stream=[IO.File]::Create((Join-Path $owned "$version/AzureArchive.Recorder.dll"))
$provider=[Func[IO.Stream]] { [IO.File]::OpenRead($resource) }.GetNewClosure()
$embedded=[Microsoft.CodeAnalysis.ResourceDescription]::new('recorder.payload',$provider,$false)
try{$emit=$compilation.Emit($stream,$null,$null,$null,[Microsoft.CodeAnalysis.ResourceDescription[]]@($embedded))}finally{$stream.Dispose()}
if(!$emit.Success){$emit.Diagnostics | ForEach-Object ToString;throw 'Loader compilation failed'}
Copy-Item -LiteralPath (Join-Path $mod 'manifest.json') -Destination (Join-Path $owned "$version/manifest.json")
$runtime=Join-Path $owned 'runtime';$null=New-Item -ItemType Directory -Force -Path $runtime
Copy-Item -Path (Join-Path $PSScriptRoot 'frozen/EnhanceHost/*') -Destination $runtime -Recurse -Force
} elseif(!(Test-Path -LiteralPath (Join-Path $owned "$version/AzureArchive.Recorder.dll"))) {throw 'No previously verified protected payload available'}
$null=[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'tools/obfuscar/tools/Mono.Cecil.dll'))
$recorderAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $owned "$version/AzureArchive.Recorder.dll"))
try {
    $recorderResources=@($recorderAssembly.MainModule.Resources)
    if($recorderResources.Count -ne 1 -or $recorderResources[0].Name -ne 'recorder.payload' -or
        !($recorderAssembly.MainModule.Types | Where-Object FullName -eq 'RecorderLoader') -or
        ($recorderAssembly.MainModule.Types | Where-Object FullName -eq 'AzureArchive.Recorder.RecorderPlugin')) {throw 'Recorder payload is not the protected loader; plaintext core reuse is forbidden'}
    $recorderEncryptedStream=$recorderResources[0].GetResourceStream()
    try {if($recorderEncryptedStream.Length -lt 16 -or $recorderEncryptedStream.Length%16 -ne 0){throw 'Recorder encrypted resource is invalid'}}finally{$recorderEncryptedStream.Dispose()}
    Write-Host 'Recorder DLL protected loader structure verified.'
} finally {$recorderAssembly.Dispose()}
$ffmpegVendor=Join-Path $PSScriptRoot 'ffmpeg-vendor'
$ffmpegHashes=@{'ffmpeg.exe'='B1383F5D07470D503EDECDAEE4BDDC5891E986E916A698299B357F79CFE445FD';'ffprobe.exe'='012BDDDED3CBC5204055210D7FF4F0B3F7521BCA441A694939856D01909F5756'}
foreach($name in $ffmpegHashes.Keys){
    if((Get-FileHash -LiteralPath (Join-Path $ffmpegVendor $name)).Hash -ne $ffmpegHashes[$name]){throw "Bundled FFmpeg binary hash mismatch: $name"}
}
$ffmpegStage=Join-Path $owned 'runtime/ffmpeg'
$null=New-Item -ItemType Directory -Force -Path $ffmpegStage
foreach($name in @('ffmpeg.exe','ffprobe.exe','COPYING.GPLv3.txt','UPSTREAM-README.txt','VERSION.txt','BUILD-CONFIG.txt','PROVENANCE.json')){
    Copy-Item -LiteralPath (Join-Path $ffmpegVendor $name) -Destination (Join-Path $ffmpegStage $name) -Force
}
$installerSource=(Get-Content (Join-Path $PSScriptRoot 'Installer.cs') -Raw).Replace('@@HOSTHASH@@',$hostHash)
$generated=Join-Path $PSScriptRoot 'Installer.generated.cs';[IO.File]::WriteAllText($generated,$installerSource,[Text.UTF8Encoding]::new($true))
$csc='C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$uninstallerBuild=Join-Path $PSScriptRoot 'protected-build/uninstaller/input'
$installerBuild=Join-Path $PSScriptRoot 'protected-build/installer/input'
$null=New-Item -ItemType Directory -Force -Path $uninstallerBuild,$installerBuild
$uninstallerCore=Join-Path $uninstallerBuild 'AARecorder.Uninstall.Core.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /debug- /define:UNINSTALL $references "/out:$uninstallerCore" $generated
if($LASTEXITCODE -ne 0){throw 'Uninstaller compilation failed'}
& (Join-Path $PSScriptRoot 'protect-executable.ps1') -InputPath $uninstallerCore -OutputPath (Join-Path $owned '卸载内录MOD.exe') -Role uninstaller
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'USER-GUIDE.txt') -Destination (Join-Path $owned '使用说明.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY.txt') -Destination (Join-Path $owned '第三方许可.txt')
$files=@(Get-ChildItem $owned -Recurse -File | ForEach-Object { @{Path=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/');Sha256=(Get-FileHash $_.FullName).Hash} })
if($files.Path | Where-Object {$_ -match '\.(cs|pdb|ps1|spec)$' -or $_ -match 'enhance(_entry)?\.py$|mapping\.txt$'}){throw 'Development source/debug files in payload'}
$legacy=@();foreach($old in @('0.1.0','0.1.1','0.2.0')){foreach($name in @('AzureArchive.Recorder.dll','manifest.json')){$relative="mods/AzureArchiveRecorder/$old/$name";$path=Join-Path $game $relative;if(Test-Path $path){$legacy+=@{Path=$relative;Sha256=(Get-FileHash $path).Hash}}}}
$profiles=(Get-Content (Join-Path $mod 'bridge/gpu_profiles.json') -Raw | ConvertFrom-Json).profiles
$generatedFiles=@(30,40,50 | ForEach-Object {@{Path="mods/AzureArchiveRecorder/runtime/gpu-runtimes/rtx$_/nvngx_dlssnr.dll";Sha256=$profiles."$_".sha256.ToUpperInvariant()}})
$receipt=@{Product='AzureArchiveRecorder';Version=$version;Root='';Files=$files;LegacyFiles=$legacy;GeneratedFiles=$generatedFiles}
[IO.File]::WriteAllText((Join-Path $stage 'payload-manifest.json'),($receipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$payload=Join-Path $PSScriptRoot 'install-payload.zip'
if(Test-Path $payload){Remove-Item -LiteralPath $payload}
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$payload,[IO.Compression.CompressionLevel]::Optimal,$false)
$output=Join-Path $game 'AzureArchive内录MOD-0.2.1-安装.exe'
$installerCore=Join-Path $installerBuild 'AARecorder.Install.Core.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /debug- $references "/resource:$payload,installer.payload" "/out:$installerCore" $generated
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
& (Join-Path $PSScriptRoot 'protect-executable.ps1') -InputPath $installerCore -OutputPath $output -Role installer
Copy-Item -LiteralPath $output -Destination (Join-Path $game 'AzureArchive内录MOD-0.2.1-R5-RTX验证安装.exe') -Force
Get-FileHash -LiteralPath $output
Get-Item -LiteralPath $output | Select-Object Name,Length
