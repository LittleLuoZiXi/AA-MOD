using System;
using System.Linq;
using System.Reflection;
using AzureArchive.Automation;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Studio.Scripts;
using UnityEngine;
using UnityEngine.Events;

namespace AzureArchive.RevisionCompare;

[BepInPlugin("azurearchive.revisioncompare", "AzureArchiveRevisionCompare", "1.0.0")]
public sealed class RevisionComparePlugin : BasePlugin
{
    internal static RevisionComparePlugin Instance = null!;
    public override void Load()
    {
        Instance = this;
        var harmony = new Harmony("azurearchive.revisioncompare");
        try
        {
            var probe = GetType().Assembly.GetType("AzureArchive.RevisionCompare.IntegrationProbe");
            bool skipPreview = probe != null && Environment.GetCommandLineArgs().Contains("--revision-core-only");
            Log.LogInfo("Initializing preview hooks.");
            if (!skipPreview) ComparisonPreview.InstallHooks(harmony);
            Log.LogInfo("Initializing selection hooks.");
            harmony.PatchAll(typeof(RevisionComparePlugin).Assembly);
            Log.LogInfo("Initializing revision behaviour.");
            AddComponent<RevisionCompareBehaviour>();
            // The integration probe is compiled only into development builds.
            // Normal releases contain neither test hooks nor test commands.
            Log.LogInfo("Initializing development probe when present.");
            probe?.GetMethod("Install", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { harmony });
        }
        catch { harmony.UnpatchSelf(); throw; }
        Log.LogInfo("RevisionCompare 1.0.0 ready: distinct automatic/manual previews retain previous and latest versions; open comparison stays pinned during current playback.");
    }
}

public sealed class RevisionCompareBehaviour : MonoBehaviour
{
    internal static RevisionCompareBehaviour? Instance;
    internal readonly NativeRevisionAdapter Adapter = new();
    internal NativeCompareUi? Ui;
    internal NativeComparison? ActiveComparison;
    private UI.MXButton? playbackButton;
    private UnityAction? playbackListener;
    private string lastError = "";
    private float nextError;
    private MethodInfo? probeTick;
    private int suppressPlaybackCapture;

    public RevisionCompareBehaviour(IntPtr pointer) : base(pointer) { }
    public void Awake()
    {
        Instance = this;
        Ui = new NativeCompareUi(OpenComparison, RestoreComparison, CloseComparison, Report);
        probeTick = GetType().Assembly.GetType("AzureArchive.RevisionCompare.IntegrationProbe")?.GetMethod("Tick", BindingFlags.Static | BindingFlags.NonPublic);
    }

    public void Update()
    {
        try
        {
            var inspector = ScriptNodeInspector.instance;
            Adapter.Observe(inspector);
            if (inspector != null && AuthoringEditorSession.Current != null)
            {
                BindPlaybackButton(inspector);
                if (ActiveComparison != null && !Adapter.IsCurrent(ActiveComparison))
                {
                    CloseComparison();
                    RecordNativePreview(inspector);
                }
                if (Ui?.IsVisible != true || !Ui.IsPlayingPrevious) Adapter.CommitAutomaticPreview();
                Ui!.Update(inspector, Adapter.HasBaseline, "上一次不同内容的播放版本");
            }
            else
            {
                CloseComparison();
                UnbindPlaybackButton();
            }
            if (Input.GetKeyDown(KeyCode.Escape) && Ui?.IsVisible == true) CloseComparison();
            probeTick?.Invoke(null, new object[] { this });
        }
        catch (Exception error) { Report(error.InnerException?.Message ?? error.Message); }
    }

    [HideFromIl2Cpp]
    private void BindPlaybackButton(ScriptNodeInspector inspector)
    {
        var go = inspector.transform.Find("Preview/PlayPreviewBtn");
        var current = go == null ? null : go.GetComponent<UI.MXButton>();
        if (current == null || (playbackButton != null && current.Pointer == playbackButton.Pointer)) return;
        UnbindPlaybackButton();
        playbackButton = current;
        playbackListener = (UnityAction)(Action)(() =>
        {
            try
            {
                // A manual click commits immediately. Full-content deduplication
                // prevents the automatic preview of this same draft from pushing
                // the preceding version out of history.
                if (current.disabled || !current.gameObject.activeInHierarchy) return;
                if (Ui?.IsVisible == true) Ui.PlayCurrent();
                if (Adapter.CaptureAtManualPreview(inspector))
                    RevisionComparePlugin.Instance.Log.LogInfo("Distinct played version recorded for " + Adapter.SelectedLineId);
            }
            catch (Exception ex) { Report(ex.Message); }
        });
        current.onClick.AddListener(playbackListener);
    }

    [HideFromIl2Cpp]
    private void UnbindPlaybackButton()
    {
        if (playbackButton != null && playbackListener != null) playbackButton.onClick.RemoveListener(playbackListener);
        playbackButton = null; playbackListener = null;
    }

    [HideFromIl2Cpp]
    internal void OpenComparison()
    {
        var inspector = ScriptNodeInspector.instance;
        Adapter.Observe(inspector);
        if (!Adapter.HasBaseline || inspector?.preview == null) { Report("请先选中一条对话。"); return; }
        if (Ui?.IsVisible == true) { CloseComparison(); return; }
        ActiveComparison = Adapter.CaptureComparison();
        var line = Adapter.CreatePreviewLine(ActiveComparison);
        Adapter.PinComparison(ActiveComparison);
        try { Ui!.Show(inspector.preview, line.Cast<FlatData.IScenarioScriptExcel>()); }
        catch { CloseComparison(); throw; }
    }

    [HideFromIl2Cpp]
    internal void RestoreComparison()
    {
        if (ActiveComparison == null) return;
        var result = Adapter.Restore(ActiveComparison);
        if (result.Success) CloseComparison();
        Report(result.Message);
    }

    [HideFromIl2Cpp]
    internal void CloseComparison()
    {
        suppressPlaybackCapture++;
        try { Ui?.Hide(); ActiveComparison = null; Adapter.ReleaseComparison(); }
        finally { suppressPlaybackCapture--; }
    }

    [HideFromIl2Cpp]
    internal void RecordNativePreview(ScriptNodeInspector inspector)
    {
        if (suppressPlaybackCapture != 0 || (Ui?.IsVisible == true && Ui.IsPlayingPrevious) || Adapter.IsRestoring) return;
        Adapter.QueueAutomaticPreview(inspector);
    }

    [HideFromIl2Cpp]
    internal void Report(string message)
    {
        if (lastError == message && Time.realtimeSinceStartup < nextError) return;
        lastError = message; nextError = Time.realtimeSinceStartup + 5;
        RevisionComparePlugin.Instance.Log.LogInfo(message);
        try { Studio.Scripts.NotificationManager.Instance.Notify(message); } catch { }
    }

    public void OnDestroy()
    {
        UnbindPlaybackButton(); Ui?.Dispose(); Ui = null; Adapter.Clear();
        if (Instance == this) Instance = null;
    }
}

[HarmonyPatch(typeof(ScriptNodeInspector), nameof(ScriptNodeInspector.PlayPreview))]
internal static class AutomaticPlaybackHistory
{
    private static void Postfix(ScriptNodeInspector __instance, bool __runOriginal)
    {
        if (!__runOriginal) return;
        try { RevisionCompareBehaviour.Instance?.RecordNativePreview(__instance); }
        catch (Exception ex) { RevisionComparePlugin.Instance.Log.LogWarning(ex.Message); }
    }
}

[HarmonyPatch(typeof(ScriptListItem), nameof(ScriptListItem.Select))]
internal static class SelectionBaseline
{
    private static void Postfix()
    {
        // Observe selection before a following input event can edit the line.
        try { RevisionCompareBehaviour.Instance?.Adapter.Observe(ScriptNodeInspector.instance); }
        catch (Exception ex) { RevisionComparePlugin.Instance.Log.LogWarning(ex.Message); }
    }
}
