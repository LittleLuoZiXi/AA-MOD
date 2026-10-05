using System;
using System.Collections.Generic;

namespace AzureArchive.RevisionCompare;

internal sealed record RevisionIdentity(string ProjectId, string EditSession, string NodeId, string LineId);

internal sealed record RevisionComparison<T>(RevisionIdentity Identity, long BaselineGeneration,
    long SelectionGeneration, T Previous, T Current) where T : class;
internal sealed record RevisionRestorePlan<T>(RevisionIdentity Identity, T Before, T After) where T : class;

// This store knows nothing about Unity. Every boundary owns a clone, so editing
// a live line or a comparison copy can never change the remembered version.
internal sealed class RevisionStore<T> where T : class
{
    // LatestPlayed is null until the first captured playback. The initial
    // selection supplies a fallback, not an additional playback revision.
    private sealed record Baseline(long Generation, T Previous, T? LatestPlayed);
    private sealed record PinnedComparison(RevisionIdentity Identity, long BaselineGeneration,
        long SelectionGeneration, T Previous);
    private readonly Dictionary<RevisionIdentity, Baseline> baselines = new();
    private readonly Func<T, T> clone;
    private readonly Func<T, T, bool> contentEquals;
    private RevisionIdentity? selected;
    private string? sessionKey;
    private long generation;
    private long selectionGeneration;
    private PinnedComparison? pinnedComparison;

    internal RevisionStore(Func<T, T> clone, Func<T, T, bool> contentEquals)
    {
        this.clone = clone ?? throw new ArgumentNullException(nameof(clone));
        this.contentEquals = contentEquals ?? throw new ArgumentNullException(nameof(contentEquals));
    }
    internal RevisionIdentity? Selected => selected;
    internal bool HasBaseline => selected != null && baselines.ContainsKey(selected);

    internal void Observe(RevisionIdentity? identity, T? current)
    {
        if (identity == null || current == null)
        {
            if (selected != null) selectionGeneration++;
            selected = null;
            return;
        }
        string nextSession = identity.ProjectId + "\0" + identity.EditSession;
        if (!string.Equals(sessionKey, nextSession, StringComparison.Ordinal))
        {
            Clear();
            sessionKey = nextSession;
        }
        if (selected != identity)
        {
            selected = identity;
            selectionGeneration++;
        }
        if (!baselines.ContainsKey(identity))
            baselines.Add(identity, new Baseline(++generation, clone(current), null));
    }

    internal bool CaptureAtManualPreview(RevisionIdentity identity, T current)
    {
        Observe(identity, current);
        var baseline = baselines[identity];
        if (baseline.LatestPlayed != null && contentEquals(baseline.LatestPlayed, current)) return false;

        // Keep two distinct captured plays: A -> B still compares against A;
        // only the next different playback C advances the comparison to B.
        // Both stored values own a deep copy, even on the first formal play.
        var previous = clone(baseline.LatestPlayed ?? current);
        var latest = clone(current);
        baselines[identity] = new Baseline(++generation, previous, latest);
        return true;
    }

    internal RevisionComparison<T> CaptureComparison(T current)
    {
        if (selected == null || !baselines.TryGetValue(selected, out var baseline))
            throw new InvalidOperationException("请先选中一条对话。");
        return new RevisionComparison<T>(selected, baseline.Generation, selectionGeneration,
            clone(baseline.Previous), clone(current));
    }

    internal bool IsCurrent(RevisionComparison<T> comparison) =>
        selected == comparison.Identity && selectionGeneration == comparison.SelectionGeneration &&
        (MatchesPinned(comparison) || (baselines.TryGetValue(comparison.Identity, out var baseline) &&
        baseline.Generation == comparison.BaselineGeneration));

    internal void PinComparison(RevisionComparison<T> comparison)
    {
        if (!IsCurrent(comparison)) throw new InvalidOperationException("对比对象已变化，请重新打开对比。");
        // A pinned window owns the version it opened with. Later playback may
        // roll history forward, but must not retarget the visible restore action.
        // Re-pinning that same token also reads its protected copy, not history.
        var previous = clone(ProtectedPrevious(comparison));
        pinnedComparison = new(comparison.Identity, comparison.BaselineGeneration,
            comparison.SelectionGeneration, previous);
    }

    internal void ReleaseComparison() => pinnedComparison = null;

    private bool MatchesPinned(RevisionComparison<T> comparison) => pinnedComparison != null &&
        pinnedComparison.Identity == comparison.Identity &&
        pinnedComparison.SelectionGeneration == comparison.SelectionGeneration &&
        pinnedComparison.BaselineGeneration == comparison.BaselineGeneration;

    private T ProtectedPrevious(RevisionComparison<T> comparison) => MatchesPinned(comparison)
        ? pinnedComparison!.Previous : baselines[comparison.Identity].Previous;

    internal T? BaselineCopy() => selected != null && baselines.TryGetValue(selected, out var baseline)
        ? clone(baseline.Previous) : null;

    internal T? LatestPlayedCopy() => selected != null && baselines.TryGetValue(selected, out var baseline) && baseline.LatestPlayed != null
        ? clone(baseline.LatestPlayed) : null;

    internal RevisionComparison<T> RefreshComparison(RevisionComparison<T> comparison, T current)
    {
        if (!IsCurrent(comparison)) throw new InvalidOperationException("对比对象已变化，请重新打开对比。");
        return comparison with { Previous = clone(ProtectedPrevious(comparison)), Current = clone(current) };
    }

    internal RevisionRestorePlan<T> PrepareRestore(RevisionComparison<T> comparison, T current)
    {
        if (!IsCurrent(comparison)) throw new InvalidOperationException("对比对象已变化，请重新打开对比。");
        // Read the protected stored version, never a possibly mutated preview
        // copy. The caller applies this plan through its native Undo operation.
        return new RevisionRestorePlan<T>(comparison.Identity, clone(current),
            clone(ProtectedPrevious(comparison)));
    }

    internal void Clear()
    {
        baselines.Clear();
        pinnedComparison = null;
        selected = null;
        sessionKey = null;
        // Never reuse tokens: dialogs from a closed session must stay invalid.
        generation++;
        selectionGeneration++;
    }
}
