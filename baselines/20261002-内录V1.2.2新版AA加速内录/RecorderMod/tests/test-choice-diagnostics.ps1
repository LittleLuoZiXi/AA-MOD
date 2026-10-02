#requires -Version 7.0
param(
    [string]$OutputDirectory,
    [switch]$Worker,
    [string]$AssemblyPath,
    [string]$WorkerOutput,
    [switch]$OptIn,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$ExtraArguments
)
$ErrorActionPreference='Stop'
if($Worker){
    $assembly=[Reflection.Assembly]::LoadFrom($AssemblyPath)
    $type=$assembly.GetType('AzureArchive.Recorder.ChoiceDiagnosticsRegression',$true)
    $checks=@($type.GetMethod('Run').Invoke($null,@([bool]$OptIn)))
    [ordered]@{Passed=$true;OptIn=[bool]$OptIn;AssemblyPath=$AssemblyPath;Checks=$checks} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $WorkerOutput -Encoding utf8
    $checks | ForEach-Object {Write-Output ('PASS: '+$_)}
    exit 0
}
$mod=Split-Path $PSScriptRoot -Parent
$source=Join-Path $mod 'src/ChoiceProbe.cs'
$testSource=Join-Path $PSScriptRoot 'test-choice-diagnostics.cs'
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $mod ('test-output/choice-diagnostics-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh output directory; existing evidence is never overwritten.'}
$null=New-Item -ItemType Directory -Path $output
$plainDir=Join-Path $output 'plain';$protectedDir=Join-Path $output 'obfuscated'
$null=New-Item -ItemType Directory -Path $plainDir,$protectedDir
$assembly=Join-Path $plainDir 'ChoiceDiagnostics.Regression.dll'
Add-Type -Path $source,$testSource -OutputAssembly $assembly -OutputType Library -CompilerOptions '/nullable:enable' -IgnoreWarnings
$xml=[xml](Get-Content -LiteralPath (Join-Path $mod 'packaging/obfuscar.xml') -Raw)
($xml.Obfuscator.Var|Where-Object name -eq 'InPath').value=$plainDir
($xml.Obfuscator.Var|Where-Object name -eq 'OutPath').value=$protectedDir
$xml.Obfuscator.Module.file=$assembly
foreach($node in @($xml.Obfuscator.AssemblySearchPath)){$null=$xml.Obfuscator.RemoveChild($node)}
foreach($path in @($PSHOME,(Join-Path $PSHOME 'ref'))){$node=$xml.CreateElement('AssemblySearchPath');$node.SetAttribute('path',$path);$null=$xml.Obfuscator.InsertBefore($node,$xml.Obfuscator.Module)}
$config=Join-Path $output 'obfuscar.generated.xml';$xml.Save($config)
& (Join-Path $mod 'packaging/tools/obfuscar/tools/Obfuscar.Console.exe') $config *> (Join-Path $output 'obfuscar.log')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $output 'obfuscar.log') -Tail 30;throw 'Regression assembly obfuscation failed.'}
$protected=Join-Path $protectedDir 'ChoiceDiagnostics.Regression.dll'
if(!(Test-Path -LiteralPath $protected)){throw 'Missing obfuscated regression assembly.'}
$mapping=Get-Content -LiteralPath (Join-Path $protectedDir 'Mapping.txt') -Raw
if($mapping -notmatch 'AzureArchive\.Recorder\.ChoiceDiagnostics -> '){throw 'Production diagnostic class was not included in obfuscation.'}
$pwsh=(Get-Process -Id $PID).Path
$results=@()
foreach($variant in @(@{Name='plain';Path=$assembly},@{Name='obfuscated';Path=$protected})){
    foreach($opt in @($false,$true)){
        $name=$variant.Name+'-'+$(if($opt){'optin'}else{'normal'})
        $workerOutput=Join-Path $output ($name+'.json')
        $arguments=@('-NoProfile','-File',$PSCommandPath,'-Worker','-AssemblyPath',$variant.Path,'-WorkerOutput',$workerOutput)
        if($opt){$arguments+=@('-OptIn','--aa-recorder-choice-probe')}
        & $pwsh @arguments *> (Join-Path $output ($name+'.log'))
        if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $output ($name+'.log')) -Tail 25;throw ('Choice diagnostic regression failed: '+$name)}
        $results+=Get-Content -LiteralPath $workerOutput -Raw | ConvertFrom-Json
    }
}
$proof=[ordered]@{Passed=$true;ProductionSource=$source;ProductionSourceSha256=(Get-FileHash -LiteralPath $source).Hash;ObfuscationTemplateSha256=(Get-FileHash -LiteralPath (Join-Path $mod 'packaging/obfuscar.xml')).Hash;Checks=@($results | ForEach-Object Checks).Count;Results=$results;Scope='Exact ChoiceProbe.cs compiled with deterministic Harmony/AA registration doubles, plain and Obfuscar production configuration. No AA launch, native detour, UI rendering or DLSS execution.';VerifiedUtc=[DateTime]::UtcNow.ToString('o')}
$proof | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
Write-Output ('PASS: '+$proof.Checks+' checks across normal/opt-in and plain/obfuscated assemblies. Evidence: '+$output)