#requires -Version 7.0
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $project 'evidence/store-tests' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
foreach ($name in @('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $PSHOME $name)) | Out-Null
}
$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
$trusted = [AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES') -split [IO.Path]::PathSeparator
foreach ($file in ($trusted | Sort-Object -Unique)) {
    $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file))
}
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$parse = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp10)
foreach ($file in @((Join-Path $project 'src/RevisionStore.cs'), (Join-Path $PSScriptRoot 'RevisionStoreTests.cs'))) {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file), $parse, $file))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(('RevisionStoreTests_' + [Guid]::NewGuid().ToString('N')), $trees, $references, $options)
$stream = [IO.MemoryStream]::new()
try {
    $result = $compilation.Emit($stream)
    $result.Diagnostics | Where-Object { $_.Severity -in @('Error', 'Warning') } | ForEach-Object ToString
    if (!$result.Success) { throw 'RevisionStore test compilation failed.' }
    $assembly = [Reflection.Assembly]::Load($stream.ToArray())
} finally { $stream.Dispose() }
$json = $assembly.GetType('AzureArchive.RevisionCompare.RevisionStoreTests').GetMethod('Run').Invoke($null, @())
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'revision-store-result.json'), $json)
$report = $json | ConvertFrom-Json
$report.checks | ForEach-Object { '{0}: {1}' -f $(if ($_.passed) { 'PASS' } else { 'FAIL' }), $_.name }
if (!$report.passed) { throw ($report.checks | Where-Object { !$_.passed } | ConvertTo-Json -Depth 5) }
'Passed {0} headless revision-store checks. Native Undo/playback require separate host verification.' -f $report.count
