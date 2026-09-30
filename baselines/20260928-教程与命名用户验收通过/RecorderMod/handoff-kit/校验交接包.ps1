#requires -Version 7.0
param([string]$BundleRoot=$PSScriptRoot)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($BundleRoot).TrimEnd('\','/')
$manifest=Get-Content -LiteralPath (Join-Path $root '文件清单.json') -Raw | ConvertFrom-Json
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$count=0
foreach($entry in $manifest.Files) {
    $relative=[string]$entry.Path
    if([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or !$seen.Add($relative)){throw "Invalid or duplicate manifest path: $relative"}
    $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
    if(!$path.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "Path escaped bundle: $relative"}
    $item=Get-Item -LiteralPath $path -Force
    if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -ne $entry.Size){throw "File type/size mismatch: $relative"}
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Sha256){throw "SHA256 mismatch: $relative"}
    $count++
    if($count%200 -eq 0){Write-Host "Verified $count / $($manifest.Files.Count) files"}
}
Write-Host "PASS: $count files match SHA-256 manifest. No GPU inference or installation performed."
