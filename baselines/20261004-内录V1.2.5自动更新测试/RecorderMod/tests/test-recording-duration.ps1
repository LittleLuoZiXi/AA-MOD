#requires -Version 7.0
$ErrorActionPreference='Stop'
$recorder=Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $recorder 'src/RecordingDurationPlan.cs'),(Join-Path $PSScriptRoot 'RecordingDurationPlanTests.cs') -CompilerOptions '/nullable:enable'
[RecordingDurationPlanTests]::Run()