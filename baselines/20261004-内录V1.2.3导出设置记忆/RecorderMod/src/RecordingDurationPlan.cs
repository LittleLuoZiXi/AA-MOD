using System;
using System.Globalization;

namespace AzureArchive.Recorder;

// A completed native preflight determines this value before encoding starts.
// The real export cannot revise it as rendered frames accumulate.
internal sealed class RecordingDurationPlan
{
    long frames;
    int fps;
    internal bool IsReady { get; private set; }
    internal long Frames=>frames;
    internal double Seconds=>IsReady?(double)frames/fps:0;
    internal string Label=>IsReady
        ?"预计总时长 "+Seconds.ToString("0.0",CultureInfo.InvariantCulture)+" 秒"
        :"预计总时长：计算中";

    internal void Reset()
    {
        frames=0;fps=0;IsReady=false;
    }

    internal void Freeze(long totalFrames,int frameRate)
    {
        if(IsReady)throw new InvalidOperationException("Recording duration is already fixed; reset before another preflight.");
        if(totalFrames<=0)throw new ArgumentOutOfRangeException(nameof(totalFrames),"A duration plan requires at least one simulated frame.");
        if(frameRate<=0)throw new ArgumentOutOfRangeException(nameof(frameRate),"Frame rate must be positive.");
        frames=totalFrames;fps=frameRate;IsReady=true;
    }
}