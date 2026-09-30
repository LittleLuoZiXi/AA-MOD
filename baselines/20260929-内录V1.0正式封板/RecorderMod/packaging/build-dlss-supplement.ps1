param([string]$GameRoot,[string]$ToolRoot,[string]$Rtx30Archive,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$game=if([string]::IsNullOrWhiteSpace($GameRoot)){Split-Path $mod -Parent}else{[IO.Path]::GetFullPath($GameRoot)}
$workspace=Split-Path (Split-Path $game -Parent) -Parent
if([string]::IsNullOrWhiteSpace($ToolRoot)){$ToolRoot=Join-Path $workspace 'DLSS5Tool-v2.3.3-win64'}
$ToolRoot=[IO.Path]::GetFullPath($ToolRoot)
$sourceRoot=Join-Path $PSScriptRoot 'dlss-supplement-sources'
if([string]::IsNullOrWhiteSpace($Rtx30Archive)){$Rtx30Archive=Join-Path $sourceRoot '30.-310.8.SF-v2.zip'}
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path (Split-Path $game -Parent) '安装交付/20260929'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$profiles=(Get-Content -LiteralPath (Join-Path $mod 'bridge/gpu_profiles.json') -Raw|ConvertFrom-Json).profiles
$build=Join-Path $PSScriptRoot 'build-dlss-supplement'
$stage=Join-Path $build 'stage'
$product='AzureArchiveDLSSSupplement';$version='1.0.0';$owned='mods/AzureArchiveDLSS/'

function Assert-Hash([string]$Path,[string]$Expected) {
    if(!(Test-Path -LiteralPath $Path -PathType Leaf)){throw "Missing verified component: $Path"}
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected){throw "Component SHA256 mismatch; source not changed: $Path"}
}
if(!(Test-Path -LiteralPath (Join-Path $game 'AzureArchive.exe') -PathType Leaf)){throw 'Set -GameRoot to the supported AA 1.0 fix4 root.'}
if(!(Test-Path -LiteralPath $Rtx30Archive -PathType Leaf)){throw "RTX30 archive is absent. Obtain the fixed 30.-310.8.SF-v2.zip through an explicitly authorized download and place it at: $Rtx30Archive. This builder never downloads or accepts unverified replacements."}
Assert-Hash $Rtx30Archive $profiles.'30'.zip_sha256
$common=@{
    'ffmpeg.exe'='B1383F5D07470D503EDECDAEE4BDDC5891E986E916A698299B357F79CFE445FD'
    'ffprobe.exe'='012BDDDED3CBC5204055210D7FF4F0B3F7521BCA441A694939856D01909F5756'
    'vsr_host.dll'='5E66DDC8B4C56F2B77DAED782CC8429B607D32BBA358EFB488FBD4C03AE515B7'
    'nvngx_vsr.dll'='C3D88EEA5FF7A548EDEFA66414CF6E77464D0947277C904F324DD23ABF58A1ED'
    'dlssg_video_worker.exe'='7E6C281608BE2E8A6D63A574A3EAE8621C6C38635260E150571EF20F422C4659'
    'nvngx_dlssg.dll'='135EAF0733C1E37381A8C28ABCF7A862404A54132B81787C04E35D09EFC5E36F'
    'dlssnr_host_v2.dll'='C8AD631F8F78B2DEDEC6AEC418C7A570D6FC5BF1B9FFA9F3514BCB0EDD58FC13'
}
foreach($entry in $common.GetEnumerator()){Assert-Hash (Join-Path $ToolRoot ('_internal/'+$entry.Key)) $entry.Value}
Assert-Hash (Join-Path $ToolRoot '_internal/nvngx_dlssnr.dll') $profiles.'40'.sha256
Assert-Hash (Join-Path $ToolRoot 'mods/nvngx_dlssnr.dll') $profiles.'50'.sha256

$null=New-Item -ItemType Directory -Force -Path $sourceRoot,$build,$stage,$OutputDirectory
$rtx30=Join-Path $sourceRoot 'rtx30/nvngx_dlssnr.dll'
if(!(Test-Path -LiteralPath $rtx30)) {
    $null=New-Item -ItemType Directory -Force -Path (Split-Path $rtx30 -Parent)
    $archive=[IO.Compression.ZipFile]::OpenRead($Rtx30Archive)
    try {
        $entries=@($archive.Entries|Where-Object {[IO.Path]::GetFileName($_.FullName) -eq 'nvngx_dlssnr.dll'})
        if($entries.Count -ne 1 -or $entries[0].Length -lt 1 -or $entries[0].Length -gt 512MB){throw 'RTX30 archive contains an unexpected DLL entry.'}
        $temporary=Join-Path $sourceRoot ('rtx30-verified-'+[Guid]::NewGuid().ToString('N')+'.partial')
        $entryStream=$entries[0].Open();$extractedStream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew)
        try {$entryStream.CopyTo($extractedStream)}finally{$extractedStream.Dispose();$entryStream.Dispose()}
        Assert-Hash $temporary $profiles.'30'.sha256
        [IO.File]::Move($temporary,$rtx30)
    }finally{$archive.Dispose()}
}
Assert-Hash $rtx30 $profiles.'30'.sha256

$expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-Payload([string]$Source,[string]$Relative) {
    if(!$Relative.StartsWith($owned,[StringComparison]::Ordinal) -or $Relative.Contains('..') -or $Relative.Contains(':')){throw "Invalid supplemental payload path: $Relative"}
    $destination=Join-Path $stage $Relative
    $null=New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent)
    Copy-Item -LiteralPath $Source -Destination $destination -Force
    if(!$expected.Add($Relative)){throw "Duplicate payload path: $Relative"}
}
foreach($name in $common.Keys|Sort-Object){Add-Payload (Join-Path $ToolRoot ('_internal/'+$name)) ($owned+'runtime/_internal/'+$name)}
foreach($language in @('en_US','zh_CN')){Add-Payload (Join-Path $ToolRoot ('_internal/locales/'+$language+'.json')) ($owned+'runtime/_internal/locales/'+$language+'.json')}
$seriesSources=@{30=$rtx30;40=(Join-Path $ToolRoot '_internal/nvngx_dlssnr.dll');50=(Join-Path $ToolRoot 'mods/nvngx_dlssnr.dll')}
foreach($series in @(30,40,50)){Add-Payload $seriesSources[$series] ($owned+'runtime/mods/dlss/rtx'+$series+'/nvngx_dlssnr.dll')}
$ffmpeg=Join-Path $PSScriptRoot 'ffmpeg-vendor'
$licenses=[ordered]@{
    'DLSS5Tool-MIT.txt'=(Join-Path $ToolRoot 'LICENSE')
    'DLSS5Tool-THIRD_PARTY_NOTICES.md'=(Join-Path $ToolRoot 'THIRD_PARTY_NOTICES.md')
    'NVIDIA-DLSS-SDK-LICENSE.txt'=(Join-Path $ToolRoot '_internal/licenses/NVIDIA-DLSS/LICENSE.txt')
    'NVIDIA-RTX-Video-SDK-LICENSE.pdf'=(Join-Path $ToolRoot 'NVIDIA_RTX_Video_SDK_License.pdf')
    'NVIDIA-Optical-Flow-Headers-LICENSE.txt'=(Join-Path $ToolRoot '_internal/licenses/NVIDIA-Optical-Flow-Headers-LICENSE.txt')
    'RTX40MFG-Unlock-LICENSE.txt'=(Join-Path $ToolRoot '_internal/licenses/RTX40MFG-Unlock/LICENSE.txt')
    'FFmpeg-COPYING.GPLv3.txt'=(Join-Path $ffmpeg 'COPYING.GPLv3.txt')
    'FFmpeg-UPSTREAM-README.txt'=(Join-Path $ffmpeg 'UPSTREAM-README.txt')
    'FFmpeg-VERSION.txt'=(Join-Path $ffmpeg 'VERSION.txt')
    'FFmpeg-BUILD-CONFIG.txt'=(Join-Path $ffmpeg 'BUILD-CONFIG.txt')
    'FFmpeg-PROVENANCE.json'=(Join-Path $ffmpeg 'PROVENANCE.json')
}
foreach($entry in $licenses.GetEnumerator()){Add-Payload $entry.Value ($owned+'许可证/'+$entry.Key)}
Add-Payload (Join-Path $PSScriptRoot 'DLSS-SUPPLEMENT-GUIDE.txt') ($owned+'使用说明.txt')
$notice=@'
DLSS 补充 MOD 第三方许可与来源说明

本包只包含内录增强实际使用的原生运行文件、三系固定运行库及原始许可。
不包含 DLSS5Tool GUI、更新器、设置、队列或导出历史；Python 和媒体绑定由内录 MOD 的 EnhanceHost 提供。
保留的上游第三方清单还会提及本补充包未包含的 GUI、Torch/RAFT 和实验 GPU-export 组件，不表示这些功能被安装。

DLSS5Tool 自有 host/worker 实现采用 MIT。原始版权声明见许可证/DLSS5Tool-MIT.txt。
NVIDIA DLSS、RTX Video 和对应运行库保留 NVIDIA 原始条款，不被重新许可为 MIT。
RTX40MFG temporal adapter 的 MIT 版权与 Optical Flow 头文件的 BSD-3-Clause 声明一并保留。
FFmpeg/ffprobe 为未修改的 Gyan 7.1.1 full 静态构建，按 GPL v3 条款保留原始 LICENSE、构建信息和源码来源。

文件来源及 SHA-256 见组件来源.json，原始条款见许可证目录。
40/50 系沿用已验证的本地固定文件；30 系归档与 DLL 均按项目预存摘要验证。
这些摘要确认文件身份，不代表 NVIDIA 官方认证或所有显卡已通过推理测试。
本次不上传、不公开发布，也未将上游记录的公开再分发许可审查状态擅自改成通过。
源码来源链接不等于已附上所有 FFmpeg 对应构建的完整源码归档；相关来源状态按 FFmpeg-PROVENANCE.json 原记录保留。
'@
$noticeFile=Join-Path $build '第三方许可.txt'
[IO.File]::WriteAllText($noticeFile,$notice,[Text.UTF8Encoding]::new($true));Add-Payload $noticeFile ($owned+'第三方许可.txt')
$componentSources=@()
foreach($series in @(30,40,50)){
    $gpuProfile=$profiles."$series"
    $componentSources+=@{
        series=$series;asset=$gpuProfile.asset;release='https://github.com/banbanzhige/DLSS5Tool/releases/tag/zip'
        asset_url=('https://github.com/banbanzhige/DLSS5Tool/releases/download/zip/'+$gpuProfile.asset)
        zip_sha256=$gpuProfile.zip_sha256;dll_sha256=$gpuProfile.sha256
        local_source=if($series -eq 30){'fixed RTX30 archive, verified before extracting the single DLL'}elseif($series -eq 40){'existing DLSS5Tool-v2.3.3-win64/_internal/nvngx_dlssnr.dll'}else{'existing DLSS5Tool-v2.3.3-win64/mods/nvngx_dlssnr.dll'}
        zip_verified_locally=($series -eq 30);dll_verified_locally=$true;bytes=(Get-Item -LiteralPath $seriesSources[$series]).Length
        hardware_validation=if($series -eq 50){'Project RTX 5080 runtime evidence exists; package checks recorded separately'}else{'No physical GPU execution on this series in this build'}
    }
}
$provenance=[ordered]@{
    schema=1;product=$product;version=$version;verified_at_utc=[DateTime]::UtcNow.ToString('o')
    upstream='https://github.com/banbanzhige/DLSS5Tool/tree/v2.3.3';upstream_commit='e53e0b4d5139126e7b31866ed6b3653bbba0f449'
    release_metadata='https://github.com/banbanzhige/DLSS5Tool/releases/expanded_assets/zip'
    metadata_checked='Published RTX30 ZIP SHA256 agrees with the pre-existing C# and JSON profile snapshots.'
    common_native_sha256=$common;gpu_profiles=$componentSources
    binary_changes='None. All upstream native executable bytes are preserved.'
    excluded=@('DLSS5Tool GUI/updater','user settings/queue/export history','Python/Tk duplicate runtime','Torch/RAFT/guidance','experimental GPU-export runtime and unrelated source archives','NVIDIA driver/system DLLs')
    driver_dependencies=@('Windows D3D12/DXGI/D3DCOMPILER_47','NVIDIA nvcuda.dll','NVIDIA nvofapi64.dll')
    license_status='Original third-party terms retained; no claim of completed public redistribution license review or NVIDIA endorsement.'
    ffmpeg_source_status='Original executables, GPLv3 license, build configuration and source provenance retained; not a complete corresponding-source archive.'
}
$provenanceFile=Join-Path $sourceRoot '组件来源.json'
[IO.File]::WriteAllText($provenanceFile,($provenance|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
Add-Payload $provenanceFile ($owned+'组件来源.json')

$hostHash=(Get-FileHash -LiteralPath (Join-Path $game 'AzureArchive.exe') -Algorithm SHA256).Hash
$generated=Join-Path $build 'Installer.generated.cs'
$installer=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Installer.cs') -Raw).Replace('@@HOSTHASH@@',$hostHash)
[IO.File]::WriteAllText($generated,$installer,[Text.UTF8Encoding]::new($true))
$csc=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$sources=@($generated,(Join-Path $PSScriptRoot 'DlssSupplementInstaller.cs'),(Join-Path $mod 'src/DlssComponents.cs'))
$uninstaller=Join-Path $stage ($owned+'卸载DLSS补充MOD.exe')
& $csc /nologo /target:winexe /platform:x64 /optimize- /debug- /main:DlssProgram /define:UNINSTALL $references "/out:$uninstaller" $sources
if($LASTEXITCODE -ne 0){throw 'DLSS supplemental uninstaller compilation failed.'}
$null=$expected.Add($owned+'卸载DLSS补充MOD.exe')
$files=@(Get-ChildItem -LiteralPath (Join-Path $stage $owned) -File -Recurse|ForEach-Object {
    $relative=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')
    if(!$expected.Contains($relative)){throw "Unknown file in supplemental stage; retained and not packaged: $relative"}
    @{Path=$relative;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
if($files.Count -ne $expected.Count){throw 'Supplemental stage is incomplete.'}
$receipt=@{Product=$product;Version=$version;Root='';Files=$files;LegacyFiles=@();GeneratedFiles=@()}
[IO.File]::WriteAllText((Join-Path $stage 'payload-manifest.json'),($receipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
if(@(Get-ChildItem -LiteralPath $stage -File -Recurse).Count -ne $expected.Count+1){throw 'Unexpected file outside supplemental owned stage; not packaged.'}
$payload=Join-Path $PSScriptRoot 'dlss-supplement-payload.zip'
$temporaryPayload=Join-Path $build ('payload-'+[Guid]::NewGuid().ToString('N')+'.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$temporaryPayload,[IO.Compression.CompressionLevel]::Optimal,$false)
if(Test-Path -LiteralPath $payload){[IO.File]::Replace($temporaryPayload,$payload,$null)}else{[IO.File]::Move($temporaryPayload,$payload)}
$installerOutput=Join-Path $OutputDirectory 'AzureArchive-DLSS补充MOD-1.0.0-RTX30-40-50-未加壳安装.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize- /debug- /main:DlssProgram $references "/resource:$payload,dlss-supplement.payload" "/out:$installerOutput" $sources
if($LASTEXITCODE -ne 0){throw 'DLSS supplemental installer compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFile($installerOutput)
if($assembly.EntryPoint.DeclaringType.Name -ne 'DlssProgram' -or $assembly.GetManifestResourceNames() -notcontains 'dlss-supplement.payload'){throw 'Supplemental installer entry point or payload resource is invalid.'}
if($assembly.GetManifestResourceNames() -contains 'aa.encrypted-core'){throw 'Supplemental EXE must remain unprotected.'}
$bytes=([long](Get-ChildItem -LiteralPath (Join-Path $stage $owned) -File -Recurse|Measure-Object Length -Sum).Sum)
$report=[ordered]@{
    product=$product;version=$version;built_at_utc=[DateTime]::UtcNow.ToString('o');host_sha256=$hostHash
    installer=$installerOutput;installer_sha256=(Get-FileHash -LiteralPath $installerOutput).Hash;installer_bytes=(Get-Item -LiteralPath $installerOutput).Length
    uninstaller_relative=$owned+'卸载DLSS补充MOD.exe';uninstaller_sha256=(Get-FileHash -LiteralPath $uninstaller).Hash
    payload_file_count=$files.Count;installed_bytes=$bytes;installed_mib=[math]::Round($bytes/1MB,2)
    protection='none';configs_modified=$false;original_sources_modified=$false;gpu_execution='Not performed by the build; consult runtime/package test reports.'
    files=$files
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'DLSS补充组件清单.json'),($report|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DLSS-SUPPLEMENT-GUIDE.txt') -Destination (Join-Path $OutputDirectory 'DLSS补充MOD使用说明.txt') -Force
Get-FileHash -LiteralPath $installerOutput,$uninstaller
Write-Host "Supplemental payload: $payload"
Write-Host "Installed size: $([math]::Round($bytes/1MB,2)) MiB ($($files.Count) files)."
