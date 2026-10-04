#requires -Version 7.0
param([string]$RuntimeDirectory,[switch]$Protected)
$ErrorActionPreference='Stop'
$recorder=Split-Path $PSScriptRoot -Parent
$output=Join-Path $recorder ('test-output/recorder-update-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
$sources=@((Join-Path $recorder 'src/RecorderUpdateClient.cs'),(Join-Path $recorder 'src/RecorderUpdateDownload.cs'),(Join-Path $PSScriptRoot 'RecorderUpdateTests.cs'))
if ($Protected -and !$RuntimeDirectory) {throw '-Protected requires the exact .NET 6 -RuntimeDirectory.'}
if (!$RuntimeDirectory) {
    Add-Type -Path $sources -CompilerOptions '/nullable:enable'
    [RecorderUpdateTests]::Run($output)
    return
}
# Optional exact .NET 6 check: reference the supplied runtime read-only, and emit only test artifacts.
$null=New-Item -ItemType Directory -Force -Path $output
foreach($name in @('Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll')) {
    $null=[Reflection.Assembly]::LoadFrom((Join-Path $PSHOME $name))
}
$references=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath $RuntimeDirectory -Filter '*.dll') {
    try {
        $null=[Reflection.AssemblyName]::GetAssemblyName($file.FullName)
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
    } catch [BadImageFormatException] { }
}
$trees=[Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$parseOptions=[Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp10)
foreach($file in $sources) {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file),$parseOptions,$file))
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('RecorderUpdateTransport.Tests',$trees,$references,$options)
$assembly=Join-Path $output 'RecorderUpdateTransport.Tests.dll'
$stream=[IO.File]::Create($assembly)
try {$result=$compilation.Emit($stream)} finally {$stream.Dispose()}
$result.Diagnostics | Where-Object {$_.Severity -in @('Error','Warning')} | ForEach-Object ToString
if (!$result.Success) {throw 'Recorder update .NET 6 test compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"}}}' | Set-Content -LiteralPath (Join-Path $output 'RecorderUpdateTransport.Tests.runtimeconfig.json') -Encoding utf8
if($Protected) {
    $protectedOutput=Join-Path $output 'protected'
    $null=New-Item -ItemType Directory -Force -Path $protectedOutput
    [xml]$protectionConfig='<Obfuscator><Var name="InPath" value=""/><Var name="OutPath" value=""/><Var name="KeepPublicApi" value="true"/><Var name="HidePrivateApi" value="true"/><Var name="HideStrings" value="true"/><Var name="RenameProperties" value="false"/><Var name="RegenerateDebugInfo" value="false"/><AssemblySearchPath path=""/><Module file=""><SkipType rx=".*AnonymousType.*" skipMethods="true" skipFields="true" skipProperties="true"/></Module></Obfuscator>'
    ($protectionConfig.Obfuscator.Var|Where-Object name -eq 'InPath').value=$output
    ($protectionConfig.Obfuscator.Var|Where-Object name -eq 'OutPath').value=$protectedOutput
    $protectionConfig.Obfuscator.AssemblySearchPath.path=[IO.Path]::GetFullPath($RuntimeDirectory)
    $protectionConfig.Obfuscator.Module.file=$assembly
    $protectionConfigPath=Join-Path $output 'obfuscar.tests.xml'
    $protectionConfig.Save($protectionConfigPath)
    & (Join-Path $recorder 'packaging/tools/obfuscar/tools/Obfuscar.Console.exe') $protectionConfigPath | Tee-Object -FilePath (Join-Path $output 'obfuscar.log')
    if($LASTEXITCODE -ne 0) {throw 'Recorder transport test DLL obfuscation failed.'}
    Copy-Item -LiteralPath (Join-Path $output 'RecorderUpdateTransport.Tests.runtimeconfig.json') -Destination $protectedOutput
    $assembly=Join-Path $protectedOutput 'RecorderUpdateTransport.Tests.dll'
    [ordered]@{Assembly=$assembly;Sha256=(Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash;Framework='net6.0';KeepPublicApi=$true;HidePrivateApi=$true;HideStrings=$true;RenameProperties=$false;TestOnly=$true;Distributed=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'protected-test-proof.json') -Encoding utf8
}
& dotnet $assembly $output | Tee-Object -FilePath (Join-Path $output 'test-results.log')
if ($LASTEXITCODE -ne 0) {throw ('Recorder update .NET 6 tests failed: '+$LASTEXITCODE)}

