param([Parameter(Mandatory=$true)][string]$JobPath,[Parameter(Mandatory=$true)][string]$JobSha256)
$ErrorActionPreference='Stop'
$directory=[IO.Path]::GetFullPath($PSScriptRoot)
try {
    if([IO.Path]::GetFullPath($JobPath) -ne (Join-Path $directory 'job.json')){throw 'Update job must be beside the trusted helper.'}
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if(!(Test-Path -LiteralPath $compiler)){$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'}
    $library=Join-Path $directory 'RevisionCompare.ApplyCore.dll'
    $cecil=Join-Path $directory 'Mono.Cecil.dll'
    $netstandard=Join-Path ([IO.Path]::GetDirectoryName($compiler)) 'netstandard.dll'
    if(!(Test-Path -LiteralPath $netstandard)){$netstandard=Join-Path ([IO.Path]::GetDirectoryName($compiler)) 'Facades\netstandard.dll'}
    if(!(Test-Path -LiteralPath $netstandard)){throw 'The local .NET Framework netstandard facade is missing.'}
    & $compiler /nologo /target:library /optimize+ /utf8output /codepage:65001 "/out:$library" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll "/reference:$netstandard" "/reference:$cecil" (Join-Path $directory 'UpdateHelper.cs')
    if($LASTEXITCODE -ne 0){throw 'The local update helper could not be compiled.'}
    [void][Reflection.Assembly]::LoadFrom($cecil)
    [void][Reflection.Assembly]::LoadFrom($library)
    $result=[AzureArchive.RevisionCompare.UpdateApplication.UpdateApplyHelper]::Run($JobPath,$JobSha256)
    exit $result
} catch {
    $status=@{State='Failed';Message='Update preparation failed; the installed MOD was not changed.';Error=$_.Exception.Message;InstallationUncertain=$false}|ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $directory 'status.json'),$status,[Text.UTF8Encoding]::new($false))
    exit 1
}
