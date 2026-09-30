param([Parameter(Mandatory)][ValidateSet(30,40,50)][int]$Series)
$ErrorActionPreference = 'Stop'
$profiles = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bridge/gpu_profiles.json') -Raw | ConvertFrom-Json
$entry = $profiles.profiles."$Series"
$dest = Join-Path $PSScriptRoot "gpu-runtimes/rtx$Series"
$dll = Join-Path $dest 'nvngx_dlssnr.dll'
if (Test-Path -LiteralPath $dll) {
    if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ieq $entry.sha256) { Write-Host "RTX $Series runtime ready."; return }
    throw "已有 DLL 哈希不同，未覆盖：$dll"
}
$null = New-Item -ItemType Directory -Force -Path $dest
$zip = Join-Path $dest ('download-' + [Guid]::NewGuid().ToString('N') + '.zip')
$uri = 'https://github.com/banbanzhige/DLSS5Tool/releases/download/zip/' + [Uri]::EscapeDataString($entry.asset)
Invoke-WebRequest -Uri $uri -OutFile $zip
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ine $entry.zip_sha256) { throw "ZIP 校验失败，保留下载以供检查：$zip" }
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $files = @($archive.Entries | Where-Object { [IO.Path]::GetFileName($_.FullName) -ieq 'nvngx_dlssnr.dll' })
    if ($files.Count -ne 1) { throw '压缩包中 nvngx_dlssnr.dll 缺失或不唯一。' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($files[0],$dll,$false)
} finally { $archive.Dispose() }
if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ine $entry.sha256) { throw "DLL 校验失败：$dll" }
Remove-Item -LiteralPath $zip
Write-Host "已安装 RTX $Series 独立运行库；未修改 DLSS5Tool 原文件：$dll"
