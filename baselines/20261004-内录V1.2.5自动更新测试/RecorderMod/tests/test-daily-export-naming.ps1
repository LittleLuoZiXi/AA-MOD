#requires -Version 7.0
$ErrorActionPreference='Stop'
$recorder=Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $recorder 'src/DailyExportNaming.cs'),(Join-Path $PSScriptRoot 'DailyExportNamingTests.cs') -CompilerOptions '/nullable:enable'
$output=Join-Path $recorder ('test-output/daily-naming-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
[DailyExportNamingTests]::Run($output)
