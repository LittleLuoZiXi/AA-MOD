param(
    [string]$GameRoot,
    [ValidateSet('Core','Recorder','R6','Dependency','Safety','Ffmpeg','Variant','Protection','All')][string]$Suite='Core',
    [string]$DevelopmentInstaller,
    [string]$ProtectedInstaller,
    [string]$DlssInstaller,
    [string]$DevelopmentPayload,
    [string]$ProtectedPayload,
    [string]$DlssPayload,
    [string]$OutputDirectory,
    [switch]$CompileOnly
)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$game=if([string]::IsNullOrWhiteSpace($GameRoot)){Split-Path $mod -Parent}else{[IO.Path]::GetFullPath($GameRoot)}
if(!(Test-Path -LiteralPath (Join-Path $game 'AzureArchive.exe'))){throw 'GameRoot must contain the genuine reference AzureArchive.exe.'}
$allowedRoot=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')+'\'
$output=if([string]::IsNullOrWhiteSpace($OutputDirectory)){Join-Path $PSScriptRoot ('r6-tests-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))}else{[IO.Path]::GetFullPath($OutputDirectory)}
if(!$output.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Test records/fixtures must remain under this packaging directory.'}
if(Test-Path -LiteralPath $output){throw 'Choose a new output directory; this script never overwrites previous test evidence.'}
$null=New-Item -ItemType Directory -Path $output
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$cecil=Join-Path $PSScriptRoot 'tools/obfuscar/tools/Mono.Cecil.dll'
if(!(Test-Path -LiteralPath $cecil)){throw 'Mono.Cecil test dependency is unavailable.'}
Copy-Item -LiteralPath $cecil -Destination (Join-Path $output 'Mono.Cecil.dll')
$generated=Join-Path $output 'Installer.test-generated.cs'
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Installer.cs'))
$hostHash=(Get-FileHash -LiteralPath (Join-Path $game 'AzureArchive.exe')).Hash
if(!$source.Contains('@@HOSTHASH@@')){throw 'Installer host hash placeholder has changed; review test compilation.'}
[IO.File]::WriteAllText($generated,$source.Replace('@@HOSTHASH@@',$hostHash),[Text.UTF8Encoding]::new($false))
$helper=Join-Path $output 'DlssComponents.test-source.cs'
Copy-Item -LiteralPath (Join-Path $mod 'src/DlssComponents.cs') -Destination $helper
$supplement=Join-Path $output 'DlssSupplementInstaller.test-source.cs'
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'DlssSupplementInstaller.cs')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DlssSupplementInstaller.cs') -Destination $supplement}
$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$testClasses=[ordered]@{R6InstallerTests='R6InstallerTests.cs';DependencyTests='DependencyTests.cs';InstallerSafetyTests='SafetyTests.cs';FfmpegTests='FfmpegTests.cs';VariantSwitchTests='VariantSwitchTests.cs';ProtectionTests='ProtectionTests.cs'}
foreach($entry in $testClasses.GetEnumerator()) {
    $testCopy=Join-Path $output $entry.Value
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $entry.Value) -Destination $testCopy
    $arguments=@('/nologo','/target:exe','/platform:x64','/optimize+',('/main:'+$entry.Key),('/out:'+(Join-Path $output ($entry.Key+'.exe'))))+$references
    if($entry.Key -eq 'ProtectionTests'){$arguments+=@($testCopy)}else{$arguments+=@($generated,$helper,$testCopy)}
    if($entry.Key -eq 'R6InstallerTests'){$arguments+=('/r:'+$cecil);if(Test-Path -LiteralPath $supplement){$arguments+=$supplement}}
    & $csc @arguments 2>&1 | Tee-Object -FilePath (Join-Path $output ($entry.Key+'-compile.log'))
    if($LASTEXITCODE -ne 0){throw 'Test compilation failed: '+$entry.Key}
}
Write-Output ('Compiled test executables: '+$output)
if($CompileOnly){return}
$fixtureRoot=Join-Path (Join-Path (Split-Path $game -Parent) 'R6安装测试') ((Get-Date -Format 'HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6))
$settings=[ordered]@{GameRoot=$game;Workspace=$fixtureRoot;Phase=if($Suite -eq 'Core'){'Core'}elseif($Suite -eq 'Recorder'){'Recorder'}else{'Final'}}
foreach($name in @('DevelopmentInstaller','ProtectedInstaller','DlssInstaller','DevelopmentPayload','ProtectedPayload','DlssPayload')) {
    $value=Get-Variable -Name $name -ValueOnly
    if(![string]::IsNullOrWhiteSpace($value)){$settings[$name]=[IO.Path]::GetFullPath($value)}
}
$needed=switch($Suite) {
    'Core' {@()}
    'Recorder' {@('DevelopmentInstaller','ProtectedInstaller','DevelopmentPayload','ProtectedPayload')}
    'R6' {@('DevelopmentInstaller','ProtectedInstaller','DlssInstaller','DevelopmentPayload','ProtectedPayload','DlssPayload')}
    'Dependency' {@('ProtectedInstaller','ProtectedPayload')}
    'Safety' {@('ProtectedInstaller','ProtectedPayload')}
    'Ffmpeg' {@('ProtectedPayload')}
    'Variant' {@('DevelopmentPayload','ProtectedPayload')}
    'Protection' {@('ProtectedInstaller','ProtectedPayload')}
    'All' {@('DevelopmentInstaller','ProtectedInstaller','DlssInstaller','DevelopmentPayload','ProtectedPayload','DlssPayload')}
}
foreach($name in $needed){if(!$settings.Contains($name) -or !(Test-Path -LiteralPath $settings[$name] -PathType Leaf)){throw ('Supply existing -'+$name+' for '+$Suite)}}
$inputRecord=[ordered]@{Timestamp=(Get-Date).ToString('o');Suite=$Suite;ReferenceHostSha256=$hostHash;Files=@()}
foreach($name in $settings.Keys){if($name -match '(Installer|Payload)$'){$file=Get-Item -LiteralPath $settings[$name];$inputRecord.Files+=@{Role=$name;Path=$file.FullName;Bytes=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}}}
$inputRecord|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'inputs.json') -Encoding utf8
$settings|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'test-settings.json') -Encoding utf8
function Invoke-Test([string]$Class,[string[]]$TestArguments) {
    Write-Output ('Running '+$Class+' only against disposable fixtures.')
    & (Join-Path $output ($Class+'.exe')) @TestArguments 2>&1 | Tee-Object -FilePath (Join-Path $output ($Class+'-run.log'))
    if($LASTEXITCODE -ne 0){throw ($Class+' failed; see '+$output)}
}
if($Suite -in @('Core','Recorder','R6','All')){Invoke-Test 'R6InstallerTests' @((Join-Path $output 'test-settings.json'))}
if($Suite -in @('Dependency','All')){Invoke-Test 'DependencyTests' @($game,$ProtectedInstaller,$ProtectedPayload)}
if($Suite -in @('Safety','All')){Invoke-Test 'InstallerSafetyTests' @($game,$ProtectedPayload,$ProtectedInstaller)}
if($Suite -in @('Ffmpeg','All')){Invoke-Test 'FfmpegTests' @($game,$ProtectedPayload)}
if($Suite -in @('Variant','All')){Invoke-Test 'VariantSwitchTests' @($game,$ProtectedPayload,$DevelopmentPayload)}
if($Suite -in @('Protection','All')) {
    $uninstaller=Join-Path $output 'protected-uninstaller-inspection.exe'
    $zip=[IO.Compression.ZipFile]::OpenRead($ProtectedPayload)
    try {
        $entry=$zip.GetEntry('mods/AzureArchiveRecorder/卸载内录MOD.exe')
        if($null -eq $entry){throw 'Protected payload lacks uninstall EXE.'}
        $input=$entry.Open();$destination=[IO.File]::Create($uninstaller)
        try{$input.CopyTo($destination)}finally{$destination.Dispose();$input.Dispose()}
    }finally{$zip.Dispose()}
    Invoke-Test 'ProtectionTests' @($ProtectedInstaller,$uninstaller)
}
('PASS: '+$Suite+'; '+(Get-Date).ToString('o'))|Set-Content -LiteralPath (Join-Path $output 'PASS.txt') -Encoding utf8
Write-Output ('PASS evidence: '+$output)
