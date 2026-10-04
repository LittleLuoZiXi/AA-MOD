#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$recorder=Split-Path $PSScriptRoot -Parent
$queue=Join-Path $recorder 'src/EncoderWorkQueue.cs'
$tests=Join-Path $PSScriptRoot 'EncoderWorkQueueTests.cs'
Add-Type -Path $queue,$tests -CompilerOptions '/nullable:enable'
$output=Join-Path $recorder ('test-output/encoder-work-queue-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$inputs=@($queue,$tests) | ForEach-Object {[ordered]@{Path=$_;Sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}}
try{[EncoderWorkQueueTests]::Run($output,(Get-FileHash -LiteralPath $queue -Algorithm SHA256).Hash)}
finally{if(Test-Path -LiteralPath $output -PathType Container){[IO.File]::WriteAllText((Join-Path $output 'source-inputs.json'),($inputs | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))}}
Write-Output ('Evidence: '+(Join-Path $output 'results.json'))