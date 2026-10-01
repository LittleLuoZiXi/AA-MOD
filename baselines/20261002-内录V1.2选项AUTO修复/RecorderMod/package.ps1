$ErrorActionPreference='Stop'
$game=Split-Path $PSScriptRoot -Parent
$version=(Get-Content (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json).version_number
$output=Join-Path $PSScriptRoot "AzureArchiveRecorder-$version.zip"
if(Test-Path -LiteralPath $output) { throw "打包文件已存在，未覆盖：$output" }
$archive=[IO.Compression.ZipFile]::Open($output,[IO.Compression.ZipArchiveMode]::Create)
try {
    $files=@(Get-ChildItem (Join-Path $PSScriptRoot 'src') -File)
    $files+=@(Get-ChildItem (Join-Path $PSScriptRoot 'tests') -File | Where-Object Extension -in @('.py','.ps1'))
    $files+=@(Get-ChildItem (Join-Path $PSScriptRoot 'bridge') -Recurse -File | Where-Object { $_.Extension -in @('.py','.json','.txt') -or $_.Name -eq 'DLSS5Tool-LICENSE' })
    $files+=@(Get-ChildItem $PSScriptRoot -File | Where-Object Extension -in @('.ps1','.md','.json'))
    $files+=@(Get-ChildItem (Join-Path $game "mods/AzureArchiveRecorder/$version") -File)
    foreach($file in $files) {
        $entry=[IO.Path]::GetRelativePath($game,$file.FullName).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$entry,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Get-FileHash -LiteralPath $output -Algorithm SHA256
