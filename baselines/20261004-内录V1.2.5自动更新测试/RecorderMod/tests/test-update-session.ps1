#requires -Version 7.0
param([string]$RuntimeDirectory = 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App\6.0.36')
$ErrorActionPreference='Stop'
$recorder=Split-Path $PSScriptRoot -Parent
$output=Join-Path $recorder ('test-output/update-session-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$sources=@((Join-Path $recorder 'src/RecorderUpdateClient.cs'),(Join-Path $recorder 'src/RecorderUpdateSession.cs'),(Join-Path $PSScriptRoot 'RecorderUpdateSessionTests.cs'))
$null=New-Item -ItemType Directory -Force -Path $output
foreach($name in @('Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll')) {
    $null=[Reflection.Assembly]::LoadFrom((Join-Path $PSHOME $name))
}
$references=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath $RuntimeDirectory -Filter '*.dll') {
    try { $null=[Reflection.AssemblyName]::GetAssemblyName($file.FullName); $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName)) }
    catch [BadImageFormatException] { }
}
$trees=[Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$parseOptions=[Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp10)
foreach($file in $sources) { $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file),$parseOptions,$file)) }
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('RecorderUpdateSession.Tests',$trees,$references,$options)
$assembly=Join-Path $output 'RecorderUpdateSession.Tests.dll'
$stream=[IO.File]::Create($assembly)
try { $result=$compilation.Emit($stream) } finally { $stream.Dispose() }
$result.Diagnostics | Where-Object {$_.Severity -in @('Error','Warning')} | ForEach-Object ToString
if (!$result.Success) {throw 'Recorder update session .NET 6/C#10 compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"}}}' | Set-Content -LiteralPath (Join-Path $output 'RecorderUpdateSession.Tests.runtimeconfig.json') -Encoding utf8
& dotnet $assembly $output
if ($LASTEXITCODE -ne 0) {throw ('Recorder update session tests failed: '+$LASTEXITCODE)}
