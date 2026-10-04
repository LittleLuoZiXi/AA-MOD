#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$recorder=Split-Path $PSScriptRoot -Parent
$ffmpeg=Join-Path $recorder 'packaging/ffmpeg-vendor/ffmpeg.exe'
$ffprobe=Join-Path $recorder 'packaging/ffmpeg-vendor/ffprobe.exe'
foreach($path in @($ffmpeg,$ffprobe)){if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing bundled CPU test dependency: $path"}}
$encoder=Join-Path $recorder 'src/Encoder.cs'
$queue=Join-Path $recorder 'src/EncoderWorkQueue.cs'
Add-Type -Path $encoder,$queue,(Join-Path $PSScriptRoot 'EncoderCleanupTests.cs') -CompilerOptions '/nullable:enable'
$output=Join-Path $recorder ('test-output/encoder-cleanup-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$sourceInputs=@($encoder,$queue,(Join-Path $PSScriptRoot 'EncoderCleanupTests.cs')) | ForEach-Object { [ordered]@{Path=$_;Sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} }
try {
[EncoderCleanupTests]::Run($output,$ffmpeg,$ffprobe,(Get-FileHash -LiteralPath $encoder -Algorithm SHA256).Hash)
} finally {
    if(Test-Path -LiteralPath $output -PathType Container){[IO.File]::WriteAllText((Join-Path $output 'source-inputs.json'),($sourceInputs | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))}
}
Write-Output ("Evidence: "+(Join-Path $output 'results.json'))
