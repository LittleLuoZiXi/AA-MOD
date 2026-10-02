#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('ChoiceSecondAuto','ChoiceConsecutiveAuto','ChoiceNoDefault','PlainNoChoice','TitleNoChoice','MenuDialogue','ChoiceDirectEnd','ChoiceLoop')][string]$Fixture='ChoiceSecondAuto',
    [ValidateSet(24,30,60)][int]$Fps=24,
    [ValidateRange(60,1800)][int]$TimeoutSeconds=120,
    [ValidateRange(1,120)][int]$MaxMinutes=1,
    [string]$ScenarioPath='',
    [ValidateRange(0,2000)][int]$FastSimulationHz=0,
    [switch]$NativeTiming,
    [ValidateRange(0,2147483647)][int]$NativeStopRow=0,
    [ValidateSet(0,30,60)][int]$NativeFps=0,
    [switch]$ChoiceProbe,
    [switch]$InspectProgress,
    [switch]$ControlsProbe,
    [switch]$MeasureTotal,
    [switch]$TimingOptionsProbe,
    [ValidateSet("","preflight","before-render")][string]$CancelPhase="",
    [switch]$DryRun,
    [string]$GameRoot=(Join-Path $PSScriptRoot '..\..'),
    [string]$FixtureDirectory=(Join-Path $PSScriptRoot 'choice-fixtures')
)
$ErrorActionPreference='Stop'
if($CancelPhase -and -not $MeasureTotal){throw 'CancelPhase requires MeasureTotal; direct recording has no preflight to cancel.'}
if($TimingOptionsProbe -and -not [string]::IsNullOrWhiteSpace($ScenarioPath)){throw 'TimingOptionsProbe requires an AAP2 fixture; ScenarioPath bypasses the settings interaction.'}
if($NativeTiming -and [string]::IsNullOrWhiteSpace($ScenarioPath)){throw 'NativeTiming requires ScenarioPath pointing to an existing .aas file.'}
if($NativeTiming -and ($InspectProgress -or $CancelPhase)){throw 'NativeTiming cannot enable recorder progress UI or recorder cancellation diagnostics.'}
if(-not $NativeTiming -and ($NativeStopRow -ne 0 -or $NativeFps -ne 0)){throw 'NativeStopRow and NativeFps require NativeTiming.'}
$GameRoot=[IO.Path]::GetFullPath($GameRoot).TrimEnd([char[]]@([char]92,[char]47))
if($GameRoot -eq [IO.Path]::GetPathRoot($GameRoot).TrimEnd([char[]]@([char]92,[char]47))){throw 'GameRoot must be a dedicated isolated copy, never a filesystem root.'}
function Assert-NoReparse([string]$Path) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while(-not [string]::IsNullOrEmpty($cursor)){
        if(Test-Path -LiteralPath $cursor){
            if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Linked path is not allowed: $cursor"}
        }
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
}
Assert-NoReparse $GameRoot
$markerPath=Join-Path $GameRoot 'RecorderChoiceTestRoot.json'
Assert-NoReparse $markerPath
if(-not(Test-Path -LiteralPath $markerPath -PathType Leaf)){throw 'Missing RecorderChoiceTestRoot.json. Create a dedicated isolated AA copy and its explicit marker first; never mark a live installation.'}
$marker=[IO.File]::ReadAllText($markerPath)|ConvertFrom-Json
if($marker.schema_version -ne 1 -or $marker.kind -ne 'RecorderChoiceSmokeIsolatedCopy' -or [string]::IsNullOrWhiteSpace([string]$marker.root) -or -not [IO.Path]::IsPathFullyQualified([string]$marker.root)){throw 'Invalid isolated-copy marker identity or root.'}
$markerRoot=[IO.Path]::GetFullPath([string]$marker.root).TrimEnd([char[]]@([char]92,[char]47))
if(-not [string]::Equals($GameRoot,$markerRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'GameRoot does not match RecorderChoiceTestRoot.json root. Do not reuse a marker copied from another directory.'}
$allowed=$GameRoot
function Assert-PrivatePath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    if(-not ($full.StartsWith($allowed+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($full,$allowed,[StringComparison]::OrdinalIgnoreCase))){throw "Path escapes isolated copy: $full"}
    Assert-NoReparse $full
    return $full
}
function Set-ConfigValue([string]$Text,[string]$Section,[string]$Key,[string]$Value) {
    $lines=[Collections.Generic.List[string]]::new();$lines.AddRange([string[]]($Text -split '\r?\n'))
    $sectionStart=-1;$sectionEnd=$lines.Count
    for($i=0;$i -lt $lines.Count;$i++){
        if($lines[$i] -match '^\s*\[([^\]]+)\]\s*$'){
            if($sectionStart -ge 0){$sectionEnd=$i;break}
            if($Matches[1] -eq $Section){$sectionStart=$i}
        }
    }
    if($sectionStart -lt 0){$lines.Add('');$lines.Add('['+$Section+']');$lines.Add($Key+' = '+$Value);return ($lines -join "`r`n")}
    for($i=$sectionStart+1;$i -lt $sectionEnd;$i++){
        if($lines[$i] -match ('^\s*'+[regex]::Escape($Key)+'\s*=')){$lines[$i]=$Key+' = '+$Value;return ($lines -join "`r`n")}
    }
    $lines.Insert($sectionEnd,$Key+' = '+$Value);return ($lines -join "`r`n")
}
function Save-Json([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30)+"`n",[Text.UTF8Encoding]::new($false))}
$exe=Assert-PrivatePath (Join-Path $GameRoot 'AzureArchive.exe')
$ffmpeg=Assert-PrivatePath (Join-Path $GameRoot 'mods\AzureArchiveRecorder\runtime\ffmpeg\ffmpeg.exe')
$ffprobe=Assert-PrivatePath (Join-Path $GameRoot 'mods\AzureArchiveRecorder\runtime\ffmpeg\ffprobe.exe')
foreach($path in @($exe,$ffmpeg,$ffprobe)){if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing private dependency: $path"}}
$runLabel=$Fixture
if(-not [string]::IsNullOrWhiteSpace($ScenarioPath)){
    $fixtureSource=[IO.Path]::GetFullPath($ScenarioPath)
    Assert-NoReparse $fixtureSource
    if(-not [string]::Equals([IO.Path]::GetExtension($fixtureSource),'.aas',[StringComparison]::OrdinalIgnoreCase)){throw 'ScenarioPath must reference an existing .aas file.'}
    if(-not(Test-Path -LiteralPath $fixtureSource -PathType Leaf)){throw "Missing scenario: $fixtureSource"}
    $ScenarioPath=$fixtureSource
    $runLabel='Scenario'
} else {
    $FixtureDirectory=[IO.Path]::GetFullPath($FixtureDirectory)
    Assert-NoReparse $FixtureDirectory
    $fixtureSource=Join-Path $FixtureDirectory ($Fixture+'.aap2')
    Assert-NoReparse $fixtureSource
    if(-not(Test-Path -LiteralPath $fixtureSource -PathType Leaf)){throw "Missing fixture: $fixtureSource"}
}
# Read the source once and write only its private input copy; the supplied scenario remains untouched.
$inputFileName=[IO.Path]::GetFileName($fixtureSource)
$inputBytes=[IO.File]::ReadAllBytes($fixtureSource)
$sourceHasher=[Security.Cryptography.SHA256]::Create()
try {$sourceHash=[BitConverter]::ToString($sourceHasher.ComputeHash($inputBytes)).Replace('-','')} finally {$sourceHasher.Dispose()}
$scenarioResourceRoot=$null
$resourceDirectories=[Collections.Generic.List[string]]::new()
$resourceFiles=[Collections.Generic.List[object]]::new()
if(-not [string]::IsNullOrWhiteSpace($ScenarioPath)){
    $resourceCandidate=Join-Path ([IO.Path]::GetDirectoryName($fixtureSource)) ([IO.Path]::GetFileNameWithoutExtension($fixtureSource))
    Assert-NoReparse $resourceCandidate
    if(Test-Path -LiteralPath $resourceCandidate -PathType Container){
        $scenarioResourceRoot=$resourceCandidate
        $pending=[Collections.Generic.Queue[string]]::new();$pending.Enqueue($scenarioResourceRoot)
        while($pending.Count -gt 0){
            $directory=$pending.Dequeue();Assert-NoReparse $directory
            $resourceDirectories.Add([IO.Path]::GetRelativePath($scenarioResourceRoot,$directory))
            foreach($item in Get-ChildItem -LiteralPath $directory -Force){
                # Inspect every child before descending; never traverse a linked resource directory.
                Assert-NoReparse $item.FullName
                if($item.PSIsContainer){$pending.Enqueue($item.FullName)}
                else {$resourceFiles.Add([ordered]@{source=$item.FullName;relative=[IO.Path]::GetRelativePath($scenarioResourceRoot,$item.FullName)})}
            }
        }
    }
}
$profilePath=Assert-PrivatePath (Join-Path $GameRoot 'ActiveProfile.txt')
$profile=([IO.File]::ReadAllText($profilePath)).Trim()
if($profile -notmatch '^[A-Za-z0-9_-]+$'){throw 'Unexpected isolated profile name.'}
$config=Assert-PrivatePath (Join-Path $GameRoot "profiles\$profile\configs\azurearchive.recorder.cfg")
if(-not(Test-Path -LiteralPath $config -PathType Leaf)){throw 'Expected existing isolated recorder config for byte-exact restoration.'}
foreach($existing in Get-Process -Name AzureArchive -ErrorAction SilentlyContinue){
    if($existing.Path -and [IO.Path]::GetFullPath($existing.Path) -eq $exe){throw 'The isolated application is already running; finish it before starting a smoke run.'}
}
$runs=Assert-PrivatePath (Join-Path $GameRoot 'smoke-runs')
[IO.Directory]::CreateDirectory($runs)|Out-Null
$lockPath=Assert-PrivatePath (Join-Path $runs '.driver.lock')
$lock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$rateLabel=if($NativeTiming){if($NativeFps -gt 0){[string]$NativeFps+'fps-native'}else{'native-default'}}else{[string]$Fps+'fps'}
$runRoot=Join-Path $runs ((Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-'+$runLabel+'-'+$rateLabel+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6))
[IO.Directory]::CreateDirectory($runRoot)|Out-Null
$captures=Join-Path $runRoot 'captures';$published=Join-Path $runRoot 'published';$inputDir=Join-Path $runRoot 'input'
foreach($path in @($captures,$published,$inputDir)){[IO.Directory]::CreateDirectory($path)|Out-Null}
$fixturePath=Assert-PrivatePath (Join-Path $inputDir $inputFileName)
[IO.File]::WriteAllBytes($fixturePath,$inputBytes)
$resourceCopies=[Collections.Generic.List[object]]::new()
if($scenarioResourceRoot){
    $resourceDestination=Assert-PrivatePath (Join-Path $inputDir ([IO.Path]::GetFileNameWithoutExtension($fixtureSource)))
    foreach($relative in $resourceDirectories){$destination=Assert-PrivatePath (Join-Path $resourceDestination $relative);[IO.Directory]::CreateDirectory($destination)|Out-Null}
    foreach($resource in $resourceFiles){
        Assert-NoReparse $resource.source
        $destination=Assert-PrivatePath (Join-Path $resourceDestination $resource.relative)
        $resourceHash=(Get-FileHash -LiteralPath $resource.source -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $resource.source -Destination $destination
        if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $resourceHash){throw "Scenario resource changed while copying: $($resource.source)"}
        $resourceCopies.Add([ordered]@{relative=$resource.relative;source_sha256=$resourceHash})
    }
}
$original=[IO.File]::ReadAllBytes($config)
[IO.File]::WriteAllBytes((Join-Path $runRoot 'config.before.cfg'),$original)
$originalHash=(Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash
$process=$null;$started=$false;$timedOut=$false;$failure=$null;$exitCode=$null;$restored=$false
$startTime=[DateTime]::UtcNow
if($NativeTiming){
    $arguments=@('--aa-recorder-native-timing',$fixturePath,'--aa-recorder-smoke-evidence',$runRoot,'-logFile',(Join-Path $runRoot 'Unity-player.log'))
    if($NativeStopRow -gt 0){$arguments+=@('--aa-recorder-native-stop-row',[string]$NativeStopRow)}
    if($NativeFps -gt 0){$arguments+=@('--aa-recorder-native-fps',[string]$NativeFps)}
}else{
    $arguments=@('--aa-recorder-smoke',$fixturePath,'--aa-recorder-smoke-output',$published,'--aa-recorder-test-fps',[string]$Fps,'-logFile',(Join-Path $runRoot 'Unity-player.log'))
    if($InspectProgress){$arguments+=@('--aa-recorder-progress-inspect','--aa-recorder-smoke-evidence',$runRoot)}
    if($CancelPhase){$arguments+=@(('--aa-recorder-cancel-'+$CancelPhase),'--aa-recorder-smoke-evidence',$runRoot)}
}
if($PSBoundParameters.ContainsKey('FastSimulationHz')){if($FastSimulationHz -gt 0 -and ($NativeTiming -or $MeasureTotal)){throw 'Fast simulation requires direct recording.'};$arguments+=@('--aa-recorder-fast-hz',[string]$FastSimulationHz)}
if($ChoiceProbe){$arguments+='--aa-recorder-choice-probe'}
if($ControlsProbe){$arguments+='--aa-recorder-controls-probe'}
if($MeasureTotal){$arguments+='--aa-recorder-measure-total'}
if($TimingOptionsProbe){$arguments+='--aa-recorder-timing-options-probe'}
Save-Json (Join-Path $runRoot 'invocation.json') ([ordered]@{executable=$exe;arguments=$arguments;fixture_source=$fixtureSource;source_sha256=$sourceHash;scenario_path=$ScenarioPath;scenario_resource_source=$scenarioResourceRoot;scenario_resource_files=$resourceCopies.ToArray();input_sha256=(Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash;fps=$Fps;measure_total=[bool]$MeasureTotal;timing_options_probe=[bool]$TimingOptionsProbe;native_timing=[bool]$NativeTiming;native_stop_row=$NativeStopRow;native_fps=$NativeFps;max_minutes=$MaxMinutes;timeout_seconds=$TimeoutSeconds;dry_run=[bool]$DryRun;config=$config;config_original_sha256=$originalHash})
try {
    if(-not $NativeTiming){
        $text=[Text.Encoding]::UTF8.GetString($original).TrimStart([char]0xFEFF)
        foreach($item in @(@('Recording','OutputDirectory',$captures),@('Recording','FFmpegPath',$ffmpeg),@('Recording','FrameRate',[string]$Fps),@('Recording','MaxMinutes',[string]$MaxMinutes),@('Tutorial','SeenVersion','1'))){$text=Set-ConfigValue $text $item[0] $item[1] $item[2]}
        [IO.File]::WriteAllText($config,$text,[Text.UTF8Encoding]::new($false))
    }
    Copy-Item -LiteralPath $config -Destination (Join-Path $runRoot 'config.effective.cfg')
    $bepLog=Assert-PrivatePath (Join-Path $GameRoot 'BepInEx\LogOutput.log')
    if(Test-Path -LiteralPath $bepLog){Copy-Item -LiteralPath $bepLog -Destination (Join-Path $runRoot 'BepInEx.previous.log')}
    if(-not $DryRun){
        $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=$exe;$psi.WorkingDirectory=$GameRoot
        $psi.UseShellExecute=$false;$psi.CreateNoWindow=-not $NativeTiming;$psi.WindowStyle=if($NativeTiming){[Diagnostics.ProcessWindowStyle]::Normal}else{[Diagnostics.ProcessWindowStyle]::Hidden}
        $psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
        foreach($arg in $arguments){$psi.ArgumentList.Add($arg)}
        $process=[Diagnostics.Process]::new();$process.StartInfo=$psi
        if(-not $process.Start()){throw 'Process start returned false.'}
        $started=$true;$pidOwned=$process.Id
        $stdoutTask=$process.StandardOutput.ReadToEndAsync();$stderrTask=$process.StandardError.ReadToEndAsync()
        Write-Output "Smoke started: PID $pidOwned; evidence $runRoot"
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while(-not $process.WaitForExit(250)){
            if($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds){
                $timedOut=$true
                # Kill only this Process object and its descendants; never kill by name.
                $process.Kill($true)
                if(-not $process.WaitForExit(10000)){throw 'Owned process did not exit after timeout termination.'}
                break
            }
        }
        $process.WaitForExit();$exitCode=$process.ExitCode
        [IO.File]::WriteAllText((Join-Path $runRoot 'stdout.log'),$stdoutTask.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $runRoot 'stderr.log'),$stderrTask.GetAwaiter().GetResult())
    }
} catch {$failure=$_.Exception.ToString()} finally {
    try {
        if($started -and $process -and -not $process.HasExited){$process.Kill($true);$process.WaitForExit(10000)|Out-Null}
        if(Test-Path -LiteralPath $config){Copy-Item -LiteralPath $config -Destination (Join-Path $runRoot 'config.after-run.cfg')}
        [IO.File]::WriteAllBytes($config,$original)
        $restored=(Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -eq $originalHash
        if(-not $restored){throw 'Byte-exact config restoration failed.'}
        $bepLog=Assert-PrivatePath (Join-Path $GameRoot 'BepInEx\LogOutput.log')
        if(Test-Path -LiteralPath $bepLog){Copy-Item -LiteralPath $bepLog -Destination (Join-Path $runRoot 'BepInEx.log')}
    } catch {$failure=(@($failure,$_.Exception.ToString())|Where-Object {$_}) -join "`n"}
    finally {if($process){$process.Dispose()};$lock.Dispose()}
}
$probeResults=[Collections.Generic.List[object]]::new()
foreach($mp4 in Get-ChildItem -LiteralPath $runRoot -Recurse -File -Filter '*.mp4'){
    $probeInfo=[Diagnostics.ProcessStartInfo]::new();$probeInfo.FileName=$ffprobe;$probeInfo.UseShellExecute=$false;$probeInfo.CreateNoWindow=$true;$probeInfo.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $probeInfo.RedirectStandardOutput=$true;$probeInfo.RedirectStandardError=$true
    $probeArgs=@('-v','error','-show_streams','-show_format','-of','json',$mp4.FullName)
    foreach($arg in $probeArgs){$probeInfo.ArgumentList.Add($arg)}
    $probeProcess=[Diagnostics.Process]::Start($probeInfo)
    $outTask=$probeProcess.StandardOutput.ReadToEndAsync();$errTask=$probeProcess.StandardError.ReadToEndAsync()
    $probeTimedOut=-not $probeProcess.WaitForExit(30000)
    if($probeTimedOut){$probeProcess.Kill($true);$probeProcess.WaitForExit(10000)|Out-Null}
    $probeJson=$mp4.FullName+'.ffprobe.json';$probeErr=$mp4.FullName+'.ffprobe.stderr.txt'
    [IO.File]::WriteAllText($probeJson,$outTask.GetAwaiter().GetResult());[IO.File]::WriteAllText($probeErr,$errTask.GetAwaiter().GetResult())
    $probeResults.Add([ordered]@{video=$mp4.FullName;bytes=$mp4.Length;executable=$ffprobe;arguments=$probeArgs;exit_code=$probeProcess.ExitCode;timed_out=$probeTimedOut;json=$probeJson;stderr=$probeErr})
    $probeProcess.Dispose()
}
$completed=@(Get-ChildItem -LiteralPath $captures -Recurse -File -Filter 'completed.json'|ForEach-Object {[IO.File]::ReadAllText($_.FullName)|ConvertFrom-Json})
$events=@(Get-ChildItem -LiteralPath $captures -Recurse -File -Filter '*events*.jsonl'|ForEach-Object {$_.FullName})
$artifacts=@(Get-ChildItem -LiteralPath $captures -Recurse -File|Where-Object {$_.Extension -in '.json','.jsonl','.aas','.log'}|ForEach-Object {$_.FullName})
$evidenceLog=Join-Path $runRoot 'BepInEx.log'
$logText=if(Test-Path -LiteralPath $evidenceLog){[IO.File]::ReadAllText($evidenceLog)}else{''}
$diagnosticLines=@($logText -split '\r?\n'|Where-Object {$_ -match 'Compiling recording snapshot|Snapshot compilation returned|Opening compiled scenario|Capture started|Exception|Error|VALIDATION|ChoiceProbe|CHOICE|choice-probe|Save failed|编译|保存失败'})
[IO.File]::WriteAllLines((Join-Path $runRoot 'diagnostics.txt'),$diagnosticLines,[Text.UTF8Encoding]::new($false))
$nativeResultPath=Join-Path $runRoot 'native-timing-result.json'
$nativeResult=if($NativeTiming -and (Test-Path -LiteralPath $nativeResultPath)){[IO.File]::ReadAllText($nativeResultPath)|ConvertFrom-Json}else{$null}
if($NativeTiming -and -not $DryRun -and -not $timedOut -and $exitCode -eq 0){
    if($null -eq $nativeResult){$failure=(@($failure,'Native timing exited without a result.')|Where-Object {$_}) -join "`n"}
    elseif(-not $nativeResult.validNativeClock){$failure=(@($failure,'Native timing observed nonzero captureFramerate; result is not a native-clock baseline.')|Where-Object {$_}) -join "`n"}
}
$summary=[ordered]@{fixture=$runLabel;scenario_path=$ScenarioPath;fps=$Fps;measure_total=[bool]$MeasureTotal;timing_options_probe=[bool]$TimingOptionsProbe;native_timing=[bool]$NativeTiming;native_stop_row=$NativeStopRow;native_fps=$NativeFps;max_minutes=$MaxMinutes;timeout_seconds=$TimeoutSeconds;dry_run=[bool]$DryRun;started=$started;timed_out=$timedOut;exit_code=$exitCode;config_restored=$restored;native_result=$nativeResult;failure=$failure;run_directory=$runRoot;compile_started=(!$DryRun -and $logText.Contains('Compiling recording snapshot'));compile_returned=(!$DryRun -and $logText.Contains('Snapshot compilation returned'));capture_started=(!$DryRun -and $logText.Contains('Capture started'));completed=$completed;script_events=$events;artifacts=$artifacts;mp4=$probeResults.ToArray()}
Save-Json (Join-Path $runRoot 'summary.json') $summary
$summary|ConvertTo-Json -Depth 8
if($failure -or $timedOut -or (-not $DryRun -and $exitCode -ne 0)){exit 1}