param([Parameter(Mandatory)][string]$Reference,[Parameter(Mandatory)][string]$Accelerated)
$ErrorActionPreference='Stop'
$baseline=@(Get-Content -LiteralPath (Join-Path $Reference 'script-events.jsonl') | ForEach-Object {$_ | ConvertFrom-Json} | Where-Object {$null -ne $_.index})
$fast=@(Get-Content -LiteralPath (Join-Path $Accelerated 'script-events.jsonl') | ForEach-Object {$_ | ConvertFrom-Json} | Where-Object {$null -ne $_.index})
if($baseline.Count -ne $fast.Count){throw 'Story event count mismatch.'}
for($i=0;$i -lt $baseline.Count;$i++){
    if($baseline[$i].index -ne $fast[$i].index){throw 'Story event order mismatch.'}
    $difference=[Math]::Abs($baseline[$i].videoTime-$fast[$i].videoTime)
    if($difference -gt .1){throw "Story event $i moved by $difference seconds."}
}
$a=Get-Content -LiteralPath (Join-Path $Reference 'completed.json') -Raw | ConvertFrom-Json
$b=Get-Content -LiteralPath (Join-Path $Accelerated 'completed.json') -Raw | ConvertFrom-Json
if([Math]::Abs($a.videoSeconds-$b.videoSeconds) -gt .1){throw 'Normal/accelerated output duration mismatch.'}
Write-Host "PASS: $($fast.Count) story events and total duration match within 100 ms. Reference $($a.videoSeconds)s; accelerated $($b.videoSeconds)s."
