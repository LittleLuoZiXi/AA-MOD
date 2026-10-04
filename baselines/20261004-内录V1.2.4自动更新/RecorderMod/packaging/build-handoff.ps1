#requires -Version 7.0
# Explicit allowlist export. Does not install, delete development data, or copy user profiles/stories.
param([string]$StageDirectory, [string]$DlssDirectory='E:\DLSS5Tool-v2.3.3-win64')
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$game=Split-Path $mod -Parent
if(!$StageDirectory){$StageDirectory=Join-Path $mod 'handoff-stage-20260928'}
$stage=[IO.Path]::GetFullPath($StageDirectory)
if(Test-Path -LiteralPath $stage){throw "Use a fresh stage directory: $stage"}
$bundle=Join-Path $stage 'AA录制MOD'
$source=Join-Path $bundle '源码工程/RecorderMod'
$evidence=Join-Path $mod 'verification-latest'
$null=New-Item -ItemType Directory -Path $source,$evidence -Force
function Copy-Exact([string]$From,[string]$To) {
    $item=Get-Item -LiteralPath $From -Force
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Refusing reparse point: $From"}
    if($item.PSIsContainer){throw "Expected file: $From"}
    $null=New-Item -ItemType Directory -Path (Split-Path $To -Parent) -Force
    Copy-Item -LiteralPath $From -Destination $To -Force
}
function Copy-Tree([string]$From,[string]$To,[switch]$CleanSource) {
    $rootItem=Get-Item -LiteralPath $From -Force
    if($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Refusing reparse point: $From"}
    $null=New-Item -ItemType Directory -Path $To -Force
    foreach($child in Get-ChildItem -LiteralPath $From -Force) {
        if($CleanSource -and $child.Name -in @('.git','__pycache__','.pytest_cache','.venv')){continue}
        if($CleanSource -and !$child.PSIsContainer -and $child.Extension -in @('.pyc','.pyo','.aap.lock')){continue}
        if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Refusing reparse point: $($child.FullName)"}
        $target=Join-Path $To $child.Name
        if($child.PSIsContainer){Copy-Tree $child.FullName $target -CleanSource:$CleanSource}else{Copy-Exact $child.FullName $target}
    }
}
foreach($name in @('build-r4-protected.log','safety-tests-r4-protected.log','dependency-tests-r4-protected.log','ffmpeg-tests-r4-protected.log','protection-tests-r4.log','local-install-r4-protected.log','build-r4-development.log','safety-tests-r4-development-verified.log','ffmpeg-tests-r4-development-verified.log','variant-switch-r4-development.log')){
    Copy-Exact (Join-Path $PSScriptRoot $name) (Join-Path $evidence $name)
}
foreach($name in @('r4-bundled-ffmpeg-bepinex.log','r4-bundled-ffmpeg-verification.log')){
    Copy-Exact (Join-Path $mod "test-output/$name") (Join-Path $evidence $name)
}
foreach($role in @('installer','uninstaller')){
    Copy-Exact (Join-Path $PSScriptRoot "protected-build/$role/protection-proof.json") (Join-Path $evidence "$role-protection-proof.json")
}
$records=@()
foreach($variant in @(
    @{Name='AzureArchive内录MOD-0.2.1-R4-内置FFmpeg安装.exe';Sha256='A52632638701418F0EDE184E40E245516D2A10ECFC29A7D860AC429CBBE2E3A5';Protected=$true},
    @{Name='AzureArchive内录MOD-0.2.1-R4-开发未加壳安装.exe';Sha256='1CA43E753C825C749FE94B69C3B6EB8092D32A6C278CA786DD9C5F587FA02B1C';Protected=$false})) {
    $path=Join-Path $game $variant.Name
    $hash=(Get-FileHash -LiteralPath $path).Hash
    if($hash -ne $variant.Sha256){throw "Unverified installer: $path"}
    Copy-Exact $path (Join-Path $bundle "安装程序/$($variant.Name)")
    $records+=@{Name=$variant.Name;Size=(Get-Item -LiteralPath $path).Length;Sha256=$hash;Protected=$variant.Protected}
}
$release=@{Version='0.2.1 R4';Exported=(Get-Date).ToString('o');HostExeSha256=(Get-FileHash -LiteralPath (Join-Path $game 'AzureArchive.exe')).Hash;Installers=$records;ProtectedChecks=37;DevelopmentChecks=24;NvidiaInferenceTested=$false;DevelopmentPluginLoadedInAA=$false;Upstream='';UpstreamSnapshotCommit='e53e0b4d5139126e7b31866ed6b3653bbba0f449'}
# Upstream's actual remote is recorded below, avoiding any inferred repository URL.
$remote=& git -C (Join-Path $DlssDirectory 'DLSS5Tool-source') remote get-url origin
if($LASTEXITCODE -ne 0){throw 'Cannot read upstream provenance'}
$release.Upstream=$remote.Trim()
$release | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence '制品信息.json') -Encoding utf8
Copy-Tree $evidence (Join-Path $bundle '验证记录')
Copy-Tree (Join-Path $mod 'handoff-kit') $bundle -CleanSource
foreach($directory in @('src','bridge','handoff-kit')){Copy-Tree (Join-Path $mod $directory) (Join-Path $source $directory) -CleanSource}
foreach($file in Get-ChildItem -LiteralPath $mod -File){
    if($file.Extension -in @('.ps1','.md') -or $file.Name -eq 'manifest.json'){Copy-Exact $file.FullName (Join-Path $source $file.Name)}
}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $mod 'tests') -File){
    if($file.Extension -in @('.py','.ps1')){Copy-Exact $file.FullName (Join-Path $source "tests/$($file.Name)")}
}
Copy-Exact (Join-Path $mod 'test-fixture/RecorderSmoke.aap2') (Join-Path $source 'test-fixture/RecorderSmoke.aap2')
foreach($file in Get-ChildItem -LiteralPath $PSScriptRoot -File){
    if(($file.Extension -in @('.cs','.ps1','.in','.xml','.txt','.md','.py')) -and $file.Name -notlike '*.generated.cs'){
        Copy-Exact $file.FullName (Join-Path $source "packaging/$($file.Name)")
    }
}
foreach($directory in @('tools','frozen','ffmpeg-vendor')){Copy-Tree (Join-Path $PSScriptRoot $directory) (Join-Path $source "packaging/$directory")}
$dlssTarget=Join-Path $bundle '依赖与上游/DLSS5Tool-v2.3.3-win64'
Copy-Tree (Join-Path $DlssDirectory '_internal') (Join-Path $dlssTarget '_internal')
foreach($name in @('DLSS5Tool.exe','DLSS5Update.exe','README.md','README.en.md','CHANGELOG.md','LICENSE','THIRD_PARTY_NOTICES.md','NVIDIA_RTX_Video_SDK_License.pdf','DISTRIBUTION-REVIEW.txt')){
    Copy-Exact (Join-Path $DlssDirectory $name) (Join-Path $dlssTarget $name)
}
Copy-Tree (Join-Path $DlssDirectory 'DLSS5Tool-source') (Join-Path $bundle '依赖与上游/DLSS5Tool-source') -CleanSource
Write-Host "Handoff files staged at: $bundle"
Write-Host 'Run independent source/build checks, then create 文件清单.json and ZIP. This script does not delete anything.'
