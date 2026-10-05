using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AzureArchive.Automation;
using BepInEx;
using HarmonyLib;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AzureArchive.RevisionCompare;

internal static class IntegrationProbe
{
    private static bool enabled, done;
    private static string root = "", evidence = "";
    private static float next, started;
    private static int stage;
    private static readonly List<string> checks = new();
    private static ScriptNode? node;
    private static string neighbor = "";
    private static NativeComparison? comparison;
    private static int history;
    private static IntPtr nativeTexture;
    private const string Before = "这是上次播放的版本。";
    private const string After = "这是本轮修改后的版本。";
    private static bool CoreOnly => Environment.GetCommandLineArgs().Contains("--revision-core-only");

    internal static void Install(Harmony harmony)
    {
        enabled = Environment.GetCommandLineArgs().Contains("--revision-compare-probe");
        if (!enabled) return;
        root = Path.GetFullPath(Paths.GameRootPath);
        evidence = Path.Combine(root, "evidence");
        var markerPath = Path.Combine(root, "RevisionTestHost.json");
        if (!File.Exists(markerPath)) throw new InvalidOperationException("Isolated test marker missing.");
        using var json = JsonDocument.Parse(File.ReadAllText(markerPath));
        if (json.RootElement.GetProperty("kind").GetString() != "RevisionCompareIsolatedHost" ||
            !string.Equals(root.TrimEnd('\\'), json.RootElement.GetProperty("root").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid isolated test host.");
        if (!File.Exists(Path.Combine(evidence, "fixture.aap2"))) throw new InvalidOperationException("Synthetic fixture missing.");
        harmony.Patch(AccessTools.PropertyGetter(typeof(Application), nameof(Application.persistentDataPath)),
            prefix: new HarmonyMethod(typeof(IntegrationProbe), nameof(PersistentPath)));
        Application.runInBackground = true;
    }

    private static bool PersistentPath(ref string __result)
    {
        if (!enabled) return true;
        __result = Path.Combine(root, "UserData"); return false;
    }

    private static string fingerprintA = "", fingerprintB = "", fingerprintC = "", fingerprintD = "";
    private static NativeComparison? stableComparison;
    private const string AutomaticDraft = "自动预览合并后的第三稿。";
    private const string ThirdPlayback = "第三次不同内容的正式播放。";

    internal static void Tick(RevisionCompareBehaviour owner)
    {
        if (!enabled || done) return;
        try
        {
            if (started == 0) started = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - started > 180) throw new TimeoutException("Probe timed out at stage " + stage);
            if (Time.realtimeSinceStartup < next) return;
            var inspector = ScriptNodeInspector.instance;
            switch (stage)
            {
                case 0:
                    var settings = Object.FindObjectOfType<UserSettings>();
                    if (settings == null || !settings.isReady || AuthoringWorkbench.Instance == null || ScenarioResourceManager.Instance == null || !ScenarioResourceManager.Instance.AreDbsLoaded) return;
                    Check(Application.companyName == "aarevtest" && Application.productName == "AARevCompare", "Dedicated Unity identity");
                    Check(settings.settingFilePath.StartsWith(root, StringComparison.OrdinalIgnoreCase), "User settings isolated inside test host: " + settings.settingFilePath);
                    Check(settings.WorkspacePath.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Workspace isolated inside test host");
                    AuthoringWorkbench.OpenProject(Path.Combine(evidence, "fixture.aap2"));
                    Go(1, 4); break;
                case 1:
                    if (AuthoringEditorSession.Current == null || inspector == null) return;
                    node = Object.FindObjectsOfType<ScriptNode>().FirstOrDefault(x => x.scripts != null && x.scripts.Count == 2);
                    if (node == null) return;
                    inspector.Load(node.Cast<Node>(), false); inspector.Show();
                    Go(2, 2); break;
                case 2:
                    if (inspector!.scriptNodeListItemsCache == null || inspector.scriptNodeListItemsCache.Count < 2) return;
                    inspector.scriptNodeListItemsCache[0].Select(false);
                    owner.Adapter.Observe(inspector);
                    node!.scripts[1].bgmId = 0;
                    neighbor = Fingerprint(1);
                    fingerprintA = Fingerprint(0);
                    Check(owner.Adapter.CurrentText == Before && node.scripts[0].bgmId == 37, "Synthetic first line starts at A with BGM 37");
                    inspector.SyncAndPlayPreview();
                    Go(3, 1.3f); break;
                case 3:
                    ManualPlay(inspector!);
                    Check(owner.Adapter.BaselineText == Before, "Initial automatic and manual A share one played version");
                    node!.scripts[0].bgmId = Script.MuteBgmId;
                    inspector!.SetBGM();
                    Edit(inspector, After);
                    Go(4, 1.3f); break;
                case 4:
                    Check(owner.Adapter.CurrentText == After, "Native text editing produces B");
                    fingerprintB = Fingerprint(0);
                    Check(owner.Adapter.BaselineText == Before, "Automatic B preserves preceding played A");
                    ManualPlay(inspector!);
                    comparison = owner.Adapter.CaptureComparison();
                    Check(comparison.PreviousFingerprint == fingerprintA && comparison.CurrentFingerprint == fingerprintB, "Second manual B compares full A against full B");
                    Go(5, 2); break;
                case 5:
                    Check(inspector!.ResolveBgmIdForPreview(0) == Script.MuteBgmId, "Edited B has a different music setting from A");
                    Check(!BgmIsPlaying(37), "Current muted B stops A music before comparison");
                    if (!CoreOnly)
                    {
                        var entry = Object.FindObjectsOfType<UI.MXButton>().First(x => x.name == "AARevisionCompare_Entry");
                        var play = inspector.transform.Find("Preview/PlayPreviewBtn");
                        Check(entry.transform.localPosition.x < play.localPosition.x, "Compare icon is left of native play button");
                        Check(!entry.GetComponentsInChildren<UILabel>().Any(x => x.gameObject.activeInHierarchy), "Compare entry is icon only");
                    }
                    OpenComparison(owner, inspector);
                    CheckPair(owner, fingerprintA, fingerprintB, "Opening comparison retains immutable A and current B");
                    Go(6, 5); break;
                case 6:
                    DumpHierarchy(); SaveFrame(Path.Combine(evidence, "comparison.ppm"));
                    if (!CoreOnly)
                    {
                        Check(owner.Ui!.IsVisible && owner.Ui.PreviewReady, "Floating native old-version preview is visible and ready");
                        Check(BgmIsPlaying(37), "Floating A replays original BGM through native audio source");
                        File.WriteAllText(Path.Combine(evidence, "preview-text.json"), JsonSerializer.Serialize(new { text = owner.Ui.PreviewText }));
                        File.WriteAllText(Path.Combine(evidence, "preview-labels.json"), JsonSerializer.Serialize(inspector!.preview.dialogPanel.GetComponentsInChildren<UILabel>(true).Select(x => new { path = PathOf(x.transform), text = x.text, active = x.gameObject.activeInHierarchy }).ToArray()));
                        Check(VisibleText(owner).Contains(Before), "Floating native preview displays A");
                        var viewport = inspector!.transform.Find("Preview").GetComponent<UIWidget>();
                        var frame = owner.Ui.PreviewTexture!;
                        Check(Math.Abs((double)frame.width / frame.height - (double)viewport.width / viewport.height) < .004, "Floating preview retains native aspect ratio");
                        Check(frame.Pointer == nativeTexture && inspector.transform.Find("Preview").GetComponent<UITexture>().mainTexture.Pointer != nativeTexture, "Live native output is borrowed while right display keeps an independent frozen frame");
                    }
                    Check(Fingerprint(0) == fingerprintB, "Playing old A leaves all current B fields intact");
                    stableComparison = owner.Adapter.CaptureComparison();
                    if (!CoreOnly) Click("AARevisionCompare_Replay");
                    Go(7, 3); break;
                case 7:
                    Check(owner.Adapter.IsCurrent(stableComparison!), "Replaying old A does not create a playback version");
                    if (!CoreOnly) Check(owner.Ui!.PreviewReady && VisibleText(owner).Contains(Before), "Old-version replay stays on A");
                    ManualPlay(inspector!);
                    if (!CoreOnly) Check(owner.Ui!.IsVisible && !owner.Ui.IsPlayingPrevious && inspector!.transform.Find("Preview").GetComponent<UITexture>().mainTexture.Pointer == nativeTexture, "Native B playback keeps comparison open and returns live output to the right side");
                    Go(8, 1.3f); break;
                case 8:
                    CheckPair(owner, fingerprintA, fingerprintB, "Repeated manual B still compares against A");
                    Check(owner.Adapter.IsCurrent(stableComparison!), "Repeated B and comparison close refresh do not advance the revision token");
                    if(!CoreOnly)
                    {
                        Check(VisibleText(owner).Contains(Before) && NativeText(inspector!).Contains(After),"Old A remains frozen in the open window while native right preview plays B");
                        Check(!BgmIsPlaying(37),"Right-side playback uses current B music instead of old A music");
                        SaveFrame(Path.Combine(evidence,"window-kept-open.ppm"));
                        Click("AARevisionCompare_Replay");
                        Go(80,2);break;
                    }
                    inspector!.SyncAndPlayPreview();
                    Go(9, 1.3f); break;
                case 80:
                    Check(owner.Ui!.IsVisible && owner.Ui.IsPlayingPrevious && VisibleText(owner).Contains(Before),"Floating replay switches back to old A without closing the window");
                    Check(BgmIsPlaying(37),"Switching back to old A restores its music");
                    ManualPlay(inspector!);
                    Go(81,1.3f);break;
                case 81:
                    Check(owner.Ui!.IsVisible && !owner.Ui.IsPlayingPrevious && VisibleText(owner).Contains(Before),"Second right-side replay still preserves the same old window");
                    inspector!.SyncAndPlayPreview();
                    Go(9,1.3f);break;
                case 9:
                    CheckPair(owner, fingerprintA, fingerprintB, "Repeated identical automatic B is deduplicated");
                    Check(owner.Adapter.IsCurrent(stableComparison!), "Identical automatic B preserves the revision token");
                    OpenComparison(owner, inspector!);
                    history = OperationManager.history.Count;
                    Go(10, 2); break;
                case 10:
                    comparison = CoreOnly ? owner.Adapter.CaptureComparison() : owner.ActiveComparison;
                    Check(comparison != null && comparison.PreviousFingerprint == fingerprintA, "Restore target remains full original A after repeated playback");
                    if (CoreOnly) Check(owner.Adapter.Restore(comparison!).Success, "Single-line restore reports success");
                    else Click("AARevisionCompare_Restore");
                    Go(11, 1); break;
                case 11:
                    if (!CoreOnly) Check(!owner.Ui!.IsVisible && inspector!.transform.Find("Preview").GetComponent<UITexture>().mainTexture.Pointer == nativeTexture, "Restore closes comparison and returns native preview texture");
                    Check(owner.Adapter.CurrentText == Before && Fingerprint(0) == fingerprintA, "Restore returns every serialized field of the target line to A");
                    Check(Fingerprint(1) == neighbor, "Neighbor line is unchanged by restore");
                    File.AppendAllText(Path.Combine(evidence, "progress.txt"), $"Undo history entries before restore: {history}; after: {OperationManager.history.Count}\n");
                    OperationManager.Undo();
                    Go(12, 1); break;
                case 12:
                    Check(owner.Adapter.CurrentText == After && Fingerprint(0) == fingerprintB, "One native Undo restores all fields of edited B");
                    Check(Fingerprint(1) == neighbor, "One Undo leaves neighbor untouched");
                    OperationManager.Redo();
                    Go(13, 1.3f); break;
                case 13:
                    Check(owner.Adapter.CurrentText == Before && Fingerprint(0) == fingerprintA, "One native Redo restores all fields of A");
                    Check(Fingerprint(1) == neighbor, "One Redo leaves neighbor untouched");
                    // Start an independent playback history after verifying native
                    // restore/Undo/Redo. This changes only the test's revision cache.
                    owner.CloseComparison(); owner.Adapter.Clear(); owner.Adapter.Observe(inspector);
                    ManualPlay(inspector!);
                    Check(owner.Adapter.BaselineText == Before, "Independent automatic-edit scenario starts from played A");
                    Edit(inspector!, "连续自动修改一");
                    Go(14, .2f); break;
                case 14:
                    Edit(inspector!, "连续自动修改二");
                    Go(15, .2f); break;
                case 15:
                    Edit(inspector!, AutomaticDraft);
                    Go(16, 1.3f); break;
                case 16:
                    fingerprintC = Fingerprint(0);
                    Check(owner.Adapter.CurrentText == AutomaticDraft, "Continuous native edits reach final automatic C");
                    CheckPair(owner, fingerprintA, fingerprintC, "Three automatic edits inside the merge window create only final C after A");
                    stableComparison = owner.Adapter.CaptureComparison();
                    inspector!.SyncAndPlayPreview();
                    Go(17, 1.3f); break;
                case 17:
                    Check(owner.Adapter.IsCurrent(stableComparison!), "Identical automatic C does not create another version");
                    OpenComparison(owner, inspector!);
                    Go(18, 3); break;
                case 18:
                    if (!CoreOnly)
                    {
                        Check(owner.Ui!.PreviewReady && VisibleText(owner).Contains(Before), "Merged automatic C compares against stable A");
                        SaveFrame(Path.Combine(evidence, "automatic-merge.ppm"));
                        Click("AARevisionCompare_Replay");
                    }
                    Go(19, 2); break;
                case 19:
                    Check(owner.Adapter.IsCurrent(stableComparison!), "Comparison replay does not enter the automatic revision history");
                    if (!CoreOnly) Click("AARevisionCompare_Close");
                    Go(20, 1.3f); break;
                case 20:
                    Check(owner.Adapter.IsCurrent(stableComparison!), "Closing comparison and refreshing current C does not create a version");
                    CheckPair(owner, fingerprintA, fingerprintC, "Automatic history remains A to C after comparison close");
                    inspector!.scriptNodeListItemsCache[1].Select(false); owner.Adapter.Observe(inspector);
                    Go(21, 1.3f); break;
                case 21:
                    Check(Fingerprint(1) == neighbor, "Switching selection preserves all neighbor fields");
                    OpenComparison(owner, inspector!);
                    Go(22, 4); break;
                case 22:
                    if (!CoreOnly)
                    {
                        Check(owner.Ui!.PreviewReady && VisibleText(owner).Contains("这一条应当保持不变。"), "Neighbor old-line preview works with a frozen predecessor stage");
                        Check(BgmIsPlaying(37), "Neighbor preview resolves inherited BGM from its frozen stage");
                    }
                    inspector!.scriptNodeListItemsCache[0].Select(false); owner.Adapter.Observe(inspector);
                    Go(23, 1.3f); break;
                case 23:
                    if (!CoreOnly) Check(!owner.Ui!.IsVisible, "Changing selection closes stale comparison");
                    CheckPair(owner, fingerprintA, fingerprintC, "Selecting another line and returning does not advance played C");
                    Edit(inspector!, ThirdPlayback);
                    ManualPlay(inspector!);
                    fingerprintD = Fingerprint(0);
                    CheckPair(owner, fingerprintC, fingerprintD, "Third distinct manual playback D compares against second version C");
                    stableComparison = owner.Adapter.CaptureComparison();
                    Go(24, 1.3f); break;
                case 24:
                    Check(owner.Adapter.IsCurrent(stableComparison!), "Automatic notification following manual D is deduplicated");
                    CheckPair(owner, fingerprintC, fingerprintD, "Delayed duplicate D does not replace previous C");
                    // Keep the dialogue text identical and change only one
                    // non-text field, so text-only deduplication cannot pass.
                    node!.scripts[0].bgmId = Script.MuteBgmId;
                    inspector!.SetBGM(); inspector.SyncAndPlayPreview();
                    Go(25, 1.3f); break;
                case 25:
                    var fieldOnly = owner.Adapter.CaptureComparison();
                    Check(fieldOnly.Previous.text == ThirdPlayback && fieldOnly.Current.text == ThirdPlayback, "Field-only edit keeps the same dialogue text");
                    Check(fieldOnly.PreviousFingerprint == fingerprintD && fieldOnly.CurrentFingerprint != fingerprintD && fieldOnly.Previous.bgmId == 37 && fieldOnly.Current.bgmId == Script.MuteBgmId, "A music-only automatic change advances the complete version history");
                    stableComparison = fieldOnly;
                    inspector!.SyncAndPlayPreview();
                    Go(26, 1.3f); break;
                case 26:
                    Check(owner.Adapter.IsCurrent(stableComparison!), "Identical full-field automatic preview is deduplicated");
                    OpenComparison(owner, inspector!);
                    Go(27, 4); break;
                case 27:
                    if (!CoreOnly)
                    {
                        Check(owner.Ui!.PreviewReady && VisibleText(owner).Contains(ThirdPlayback), "Field-only comparison still renders the preceding native dialogue");
                        Check(BgmIsPlaying(37), "Field-only comparison replays preceding version music");
                        SaveFrame(Path.Combine(evidence, "field-only-comparison.ppm"));
                        Click("AARevisionCompare_Close");
                    }
                    Go(28, 1.3f); break;
                case 28:
                    Check(owner.Adapter.IsCurrent(stableComparison!), "Closing field-only comparison does not overwrite its previous version");
                    Check(Fingerprint(1) == neighbor, "Neighbor remains unchanged across every playback, comparison, restore and selection");
                    if(CoreOnly){Finish(true,null);break;}
                    OpenComparison(owner,inspector!);
                    Go(29,2);break;
                case 29:
                    Check(owner.ActiveComparison!.PreviousFingerprint==fingerprintD,"Window pins the complete D snapshot including original music");
                    Edit(inspector!,"对比开窗后的新稿");
                    ManualPlay(inspector!);
                    Go(30,2);break;
                case 30:
                    Check(owner.Ui!.IsVisible && !owner.Ui.IsPlayingPrevious && NativeText(inspector!).Contains("对比开窗后的新稿"),"A different new draft plays on the right without closing the existing comparison");
                    Check(owner.Adapter.IsCurrent(owner.ActiveComparison!) && owner.ActiveComparison!.PreviousFingerprint==fingerprintD && VisibleText(owner).Contains(ThirdPlayback),"History can advance while the open comparison remains pinned to its original full snapshot");
                    Click("AARevisionCompare_Replay");
                    Go(31,2);break;
                case 31:
                    Check(owner.Ui!.IsPlayingPrevious && VisibleText(owner).Contains(ThirdPlayback) && BgmIsPlaying(37),"Pinned old version can still replay after recording a newer draft");
                    Click("AARevisionCompare_Restore");
                    Go(32,1);break;
                case 32:
                    Check(Fingerprint(0)==fingerprintD,"Restore uses the pinned displayed old version rather than a later history entry");
                    OperationManager.Undo();
                    Go(33,1);break;
                case 33:
                    Check(owner.Adapter.CurrentText=="对比开窗后的新稿","One Undo recovers the new draft played while the window stayed open");
                    Check(Fingerprint(1)==neighbor,"Pinned playback and restore preserve the neighbor line");
                    owner.Adapter.Clear();owner.Adapter.Observe(inspector);ManualPlay(inspector!);
                    Edit(inspector!,"取消选择前自动预览");
                    var pendingFingerprint=Fingerprint(0);
                    owner.Adapter.Observe(null);owner.Adapter.Observe(inspector);
                    Edit(inspector!,"重新选择后的新稿");ManualPlay(inspector!);
                    Check(owner.Adapter.CaptureComparison().PreviousFingerprint==pendingFingerprint,"Ending selection commits the captured pending automatic version before recording a later draft");
                    Finish(true,null);break;
            }
        }
        catch (Exception error) { Finish(false, error.ToString()); }
    }

    private static void Go(int value, float seconds)
    { stage = value; next = Time.realtimeSinceStartup + seconds; }

    private static string Fingerprint(int index) =>
        NativeRevisionAdapter.Fingerprint(new ScriptData(node!.scripts[index]));

    private static void Edit(ScriptNodeInspector inspector, string text)
    {
        inspector.contentInput.value = text;
        inspector.ApplyScriptText();
        inspector.SyncAndPlayPreview();
    }

    private static void ManualPlay(ScriptNodeInspector inspector) =>
        inspector.transform.Find("Preview/PlayPreviewBtn").GetComponent<UI.MXButton>().onClick.Invoke();

    private static void OpenComparison(RevisionCompareBehaviour owner, ScriptNodeInspector inspector)
    {
        if (CoreOnly) { comparison = owner.Adapter.CaptureComparison(); return; }
        if(owner.Ui?.IsVisible==true){comparison=owner.ActiveComparison;return;}
        RenderOffscreen();
        nativeTexture = inspector.transform.Find("Preview").GetComponent<UITexture>().mainTexture.Pointer;
        Click("AARevisionCompare_Entry");
        comparison = owner.ActiveComparison;
    }

    private static string VisibleText(RevisionCompareBehaviour owner) =>
        System.Text.RegularExpressions.Regex.Replace(owner.Ui!.PreviewText, @"\[[^\]]*\]", "");

    private static string NativeText(ScriptNodeInspector inspector) => string.Join("",inspector.preview.dialogPanel.GetComponentsInChildren<UILabel>().Where(x=>x.gameObject.activeInHierarchy).Select(x=>x.text));

    private static void CheckPair(RevisionCompareBehaviour owner, string previous, string current, string message)
    {
        var pair = owner.Adapter.CaptureComparison();
        Check(pair.PreviousFingerprint == previous && pair.CurrentFingerprint == current, message);
    }
    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("FAILED: " + message);
        checks.Add(message); File.AppendAllText(Path.Combine(evidence, "progress.txt"), message + "\n");
    }
    private static bool BgmIsPlaying(long id)
    {
        var manager = AudioManager.Instance;
        if(manager?.playingBGMs==null)return false;
        foreach(var entry in manager.playingBGMs)
            if(entry.Key!=null && entry.Key.id==id && entry.Key.currentSource!=null && entry.Key.currentSource.clip!=null && entry.Key.currentSource.isPlaying)return true;
        return false;
    }
    private static void Click(string name)
    {
        var button = Object.FindObjectsOfType<UI.MXButton>().First(x => x.name == name);
        Physics.SyncTransforms();
        var camera = UICamera.FindCameraForLayer(button.gameObject.layer);
        var point = camera.cachedCamera.WorldToScreenPoint(button.transform.position);
        bool hit = UICamera.Raycast(point);
        var target = UICamera.lastHit.collider?.GetComponentInParent<UI.MXButton>();
        Check(hit && target != null && target.Pointer == button.Pointer, "Native pointer hit testing reaches " + name);
        UIEventListener.Get(button.gameObject).OnClick();
    }
    private static void Finish(bool success, string? error)
    {
        if (done) return; done = true;
        try
        {
            Directory.CreateDirectory(evidence); DumpHierarchy();
            File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new { passed = success, coreOnly = CoreOnly, stage, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            if (!success) { SaveFrame(Path.Combine(evidence, "failure.ppm")); RevisionComparePlugin.Instance.Log.LogError(error); }
        }
        finally { Application.Quit(success ? 0 : 1); }
    }
    private static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;
    private static void RenderOffscreen()
    {
        foreach (var camera in Camera.allCameras.Where(c => c.enabled && c.targetTexture != null).OrderBy(c => c.depth)) camera.Render();
    }
    private static void DumpHierarchy()
    {
        var result = Object.FindObjectsOfType<Transform>(true).Where(x => x.name.Contains("Compare") || x.name.Contains("Revision") || x.name == "PlayPreviewBtn")
            .Select(x => new { path = PathOf(x), active = x.gameObject.activeInHierarchy, layer = x.gameObject.layer, position = x.localPosition.ToString(), width = x.GetComponent<UIWidget>()?.width, height = x.GetComponent<UIWidget>()?.height }).ToArray();
        File.WriteAllText(Path.Combine(evidence, "ui-hierarchy.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void SaveFrame(string path)
    {
        var rt = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32); rt.Create();
        var texture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        var old = RenderTexture.active;
        try
        {
            // Hidden native test windows may not present their offscreen UI
            // cameras. Render those targets before compositing the screen.
            RenderOffscreen();
            RenderTexture.active = rt; GL.Clear(true, true, Color.black);
            foreach (var camera in Camera.allCameras.Where(c => c.enabled && c.targetTexture == null).OrderBy(c => c.depth))
            { try { camera.targetTexture = rt; camera.Render(); } finally { camera.targetTexture = null; } }
            RenderTexture.active = rt; texture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
            var raw = texture.GetRawTextureData(); var bytes = new byte[raw.Length];
            System.Runtime.InteropServices.Marshal.Copy(raw.Pointer + 4 * IntPtr.Size, bytes, 0, bytes.Length);
            using var file = File.Create(path); file.Write(Encoding.ASCII.GetBytes($"P6\n{Screen.width} {Screen.height}\n255\n")); file.Write(bytes);
        }
        finally { RenderTexture.active = old; rt.Release(); Object.Destroy(rt); Object.Destroy(texture); }
    }
}
