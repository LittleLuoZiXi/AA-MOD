param([string]$OutputDirectory,[string]$RegressionBaselineHelperSource)
$ErrorActionPreference='Stop'
$package=$PSScriptRoot
$allowed=Join-Path $package 'update-test-output'
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $allowed ([Guid]::NewGuid().ToString('N'))}
$test=[IO.Path]::GetFullPath($OutputDirectory)
if(!$test.StartsWith([IO.Path]::GetFullPath($allowed).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Update test output must remain in packaging/update-test-output.'}
if(Test-Path -LiteralPath $test){throw 'Refusing to reuse an existing test directory.'}
$null=New-Item -ItemType Directory -Path $test
$csc='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fake=@"
using System; using System.Collections.Generic; using System.IO; using System.Threading; using System.Web.Script.Serialization;
public static class FakeAA {
    public static int Main(string[] args) {
        if(args.Length==2 && args[0]=="--wait") { while(!File.Exists(args[1]))Thread.Sleep(50);return File.ReadAllText(args[1])=="abnormal"?7:0; }
        var env=Environment.GetEnvironmentVariables();
        var probe=new Dictionary<string,string> {
            {"DisablePresent",env.Contains("DOORSTOP_DISABLE").ToString()},
            {"DisableValue",env.Contains("DOORSTOP_DISABLE")?(string)env["DOORSTOP_DISABLE"]:"<absent>"},
            {"Initialized",Environment.GetEnvironmentVariable("DOORSTOP_INITIALIZED")??"<absent>"},
            {"PreserveMarker",Environment.GetEnvironmentVariable("AARECORDER_RESTART_TEST_MARKER")??"<absent>"},
            {"WorkingDirectory",Environment.CurrentDirectory}
        };
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"restart-environment.json"),new JavaScriptSerializer().Serialize(probe));
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"restarted.flag"),"started");return 0;
    }
}
"@
[IO.File]::WriteAllText((Join-Path $test 'FakeAA.cs'),$fake,[Text.UTF8Encoding]::new($false))
& $csc /nologo /target:winexe /platform:x64 /r:System.Web.Extensions.dll "/out:$test\AzureArchive.exe" "$test\FakeAA.cs"
if($LASTEXITCODE -ne 0){throw 'Fake AA compilation failed.'}
$hostHash=(Get-FileHash -LiteralPath "$test\AzureArchive.exe").Hash
[IO.File]::WriteAllText((Join-Path $test 'Installer.generated.cs'),[IO.File]::ReadAllText((Join-Path $package 'Installer.cs')).Replace('@@HOSTHASH@@',$hostHash),[Text.UTF8Encoding]::new($false))
$refs=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Net.Http.dll')
$sources=@("$test\Installer.generated.cs","$package\UpdateInstallerCore.cs","$package\UpdateHelper.cs","$package\..\src\DlssComponents.cs")
& $csc /nologo /target:winexe /platform:x64 /optimize+ /debug- /main:UpdateHelperProgram $refs "/out:$test\AARecorder.Update.Core.exe" $sources "$package\V1AssemblyInfo.cs"
if($LASTEXITCODE -ne 0){throw 'Updater core compilation failed.'}
& "$package\protect-executable.ps1" -InputPath "$test\AARecorder.Update.Core.exe" -OutputPath "$test\更新内录MOD.exe" -Role updater -BuildDirectory "$test\protection"
& $csc /nologo /target:exe /platform:x64 /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/out:$test\ProtectionTests.exe" "$package\ProtectionTests.cs"
if($LASTEXITCODE -ne 0){throw 'Protection inspection compilation failed.'}
& "$test\ProtectionTests.exe" "$test\更新内录MOD.exe" 2>&1 | Tee-Object -FilePath "$test\protection-tests.log"
if($LASTEXITCODE -ne 0){throw 'Updater protection inspection failed.'}
& $csc /nologo /target:exe /platform:x64 /main:UpdateInstallerTests $refs "/out:$test\UpdateInstallerTests.exe" $sources "$package\UpdateInstallerTests.cs"
if($LASTEXITCODE -ne 0){throw 'Updater test compilation failed.'}
if(![string]::IsNullOrWhiteSpace($RegressionBaselineHelperSource)){
    $baseline=[IO.Path]::GetFullPath($RegressionBaselineHelperSource)
    if(!(Test-Path -LiteralPath $baseline -PathType Leaf)){throw 'Regression baseline helper source missing.'}
    $red=Join-Path $test 'old-behavior'
    $null=New-Item -ItemType Directory -Path $red
    Copy-Item -LiteralPath $baseline -Destination (Join-Path $red 'UpdateHelper.baseline.cs')
    $redSources=@("$test\Installer.generated.cs","$package\UpdateInstallerCore.cs","$red\UpdateHelper.baseline.cs","$package\..\src\DlssComponents.cs")
    & $csc /nologo /target:winexe /platform:x64 /optimize+ /debug- /main:UpdateHelperProgram $refs "/out:$red\AARecorder.Update.Core.exe" $redSources "$package\V1AssemblyInfo.cs"
    if($LASTEXITCODE -ne 0){throw 'Regression baseline updater compilation failed.'}
    & "$package\protect-executable.ps1" -InputPath "$red\AARecorder.Update.Core.exe" -OutputPath "$red\更新内录MOD.exe" -Role updater -BuildDirectory "$red\protection"
    $baselineCases=@()
    foreach($variant in @('true','empty')){
        & "$test\UpdateInstallerTests.exe" "$red\fixtures-$variant" "$test\AzureArchive.exe" "$red\更新内录MOD.exe" --restart-environment $variant 2>&1 | Tee-Object -FilePath "$red\regression-$variant.log"
        $baselineExit=$LASTEXITCODE
        $baselineLog=Get-Content -LiteralPath "$red\regression-$variant.log" -Raw
        if($baselineExit -eq 0 -or !$baselineLog.Contains("DOORSTOP_DISABLE survived restart ($variant)")){throw "Regression case $variant did not reproduce the original environment inheritance bug."}
        $probes=@(Get-ChildItem -LiteralPath "$red\fixtures-$variant" -Recurse -Filter restart-environment.json)
        if($probes.Count -ne 1){throw 'Regression case did not write one restart probe.'}
        $probe=Get-Content -LiteralPath $probes[0].FullName -Raw | ConvertFrom-Json
        $expectedDisable=if($variant -eq 'empty'){''}else{'TRUE'}
        if($probe.DisablePresent -ne 'True' -or $probe.DisableValue -ne $expectedDisable){throw 'Baseline probe did not inherit the exact original disable value.'}
        $baselineCases += [pscustomobject]@{Case=$variant;ExpectedFailure=$true;ExitCode=$baselineExit;Probe=$probes[0].FullName}
        Write-Output "EXPECTED FAILURE CONFIRMED: old protected helper inherited DOORSTOP_DISABLE=$variant"
    }
    [pscustomobject]@{BaselineSource=$baseline;BaselineSha256=(Get-FileHash -LiteralPath $baseline).Hash;Cases=$baselineCases} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$red\regression-proof.json" -Encoding utf8
}
& "$test\UpdateInstallerTests.exe" "$test\fixtures" "$test\AzureArchive.exe" "$test\更新内录MOD.exe" 2>&1 | Tee-Object -FilePath "$test\update-installer-tests.log"
if($LASTEXITCODE -ne 0){throw 'Updater tests failed.'}
$futureInstaller=Join-Path $test 'Installer.future-generated.cs'
[IO.File]::WriteAllText($futureInstaller,[IO.File]::ReadAllText((Join-Path $test 'Installer.generated.cs')).Replace('Version="1.2.4"','Version="1.2.5"'),[Text.UTF8Encoding]::new($false))
$futureSources=@($futureInstaller,"$package\UpdateInstallerCore.cs","$package\UpdateHelper.cs","$package\..\src\DlssComponents.cs")
& $csc /nologo /target:exe /platform:x64 /main:UpdateInstallerTests $refs "/out:$test\FutureUpdateInstallerTests.exe" $futureSources "$package\UpdateInstallerTests.cs"
if($LASTEXITCODE -ne 0){throw 'Future updater fixture compilation failed.'}
& "$test\FutureUpdateInstallerTests.exe" "$test\future-fixtures" "$test\AzureArchive.exe" "$test\更新内录MOD.exe" --future 2>&1 | Tee-Object -FilePath "$test\future-helper-tests.log"
if($LASTEXITCODE -ne 0){throw 'Future helper ownership tests failed.'}
[IO.File]::WriteAllText((Join-Path $package 'update-test-latest.txt'),$test,[Text.UTF8Encoding]::new($false))
Write-Output "UPDATE_TEST_EVIDENCE=$test"