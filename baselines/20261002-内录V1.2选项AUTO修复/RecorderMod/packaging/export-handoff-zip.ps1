#requires -Version 7.0
param([Parameter(Mandatory)][string]$BundleRoot,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($BundleRoot).TrimEnd('\','/')
$output=[IO.Path]::GetFullPath($OutputPath)
if((Split-Path $root -Leaf) -ne 'AA录制MOD'){throw 'Expected AA录制MOD bundle root'}
if(Test-Path -LiteralPath $output){throw "Refusing to overwrite existing archive: $output"}
if($output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'ZIP must be outside bundle'}
function Get-SafeFiles([string]$Directory) {
    $info=Get-Item -LiteralPath $Directory -Force
    if($info.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Reparse point refused: $Directory"}
    foreach($item in Get-ChildItem -LiteralPath $Directory -Force) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Reparse point refused: $($item.FullName)"}
        if($item.PSIsContainer){Get-SafeFiles $item.FullName}else{$item}
    }
}
$files=@(Get-SafeFiles $root | Sort-Object FullName)
$records=@($files | Where-Object FullName -ne (Join-Path $root '文件清单.json') | ForEach-Object {
    [ordered]@{Path=[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/');Size=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$manifestPath=Join-Path $root '文件清单.json'
[ordered]@{Product='AzureArchiveRecorder';Version='0.2.1 R4';Created=(Get-Date).ToString('o');Algorithm='SHA256';ManifestSelfExcluded=$true;Files=$records} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
& (Join-Path $root '校验交接包.ps1') -BundleRoot $root
Write-Host 'Compressing complete handoff package...'
[IO.Compression.ZipFile]::CreateFromDirectory($root,$output,[IO.Compression.CompressionLevel]::Optimal,$true,[Text.Encoding]::UTF8)
$expected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($record in $records){$expected.Add('AA录制MOD/'+$record.Path,$record)}
$expected.Add('AA录制MOD/文件清单.json',@{Size=(Get-Item -LiteralPath $manifestPath).Length;Sha256=(Get-FileHash -LiteralPath $manifestPath).Hash})
$zip=[IO.Compression.ZipFile]::OpenRead($output)
$verified=0
try {
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $zip.Entries) {
        $name=$entry.FullName
        if(!$seen.Add($name) -or $name.Contains('..') -or $name.Contains(':') -or $name.Contains('\') -or !$name.StartsWith('AA录制MOD/')){throw "Unsafe/duplicate ZIP entry: $name"}
        if($name.EndsWith('/')){if($entry.Length -ne 0){throw 'Nonempty directory entry'};continue}
        if(!$expected.ContainsKey($name)){throw "Unexpected ZIP entry: $name"}
        $record=$expected[$name]
        if($entry.Length -ne $record.Size){throw "ZIP size mismatch: $name"}
        $stream=$entry.Open()
        try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if($hash -ne $record.Sha256){throw "ZIP contents mismatch: $name"}
        $verified++
        if($verified%500 -eq 0){Write-Host "ZIP stream hashes verified: $verified / $($expected.Count)"}
    }
    if($verified -ne $expected.Count){throw 'ZIP is missing expected files'}
}finally{$zip.Dispose()}
$proof=[ordered]@{Archive=$output;Size=(Get-Item -LiteralPath $output).Length;Sha256=(Get-FileHash -LiteralPath $output).Hash;VerifiedFiles=$verified;AllDecompressedFileHashesMatched=$true;VerifiedAt=(Get-Date).ToString('o')}
$proof | ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'verification-latest/archive-verification.json') -Encoding utf8
$proof | ConvertTo-Json
