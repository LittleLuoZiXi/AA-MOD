param(
    [Parameter(Mandatory=$true)][string]$BaseBuildProof,
    [Parameter(Mandatory=$true)][string]$VerifiedSourceDirectory
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'build-r6-support.ps1')
$base=Get-Content -LiteralPath $BaseBuildProof -Raw|ConvertFrom-Json
$mod=Split-Path $PSScriptRoot -Parent
$work=Split-Path (Split-Path $mod -Parent) -Parent
$delivery=[IO.Path]::GetFullPath((Join-Path $work '安装交付/V1.0'))
$records=Join-Path $delivery '构建记录'
if($base.Version -ne '1.0.0' -or $base.Product -ne 'AzureArchiveRecorder' -or $base.Variant -notin @('development-V1.0','protected-V1.0')){throw 'Only a verified V1.0 build can receive this UI-only revision.'}
foreach($path in @($BaseBuildProof,$base.Payload,$base.Plugin,$base.Uninstaller,$base.DlssUninstaller,$base.InstallerSource,$base.Stage)){
    if(![IO.Path]::GetFullPath($path).StartsWith($records+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'All reused input must be in this V1.0 build record tree.'}
}
if(![IO.Path]::GetFullPath($base.Installer).StartsWith($delivery+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected output location.'}
& (Join-Path $PSScriptRoot 'verify-r6-payload.ps1') -BuildProof $BaseBuildProof | Out-Host
$validation=Get-Content -LiteralPath (Join-Path (Split-Path $BaseBuildProof -Parent) 'payload-verification.json') -Raw|ConvertFrom-Json
if(!$validation.Passed -or !$validation.NoBundledDlssNative){throw 'Base payload validation failed.'}
if((Get-FileHash -LiteralPath $base.DlssUninstaller).Hash -ne $base.DlssUninstallerSha256){throw 'Base DLSS uninstaller changed.'}
# The main installer changes only its presentation; all transaction, hardware and
# downloader sources must exactly match the already accepted real-EXE evidence.
foreach($name in @('OnlineDlssInstall.cs','V1Hardware.cs')){
    if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $VerifiedSourceDirectory $name)).Hash){throw "Non-UI source changed: $name"}
}
$shared=Join-Path $mod 'src/DlssComponents.cs'
if((Get-FileHash -LiteralPath $shared).Hash -ne $base.SharedSourceSha256){throw 'Shared runtime gate changed.'}
$hostHash=(Get-FileHash -LiteralPath (Join-Path (Split-Path $mod -Parent) 'AzureArchive.exe')).Hash
$currentCore=([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Installer.cs'))).Replace('@@HOSTHASH@@',$hostHash)
if($currentCore -cne [IO.File]::ReadAllText($base.InstallerSource)){throw 'Recorder installation/uninstallation source changed.'}
$build=Join-Path $records ($base.Variant+'-ui-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
if(Test-Path -LiteralPath $build){throw 'UI build directory already exists.'}
$null=New-Item -ItemType Directory -Path $build
$generated=Join-Path $build 'Installer.generated.cs'
Copy-Item -LiteralPath $base.InstallerSource -Destination $generated
$sources=@($generated)
foreach($name in @('OnlineDlssInstall.cs','V1Hardware.cs','V1Installer.cs','V1AssemblyInfo.cs')){
    $copy=Join-Path $build $name;Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $copy;$sources+=$copy
}
$copy=Join-Path $build 'DlssComponents.cs';Copy-Item -LiteralPath $shared -Destination $copy;$sources+=$copy
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Net.Http.dll')
$protected=$base.Variant -eq 'protected-V1.0'
$pending=Join-Path $build ([IO.Path]::GetFileName($base.Installer))
$core=if($protected){Join-Path $build 'AARecorder.Install.Core.exe'}else{$pending}
$optimize=if($protected){'/optimize+'}else{'/optimize-'}
& $csc /nologo /target:winexe /platform:x64 $optimize /debug- /main:V1Program $references "/resource:$($base.Payload),installer.payload" "/resource:$($base.DlssUninstaller),installer.dlss-uninstaller" "/out:$core" $sources
if($LASTEXITCODE -ne 0){throw 'UI revision compilation failed.'}
if($protected){
    & (Join-Path $PSScriptRoot 'protect-executable.ps1') -InputPath $core -OutputPath $pending -Role installer -BuildDirectory (Join-Path $build 'protection/installer')
    $inspection=Join-Path (Split-Path $BaseBuildProof -Parent) 'ProtectionTests.exe'
    & $inspection $pending $base.Uninstaller *> (Join-Path $build 'protection-tests.log')
    if($LASTEXITCODE -ne 0){throw 'UI revision protection check failed.'}
}
Publish-R6Artifact $pending $base.Installer $build
$next=[ordered]@{}
foreach($property in $base.PSObject.Properties){$next[$property.Name]=$property.Value}
$next.InstallerSha256=(Get-FileHash -LiteralPath $base.Installer).Hash
$next.BuildDirectory=$build;$next.InstallerSource=$generated;$next.BuiltUtc=[DateTime]::UtcNow.ToString('o')
$next.ReusedPayloadFrom=[IO.Path]::GetFullPath($BaseBuildProof)
$next.ReusedPayloadSha256=$validation.PayloadSha256
$next.UiRevision='Refresh installed component status; show completion text; suppress stale queued progress after completion.'
$next.SourceFiles=@($sources|ForEach-Object{[ordered]@{Path=$_;Sha256=(Get-FileHash -LiteralPath $_).Hash}})
$proof=Join-Path $build 'build-proof.json'
[IO.File]::WriteAllText($proof,($next|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path (Split-Path $BaseBuildProof -Parent) 'frozen-runtime-source-check.json') -Destination (Join-Path $build 'frozen-runtime-source-check.json')
& (Join-Path $PSScriptRoot 'verify-r6-payload.ps1') -BuildProof $proof | Out-Host
[pscustomobject]@{BuildProof=$proof;Installer=$base.Installer;Sha256=$next.InstallerSha256;PayloadUnchanged=$true;PluginSha256=$base.PluginSha256}
