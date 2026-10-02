param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [string]$OutputDirectory,
    [ValidateSet('CoreOnly','SelectedReuse','SelectThenUncheck')][string]$Mode='CoreOnly',
    [switch]$CompileOnly
)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$reference=Split-Path $mod -Parent
$allowed=[IO.Path]::GetFullPath((Split-Path $reference -Parent)).TrimEnd('\')
$target=[IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
if(!$target.StartsWith($allowed+'\V1实机验收\',[StringComparison]::OrdinalIgnoreCase) -and !$target.StartsWith($allowed+'\V1安装测试\',[StringComparison]::OrdinalIgnoreCase)){throw 'Real EXE driver is restricted to new V1 acceptance directories.'}
if(!(Test-Path -LiteralPath (Join-Path $target 'AzureArchive.exe'))){throw 'Acceptance root must already exist with genuine AA files.'}
$output=if([string]::IsNullOrWhiteSpace($OutputDirectory)){Join-Path (Join-Path $allowed 'V1安装测试') ('real-exe-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}else{[IO.Path]::GetFullPath($OutputDirectory)}
if(!$output.StartsWith($allowed+'\V1安装测试\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a new V1安装测试 evidence directory.'}
$null=New-Item -ItemType Directory -Path $output
$hostHash=(Get-FileHash -LiteralPath (Join-Path $reference 'AzureArchive.exe')).Hash
$generated=Join-Path $output 'Installer.generated.cs'
[IO.File]::WriteAllText($generated,([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Installer.cs'))).Replace('@@HOSTHASH@@',$hostHash),[Text.UTF8Encoding]::new($false))
$files=@($generated)
foreach($name in @('V1ExeDriver.cs','OnlineDlssInstall.cs','V1Hardware.cs')){$copy=Join-Path $output $name;Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $copy;$files+=$copy}
$helper=Join-Path $output 'DlssComponents.cs';Copy-Item -LiteralPath (Join-Path $mod 'src/DlssComponents.cs') -Destination $helper;$files+=$helper
$exe=Join-Path $output 'V1ExeDriver.exe';$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$uia=Join-Path (Split-Path $csc -Parent) 'WPF'
& $csc /nologo /target:exe /platform:x64 /optimize+ /main:V1ExeDriver ("/out:$exe") /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ("/r:"+(Join-Path $uia 'UIAutomationClient.dll')) ("/r:"+(Join-Path $uia 'UIAutomationTypes.dll')) ("/r:"+(Join-Path $uia 'WindowsBase.dll')) $files
if($LASTEXITCODE -ne 0){throw 'Real EXE driver compilation failed.'}
$settings=[ordered]@{Installer=[IO.Path]::GetFullPath($Installer);GameRoot=$target;Evidence=$output;AllowedRoot=$allowed;Mode=$Mode;LocalOnly='true'}
$settingsPath=Join-Path $output 'settings.json';[IO.File]::WriteAllText($settingsPath,($settings|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$inputs=[ordered]@{InstallerSha256=(Get-FileHash -LiteralPath $Installer).Hash;SourceFiles=@($files|ForEach-Object{@{Path=$_;Sha256=(Get-FileHash -LiteralPath $_).Hash}});Mode=$Mode;LocalOnly=$true;Fixture='Real production V1.2.2 installer in an isolated acceptance root; CoreOnly by default, or explicit checkbox selection with complete local component reuse; no remote download mode.'}
[IO.File]::WriteAllText((Join-Path $output 'inputs.json'),($inputs|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
if($CompileOnly){Write-Output "Compiled only: $exe $settingsPath";return}
& $exe $settingsPath 2>&1|Tee-Object -FilePath (Join-Path $output 'run.log')
if($LASTEXITCODE -ne 0){throw "Real EXE test failed; evidence: $output"}
Write-Output "Real EXE test passed: $output"
