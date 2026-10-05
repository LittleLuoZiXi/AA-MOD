#requires -Version 7.0
param([switch]$Live)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
foreach ($name in @('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll')) { [void][Reflection.Assembly]::LoadFrom((Join-Path $PSHOME $name)) }
$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($path in (([AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES') -split [IO.Path]::PathSeparator) | Sort-Object -Unique)) { $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path)) }
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach ($relative in @('src/RevisionUpdateClient.cs', 'src/RevisionUpdateDownload.cs', 'tests/ReleaseFeedTests.cs')) {
    $path = Join-Path $project $relative
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path), $null, $path))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(('ReleaseFeed_' + [Guid]::NewGuid().ToString('N')), $trees, $references, $options)
$stream = [IO.MemoryStream]::new()
try {
    $result = $compilation.Emit($stream)
    if (!$result.Success) { throw ($result.Diagnostics -join "`n") }
    $assembly = [Reflection.Assembly]::Load($stream.ToArray())
} finally { $stream.Dispose() }
$json = $assembly.GetType('AzureArchive.RevisionCompare.ReleaseFeedTests').GetMethod('Run').Invoke($null, [object[]]@([string]$project, [bool]$Live))
$out = Join-Path $project 'evidence/release-feed'
[void][IO.Directory]::CreateDirectory($out)
[IO.File]::WriteAllText((Join-Path $out $(if ($Live) { 'live.json' } else { 'staged.json' })), $json)
$json
