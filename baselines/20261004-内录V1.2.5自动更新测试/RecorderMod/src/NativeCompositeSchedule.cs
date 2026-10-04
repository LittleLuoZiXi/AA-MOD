using System;

namespace AzureArchive.Recorder;

// Gates only optional offscreen compositing. Native simulation, PCM and output
// timestamp sampling continue on every source frame, independently of this grid.
internal sealed class NativeCompositeSchedule
{
    internal const int CompositeHz=60;
    internal const double IntervalSeconds=1d/CompositeHz;
    long nextTick;

    internal bool HasRendered { get; private set; }
    internal double LastRenderedSeconds { get; private set; }
    internal double NextDeadlineSeconds=>(double)nextTick/CompositeHz;

    // Queries never acknowledge a render. A failed/deferred attempt therefore
    // remains due. Mandatory native render callbacks bypass the frequency gate.
    internal bool ShouldRender(double nativeSeconds,bool mandatory)
    {
        double tick=ValidateTime(nativeSeconds);
        return mandatory || !HasRendered || tick>=nextTick;
    }

    // Call only after a successful composite and snapshot retention. Skip all
    // elapsed grid slots after a stall; never queue catch-up renders or shift the
    // absolute phase by scheduling a full interval after this late source frame.
    internal void Rendered(double nativeSeconds)
    {
        double tick=ValidateTime(nativeSeconds);
        long following=checked((long)Math.Floor(tick)+1);
        nextTick=following;LastRenderedSeconds=nativeSeconds;HasRendered=true;
    }

    double ValidateTime(double nativeSeconds)
    {
        // Keep the integer 60 Hz grid exactly representable in a double, well
        // beyond any recording length. Reject invalid clocks before state edits.
        double tick=nativeSeconds*CompositeHz;
        if(!double.IsFinite(nativeSeconds) || nativeSeconds<0 || tick>=4503599627370496d ||
            (HasRendered && nativeSeconds<LastRenderedSeconds))
            throw new ArgumentOutOfRangeException(nameof(nativeSeconds),"Native elapsed time must be finite, nonnegative and monotonic.");
        // Treat only negligible double arithmetic noise as an exact grid edge.
        // The tolerance is in grid ticks (less than 17 picoseconds), not seconds.
        double nearest=Math.Round(tick);
        return Math.Abs(tick-nearest)<=1e-9?nearest:tick;
    }
}
