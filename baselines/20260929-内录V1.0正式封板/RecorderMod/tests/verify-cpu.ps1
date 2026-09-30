param([string]$FFmpeg = 'E:/DLSS5Tool-v2.3.3-win64/_internal/ffmpeg.EXE')
$ErrorActionPreference = 'Stop'
$recorder = Split-Path $PSScriptRoot -Parent
$null = [Reflection.Assembly]::LoadFrom((Join-Path $recorder 'build/AzureArchive.Recorder.dll'))
$cases = @(@('NVIDIA GeForce RTX 2060',20),@('NVIDIA GeForce RTX 3090 Ti',30),@('NVIDIA GeForce RTX 4070 SUPER',40),@('NVIDIA GeForce RTX 4090D',40),@('NVIDIA GeForce RTX 5090D',50),@('NVIDIA GeForce RTX 5090 Laptop GPU',50),@('NVIDIA RTX A4000',-1),@('NVIDIA GeForce GTX 1080',-1))
foreach ($case in $cases) {
    if ([AzureArchive.Recorder.GpuProfiles]::Classify($case[0],0x10DE) -ne $case[1]) { throw "GPU classification: $($case[0])" }
}
if ([AzureArchive.Recorder.GpuProfiles]::Classify('RTX 4090',0x8086) -ne 0) { throw 'Non-NVIDIA must be unsupported.' }
$devices = [AzureArchive.Recorder.GpuProfiles]::Detect()
Write-Host ('Detected adapters: ' + (($devices | ForEach-Object Name) -join ', '))
try { $null=[AzureArchive.Recorder.GpuProfiles]::ResolveRuntime('missing','missing',20); throw 'Accepted unsupported RTX 20' }
catch { if ($_.Exception.ToString() -notmatch '20 系') { throw } }

Add-Type -TypeDefinition @'
using System;
public static class RecorderFixture {
    public static byte[] Frame(int width,int height) {
        var data=new byte[width*height*3];
        for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
            int at=(y*width+x)*3;
            data[at+(y<height/2?2:0)]=220; // Unity rows: bottom blue, top red.
            data[at+1]=(byte)(x*90/width);
        }
        return data;
    }
    public static float[] Sound(int frame) {
        var data=new float[1600*2];
        for(int i=0;i<1600;i++) data[2*i]=data[2*i+1]=(float)(.2*Math.Sin(2*Math.PI*440*(frame*1600+i)/48000));
        return data;
    }
}
'@
$output=Join-Path $recorder ('test-output/cpu-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
$enc=[AzureArchive.Recorder.Encoder]::new($FFmpeg,$output,320,180,30,48000,2,18)
try {
    $pixels=[RecorderFixture]::Frame(320,180)
    for($i=0;$i -lt 60;$i++) { $enc.WriteFrame($pixels,[RecorderFixture]::Sound($i)) }
    $video=$enc.FinishAsync().GetAwaiter().GetResult()
    if($enc.Frames -ne 60 -or $enc.AudioSamples -ne 96000) { throw 'Frame/sample count mismatch.' }
} finally { $enc.Dispose() }
$probe=Join-Path (Split-Path $FFmpeg -Parent) 'ffprobe.exe'
$json=& $probe -v error -show_streams -show_format -of json $video
if($LASTEXITCODE -ne 0) { throw 'ffprobe failed.' }
$metadata=($json -join "`n") | ConvertFrom-Json
$v=$metadata.streams | Where-Object codec_type -eq video
$a=$metadata.streams | Where-Object codec_type -eq audio
if($v.codec_name -ne 'h264' -or $v.nb_frames -ne '60' -or $v.avg_frame_rate -ne '30/1' -or $a.codec_name -ne 'aac') { throw 'Unexpected codecs or frame count.' }
if([Math]::Abs([double]$metadata.format.duration-2) -gt .1) { throw 'Duration mismatch.' }
& $FFmpeg -v error -i $video -f null -
if($LASTEXITCODE -ne 0) { throw 'Full video/audio decode failed.' }
$json | Set-Content -LiteralPath (Join-Path $output 'ffprobe.json') -Encoding utf8
Write-Host "PASS: GPU classification + H.264/AAC MP4, 60 frames / 2 seconds, full decode. $video"
