#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$GameRoot,[switch]$IncludeProbe)
$ErrorActionPreference='Stop'
$outDir=Join-Path $PSScriptRoot 'build'
[IO.Directory]::CreateDirectory($outDir)|Out-Null
foreach($name in @('Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $PSHOME $name))|Out-Null
}
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
$seen=@{}
foreach($part in @('dotnet','BepInEx/core','BepInEx/interop')) {
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $GameRoot $part) -Filter '*.dll') {
        if($seen.ContainsKey($file.Name)){continue}
        try {
            [Reflection.AssemblyName]::GetAssemblyName($file.FullName)|Out-Null
            $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
            $seen[$file.Name]=$true
        } catch [BadImageFormatException] {}
    }
}
$trees=[Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs') {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName),$null,$file.FullName))
}
if($IncludeProbe) {
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter '*Probe.cs') {
        $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName),$null,$file.FullName))
    }
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithAllowUnsafe($true).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('AzureArchive.RevisionCompare',$trees,$refs,$options)
$path=Join-Path $outDir 'AzureArchive.RevisionCompare.dll'
$stream=[IO.File]::Create($path)
try {$result=$compilation.Emit($stream)} finally {$stream.Dispose()}
$result.Diagnostics | Where-Object { $_.Severity -in @('Error','Warning') } | ForEach-Object ToString
if(!$result.Success){throw 'RevisionCompare compilation failed.'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Destination $outDir -Force
"Build succeeded: $path"
