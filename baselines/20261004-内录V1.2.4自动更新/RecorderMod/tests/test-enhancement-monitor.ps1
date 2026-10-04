#requires -Version 7.0
$ErrorActionPreference='Stop'
$recorder=Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $recorder 'src/EnhancementMonitor.cs'),(Join-Path $PSScriptRoot 'EnhancementMonitorTests.cs') -CompilerOptions '/nullable:enable'
$output=Join-Path $recorder ('test-output/enhancement-monitor-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
[EnhancementMonitorTests]::Run((Get-Process -Id $PID).Path,$output).GetAwaiter().GetResult()
