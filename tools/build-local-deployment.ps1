param([string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build\LocalDeployment.Core.dll'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework compiler not found.' }
$output = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($output) -ne '.dll') { throw 'This tool only builds a class library (.dll).' }
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
& $compiler /nologo /target:library /optimize+ /utf8output /codepage:65001 "/out:$output" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'LocalDeploymentCore.cs')
if ($LASTEXITCODE -ne 0) { throw 'Local deployment core compilation failed.' }
Write-Output $output
