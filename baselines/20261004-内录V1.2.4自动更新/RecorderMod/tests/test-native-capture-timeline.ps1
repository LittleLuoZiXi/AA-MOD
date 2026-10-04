#requires -Version 7.0
$ErrorActionPreference='Stop'
$recorder=Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $recorder 'src/NativeCaptureTimeline.cs'),(Join-Path $PSScriptRoot 'NativeCaptureTimelineTests.cs') -CompilerOptions '/nullable:enable'
[NativeCaptureTimelineTests]::Run()
