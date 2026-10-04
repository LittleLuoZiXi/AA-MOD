using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using AzureArchive.Automation;
using Object=UnityEngine.Object;
namespace AzureArchive.Recorder;

// Explicit, isolated-host-only integration checks. No capture or DLSS execution.
internal static class PreferencesDiagnostics
{
    internal static readonly bool Enabled=Environment.GetCommandLineArgs().Contains("--aa-recorder-preferences-probe");
    static int stage;
    static float readyAt;
    static readonly List<string> checks=new();
    static string phase="",first="",second="",folder="";
    static int expectedFps;
    static string Argument(string name)
    {
        var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,name);
        if(index<0 || index+1>=args.Length)throw new InvalidOperationException("Missing probe argument: "+name);
        return args[index+1];
    }
    static void Check(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException(message);
        checks.Add(message);RecorderPlugin.Instance.Log.LogInfo("Preferences check: "+message);
    }
    static UI.MXButton Button(string name)=>Object.FindObjectsOfType<UI.MXButton>(true).First(b=>b.name==name && b.gameObject.activeInHierarchy);
    static void Click(string name)=>UiDiagnostics.Click(name);
    static void ClickToFps(int fps)
    {
        int attempts=0;
        while(RecorderPlugin.Instance.Fps.Value!=fps && attempts++<6)Click("AARecorder_Fps");
        Check(RecorderPlugin.Instance.Fps.Value==fps,"Native FPS button selects "+fps);
    }
    static void Saved<T>(ConfigEntry<T> entry,string reason)
    {
        var c=RecorderPlugin.Instance;string section="",value="";
        foreach(var line in File.ReadAllLines(c.Config.ConfigFilePath))
        {
            var text=line.Trim();if(text.StartsWith("[") && text.EndsWith("]"))section=text.Substring(1,text.Length-2);
            if(section!=entry.Definition.Section)continue;
            int equals=line.IndexOf('=');if(equals<0)continue;
            if(line.Substring(0,equals).Trim()==entry.Definition.Key)value=line.Substring(equals+1).Trim();
        }
        Check(value==entry.GetSerializedValue(),reason+" is immediately present in ConfigFile");
    }
    static void State(string reason)
    {
        var c=RecorderPlugin.Instance;var owner=RecorderBehaviour.Instance!;var ui=owner.Ui!;
        Check(ui.IsVisible && c.Fps.Value==expectedFps && ui.OutputFolder==folder && c.ExportDirectory.Value==folder,reason+": FPS and destination restored");
        Check(c.Scale.Value==4 && c.Multiplier.Value==3 && c.Neural.Value && c.RememberedDlss.Value,reason+": saved DLSS choices retained");
        Check(!owner.EnhancementSupported && !owner.EnhancementAvailable && !ui.DlssEnabled,reason+": unsupported physical GPU cannot enable DLSS");
        foreach(var name in new[]{"AARecorder_DLSS","AARecorder_Scale","AARecorder_Multiplier","AARecorder_Neural"})
        {
            Check(Button(name).disabled,reason+": "+name+" disabled");
            UIEventListener.Get(Button(name).gameObject).OnClick();
        }
        Check(c.Scale.Value==4 && c.Multiplier.Value==3 && c.Neural.Value && c.RememberedDlss.Value && !ui.DlssEnabled,reason+": direct UI callbacks preserve unsupported-DLSS guard");
        Check(!ui.MeasureTotal,reason+": duration measurement starts off");
        Saved(c.Fps,"FPS");Saved(c.ExportDirectory,"Destination");Saved(c.RememberedDlss,"DLSS preference");
        Saved(c.Scale,"Super resolution");Saved(c.Multiplier,"Frame multiplier");Saved(c.Neural,"Neural rendering");
    }
    static bool ProjectReady(string path)
    {
        var current=AuthoringEditorSession.Current;
        return current!=null && string.Equals(Path.GetFullPath(current.SourcePath),Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase);
    }
    static void Reopen()
    {
        Click("AARecorder_Close");RecorderBehaviour.Instance!.Ui!.ToggleSettings();
    }
    static void ReadOnlyFailure()
    {
        var c=RecorderPlugin.Instance;var path=c.Config.ConfigFilePath;var attributes=File.GetAttributes(path);var before=File.ReadAllBytes(path);
        try
        {
            File.SetAttributes(path,attributes|FileAttributes.ReadOnly);Click("AARecorder_Fps");
            Check(c.Fps.Value!=60,"Read-only save failure retains the selected session value");
            Check(RecorderBehaviour.Instance!.Status.Contains("保存设置失败"),"Read-only config failure reports a visible status message");
            Check(before.SequenceEqual(File.ReadAllBytes(path)),"Read-only failure leaves disk config unchanged");
        }
        finally {File.SetAttributes(path,attributes);}
        ClickToFps(60);Saved(c.Fps,"FPS after write permission restored");
        Check(!RecorderBehaviour.Instance!.Status.StartsWith("保存设置失败",StringComparison.Ordinal),"Successful retry clears the stale save-failure warning");
    }
    internal static void Update()
    {
        if(!Enabled || stage==99)return;
        try
        {
            var owner=RecorderBehaviour.Instance;
            if(owner?.Ui==null || (stage==0 && !DiagnosticHostReady.Check(Object.FindObjectOfType<ScenarioResourceManager>())))return;
            var ui=owner.Ui;var c=RecorderPlugin.Instance;
            if(stage==0)
            {
                phase=Argument("--aa-recorder-preferences-phase");first=Path.GetFullPath(Argument("--aa-recorder-preferences-first"));second=Path.GetFullPath(Argument("--aa-recorder-preferences-second"));folder=Path.GetFullPath(Argument("--aa-recorder-preferences-folder"));
                Directory.CreateDirectory(UiDiagnostics.Root);
                Check(phase=="write" || phase=="restart","Recognized isolated probe phase");
                Check(File.Exists(Path.Combine(Paths.GameRootPath,"RecorderChoiceTestRoot.json")),"Isolated host marker exists");
                Check(Application.companyName=="aa123test" && Application.productName=="AARecTest123","Native Unity application identity is isolated");
                Check(Application.persistentDataPath.Replace('\\','/').EndsWith("/aa123test/AARecTest123",StringComparison.OrdinalIgnoreCase),"Native persistent data directory is isolated");
                Check(Object.FindObjectOfType<UserSettings>().settingFilePath.Replace('\\','/').Contains("/aa123test/AARecTest123/"),"AA user settings use the isolated native directory");
                Check(Path.GetFullPath(Object.FindObjectOfType<UserSettings>().WorkspacePath).StartsWith(Path.GetFullPath(Paths.GameRootPath)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"AA workspace stays inside the isolated host");
                stage=1;readyAt=Time.realtimeSinceStartup+4;return;
            }
            if(Time.realtimeSinceStartup<readyAt)return;
            switch(stage)
            {
                case 1:
                    AuthoringWorkbench.OpenProject(first);stage=2;readyAt=Time.realtimeSinceStartup+4;return;
                case 2:
                    if(!ProjectReady(first))return;
                    Check(true,"First synthetic project opened in the actual AA editor");
                    AuthoringMcpPanel.HideCurrent();ui.ToggleSettings();stage=3;readyAt=Time.realtimeSinceStartup+1;return;
                case 3:
                    if(ui.TutorialVisible)Click("AARecorder_TutorialSkip");
                    if(phase=="write")
                    {
                        Check(c.Fps.Value==30,"Fresh profile retains the default 30 FPS");
                        Check(ui.OutputFolder==FolderPicker.Videos && !c.RememberedDlss.Value,"Fresh profile defaults to Videos with DLSS preference off");
                        ClickToFps(60);Saved(c.Fps,"60 FPS before closing settings");
                        ui.ApplyFolderSelection(folder);Saved(c.ExportDirectory,"Selected Unicode destination before closing settings");
                        var disk=File.ReadAllBytes(c.Config.ConfigFilePath);ui.ApplyFolderSelection(null);ui.ApplyFolderSelection("");
                        Check(ui.OutputFolder==folder && c.ExportDirectory.Value==folder && disk.SequenceEqual(File.ReadAllBytes(c.Config.ConfigFilePath)),"Cancelled folder callbacks preserve destination and config bytes");
                        // Simulate existing preferences only, without altering GPU detection or enabling enhancement.
                        c.SavePreference(c.Scale,4);c.SavePreference(c.Multiplier,3);c.SavePreference(c.Neural,true);c.SavePreference(c.RememberedDlss,true);
                        ReadOnlyFailure();expectedFps=60;
                    }
                    else
                    {
                        expectedFps=60;State("Fresh process initial load");
                        UiDiagnostics.SaveFrame(Path.Combine(UiDiagnostics.Root,"preferences-restarted-60fps.ppm"));
                        ClickToFps(30);expectedFps=30;Saved(c.Fps,"Changed 30 FPS after process restart");
                    }
                    Reopen();stage=4;readyAt=Time.realtimeSinceStartup+1;return;
                case 4:
                    State("Close and reopen");
                    UiDiagnostics.SaveFrame(Path.Combine(UiDiagnostics.Root,"preferences-restored.ppm"));
                    Click("AARecorder_MeasureTotal");stage=5;readyAt=Time.realtimeSinceStartup+.6f;return;
                case 5:
                    Check(ui.TimingConfirmationVisible,"Measurement still requires confirmation");Click("AARecorder_TimingYes");
                    Check(ui.MeasureTotal,"Confirmation enables measurement for this visit");
                    Reopen();Check(!ui.MeasureTotal,"Measurement is off after closing and reopening");
                    Click("AARecorder_Close");UnityEngine.SceneManagement.SceneManager.LoadScene("CatalogScene");stage=8;readyAt=Time.realtimeSinceStartup+3;return;
                case 8:
                    if(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name!="CatalogScene")return;
                    Check(true,"Returned to the native catalog before switching projects");
                    AuthoringWorkbench.OpenProject(second);stage=6;readyAt=Time.realtimeSinceStartup+4;return;
                case 6:
                    if(!ProjectReady(second))return;
                    Check(true,"Second distinct synthetic project opened in the actual AA editor");
                    AuthoringMcpPanel.HideCurrent();ui.ToggleSettings();stage=7;readyAt=Time.realtimeSinceStartup+1;return;
                case 7:
                    State("Different project");
                    UiDiagnostics.SaveFrame(Path.Combine(UiDiagnostics.Root,"preferences-other-project.ppm"));
                    File.WriteAllText(Path.Combine(UiDiagnostics.Root,"preferences-result.json"),JsonSerializer.Serialize(new{
                        passed=true,phase,checks=checks.ToArray(),fps=c.Fps.Value,folder=c.ExportDirectory.Value,scale=c.Scale.Value,multiplier=c.Multiplier.Value,neural=c.Neural.Value,rememberedDlss=c.RememberedDlss.Value,effectiveDlss=ui.DlssEnabled,
                        durationPerVisit=!ui.MeasureTotal,configPath=c.Config.ConfigFilePath,persistentData=Application.persistentDataPath,settingsPath=Object.FindObjectOfType<UserSettings>().settingFilePath,workspace=Object.FindObjectOfType<UserSettings>().WorkspacePath,firstProject=first,secondProject=second,
                        nativeUiCallbacks=true,folderCallback=true,dlssPreferencesSeededWithoutGpuSpoof=true,desktopInputUsed=false}));
                    stage=99;Application.Quit(0);return;
            }
        }
        catch(Exception error)
        {
            stage=99;Directory.CreateDirectory(UiDiagnostics.Root);
            File.WriteAllText(Path.Combine(UiDiagnostics.Root,"preferences-error.txt"),error.ToString());RecorderPlugin.Instance.Log.LogError(error);Application.Quit(1);
        }
    }
}