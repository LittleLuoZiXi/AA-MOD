param([switch]$Install,[string]$AssemblyName='AzureArchive.Recorder')
$ErrorActionPreference = 'Stop'
$game = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $PSScriptRoot 'build'
$null = New-Item -ItemType Directory -Force -Path $outDir
# PowerShell 7 includes Roslyn. Compile against the game's actual .NET 6 runtime,
# not PowerShell's newer runtime. This avoids installing a system-wide SDK.
foreach ($name in @('Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll')) {
    $path = Join-Path $PSHOME $name
    if (!(Test-Path -LiteralPath $path)) { throw 'Use PowerShell 7, or dotnet build src/AzureArchive.Recorder.csproj with the .NET 6 SDK.' }
    $null = [Reflection.Assembly]::LoadFrom($path)
}
$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
$seen = @{}
foreach ($dir in @((Join-Path $game 'dotnet'),(Join-Path $game 'BepInEx/core'),(Join-Path $game 'BepInEx/interop'))) {
    foreach ($file in Get-ChildItem -LiteralPath $dir -Filter '*.dll') {
        if ($seen.ContainsKey($file.Name)) { continue }
        try {
            $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName)
            $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
            $seen[$file.Name] = $true
        } catch [BadImageFormatException] { }
    }
}
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter '*.cs') {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName), $null, $file.FullName))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithAllowUnsafe($true).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($AssemblyName,$trees,$refs,$options)
$output = Join-Path $outDir ($AssemblyName+'.dll')
$embedded=[Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
foreach($page in 1..6){
    $name=if($page -eq 1){'arona-guide.png'}else{'arona-page-{0:00}.png' -f $page}
    $art=Join-Path $PSScriptRoot ('assets/tutorial/'+$name)
    if(!(Test-Path -LiteralPath $art)){throw ('Missing tutorial illustration: '+$name)}
    $provider=[Func[IO.Stream]] { [IO.File]::OpenRead($art) }.GetNewClosure()
    $embedded.Add([Microsoft.CodeAnalysis.ResourceDescription]::new(('recorder.tutorial.arona.page-{0:00}.png' -f $page),$provider,$false))
}
$stream = [IO.File]::Create($output)
try { $result = $compilation.Emit($stream,$null,$null,$null,$embedded.ToArray()) } finally { $stream.Dispose() }
$result.Diagnostics | Where-Object { $_.Severity -in @('Error','Warning') } | ForEach-Object ToString
if (!$result.Success) { throw 'Recorder compilation failed.' }
if ($Install) {
    $version=(Get-Content (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json).version_number
    $dest = Join-Path $game "mods/AzureArchiveRecorder/$version"
    $null = New-Item -ItemType Directory -Force -Path $dest
    Copy-Item -LiteralPath $output -Destination $dest
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Destination $dest
    Write-Host "Installed: $dest"
}
Write-Host "Build succeeded: $output"
