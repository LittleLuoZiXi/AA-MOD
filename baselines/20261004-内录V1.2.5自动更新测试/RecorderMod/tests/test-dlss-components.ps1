#requires -Version 7.0
param([Parameter(Mandatory)][string]$ReferenceToolDirectory,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $mod ('test-output/dlss-components-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
$workspace=[IO.Path]::GetFullPath((Split-Path (Split-Path $mod -Parent) -Parent)).TrimEnd('\')+'\'
if(!$output.StartsWith($workspace,[StringComparison]::OrdinalIgnoreCase)){throw 'Keep this test output inside RTX开发测试.'}
Add-Type -Path (Join-Path $mod 'src/DlssComponents.cs'),(Join-Path $PSScriptRoot 'DlssComponentsTests.cs')
[DlssComponentsTests]::Run($output,[IO.Path]::GetFullPath($ReferenceToolDirectory))
