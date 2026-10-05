using System;
using System.Collections.Generic;
using System.Text.Json;
using AzureArchive.Automation;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;

namespace AzureArchive.RevisionCompare;

internal sealed record RestoreResult(bool Success, string Message);

internal sealed record NativeLineSnapshot(ScriptData Raw, ScriptData Preview, ScriptData? PreviousStage);

internal sealed class NativeComparison
{
    internal RevisionComparison<NativeLineSnapshot> State { get; }
    internal RevisionIdentity Identity => State.Identity;
    internal long Token => State.BaselineGeneration;
    internal ScriptData Previous => State.Previous.Raw;
    internal ScriptData Current => State.Current.Raw;
    internal ScriptData PreviousPreviewData => State.Previous.Preview;
    internal ScriptData CurrentPreviewData => State.Current.Preview;
    internal string PreviousFingerprint => NativeRevisionAdapter.Fingerprint(Previous);
    internal string CurrentFingerprint => NativeRevisionAdapter.Fingerprint(Current);
    internal bool HasChanges => PreviousFingerprint != CurrentFingerprint;
    internal NativeComparison(RevisionComparison<NativeLineSnapshot> state) => State = state;
}

internal sealed class NativeRevisionAdapter
{
    private readonly RevisionStore<NativeLineSnapshot> store = new(CloneSnapshot, SameSnapshot);
    private ScriptNodeInspector? inspector;
    private bool restoring;
    private sealed record PendingPlayback(RevisionIdentity Identity, NativeLineSnapshot Snapshot, float Due);
    private PendingPlayback? pendingPlayback;

    internal bool HasBaseline => store.HasBaseline;
    internal string SelectedLineId => store.Selected?.LineId ?? "";
    internal string CurrentText => TryResolve(out _, out _, out var line, out _) ? line.text ?? "" : "";
    internal string BaselineText => store.BaselineCopy()?.Raw.text ?? "";
    internal bool IsRestoring => restoring;

    internal void Observe(ScriptNodeInspector? value)
    {
        inspector = value;
        if (AuthoringEditorSession.Current == null) { Clear(); return; }
        if (TryResolve(out _, out _, out var line, out var identity))
        {
            if (store.Selected != identity) FinishPendingBeforeSelection(identity);
            // Selection itself never counts as another playback.
            if (store.Selected != identity || !store.HasBaseline) store.Observe(identity, Capture(line));
        }
        else { FinishPendingBeforeSelection(null); store.Observe(null, null); }
    }

    internal bool CaptureAtManualPreview(ScriptNodeInspector? value)
    {
        Observe(value);
        if (restoring || !TryResolve(out _, out _, out var line, out var identity)) return false;
        pendingPlayback = null;
        return store.CaptureAtManualPreview(identity, Capture(line));
    }

    internal bool CaptureAtManualPreview() => CaptureAtManualPreview(inspector);

    internal void QueueAutomaticPreview(ScriptNodeInspector value)
    {
        if (restoring) return;
        Observe(value);
        if (!TryResolve(out _, out _, out var line, out var identity)) return;
        var snapshot = Capture(line);
        var lastPlayed = store.LatestPlayedCopy();
        if (lastPlayed == null)
        {
            // Keep the first actual automatic preview immediately. Otherwise
            // fast typing before the debounce expires could lose the first A.
            pendingPlayback = null;
            store.CaptureAtManualPreview(identity, snapshot);
        }
        else if (SameSnapshot(lastPlayed, snapshot)) pendingPlayback = null;
        else if (pendingPlayback == null || pendingPlayback.Identity != identity || !SameSnapshot(pendingPlayback.Snapshot, snapshot))
            pendingPlayback = new(identity, snapshot, UnityEngine.Time.unscaledTime + .9f);
    }

    internal bool CommitAutomaticPreview(bool force = false)
    {
        var pending = pendingPlayback;
        if (restoring || pending == null || (!force && UnityEngine.Time.unscaledTime < pending.Due)) return false;
        pendingPlayback = null;
        if (!TryResolve(out _, out _, out _, out var identity) || identity != pending.Identity) return false;
        // Store the captured playback, even if newer unplayed edits exist now.
        // Only another actual preview replaces the pending batch snapshot.
        return store.CaptureAtManualPreview(identity, pending.Snapshot);
    }

    private void FinishPendingBeforeSelection(RevisionIdentity? next)
    {
        var pending = pendingPlayback;
        pendingPlayback = null;
        var session = AuthoringEditorSession.Current;
        if (pending != null && store.Selected == pending.Identity &&
            (next?.ProjectId ?? session?.ProjectId) == pending.Identity.ProjectId &&
            (next?.EditSession ?? session?.EditSession) == pending.Identity.EditSession)
            store.CaptureAtManualPreview(pending.Identity, pending.Snapshot);
    }

    internal NativeComparison CaptureComparison()
    {
        CommitAutomaticPreview(true);
        if (!TryResolve(out _, out _, out var line, out var identity))
            throw new InvalidOperationException("请先选中一条对话。");
        var snapshot = Capture(line);
        store.Observe(identity, snapshot);
        return new NativeComparison(store.CaptureComparison(snapshot));
    }

    internal NativeComparison RefreshComparison(NativeComparison comparison)
    {
        if (!IsCurrent(comparison) || !TryResolve(out _, out _, out var line, out _))
            throw new InvalidOperationException("对比对象已变化，请重新打开对比。");
        return new NativeComparison(store.RefreshComparison(comparison.State, Capture(line)));
    }

    internal bool IsCurrent(NativeComparison comparison)
    {
        return comparison != null && TryResolve(out _, out _, out _, out var identity) &&
            identity == comparison.Identity && store.IsCurrent(comparison.State);
    }

    internal void PinComparison(NativeComparison comparison) => store.PinComparison(comparison.State);
    internal void ReleaseComparison() => store.ReleaseComparison();

    internal RestoreResult Restore(NativeComparison comparison)
    {
        if (restoring) return new(false, "正在恢复，请稍候。");
        if (!IsCurrent(comparison) || !TryResolve(out var session, out var node, out var line, out _))
            return new(false, "对比对象已变化，请重新打开对比。");
        if (session.SaveInProgress || session.IsApplying || inspector == null || inspector.loading || inspector.unloading)
            return new(false, "编辑器正在处理操作，请稍后再恢复。");
        var manager = OperationManager.Instance;
        if (manager == null || OperationManager.Working) return new(false, "编辑器正在处理操作，请稍后再恢复。");
        pendingPlayback = null;
        var plan = store.PrepareRestore(comparison.State, Capture(line));
        var before = Clone(plan.Before.Raw);
        var after = Clone(plan.After.Raw);
        if (!string.Equals(before.LineId, after.LineId, StringComparison.Ordinal))
            return new(false, "对话身份不一致，未进行恢复。");
        if (Fingerprint(before) == Fingerprint(after)) return new(true, "这条对话已经是上一版。");
        ScenarioScriptInspectorModifyOperation? operation = null;
        bool redoStarted = false, historyWritten = false;
        restoring = true;
        try
        {
            TraceRestoreHistory("before-create", null);
            // Keep the operation scoped to one line. The current contents are
            // captured now, so Undo also preserves edits made while comparing.
            operation = new ScenarioScriptInspectorModifyOperation(node, line, before, after,
                ScenarioScriptInspectorModifyOperation.CombineType.None);
            TraceRestoreHistory("after-create", operation);
            // Native Undo/Redo sets this guard while applying an operation.
            // The line operation already synchronizes the inspector; without
            // the guard its selection refresh records a separate state change.
            var previousWorking = OperationManager.Working;
            try
            {
                OperationManager._Working_k__BackingField = true;
                redoStarted = true;
                operation.Redo();
                TraceRestoreHistory("after-redo", operation);
            }
            finally { OperationManager._Working_k__BackingField = previousWorking; }
            OperationManager.WriteIsolated(operation);
            historyWritten = true;
            TraceRestoreHistory("after-write-isolated", operation);
            session.AcceptUiChange();
            TraceRestoreHistory("after-accept-ui-change", operation);
            return new(true, "已恢复这条对话；可使用原生撤销返回修改版。");
        }
        catch (Exception error)
        {
            // WriteIsolated can push successfully and then throw while updating
            // native controls. Never roll back an operation that is in history.
            if (!historyWritten && operation != null)
            {
                try
                {
                    var history = OperationManager.history;
                    historyWritten = history != null && history.Count > 0 &&
                        history.Peek().Pointer == operation.Pointer;
                }
                catch (Exception historyError)
                {
                    return new(false, "恢复发生异常，无法确认撤销记录；请核对当前条目。" +
                        error.Message + "；撤销记录检查失败：" + historyError.Message);
                }
            }
            if (historyWritten)
                return new(true, "已恢复这条对话并保留原生撤销；编辑器状态刷新未完成：" + error.Message);
            if (!redoStarted || operation == null)
                return new(false, "恢复未开始：" + error.Message);

            // Redo may replace the line before a native refresh fails. Restore
            // its captured Before state without generating another Undo entry.
            try
            {
                var previousWorking = OperationManager.Working;
                try
                {
                    OperationManager._Working_k__BackingField = true;
                    operation.Undo();
                }
                finally { OperationManager._Working_k__BackingField = previousWorking; }
                TraceRestoreHistory("after-failure-rollback", operation);
                bool restoredBefore = false;
                if (node != null && node.scripts != null)
                    for (int i = 0; i < node.scripts.Count; i++)
                    {
                        var candidate = node.scripts[i];
                        if (candidate != null && candidate.LineId == before.LineId)
                        {
                            restoredBefore = Fingerprint(new ScriptData(candidate)) == Fingerprint(before);
                            break;
                        }
                    }
                return restoredBefore
                    ? new(false, "恢复未完成，已回退到恢复前的内容：" + error.Message)
                    : new(false, "恢复未完成，回退后的内容未能核实；请核对当前条目。" + error.Message);
            }
            catch (Exception rollbackError)
            {
                return new(false, "恢复未完成且自动回退失败；当前条目可能已改变，请先核对内容。" +
                    error.Message + "；回退失败：" + rollbackError.Message);
            }
        }
        finally { restoring = false; }
    }

    private static void TraceRestoreHistory(string phase, Operation? restoration)
    {
        if (typeof(RevisionComparePlugin).Assembly.GetType("AzureArchive.RevisionCompare.IntegrationProbe") == null) return;
        try
        {
            var history = OperationManager.history;
            var future = OperationManager.future;
            Operation? top = history != null && history.Count > 0 ? history.Peek() : null;
            RevisionComparePlugin.Instance?.Log.LogInfo("REVISION RESTORE HISTORY " + JsonSerializer.Serialize(new
            {
                phase,
                historyCount = history?.Count ?? -1,
                futureCount = future?.Count ?? -1,
                working = OperationManager.Working,
                operation = DescribeOperation(restoration),
                top = DescribeOperation(top),
                topIsRestoration = top != null && restoration != null && top.Pointer == restoration.Pointer
            }));
        }
        catch (Exception error)
        {
            // Diagnostics must never alter whether the user's edit succeeds.
            RevisionComparePlugin.Instance?.Log.LogWarning("Revision restore history diagnostics: " + error.Message);
        }
    }

    private static object? DescribeOperation(Operation? operation) => operation == null ? null : new
    {
        type = operation.GetIl2CppType().FullName,
        pointer = operation.Pointer.ToInt64(),
        operation.isEmpty,
        operation.isTransparent,
        operation.IsMajor,
        linkedPrev = operation.linkedPrev?.Pointer.ToInt64(),
        linkedNext = operation.linkedNext?.Pointer.ToInt64()
    };

    internal Script CreatePreviewLine(NativeComparison comparison, bool previous = true)
    {
        if (!IsCurrent(comparison)) throw new InvalidOperationException("对比对象已变化，请重新打开对比。");
        var snapshot = previous ? comparison.State.Previous : comparison.State.Current;
        var result = AuthoringCompiler.Copy(Clone(snapshot.Preview));
        if (snapshot.PreviousStage != null)
            result.prev = AuthoringCompiler.Copy(Clone(snapshot.PreviousStage));
        return result;
    }

    internal void Clear()
    {
        pendingPlayback = null;
        store.Clear();
        inspector = null;
    }

    private bool TryResolve(out AuthoringEditorSession session, out ScriptNode node, out Script line,
        out RevisionIdentity identity)
    {
        session = AuthoringEditorSession.Current!;
        node = null!;
        line = null!;
        identity = null!;
        var view = inspector;
        if (session == null || view == null || view.loading || view.unloading) return false;
        var item = view.selectedScriptItem;
        if (item == null) return false;
        node = item.scriptNode;
        if (node == null || node.deletionMarked || node.scripts == null || item.index < 0 || item.index >= node.scripts.Count)
            return false;
        line = node.scripts[item.index];
        if (line == null || string.IsNullOrEmpty(line.LineId)) return false;
        identity = new RevisionIdentity(session.ProjectId, session.EditSession, node.guid.ToString(), line.LineId);
        return true;
    }

    private static ScriptData Clone(ScriptData value) => AuthoringProjectSnapshot.CloneLine(value);

    private static NativeLineSnapshot CloneSnapshot(NativeLineSnapshot value) =>
        new(Clone(value.Raw), Clone(value.Preview), value.PreviousStage == null ? null : Clone(value.PreviousStage));

    private static bool SameSnapshot(NativeLineSnapshot first, NativeLineSnapshot second) =>
        Fingerprint(first.Raw) == Fingerprint(second.Raw) &&
        Fingerprint(first.Preview) == Fingerprint(second.Preview) &&
        (first.PreviousStage == null ? second.PreviousStage == null :
            second.PreviousStage != null && Fingerprint(first.PreviousStage) == Fingerprint(second.PreviousStage));

    private NativeLineSnapshot Capture(Script line)
    {
        var raw = Clone(new ScriptData(line));
        var preview = EffectivePreview(line, raw);
        // BGMId is a raw field on this AA build. Resolve its 0 = inherit value
        // through the editor now so later edits to earlier lines cannot change
        // the music of the captured version.
        int selectedIndex = inspector!.selectedScriptItem.index;
        preview.bgmId = inspector.ResolveBgmIdForPreview(selectedIndex);
        // Character records contain the entire stage, including start/end
        // positions. A frozen immediate predecessor supplies movement context;
        // resolving inherited environment through native getters removes the
        // need to copy an unbounded live prev chain.
        ScriptData? previous = null;
        var predecessor = line.prev;
        if (predecessor != null && predecessor.Pointer != line.Pointer)
        {
            previous = EffectivePreview(predecessor, new ScriptData(predecessor));
            previous.bgmId = inspector.ResolveBgmIdForPreview(selectedIndex - 1);
        }
        return new NativeLineSnapshot(raw, preview, previous);
    }

    private static ScriptData EffectivePreview(Script source, ScriptData raw)
    {
        var result = Clone(raw);
        result.bgName = source.BGName;
        result.bgEffect = source.BGEffect;
        result.bgmId = source.BGMId;
        return result;
    }

    // A deterministic value projection avoids serializing IL2CPP wrapper
    // pointers, and includes every editable ScriptData field in this host.
    internal static string Fingerprint(ScriptData value)
    {
        var characters = new List<object?>();
        if (value.characters != null)
            for (int i = 0; i < value.characters.Count; i++)
            {
                var c = value.characters[i];
                characters.Add(c == null ? null : new
                {
                    c.name, c.faceId, c.startingPos, c.endingPos, c.displayOrder,
                    emoticon = (int)c.emoticon, action = (int)c.action, effect = (int)c.effect,
                    appear = (int)c.appear, shapeOverride = (int)c.shapeOverride
                });
            }
        var highlights = new List<int>();
        if (value.highlightedSlotNums != null)
            for (int i = 0; i < value.highlightedSlotNums.Count; i++) highlights.Add(value.highlightedSlotNums[i]);
        return JsonSerializer.Serialize(new
        {
            value.LineId, value.text, value.popup, value.bgEffect, value.bgName, value.bgFriendlyName,
            value.sound, value.voice, value.transition, value.bgmId, value.selectionGroup,
            value.additionalPrompt, value.speakerSlotNum, value.isDialogScript, value.placeText,
            characters, highlightedSlotNums = highlights
        });
    }
}
