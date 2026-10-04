param([Parameter(Mandatory)][string]$Job,
      [string]$FFmpeg='E:/DLSS5Tool-v2.3.3-win64/_internal/ffmpeg.EXE',
      [switch]$RequireAudioActivity)
$ErrorActionPreference='Stop'
$completed=Get-Content -LiteralPath (Join-Path $Job 'completed.json') -Raw | ConvertFrom-Json
$recording=Get-Content -LiteralPath (Join-Path $Job 'recording.json') -Raw | ConvertFrom-Json
$probe=Join-Path (Split-Path $FFmpeg -Parent) 'ffprobe.exe'
$json=& $probe -v error -show_streams -show_format -of json $completed.output
if($LASTEXITCODE -ne 0){throw 'ffprobe failed.'}
$metadata=($json -join "`n") | ConvertFrom-Json
$video=$metadata.streams | Where-Object codec_type -eq video
$audio=$metadata.streams | Where-Object codec_type -eq audio
if($video.codec_name -ne 'h264' -or $audio.codec_name -ne 'aac'){throw 'Unexpected codecs.'}
if($video.width -ne $recording.width -or $video.height -ne $recording.height){throw 'Monitor resolution mismatch.'}
if($video.avg_frame_rate -ne "$($recording.fps)/1" -or [long]$video.nb_frames -ne $completed.frames){throw 'Frame rate/count mismatch.'}
$seconds=[double]$completed.frames/$recording.fps
if([Math]::Abs([double]$video.duration-$seconds) -gt .001 -or [Math]::Abs([double]$audio.duration-$seconds) -gt .05){throw 'Audio/video duration mismatch.'}
if((Get-FileHash -LiteralPath $completed.output).Hash -ne (Get-FileHash -LiteralPath $completed.workingVideo).Hash){throw 'Published output differs from working MP4.'}
& $FFmpeg -v error -i $completed.output -f null -
if($LASTEXITCODE -ne 0){throw 'Full decode failed.'}
$audioStats=& $FFmpeg -hide_banner -i $completed.output -vn -af volumedetect -f null - 2>&1 | Out-String
if($LASTEXITCODE -ne 0){throw 'Audio analysis failed.'}
$audioStats | Set-Content -LiteralPath (Join-Path $Job 'audio-verification.txt') -Encoding utf8
if($RequireAudioActivity -and ($audioStats -notmatch 'max_volume:\s*(-?[\d.]+) dB' -or [double]$Matches[1] -lt -80)){throw 'Expected audible fixture audio is silent.'}
$windowPath=Join-Path $Job 'restored-window.json'
if(Test-Path -LiteralPath $windowPath){
    $window=Get-Content -LiteralPath $windowPath -Raw | ConvertFrom-Json
    if($window.width -ne $window.expectedWidth -or $window.height -ne $window.expectedHeight -or $window.mode -ne $window.expectedMode){throw 'Window restore mismatch.'}
}
$sampleAt=[Math]::Min(5,$seconds/2).ToString('0.###',[Globalization.CultureInfo]::InvariantCulture)
& $FFmpeg -y -v error -ss $sampleAt -i $completed.output -frames:v 1 (Join-Path $Job 'verified-frame.png')
if($LASTEXITCODE -ne 0){throw 'Frame extraction failed.'}
$json | Set-Content -LiteralPath (Join-Path $Job 'ffprobe.json') -Encoding utf8
Write-Host "PASS: $($video.width)x$($video.height), $($recording.fps) fps, $($completed.frames) frames, $seconds s, H.264/AAC, full decode, published file identical."
Write-Host ("Render: {0:N2} s; video/render: {1:N2}x; entire job: {2:N2} s." -f $completed.renderSeconds,($seconds/$completed.renderSeconds),$completed.totalSeconds)
Write-Host ($audioStats -split "`n" | Where-Object {$_ -match 'mean_volume|max_volume'})
