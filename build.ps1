#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$GameRoot,[switch]$IncludeProbe)
$ErrorActionPreference='Stop'
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw|ConvertFrom-Json
$pluginSource=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'src/Plugin.cs'))
$versionMatch=[regex]::Match($pluginSource,'internal const string Version = "([^"]+)";')
if(!$versionMatch.Success -or $versionMatch.Groups[1].Value -ne $manifest.version_number){throw 'Plugin version and manifest.json must match for update detection.'}
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
$resources=[Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
foreach($item in @(@('RevisionUpdateHelper.ps1','RevisionCompare.UpdateHelper.ps1'),@('RevisionUpdateHelper.cs.txt','RevisionCompare.UpdateHelper.cs'))) {
    $resourcePath=Join-Path $PSScriptRoot ('src/'+$item[0])
    if(!(Test-Path -LiteralPath $resourcePath)){throw "Required update helper resource missing: $resourcePath"}
    $provider=[Func[IO.Stream]]({[IO.File]::OpenRead($resourcePath)}.GetNewClosure())
    $resources.Add([Microsoft.CodeAnalysis.ResourceDescription]::new($item[1],$provider,$false))
}
$path=Join-Path $outDir 'AzureArchive.RevisionCompare.dll'
$stream=[IO.File]::Create($path)
try {$result=$compilation.Emit($stream,$null,$null,$null,$resources)} finally {$stream.Dispose()}
$result.Diagnostics | Where-Object { $_.Severity -in @('Error','Warning') } | ForEach-Object ToString
if(!$result.Success){throw 'RevisionCompare compilation failed.'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Destination $outDir -Force
"Build succeeded: $path"
