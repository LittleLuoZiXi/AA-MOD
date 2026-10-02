#requires -Version 7.2
# Recompile the V1.1 installer UI only. The exact three approved text edits below
# are the only permitted UI change. No download, game launch or payload rebuild.
param(
    [Parameter(Mandatory=$true)][string]$BaseBuildProof,
    [Parameter(Mandatory=$true)][string]$VerifiedSourceDirectory
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'build-r6-support.ps1')
$BaseBuildProof=[IO.Path]::GetFullPath($BaseBuildProof)
$VerifiedSourceDirectory=[IO.Path]::GetFullPath($VerifiedSourceDirectory)
function Assert-V11NoLinks([string]$Path) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while($cursor){
        $item=$null
        try{$item=Get-Item -LiteralPath $cursor -Force -ErrorAction Stop}catch [Management.Automation.ItemNotFoundException]{}
        if($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw "Refusing a link/junction: $cursor"}
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
}
Assert-V11NoLinks $BaseBuildProof
Assert-V11NoLinks $VerifiedSourceDirectory
$base=Get-Content -LiteralPath $BaseBuildProof -Raw|ConvertFrom-Json
$mod=Split-Path $PSScriptRoot -Parent
$work=Split-Path (Split-Path $mod -Parent) -Parent
$delivery=[IO.Path]::GetFullPath((Join-Path $work '安装交付/V1.1'))
$records=Join-Path $delivery '构建记录'
if($base.Version -ne '1.1.0' -or $base.Product -ne 'AzureArchiveRecorder' -or $base.Variant -notin @('development-V1.1','protected-V1.1')){throw 'Only a verified V1.1 build can receive this UI-only revision.'}
foreach($path in @($BaseBuildProof,$base.BuildDirectory,$base.Payload,$base.Plugin,$base.Uninstaller,$base.DlssUninstaller,$base.InstallerSource,$base.Stage)){
    if(![IO.Path]::GetFullPath($path).StartsWith($records+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'All reused input must be in this V1.1 build record tree.'}
    Assert-V11NoLinks $path
}
if([IO.Path]::GetFullPath($base.BuildDirectory) -ne (Split-Path $BaseBuildProof -Parent)){throw 'Base proof does not belong to its named V1.1 build directory.'}
$expectedName=if($base.Variant -eq 'protected-V1.1'){'AzureArchive内录V1.1-加壳安装.exe'}else{'AzureArchive内录V1.1-未加壳安装.exe'}
if([IO.Path]::GetFullPath($base.Installer) -ne (Join-Path $delivery $expectedName)){throw 'Unexpected output location or installer name.'}
Assert-V11NoLinks $base.Installer
$testRoot=[IO.Path]::GetFullPath((Join-Path $work 'V1.1安装测试'))
if(!$VerifiedSourceDirectory.StartsWith($testRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use the accepted V1.1 local UI test source directory.'}
$inputsPath=Join-Path $VerifiedSourceDirectory 'inputs.json'
$resultsPath=Join-Path $VerifiedSourceDirectory 'fixtures/results.json'
Assert-V11NoLinks $inputsPath;Assert-V11NoLinks $resultsPath
$evidence=Get-Content -LiteralPath $inputsPath -Raw|ConvertFrom-Json
$testResult=Get-Content -LiteralPath $resultsPath -Raw|ConvertFrom-Json
if($evidence.Suite -ne 'Ui' -or $testResult.passed -isnot [bool] -or !$testResult.passed -or @($testResult.cases).Count -eq 0 -or @($testResult.cases|Where-Object {$_.passed -isnot [bool] -or !$_.passed}).Count -gt 0){throw 'The selected UI source evidence has not passed.'}
function Get-V11VerifiedSource([string]$Name) {
    $aliases=if($Name -eq 'Installer.generated.cs'){@('Installer.test-generated.cs','Installer.generated.cs')}else{@($Name.Replace('.cs','.test-source.cs'),$Name)}
    $candidates=@($aliases|ForEach-Object {Join-Path $VerifiedSourceDirectory $_}|Where-Object {Test-Path -LiteralPath $_ -PathType Leaf})
    if($candidates.Count -ne 1){throw "Missing/ambiguous accepted source: $Name"}
    $path=[IO.Path]::GetFullPath($candidates[0]);Assert-V11NoLinks $path
    $entry=@($evidence.SourceFiles|Where-Object {[IO.Path]::GetFullPath($_.Path) -eq $path})
    if($entry.Count -ne 1 -or $entry[0].Sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $path).Hash -ne $entry[0].Sha256){throw "Accepted source evidence hash mismatch: $Name"}
    return $path
}
& (Join-Path $PSScriptRoot 'verify-r6-payload.ps1') -BuildProof $BaseBuildProof | Out-Host
$validation=Get-Content -LiteralPath (Join-Path (Split-Path $BaseBuildProof -Parent) 'payload-verification.json') -Raw|ConvertFrom-Json
if(!$validation.Passed -or !$validation.NoBundledDlssNative){throw 'Base payload validation failed.'}
if((Get-FileHash -LiteralPath $base.DlssUninstaller).Hash -ne $base.DlssUninstallerSha256){throw 'Base DLSS uninstaller changed.'}
# The main installer changes only its presentation; all transaction, hardware and
# downloader sources must exactly match the already accepted real-EXE evidence.
foreach($name in @('OnlineDlssInstall.cs','V1Hardware.cs')){
    $verified=Get-V11VerifiedSource $name
    if([IO.File]::ReadAllText((Join-Path $PSScriptRoot $name)) -cne [IO.File]::ReadAllText($verified)){throw "Non-UI source changed: $name"}
}
$shared=Join-Path $mod 'src/DlssComponents.cs'
if((Get-FileHash -LiteralPath $shared).Hash -ne $base.SharedSourceSha256){throw 'Shared runtime gate changed.'}
if((Get-FileHash -LiteralPath $shared).Hash -ne (Get-FileHash -LiteralPath (Get-V11VerifiedSource 'DlssComponents.cs')).Hash){throw 'Shared runtime gate differs from accepted UI evidence.'}
$hostHash=(Get-FileHash -LiteralPath (Join-Path (Split-Path $mod -Parent) 'AzureArchive.exe')).Hash
if($hostHash -ne $evidence.ReferenceHostSha256){throw 'AA host differs from the accepted source test identity.'}
$currentCore=([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Installer.cs'))).Replace('@@HOSTHASH@@',$hostHash)
if($currentCore -cne [IO.File]::ReadAllText($base.InstallerSource)){throw 'Recorder installation/uninstallation source changed.'}
if($currentCore -cne [IO.File]::ReadAllText((Get-V11VerifiedSource 'Installer.generated.cs'))){throw 'Installer.generated differs from accepted source evidence.'}
$verifiedUi=Get-V11VerifiedSource 'V1Installer.cs'
$uiBefore=[IO.File]::ReadAllText($verifiedUi)
$oldPrompt='显卡支持。可取消勾选；保持勾选则在内录完成后安装 DLSS。'
$newPrompt='显卡支持。可在开始安装前取消勾选；保持勾选则在内录完成后安装 DLSS。'
$downloadAnchor='        working=true;Stage=V1SetupStage.DlssDownloading;DownloadComplete=false;start.Enabled=false;next.Enabled=false;Inputs(false);'
$lockedPrompt='        dlssReason.Text="本次已选择 DLSS，选择已锁定。下载过程中可使用下方的暂停或取消按钮。";'
$oldUnchecked='RefreshDlssPart();if(report!=null)ShowParts();return;'
$newUnchecked='RefreshDlssPart();if(report!=null){ShowParts();status.ForeColor=Color.FromArgb(52,78,91);status.Text="本次仅安装内录 MOD；点击开始安装内录。";}return;'
if([regex]::Matches($uiBefore,[regex]::Escape($oldPrompt)).Count -ne 1 -or [regex]::Matches($uiBefore,[regex]::Escape($downloadAnchor)).Count -ne 1 -or [regex]::Matches($uiBefore,[regex]::Escape($oldUnchecked)).Count -ne 1 -or $uiBefore.Contains($lockedPrompt)){throw 'Use the V1.1 source evidence before the exact three approved text edits.'}
$newline=if($uiBefore.Contains("`r`n")){"`r`n"}else{"`n"}
$expectedUi=$uiBefore.Replace($oldPrompt,$newPrompt).Replace($downloadAnchor,$downloadAnchor+$newline+$lockedPrompt).Replace($oldUnchecked,$newUnchecked)
if([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'V1Installer.cs')) -cne $expectedUi){throw 'UI changes extend beyond the three approved DLSS selection messages.'}
$payloadOrigin=$BaseBuildProof
if($base.PSObject.Properties.Name -contains 'ReusedPayloadFrom' -and ![string]::IsNullOrWhiteSpace([string]$base.ReusedPayloadFrom)){
    $payloadOrigin=[IO.Path]::GetFullPath($base.ReusedPayloadFrom)
    if(!$payloadOrigin.StartsWith($records+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Payload origin is outside the V1.1 build record tree.'}
    Assert-V11NoLinks $payloadOrigin
    $origin=Get-Content -LiteralPath $payloadOrigin -Raw|ConvertFrom-Json
    if($origin.Product -ne $base.Product -or $origin.Version -ne '1.1.0' -or $origin.Variant -ne $base.Variant -or
        [IO.Path]::GetFullPath($origin.Payload) -ne [IO.Path]::GetFullPath($base.Payload) -or
        [IO.Path]::GetFullPath($origin.Stage) -ne [IO.Path]::GetFullPath($base.Stage) -or
        $origin.PluginSha256 -ne $base.PluginSha256 -or $origin.UninstallerSha256 -ne $base.UninstallerSha256 -or
        $origin.DlssUninstallerSha256 -ne $base.DlssUninstallerSha256 -or $origin.SharedSourceSha256 -ne $base.SharedSourceSha256){throw 'Original payload proof is not the unchanged V1.1 payload being reused.'}
    if($base.ReusedPayloadSha256 -ne $validation.PayloadSha256){throw 'Payload digest differs from its accepted UI ancestor.'}
}
$frozenSourceHashes=@{}
foreach($name in @('OnlineDlssInstall.cs','V1Hardware.cs','V1Installer.cs','V1AssemblyInfo.cs')){$frozenSourceHashes[$name]=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $name)).Hash}
$build=Join-Path $records ($base.Variant+'-ui-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
if(Test-Path -LiteralPath $build){throw 'UI build directory already exists.'}
$null=New-Item -ItemType Directory -Path $build
$generated=Join-Path $build 'Installer.generated.cs'
Copy-Item -LiteralPath $base.InstallerSource -Destination $generated
$sources=@($generated)
foreach($name in @('OnlineDlssInstall.cs','V1Hardware.cs','V1Installer.cs','V1AssemblyInfo.cs')){
    $copy=Join-Path $build $name;Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $copy;$sources+=$copy
    if((Get-FileHash -LiteralPath $copy).Hash -ne $frozenSourceHashes[$name]){throw "Source changed while staging: $name"}
}
$copy=Join-Path $build 'DlssComponents.cs';Copy-Item -LiteralPath $shared -Destination $copy;$sources+=$copy
if((Get-FileHash -LiteralPath $copy).Hash -ne $base.SharedSourceSha256 -or [IO.File]::ReadAllText($generated) -cne $currentCore){throw 'Core/shared source changed while staging.'}
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Net.Http.dll')
$protected=$base.Variant -eq 'protected-V1.1'
$pending=Join-Path $build ([IO.Path]::GetFileName($base.Installer))
$core=if($protected){Join-Path $build 'AARecorder.Install.Core.exe'}else{$pending}
$optimize=if($protected){'/optimize+'}else{'/optimize-'}
& $csc /nologo /target:winexe /platform:x64 $optimize /debug- /main:V1Program $references "/resource:$($base.Payload),installer.payload" "/resource:$($base.DlssUninstaller),installer.dlss-uninstaller" "/out:$core" $sources
if($LASTEXITCODE -ne 0){throw 'UI revision compilation failed.'}
if($protected){
    & (Join-Path $PSScriptRoot 'protect-executable.ps1') -InputPath $core -OutputPath $pending -Role installer -BuildDirectory (Join-Path $build 'protection/installer')
    $inspection=Join-Path $build 'ProtectionTests.exe'
    & $csc /nologo /target:exe /platform:x64 /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/out:$inspection" (Join-Path $PSScriptRoot 'ProtectionTests.cs')
    if($LASTEXITCODE -ne 0){throw 'Protection inspection compilation failed.'}
    & $inspection $pending $base.Uninstaller *> (Join-Path $build 'protection-tests.log')
    if($LASTEXITCODE -ne 0){throw 'UI revision protection check failed.'}
    & $inspection $pending $base.DlssUninstaller *> (Join-Path $build 'dlss-uninstaller-protection-tests.log')
    if($LASTEXITCODE -ne 0){throw 'Reused DLSS uninstaller protection check failed.'}
}
foreach($pair in @(@('Installer','InstallerSha256'),@('Plugin','PluginSha256'),@('Uninstaller','UninstallerSha256'),@('DlssUninstaller','DlssUninstallerSha256'))){
    if((Get-FileHash -LiteralPath $base.($pair[0])).Hash -ne $base.($pair[1])){throw "Reused artifact changed during compilation: $($pair[0])"}
}
if((Get-FileHash -LiteralPath $base.Payload).Hash -ne $validation.PayloadSha256){throw 'Reused payload changed during compilation.'}
Publish-R6Artifact $pending $base.Installer $build
$next=[ordered]@{}
foreach($property in $base.PSObject.Properties){$next[$property.Name]=$property.Value}
$next.InstallerSha256=(Get-FileHash -LiteralPath $base.Installer).Hash
$next.BuildDirectory=$build;$next.InstallerSource=$generated;$next.BuiltUtc=[DateTime]::UtcNow.ToString('o')
$next.ReusedPayloadFrom=$payloadOrigin
$next.ReusedUiFrom=[IO.Path]::GetFullPath($BaseBuildProof)
$next.ReusedPayloadSha256=$validation.PayloadSha256
$next.UiRevision='V1.1 text-only correction: deselection is available before installation; downloading displays the locked-choice and pause/cancel guidance; deselecting DLSS also resets SetupStatus to ordinary recorder installation. No payload, core, hardware or downloader change.'
$next.PreviousInstallerBackup=Join-Path $build ('previous-'+[IO.Path]::GetFileName($base.Installer))
$next.PreviousInstallerSha256=$base.InstallerSha256
$next.UiSourceEvidence=[ordered]@{Directory=$VerifiedSourceDirectory;InputsSha256=(Get-FileHash -LiteralPath $inputsPath).Hash;ResultsSha256=(Get-FileHash -LiteralPath $resultsPath).Hash;PreviousUiSourceSha256=(Get-FileHash -LiteralPath $verifiedUi).Hash;AllowedEdits=3;NoDownloaderOrHardwareChange=$true}
$next.SourceFiles=@($sources|ForEach-Object{[ordered]@{Path=$_;Sha256=(Get-FileHash -LiteralPath $_).Hash}})
$proof=Join-Path $build 'build-proof.json'
[IO.File]::WriteAllText($proof,($next|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path (Split-Path $BaseBuildProof -Parent) 'frozen-runtime-source-check.json') -Destination (Join-Path $build 'frozen-runtime-source-check.json')
& (Join-Path $PSScriptRoot 'verify-r6-payload.ps1') -BuildProof $proof | Out-Host
[pscustomobject]@{BuildProof=$proof;Installer=$base.Installer;Sha256=$next.InstallerSha256;PayloadUnchanged=$true;PluginSha256=$base.PluginSha256}
