#requires -Version 7.0
$ErrorActionPreference='Stop'
$recorder=Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $recorder 'src/NativeCaptureTimeline.cs'),(Join-Path $recorder 'src/NativeLiveCaptureTimeline.cs'),(Join-Path $recorder 'src/NativeCompositeSchedule.cs'),(Join-Path $PSScriptRoot 'NativeCompositeScheduleTests.cs') -CompilerOptions '/nullable:enable'
[NativeCompositeScheduleTests]::Run()
