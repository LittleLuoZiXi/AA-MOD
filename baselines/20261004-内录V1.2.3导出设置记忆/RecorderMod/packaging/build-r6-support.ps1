# Shared recorder build and inspection helpers. No install, game launch or cleanup.
Set-StrictMode -Version Latest

function New-R6BuildContext([string]$GameRoot,[string]$OutputDirectory,[string]$Variant) {
    $mod=Split-Path $PSScriptRoot -Parent
    $game=if([string]::IsNullOrWhiteSpace($GameRoot)){Split-Path $mod -Parent}else{[IO.Path]::GetFullPath($GameRoot)}
    if(!(Test-Path -LiteralPath (Join-Path $game 'AzureArchive.exe'))){throw 'Set -GameRoot to the AA directory containing AzureArchive.exe.'}
    if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path (Split-Path $mod -Parent | Split-Path -Parent) '安装交付/V1.2.3'}
    $output=[IO.Path]::GetFullPath($OutputDirectory)
    foreach($forbidden in @((Join-Path $game 'mods'),(Join-Path (Split-Path $game -Parent) '基线'),(Join-Path (Split-Path $game -Parent) '安装交付/V1.0'),(Join-Path (Split-Path $game -Parent) '安装交付/20260929'))){
        $prefix=[IO.Path]::GetFullPath($forbidden).TrimEnd('\','/')
        if($output.Equals($prefix,[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Build output must not be an installed MOD or a saved baseline.'}
    }
    $build=Join-Path $output ('构建记录/'+$Variant+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    if(Test-Path -LiteralPath $build){throw 'Build directory already exists; refusing to reuse any stage.'}
    $stage=Join-Path $build 'stage'
    $owned=Join-Path $stage 'mods/AzureArchiveRecorder'
    $version=(Get-Content -LiteralPath (Join-Path $mod 'manifest.json') -Raw | ConvertFrom-Json).version_number
    if($version -ne '1.2.3'){throw 'The V1.2.3 release requires manifest version 1.2.3.'}
    $null=New-Item -ItemType Directory -Path (Join-Path $owned $version) -Force
    [pscustomobject]@{Mod=$mod;Game=$game;Output=$output;Build=$build;Stage=$stage;Owned=$owned;Version=$version;HostHash=(Get-FileHash -LiteralPath (Join-Path $game 'AzureArchive.exe')).Hash}
}

function Import-R6Cecil {
    $null=[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'tools/obfuscar/tools/Mono.Cecil.dll'))
}

function Assert-TutorialResources([string]$AssemblyPath,[string]$ModRoot) {
    Import-R6Cecil
    $assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($AssemblyPath)
    try {
        $found=@($assembly.MainModule.Resources|Where-Object {$_.Name.StartsWith('recorder.tutorial.arona.')})
        if($found.Count -ne 6){throw "Expected six embedded tutorial PNGs in $AssemblyPath; found $($found.Count)."}
        $proof=@(foreach($page in 1..6){
            $resourceName='recorder.tutorial.arona.page-{0:00}.png' -f $page
            $fileName=if($page -eq 1){'arona-guide.png'}else{'arona-page-{0:00}.png' -f $page}
            $resource=@($found|Where-Object Name -eq $resourceName)
            if($resource.Count -ne 1 -or $resource[0] -isnot [Mono.Cecil.EmbeddedResource]){throw "Missing or duplicate embedded illustration: $resourceName"}
            $stream=$resource[0].GetResourceStream()
            try{$length=$stream.Length;$actual=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
            $expected=(Get-FileHash -LiteralPath (Join-Path $ModRoot ('assets/tutorial/'+$fileName))).Hash
            if($actual -ne $expected){throw "Embedded tutorial hash differs from its source: $resourceName"}
            [ordered]@{Resource=$resourceName;Bytes=$length;Sha256=$actual}
        })
        return $proof
    } finally {$assembly.Dispose()}
}

function Invoke-RecorderCompilation([object]$Context,[string]$AssemblyName,[string]$OutputPath,[switch]$Development) {
    foreach($name in @('Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll')){
        $path=Join-Path $PSHOME $name
        if(!(Test-Path -LiteralPath $path)){throw 'Run the build in PowerShell 7, which supplies Roslyn.'}
        $null=[Reflection.Assembly]::LoadFrom($path)
    }
    $refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new();$seen=@{}
    foreach($relative in @('dotnet','BepInEx/core','BepInEx/interop')){
        foreach($file in Get-ChildItem -LiteralPath (Join-Path $Context.Game $relative) -Filter '*.dll'){
            if($seen.ContainsKey($file.Name)){continue}
            try{$null=[Reflection.AssemblyName]::GetAssemblyName($file.FullName);$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName));$seen[$file.Name]=$true}catch [BadImageFormatException]{}
        }
    }
    $trees=[Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('[assembly: System.Reflection.AssemblyVersion("1.2.3.0")] [assembly: System.Reflection.AssemblyFileVersion("1.2.3.0")] [assembly: System.Reflection.AssemblyInformationalVersion("1.2.3")]'))
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $Context.Mod 'src') -Filter '*.cs'){
        $source=[IO.File]::ReadAllText($file.FullName);$document=$file.FullName
        if($Development -and $file.Name -eq 'Deployment.cs'){
            $original='AppContext.GetData("AzureArchive.Recorder.RuntimeRoot") as string ?? Path.Combine(Paths.GameRootPath,"RecorderMod")'
            $replacement='AppContext.GetData("AzureArchive.Recorder.RuntimeRoot") as string ?? Path.Combine(Paths.GameRootPath,"mods","AzureArchiveRecorder","runtime")'
            if([regex]::Matches($source,[regex]::Escape($original)).Count -ne 1){throw 'Review development-only Deployment.Root adaptation: expected exactly one source expression.'}
            $source=$source.Replace($original,$replacement)
            $document=Join-Path $Context.Build 'Deployment.generated.cs'
            [IO.File]::WriteAllText($document,$source,[Text.UTF8Encoding]::new($false))
        }
        $sourceText=[Microsoft.CodeAnalysis.Text.SourceText]::From($source,[Text.UTF8Encoding]::new($false))
        $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($sourceText,$null,$document))
    }
    $level=if($Development){[Microsoft.CodeAnalysis.OptimizationLevel]::Debug}else{[Microsoft.CodeAnalysis.OptimizationLevel]::Release}
    $options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithAllowUnsafe($true).WithOptimizationLevel($level).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
    $compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($AssemblyName,$trees,$refs,$options)
    $embedded=[Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
    foreach($page in 1..6){
        $fileName=if($page -eq 1){'arona-guide.png'}else{'arona-page-{0:00}.png' -f $page}
        $art=Join-Path $Context.Mod ('assets/tutorial/'+$fileName)
        if(!(Test-Path -LiteralPath $art)){throw "Missing tutorial illustration: $art"}
        $provider=[Func[IO.Stream]]{[IO.File]::OpenRead($art)}.GetNewClosure()
        $embedded.Add([Microsoft.CodeAnalysis.ResourceDescription]::new(('recorder.tutorial.arona.page-{0:00}.png' -f $page),$provider,$false))
    }
    $emitOptions=if($Development){[Microsoft.CodeAnalysis.Emit.EmitOptions]::new().WithDebugInformationFormat([Microsoft.CodeAnalysis.Emit.DebugInformationFormat]::Embedded)}else{$null}
    $null=New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath -Parent)
    $stream=[IO.File]::Create($OutputPath)
    try{$result=$compilation.Emit($stream,$null,$null,$null,$embedded.ToArray(),$emitOptions)}finally{$stream.Dispose()}
    $result.Diagnostics|Where-Object {$_.Severity -in @('Error','Warning')}|ForEach-Object {Write-Host $_.ToString()}
    if(!$result.Success){throw 'Recorder compilation failed.'}
    Assert-TutorialResources $OutputPath $Context.Mod
}

function Assert-FrozenRuntimeCurrent([string]$ModRoot,[string]$RuntimeRoot,[string]$ReportPath) {
    $python=Join-Path $ModRoot '.venv/Scripts/python.exe'
    & $python (Join-Path $PSScriptRoot 'verify-frozen-runtime.py') --runtime $RuntimeRoot --output $ReportPath
    if($LASTEXITCODE -ne 0){throw "Frozen runtime does not match the full current bridge. Rebuild it with build-enhancer.ps1, then pass -FrozenRuntimeDirectory. See $ReportPath"}
}

function Add-R6PayloadFile([string]$Source,[string]$Relative,[string]$Stage,[object]$ExpectedFiles) {
    if(@($Relative.Split('/')|Where-Object {$_ -in @('..','.')}).Count -gt 0 -or [IO.Path]::IsPathRooted($Relative)){throw "Unsafe payload path: $Relative"}
    $destination=Join-Path $Stage $Relative
    $null=New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent)
    Copy-Item -LiteralPath $Source -Destination $destination -Force
    $null=$ExpectedFiles.Add($Relative.Replace('\','/'))
}

function Publish-R6Artifact([string]$Source,[string]$Destination,[string]$BuildDirectory) {
    if(Test-Path -LiteralPath $Destination){
        $backup=Join-Path $BuildDirectory ('previous-'+[IO.Path]::GetFileName($Destination))
        [IO.File]::Replace($Source,$Destination,$backup)
    }else{[IO.File]::Move($Source,$Destination)}
}
