#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$Source,[string]$Destination='')
$ErrorActionPreference='Stop'
if(!$Destination){$Destination=Join-Path (Split-Path $PSScriptRoot -Parent) 'test-host'}
$destinationFull=[IO.Path]::GetFullPath($Destination)
$workspace=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if(!$destinationFull.StartsWith($workspace+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Test host must be inside this mod workspace.'}
if(Test-Path -LiteralPath $destinationFull){throw 'Test host already exists; never overwrite a previous test host.'}
[IO.Directory]::CreateDirectory($destinationFull)|Out-Null
foreach($part in @('AzureArchive_Data','BepInEx','D3D12','dotnet')) {
    Copy-Item -LiteralPath (Join-Path $Source $part) -Destination (Join-Path $destinationFull $part) -Recurse
}
Get-ChildItem -LiteralPath $Source -File | Where-Object {$_.Extension -in @('.exe','.dll') -or $_.Name -in @('doorstop_config.ini','.doorstop_version')} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $destinationFull
}
function Replace-Identity([byte[]]$Bytes,[string]$Old,[string]$New) {
    if($Old.Length -ne $New.Length){throw 'Identity replacement must retain serialized byte lengths.'}
    $sourceBytes=[Text.Encoding]::ASCII.GetBytes($Old);$targetBytes=[Text.Encoding]::ASCII.GetBytes($New)
    $hits=0
    for($i=0;$i -le $Bytes.Length-$sourceBytes.Length;$i++) {
        $match=$true
        for($j=0;$j -lt $sourceBytes.Length;$j++){if($Bytes[$i+$j] -ne $sourceBytes[$j]){$match=$false;break}}
        if($match){[Array]::Copy($targetBytes,0,$Bytes,$i,$targetBytes.Length);$hits++;$i+=$sourceBytes.Length-1}
    }
    if($hits -ne 1){throw "Expected exactly one native identity field for $Old, found $hits."}
}
$nativePath=Join-Path $destinationFull 'AzureArchive_Data\globalgamemanagers'
$bytes=[IO.File]::ReadAllBytes($nativePath)
Replace-Identity $bytes 'foxxlight' 'aarevtest'
Replace-Identity $bytes 'AzureArchive' 'AARevCompare'
[IO.File]::WriteAllBytes($nativePath,$bytes)
[IO.File]::WriteAllText((Join-Path $destinationFull 'AzureArchive_Data\app.info'),"aarevtest`nAARevCompare`n",[Text.UTF8Encoding]::new($false))
$profile=Join-Path $destinationFull 'profiles\RevisionTest'
[IO.Directory]::CreateDirectory($profile)|Out-Null
[IO.File]::WriteAllText((Join-Path $destinationFull 'ActiveProfile.txt'),'RevisionTest')
$manifest=Get-Content -LiteralPath (Join-Path $workspace 'manifest.json') -Raw|ConvertFrom-Json
$config=@{EnabledMods=@(@{name=$manifest.name;version=$manifest.version_number})}|ConvertTo-Json -Depth 5 -Compress
[IO.File]::WriteAllText((Join-Path $profile 'modconfig.json'),$config)
$marker=[ordered]@{kind='RevisionCompareIsolatedHost';root=$destinationFull;source=$Source;company='aarevtest';product='AARevCompare';schema=1}
[IO.File]::WriteAllText((Join-Path $destinationFull 'RevisionTestHost.json'),($marker|ConvertTo-Json))
"Isolated host prepared: $destinationFull"
