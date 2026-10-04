param(
    [Parameter(Mandatory=$true)][string]$DevelopmentProof,
    [Parameter(Mandatory=$true)][string]$ProtectedProof,
    [ValidateSet('Metadata','Delivered','Ffmpeg','All')][string]$Suite='All',
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent;$game=Split-Path $mod -Parent;$allowed=Join-Path (Split-Path $game -Parent) 'V1安装测试'
$output=if([string]::IsNullOrWhiteSpace($OutputDirectory)){Join-Path $allowed ('delivered-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}else{[IO.Path]::GetFullPath($OutputDirectory)}
if(!$output.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a new V1安装测试 evidence directory.'}
$development=Get-Content -LiteralPath $DevelopmentProof -Raw|ConvertFrom-Json;$protected=Get-Content -LiteralPath $ProtectedProof -Raw|ConvertFrom-Json
if($development.InstallerSha256 -ne (Get-FileHash -LiteralPath $development.Installer).Hash -or $protected.InstallerSha256 -ne (Get-FileHash -LiteralPath $protected.Installer).Hash){throw 'Installer differs from supplied final build proof.'}
$null=New-Item -ItemType Directory -Path $output
$generated=Join-Path $output 'Installer.generated.cs';$hostHash=(Get-FileHash -LiteralPath (Join-Path $game 'AzureArchive.exe')).Hash
[IO.File]::WriteAllText($generated,([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Installer.cs'))).Replace('@@HOSTHASH@@',$hostHash),[Text.UTF8Encoding]::new($false))
$helper=Join-Path $output 'DlssComponents.cs';Copy-Item -LiteralPath (Join-Path $mod 'src/DlssComponents.cs') -Destination $helper
$online=Join-Path $output 'OnlineDlssInstall.cs';Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'OnlineDlssInstall.cs') -Destination $online
$tests=Join-Path $output 'V1DeliveredTests.cs';Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'V1DeliveredTests.cs') -Destination $tests
$protection=Join-Path $output 'ProtectionTests.cs';Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ProtectionTests.cs') -Destination $protection
$cecil=Join-Path $output 'Mono.Cecil.dll';Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tools/obfuscar/tools/Mono.Cecil.dll') -Destination $cecil
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe';$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$exe=Join-Path $output 'V1DeliveredTests.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ /main:V1DeliveredTests ("/out:$exe") ("/r:$cecil") $references $generated $helper $online $tests $protection
if($LASTEXITCODE -ne 0){throw 'Delivered test compilation failed.'}
$fixtureRoot=Join-Path $allowed ('p-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$settings=[ordered]@{GameRoot=$game;Workspace=$fixtureRoot;DevelopmentProof=[IO.Path]::GetFullPath($DevelopmentProof);ProtectedProof=[IO.Path]::GetFullPath($ProtectedProof);DlssFixtureStage=(Join-Path $PSScriptRoot 'build-dlss-supplement/stage');Suite=$Suite}
$settingsPath=Join-Path $output 'settings.json';[IO.File]::WriteAllText($settingsPath,($settings|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$inputs=[ordered]@{Suite=$Suite;DevelopmentInstaller=$development.Installer;DevelopmentSha256=$development.InstallerSha256;ProtectedInstaller=$protected.Installer;ProtectedSha256=$protected.InstallerSha256;Sources=@($generated,$helper,$online,$tests,$protection|ForEach-Object{@{Path=$_;Sha256=(Get-FileHash -LiteralPath $_).Hash}})}
[IO.File]::WriteAllText((Join-Path $output 'inputs.json'),($inputs|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
if($Suite -in @('Metadata','Delivered','All')){
    & $exe $settingsPath 2>&1|Tee-Object -FilePath (Join-Path $output 'delivered-run.log')
    if($LASTEXITCODE -ne 0){throw "Delivered tests failed: $output"}
}
if($Suite -in @('Ffmpeg','All')){
    # Reuse only the encoder semantic tests. Copy and redirect their isolated test
    # root; the R6 sources, receipts, baselines and original test evidence stay intact.
    $ffmpegSource=Join-Path $output 'FfmpegTests.v1-fixture.cs';$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FfmpegTests.cs'))
    if(!$source.Contains('"R6安装测试"')){throw 'Review FFmpeg test fixture-root migration.'}
    $fixtureName='V1安装测试\f-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    [IO.File]::WriteAllText($ffmpegSource,$source.Replace('"R6安装测试"','@"'+$fixtureName+'"'),[Text.UTF8Encoding]::new($false))
    $ffmpegTest=Join-Path $output 'FfmpegTests.exe'
    & $csc /nologo /target:exe /platform:x64 /optimize+ /main:FfmpegTests ("/out:$ffmpegTest") $references $generated $helper $ffmpegSource
    if($LASTEXITCODE -ne 0){throw 'Reused FFmpeg tests compilation failed.'}
    & $ffmpegTest $game $protected.Payload 2>&1|Tee-Object -FilePath (Join-Path $output 'ffmpeg-run.log')
    if($LASTEXITCODE -ne 0){throw "FFmpeg tests failed: $output"}
}
[IO.File]::WriteAllText((Join-Path $output 'PASS.txt'),('PASS '+$Suite+' '+[DateTime]::UtcNow.ToString('o')))
Write-Output "Final V1 package tests passed: $output"
