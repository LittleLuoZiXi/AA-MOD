param(
    [string]$GameRoot,
    [ValidateSet('Core','Network','Ui','Artifacts','All')][string]$Suite='Core',
    [string]$OutputDirectory,
    [string]$DlssFixtureStage,
    [string]$TestFilter,
    [switch]$CompileOnly
)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$game=if([string]::IsNullOrWhiteSpace($GameRoot)){Split-Path $mod -Parent}else{[IO.Path]::GetFullPath($GameRoot)}
if(!(Test-Path -LiteralPath (Join-Path $game 'AzureArchive.exe'))){throw 'GameRoot must contain the genuine reference AzureArchive.exe.'}
$allowed=[IO.Path]::GetFullPath((Join-Path (Split-Path $game -Parent) 'V1安装测试')).TrimEnd('\','/')
$output=if([string]::IsNullOrWhiteSpace($OutputDirectory)){Join-Path $allowed ('test-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}else{[IO.Path]::GetFullPath($OutputDirectory)}
if(!$output.StartsWith($allowed+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'V1 test evidence must remain under the isolated V1安装测试 directory.'}
if(Test-Path -LiteralPath $output){throw 'Choose a new directory; earlier evidence is never overwritten.'}
$null=New-Item -ItemType Directory -Path $output -Force
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$generated=Join-Path $output 'Installer.test-generated.cs'
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Installer.cs'))
$hostHash=(Get-FileHash -LiteralPath (Join-Path $game 'AzureArchive.exe')).Hash
if(!$source.Contains('@@HOSTHASH@@')){throw 'Installer host hash placeholder changed; review test generation.'}
[IO.File]::WriteAllText($generated,$source.Replace('@@HOSTHASH@@',$hostHash),[Text.UTF8Encoding]::new($false))
$helper=Join-Path $output 'DlssComponents.test-source.cs'
Copy-Item -LiteralPath (Join-Path $mod 'src/DlssComponents.cs') -Destination $helper
$tests=Join-Path $output 'V1InstallerTests.test-source.cs'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'V1InstallerTests.cs') -Destination $tests
$extra=@('OnlineDlssInstall.cs','V1Hardware.cs','V1Installer.cs'|ForEach-Object{
    $copy=Join-Path $output $_.Replace('.cs','.test-source.cs')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $_) -Destination $copy
    $copy
})
$stubSource=Join-Path $output 'EncoderProcessStub.cs'
$stub=@'
using System;
using System.IO;
using System.Reflection;
public static class EncoderProcessStub {
    public static int Main(string[] args) {
        if(Array.IndexOf(args,"-version")>=0)Console.WriteLine((Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location).Equals("ffprobe",StringComparison.OrdinalIgnoreCase)?"ffprobe":"ffmpeg")+" version V1-TEST-PROCESS-STUB");
        return 0;
    }
}
'@
[IO.File]::WriteAllText($stubSource,$stub,[Text.UTF8Encoding]::new($false))
$encoder=Join-Path $output 'ffmpeg.exe'
& $csc /nologo /target:exe /platform:x64 ("/out:$encoder") $stubSource
if($LASTEXITCODE -ne 0){throw 'Encoder process stub compilation failed.'}
$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$executable=Join-Path $output 'V1InstallerTests.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ /main:V1InstallerTests ("/out:$executable") $references $generated $helper $tests $extra 2>&1|Tee-Object -FilePath (Join-Path $output 'compile.log')
if($LASTEXITCODE -ne 0){throw 'V1 test compilation failed.'}
$sources=@($generated,$helper,$tests)+$extra
$inputs=[ordered]@{Suite=$Suite;ReferenceGameRoot=$game;ReferenceHostSha256=$hostHash;SourceFiles=@($sources|ForEach-Object{@{Path=$_;Sha256=(Get-FileHash -LiteralPath $_).Hash}});FixtureKind='Synthetic recorder payload/encoder; real loopback HTTP; source-level WinForms with test services; DLSS commit uses genuine R6 staged bytes. Not video/GPU validation.';CompiledUtc=[DateTime]::UtcNow.ToString('o')}
[IO.File]::WriteAllText((Join-Path $output 'inputs.json'),($inputs|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
if($CompileOnly){Write-Output $output;return}
$stage=if([String]::IsNullOrWhiteSpace($DlssFixtureStage)){Join-Path $PSScriptRoot 'build-dlss-supplement/stage'}else{[IO.Path]::GetFullPath($DlssFixtureStage)}
$settings=[ordered]@{GameRoot=$game;Workspace=(Join-Path $output 'fixtures');EncoderStub=$encoder;Suite=$Suite;DlssFixtureStage=$stage;TestFilter=$TestFilter}
$settingsPath=Join-Path $output 'settings.json'
[IO.File]::WriteAllText($settingsPath,($settings|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
& $executable $settingsPath 2>&1|Tee-Object -FilePath (Join-Path $output 'run.log')
if($LASTEXITCODE -ne 0){throw "V1 tests failed; preserve evidence at $output"}
Write-Output "V1 tests passed: $output"
