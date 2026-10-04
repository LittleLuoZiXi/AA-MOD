using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using BepInEx;
using Object = UnityEngine.Object;

namespace AzureArchive.Recorder;

// Opt-in diagnostics run only in a separately launched verification instance.
internal static class UiDiagnostics
{
    internal static readonly bool Enabled=Environment.GetCommandLineArgs().Contains("--aa-recorder-ui-inspect");
    internal static readonly bool CaptureTest=Environment.GetCommandLineArgs().Contains("--aa-recorder-catalog-smoke");
    internal static readonly bool StopTest=Environment.GetCommandLineArgs().Contains("--aa-recorder-stop-smoke");
    internal static readonly bool DefaultFolderTest=Environment.GetCommandLineArgs().Contains("--aa-recorder-default-folder-smoke");
    internal static readonly bool RealtimeTest=Environment.GetCommandLineArgs().Contains("--aa-recorder-realtime-smoke");
    internal static readonly bool InspectSettings=Environment.GetCommandLineArgs().Contains("--aa-recorder-settings-inspect");
    static float readyAt;
    static int stage;
    static int savedFps;
    static int smokeOptionStage;
    internal static readonly bool TimingOptionsProbe=Environment.GetCommandLineArgs().Contains("--aa-recorder-timing-options-probe");
    static int timingOptionStage,timingOptionChecks,timingSavedTutorial;
    static (int Fps,int Scale,int Multiplier,bool Neural) timingSavedOptions;
    static bool smokeOptionEnhance;
    static string smokeOutputFolder="";
    static float smokeOptionReady;
    static bool smokeProgressSaved,smokeCancelSent;
    static float smokeCancelAt;
    static bool componentLossExercised;
    static float componentLossStarted;
    static bool tutorialReturning;
    static (int Scale,int Multiplier,bool Neural,int Fps,string Folder,bool Dlss) tutorialSaved;
    static readonly System.Collections.Generic.Dictionary<IntPtr,bool> tutorialCameraStates=new();
    static readonly System.Collections.Generic.HashSet<int> tutorialArtIds=new();
    internal static void StartSmokeOptions(bool enhance,string folder)
    {
        Directory.CreateDirectory(Root);
        if(RecorderBehaviour.Instance!.Ui!.IsVisible)RecorderBehaviour.Instance.Ui.ToggleSettings();
        RecorderBehaviour.Instance!.Ui!.ToggleSettings();
        RecorderBehaviour.Instance.Ui.OutputFolder=folder;
        smokeOutputFolder=folder;smokeOptionEnhance=enhance;
        smokeOptionStage=TimingOptionsProbe?0:Environment.GetCommandLineArgs().Contains("--aa-recorder-tutorial-smoke")?1000:1;
        timingOptionStage=TimingOptionsProbe?1:0;timingOptionChecks=0;
        smokeOptionReady=Time.realtimeSinceStartup+2;
    }
    internal static string Root
    {
        get
        {
            var args=Environment.GetCommandLineArgs();
            int evidence=Array.IndexOf(args,"--aa-recorder-smoke-evidence");
            return evidence>=0 && evidence+1<args.Length
                ?Path.GetFullPath(args[evidence+1]):Path.Combine(Paths.GameRootPath,"RecorderMod","test-output");
        }
    }
    internal static void Update()
    {
        RecordingUiSmoke.Update();
        PreferencesDiagnostics.Update();UpdateDiagnostics.Update();
        if(timingOptionStage>0)
        {
            if(Time.realtimeSinceStartup>=smokeOptionReady)UpdateTimingOptionsProbe();
            return;
        }
        if(Environment.GetCommandLineArgs().Contains("--aa-recorder-smoke-ui") && RecorderBehaviour.Instance?.Enhancing==true)
        {
            if(!smokeProgressSaved)
            {
                smokeProgressSaved=true;smokeCancelAt=Time.realtimeSinceStartup+3;
                SaveFrame(Path.Combine(Root,"smoke-ui-progress.ppm"));
            }
            if(!smokeCancelSent && Environment.GetCommandLineArgs().Contains("--aa-recorder-smoke-cancel") && Time.realtimeSinceStartup>=smokeCancelAt)
            {smokeCancelSent=true;Click("AARecorder_Stop");}
        }
        if(smokeOptionStage>0 && Time.realtimeSinceStartup>=smokeOptionReady)
        {
            try
            {
                if(smokeOptionStage>=1000)
                {
                    TestTutorial();smokeOptionReady=Time.realtimeSinceStartup+2;
                }
                else if(smokeOptionStage==1)
                {
                    VerifyDlssControls(RecorderPlugin.Instance.RememberedDlss.Value && RecorderBehaviour.Instance!.EnhancementAvailable,"restored initial preference");
                    if(RecorderBehaviour.Instance!.Ui!.DlssEnabled)Click("AARecorder_DLSS");
                    SaveFrame(Path.Combine(Root,"smoke-ui-dlss-off.ppm"));
                    foreach(var name in new[]{"AARecorder_Close","AARecorder_ChooseFolder"})Click(name,false);
                    var config=RecorderPlugin.Instance.Fps;var before=config.Value;
                    Click("AARecorder_Fps");
                    CheckDlss(config.Value!=before,"Ordinary frame-rate control remains usable");
                    config.Value=before;
                    if(RecorderBehaviour.Instance!.EnhancementAvailable)
                    {Click("AARecorder_DLSS");smokeOptionStage=2;}
                    else
                    {
                        var button=FindButton("AARecorder_DLSS");
                        CheckDlss(button.disabled,"Unavailable DLSS disables the master button");
                        UIEventListener.Get(button.gameObject).OnClick();
                        VerifyDlssControls(false,"unavailable click ignored");
                        if(RecorderBehaviour.Instance.EnhancementSupported)VerifyMissingComponents("initially missing");
                        CheckDlss(!smokeOptionEnhance,"Unavailable-DLSS test requests ordinary recording");
                        smokeOptionStage=4;
                    }
                    smokeOptionReady=Time.realtimeSinceStartup+2;
                }
                else if(smokeOptionStage==2)
                {
                    VerifyDlssControls(true,"explicitly enabled");
                    SaveFrame(Path.Combine(Root,"smoke-ui-dlss-on.ppm"));
                    var config=RecorderPlugin.Instance;
                    var scale=config.Scale.Value;var multiplier=config.Multiplier.Value;var neural=config.Neural.Value;
                    Click("AARecorder_Scale");Click("AARecorder_Multiplier");Click("AARecorder_Neural");
                    CheckDlss(config.Scale.Value!=scale && config.Multiplier.Value!=multiplier && config.Neural.Value!=neural,"All three DLSS controls respond after enabling");
                    config.Scale.Value=scale;config.Multiplier.Value=multiplier;config.Neural.Value=neural;
                    if(!componentLossExercised && Environment.GetCommandLineArgs().Contains("--aa-recorder-test-component-loss"))
                    {
                        componentLossExercised=true;componentLossStarted=Time.realtimeSinceStartup;
                        File.WriteAllText(Path.Combine(Root,"component-loss-ready.json"),"{\"ready\":true}");
                        smokeOptionStage=20;smokeOptionReady=Time.realtimeSinceStartup+2;return;
                    }
                    Click("AARecorder_DLSS");VerifyDlssControls(false,"switched off again");
                    Click("AARecorder_DLSS");VerifyDlssControls(true,"enabled before closing");
                    Click("AARecorder_Close");RecorderBehaviour.Instance!.Ui!.ToggleSettings();
                    RecorderBehaviour.Instance.Ui.OutputFolder=smokeOutputFolder;
                    smokeOptionStage=3;smokeOptionReady=Time.realtimeSinceStartup+2;
                }
                else if(smokeOptionStage==3)
                {
                    VerifyDlssControls(true,"reopened with remembered opt-in");
                    SaveFrame(Path.Combine(Root,"smoke-ui-dlss-reopened.ppm"));
                    if(RecorderBehaviour.Instance!.Ui!.DlssEnabled!=smokeOptionEnhance)Click("AARecorder_DLSS");
                    VerifyDlssControls(smokeOptionEnhance,"recording choice");
                    smokeOptionStage=4;smokeOptionReady=Time.realtimeSinceStartup+2;
                }
                else if(smokeOptionStage==20)
                {
                    if(RecorderBehaviour.Instance!.EnhancementAvailable)
                    {
                        CheckDlss(Time.realtimeSinceStartup-componentLossStarted<30,"Waiting for isolated component removal");
                        smokeOptionReady=Time.realtimeSinceStartup+1;return;
                    }
                    VerifyMissingComponents("component removed after opt-in");
                    File.WriteAllText(Path.Combine(Root,"component-restore-ready.json"),"{\"ready\":true}");
                    componentLossStarted=Time.realtimeSinceStartup;smokeOptionStage=21;smokeOptionReady=Time.realtimeSinceStartup+2;
                }
                else if(smokeOptionStage==21)
                {
                    if(!RecorderBehaviour.Instance!.EnhancementAvailable)
                    {
                        CheckDlss(Time.realtimeSinceStartup-componentLossStarted<30,"Waiting for isolated component restoration");
                        smokeOptionReady=Time.realtimeSinceStartup+1;return;
                    }
                    VerifyDlssControls(false,"components restored without implicit opt-in");
                    CheckDlss(!FindButton("AARecorder_DLSS").disabled,"Restored components make the master available again");
                    SaveFrame(Path.Combine(Root,"smoke-ui-dlss-restored-off.ppm"));
                    Click("AARecorder_DLSS");smokeOptionStage=2;smokeOptionReady=Time.realtimeSinceStartup+2;
                }
                else
                {
                    smokeOptionStage=0;
                    SaveFrame(Path.Combine(Root,"smoke-ui-settings.ppm"));
                    File.WriteAllText(Path.Combine(Root,"smoke-ui-settings.txt"),string.Join("\n",Object.FindObjectsOfType<UILabel>().Where(x=>PathOf(x.transform).Contains("AARecorder_")).Select(x=>$"{PathOf(x.transform)}: {x.text}; font={x.trueTypeFont?.name}; visible={x.isVisible}; color={x.color}")));
                    Click("AARecorder_Start");
                }
            }
            catch(Exception error){smokeOptionStage=0;File.WriteAllText(Path.Combine(Root,"smoke-ui-error.txt"),error.ToString());SaveFrame(Path.Combine(Root,"smoke-ui-error.ppm"));RecorderPlugin.Instance.Log.LogError(error);Application.Quit(1);}
        }
        if(!Enabled) return;
        var manager=Object.FindObjectOfType<ScenarioResourceManager>();
        if(manager==null || !manager.AreDbsLoaded) return;
        if(stage==0) { stage=1;readyAt=Time.realtimeSinceStartup+8; }
        if(Time.realtimeSinceStartup<readyAt) return;
        if(stage==1)
        {
            stage=4; readyAt=Time.realtimeSinceStartup+3;
            var catalog=Object.FindObjectOfType<Catalog>();
            if(catalog!=null && catalog.items.Count>0) catalog.items[0].OnButtonClicked();
            AuthoringMcpPanelHide();
            if(InspectSettings)
            {
                var popup=Object.FindObjectsOfType<Transform>(true).First(x=>x.name=="UIPopup_Settings");
                File.WriteAllText(Path.Combine(Root,"native-settings-tree.txt"),string.Join("\n",popup.GetComponentsInChildren<Transform>(true).Select(t=>$"{PathOf(t)} pos={t.localPosition} scale={t.localScale} active={t.gameObject.activeSelf} comps={string.Join(",",t.GetComponents<Component>().Select(c=>c.GetIl2CppType().Name))}")));
                popup.gameObject.SetActive(true);
            }
            return;
        }
        if(stage==4)
        {
            stage=2; readyAt=Time.realtimeSinceStartup+3;
            var sb=new StringBuilder();
            sb.AppendLine($"Screen {Screen.width}x{Screen.height}; desktop {Screen.currentResolution}; display {Display.main.systemWidth}x{Display.main.systemHeight}");
            foreach(var label in Object.FindObjectsOfType<UILabel>(true))
                sb.AppendLine($"LABEL {PathOf(label.transform)} active={label.gameObject.activeInHierarchy} text={label.text.Replace("\n","/")} pos={label.transform.localPosition} size={label.width}x{label.height} font={label.trueTypeFont?.name}/{label.ambigiousFont?.name} fontsize={label.fontSize}");
            foreach(var info in Object.FindObjectsOfType<CatalogFileInfo>(true))
            {
                sb.AppendLine($"INFO {PathOf(info.transform)} path={info.block?.path} button={PathOf(info.btn?.transform)}");
                if(info.btn==null) continue;
                foreach(var tr in info.btn.transform.parent.GetComponentsInChildren<Transform>(true))
                    sb.AppendLine($"TREE {PathOf(tr)} pos={tr.localPosition} scale={tr.localScale} components={string.Join(",",tr.GetComponents<Component>().Select(c=>c.GetIl2CppType().Name))}");
            }
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root,"ui-inspect.txt"),sb.ToString());
            AuthoringMcpPanelHide();
            SaveFrame(Path.Combine(Root,"ui-inspect.ppm"));
            if(InspectSettings) {stage=10;readyAt=Time.realtimeSinceStartup+2;}
        }
        else if(stage==2)
        {
            stage=3;readyAt=Time.realtimeSinceStartup+3;
            Click("AARecorder_Entry");
        }
        else if(stage==3)
        {
            stage=5;readyAt=Time.realtimeSinceStartup+3;
            SaveFrame(Path.Combine(Root,"ui-settings.ppm"));
            File.WriteAllText(Path.Combine(Root,"ui-settings.txt"),string.Join("\n",Object.FindObjectsOfType<UILabel>().Where(x=>PathOf(x.transform).Contains("AARecorder_")).Select(x=>$"{PathOf(x.transform)}: {x.text}; font={x.trueTypeFont?.name}; visible={x.isVisible}; color={x.color}")));
            savedFps=RecorderPlugin.Instance.Fps.Value;
            foreach(var name in new[]{"AARecorder_Close","AARecorder_DLSS","AARecorder_ChooseFolder"})Click(name,false);
            Click("AARecorder_Fps");
        }
        else if(stage==5)
        {
            stage=6;readyAt=Time.realtimeSinceStartup+2;
            var changed=RecorderPlugin.Instance.Fps.Value!=savedFps;
            RecorderPlugin.Instance.Fps.Value=savedFps;
            File.WriteAllText(Path.Combine(Root,"ui-interactions.txt"),"Frame-rate native click changed value: "+changed+"\nDefault destination: "+RecorderBehaviour.Instance!.Ui!.OutputFolder+"\n");
            if(!RecorderBehaviour.Instance.EnhancementAvailable)
                UIEventListener.Get(FindButton("AARecorder_DLSS").gameObject).OnClick();
        }
        else if(stage==6)
        {
            stage=7;readyAt=Time.realtimeSinceStartup+2;
            SaveFrame(Path.Combine(Root,"ui-unsupported.ppm"));
            File.AppendAllText(Path.Combine(Root,"ui-interactions.txt"),"Enhancement status: "+RecorderBehaviour.Instance?.Status+"\n");
            if(CaptureTest)
            {
                stage=99;
                if(!DefaultFolderTest)
                {
                    var folder=Path.Combine(Root,"本次内录 videos");Directory.CreateDirectory(folder);
                    RecorderBehaviour.Instance!.Ui!.OutputFolder=folder;
                }
                if(RecorderBehaviour.Instance!.Ui!.DlssEnabled)Click("AARecorder_DLSS");
                Click("AARecorder_Start");
            }
        }
        else if(stage==7){stage=99;Application.Quit();}
        else if(stage==10){stage=99;Application.Quit();}
    }
    static void CheckTiming(bool passed,string description)
    {
        timingOptionChecks++;
        File.AppendAllText(Path.Combine(Root,"timing-options-checks.jsonl"),System.Text.Json.JsonSerializer.Serialize(new{check=description,passed})+"\n");
        if(!passed)throw new InvalidOperationException("Timing option regression: "+description);
    }
    static void TimingState(bool expected,string description)
    {
        var ui=RecorderBehaviour.Instance!.Ui!;
        CheckTiming(ui.MeasureTotal==expected && ui.TimingButtonText==(expected?"开启":"关闭") && !ui.TimingConfirmationVisible,description);
    }
    static void VerifyTimingConfirmationShield()
    {
        var owner=RecorderBehaviour.Instance!;var ui=owner.Ui!;var config=RecorderPlugin.Instance;
        CheckTiming(ui.TimingConfirmationVisible && !ui.MeasureTotal && ui.TimingButtonText=="关闭","Opening confirmation does not opt in");
        CheckTiming(ui.TimingConfirmationText==NativeRecorderUi.TimingConfirmationPrompt,"Confirmation preserves the user's exact warning text");
        int fps=config.Fps.Value,scale=config.Scale.Value,multiplier=config.Multiplier.Value;
        bool neural=config.Neural.Value,dlss=ui.DlssEnabled,requestedBefore=owner.TotalRequested;string folder=ui.OutputFolder;
        var startButton=FindButton("AARecorder_Start");var camera=UICamera.FindCameraForLayer(startButton.gameObject.layer);
        bool hit=UICamera.Raycast(camera.cachedCamera.WorldToScreenPoint(startButton.transform.position));
        CheckTiming(hit && UICamera.lastHit.collider?.name=="TimingConfirmationShield","Confirmation backdrop intercepts the underlying Start button");
        foreach(var name in new[]{"AARecorder_Close","AARecorder_Fps","AARecorder_Scale","AARecorder_Multiplier","AARecorder_Neural","AARecorder_DLSS","AARecorder_ChooseFolder","AARecorder_Start","AARecorder_Tutorial","AARecorder_MeasureTotal"})
            UIEventListener.Get(FindButton(name).gameObject).OnClick();
        ui.ToggleSettings();
        owner.StartFromOptions(ui.DlssEnabled,ui.OutputFolder,!requestedBefore);
        CheckTiming(ui.IsVisible && ui.TimingConfirmationVisible && !ui.MeasureTotal && !owner.Busy && owner.TotalRequested==requestedBefore &&
            config.Fps.Value==fps && config.Scale.Value==scale && config.Multiplier.Value==multiplier &&
            config.Neural.Value==neural && ui.DlssEnabled==dlss && ui.OutputFolder==folder,
            "Background callbacks, F8/F9 and direct StartFromOptions cannot dismiss or start behind confirmation");
        CheckTiming(Object.FindObjectsOfType<UICamera>(true).Where(x=>x.name!="AARecorder_ScreenCamera").All(x=>!x.enabled),
            "Confirmation disables other AA input cameras");
    }
    static void UpdateTimingOptionsProbe()
    {
        var ui=RecorderBehaviour.Instance!.Ui!;var config=RecorderPlugin.Instance;
        try
        {
            switch(timingOptionStage)
            {
                case 1:
                    timingSavedTutorial=config.TutorialSeenVersion.Value;
                    timingSavedOptions=(config.Fps.Value,config.Scale.Value,config.Multiplier.Value,config.Neural.Value);
                    if(ui.TutorialVisible)Click("AARecorder_TutorialSkip");
                    break;
                case 2:
                    TimingState(false,"Each settings visit starts with the native duration button showing off");
                    SaveFrame(Path.Combine(Root,"timing-options-default-off.ppm"));
                    Click("AARecorder_MeasureTotal");
                    break;
                case 3:
                    SaveFrame(Path.Combine(Root,"timing-options-confirmation.ppm"));
                    VerifyTimingConfirmationShield();Click("AARecorder_TimingNo");
                    break;
                case 4:
                    TimingState(false,"No dismisses confirmation and leaves measurement off");
                    Click("AARecorder_MeasureTotal");
                    break;
                case 5:
                    Click("AARecorder_TimingYes");TimingState(true,"Yes explicitly enables measurement for this visit");
                    break;
                case 6:
                    SaveFrame(Path.Combine(Root,"timing-options-enabled.ppm"));
                    Click("AARecorder_MeasureTotal");TimingState(false,"Clicking an enabled duration button turns it off without another confirmation");
                    Click("AARecorder_MeasureTotal");
                    break;
                case 7:
                    Click("AARecorder_TimingClose");TimingState(false,"Closing confirmation never enables measurement");
                    Click("AARecorder_MeasureTotal");
                    break;
                case 8:
                    // Invoke the same internal cancellation path used by Escape;
                    // no user32, synthesized keystroke, or desktop automation.
                    ui.DismissTimingConfirmation();TimingState(false,"Escape cancellation path leaves measurement off");
                    Click("AARecorder_MeasureTotal");
                    break;
                case 9:
                    var shield=Object.FindObjectsOfType<UITexture>().First(x=>x.name=="TimingConfirmationShield");
                    UIEventListener.Get(shield.gameObject).OnClick();TimingState(false,"Clicking the backdrop cancels without enabling measurement");
                    Click("AARecorder_MeasureTotal");
                    break;
                case 10:
                    Click("AARecorder_TimingYes");TimingState(true,"Measurement is enabled before testing settings reopen");
                    Click("AARecorder_Close");ui.ToggleSettings();ui.OutputFolder=smokeOutputFolder;
                    break;
                case 11:
                    TimingState(false,"Reopening settings discards the previous task's opt-in");
                    SaveFrame(Path.Combine(Root,"timing-options-reopened-off.ppm"));
                    if(Environment.GetCommandLineArgs().Contains("--aa-recorder-measure-total"))Click("AARecorder_MeasureTotal");
                    break;
                case 12:
                    if(Environment.GetCommandLineArgs().Contains("--aa-recorder-measure-total"))Click("AARecorder_TimingYes");
                    break;
                default:
                    bool requested=Environment.GetCommandLineArgs().Contains("--aa-recorder-measure-total");
                    TimingState(requested,"Final recording choice follows the driver's explicit measurement request");
                    CheckTiming(config.Fps.Value==timingSavedOptions.Fps && config.Scale.Value==timingSavedOptions.Scale &&
                        config.Multiplier.Value==timingSavedOptions.Multiplier && config.Neural.Value==timingSavedOptions.Neural,
                        "Timing option checks preserve frame-rate and DLSS configuration");
                    config.TutorialSeenVersion.Value=timingSavedTutorial;
                    ui.OutputFolder=smokeOutputFolder;
                    if(ui.DlssEnabled!=smokeOptionEnhance)Click("AARecorder_DLSS");
                    CheckTiming(ui.DlssEnabled==smokeOptionEnhance,"Recording retains only the driver's requested enhancement choice");
                    SaveFrame(Path.Combine(Root,"timing-options-final-choice.ppm"));
                    timingOptionStage=0;smokeOptionStage=0;Click("AARecorder_Start");
                    CheckTiming(RecorderBehaviour.Instance.Busy && RecorderBehaviour.Instance.TotalRequested==requested,"Final duration button choice reaches the normal recording Start handler");
                    File.WriteAllText(Path.Combine(Root,"timing-options-result.json"),System.Text.Json.JsonSerializer.Serialize(new{
                        passed=true,checks=timingOptionChecks,measureTotal=requested,defaultOff=true,controlKind="native-button",offLabel="关闭",onLabel="开启",
                        confirmationText=NativeRecorderUi.TimingConfirmationPrompt,perVisitOnly=true,
                        nativeCallbacks=true,desktopInputUsed=false,keyboardEscapePathInvoked=true,
                        width=Screen.width,height=Screen.height}));
                    return;
            }
            timingOptionStage++;smokeOptionReady=Time.realtimeSinceStartup+.6f;
        }
        catch(Exception error)
        {
            timingOptionStage=0;smokeOptionStage=0;config.TutorialSeenVersion.Value=timingSavedTutorial;
            File.WriteAllText(Path.Combine(Root,"timing-options-error.txt"),error.ToString());
            RecorderPlugin.Instance.Log.LogError(error);Application.Quit(1);
        }
    }
    static UI.MXButton FindButton(string name)=>Object.FindObjectsOfType<UI.MXButton>().First(x=>x.name==name);
    static void TestTutorial()
    {
        var ui=RecorderBehaviour.Instance!.Ui!;var config=RecorderPlugin.Instance;
        if(smokeOptionStage==1000)
        {
            bool first=config.TutorialSeenVersion.Value==0;
            tutorialReturning=!first;
            CheckDlss(ui.TutorialVisible==first,first?"First visit automatically opens tutorial":"Returning profile does not auto-open tutorial");
            SaveFrame(Path.Combine(Root,"smoke-ui-tutorial-entry.ppm"));
            if(!first){Click("AARecorder_Tutorial");smokeOptionStage=1101;return;}
            CheckDlss(ui.TutorialVisible && ui.TutorialPage==0,"Manual/automatic tutorial starts on first page");
            CheckDlss(ui.TutorialArtLoaded,"Embedded tutorial character loads successfully");
            VerifyTutorialShield();
            CheckDlss(FindButton("AARecorder_TutorialPrevious").disabled,"First page disables previous button");
            UIEventListener.Get(FindButton("AARecorder_TutorialPrevious").gameObject).OnClick();
            CheckDlss(ui.TutorialPage==0,"Previous cannot leave first page");
            var welcomeArt=ui.TutorialArtId;
            Click("AARecorder_TutorialNext");CheckDlss(ui.TutorialPage==1,"Next advances to second page");
            CheckDlss(ui.TutorialArtId!=welcomeArt,"Next immediately changes Arona's pose with the page");
            Click("AARecorder_TutorialPrevious");CheckDlss(ui.TutorialPage==0,"Previous returns to first page");
            CheckDlss(ui.TutorialArtId==welcomeArt,"Previous immediately restores the matching Arona pose");
            Click("AARecorder_TutorialNext");Click("AARecorder_TutorialSkip");
            CheckDlss(!ui.TutorialVisible && config.TutorialSeenVersion.Value==1,"Skip closes tutorial and marks it seen");
            CheckDlss(File.ReadAllText(config.Config.ConfigFilePath).Contains("SeenVersion = 1"),"Seen state persists to the MOD profile");
            Click("AARecorder_Close");ui.ToggleSettings();ui.OutputFolder=smokeOutputFolder;
            CheckDlss(!ui.TutorialVisible,"Reopening settings does not repeat dismissed tutorial");
            smokeOptionStage=1100;
        }
        else if(smokeOptionStage==1100)
        {
            tutorialCameraStates.Clear();
            tutorialArtIds.Clear();
            foreach(var camera in Object.FindObjectsOfType<UICamera>(true))tutorialCameraStates[camera.Pointer]=camera.enabled;
            Click("AARecorder_Tutorial");smokeOptionStage=tutorialReturning?1101:1001;
        }
        else if(smokeOptionStage==1101)
        {
            CheckDlss(ui.TutorialVisible && ui.TutorialPage==0,"Returning profile can replay tutorial");
            VerifyTutorialShield();
            Click("AARecorder_TutorialSkip");smokeOptionStage=1;
        }
        else if(smokeOptionStage<=1006)
        {
            var expected=smokeOptionStage-1001;
            CheckDlss(ui.TutorialVisible && ui.TutorialPage==expected,"Tutorial page "+(expected+1)+" is visible");
            CheckDlss(ui.TutorialArtLoaded && ui.TutorialArtName==$"recorder.tutorial.arona.page-{expected+1:00}.png" && tutorialArtIds.Add(ui.TutorialArtId),"Page "+(expected+1)+": distinct matching Arona pose is displayed");
            VerifyTutorialShield();
            SaveFrame(Path.Combine(Root,$"smoke-ui-tutorial-page-{expected+1}.ppm"));
            File.AppendAllText(Path.Combine(Root,"smoke-ui-tutorial-labels.txt"),$"\nPAGE {expected+1}\n"+string.Join("\n",Object.FindObjectsOfType<UILabel>().Where(x=>PathOf(x.transform).Contains("/Tutorial/")).Select(x=>$"{x.text}; font={x.trueTypeFont?.name}; size={x.fontSize}; visible={x.isVisible}"))+"\n");
            Click("AARecorder_TutorialTarget");
            CheckDlss(expected==ui.TutorialPageCount-1?!ui.TutorialVisible:ui.TutorialPage==expected+1,"Clicking highlighted target advances page "+(expected+1));
            smokeOptionStage++;
        }
        else if(smokeOptionStage==1007)
        {
            CheckDlss(!ui.TutorialVisible && config.TutorialSeenVersion.Value==1,"Finish returns to settings");
            CheckDlss(tutorialArtIds.Count==ui.TutorialPageCount,"All six tutorial pages use different Arona illustrations");
            CheckDlss(Object.FindObjectsOfType<UICamera>(true).All(x=>!tutorialCameraStates.TryGetValue(x.Pointer,out var before) || x.enabled==before),"Background input camera states restored after tutorial");
            bool supported=RecorderBehaviour.Instance.EnhancementAvailable;
            if(supported)Click("AARecorder_DLSS");
            tutorialSaved=(config.Scale.Value,config.Multiplier.Value,config.Neural.Value,config.Fps.Value,ui.OutputFolder,ui.DlssEnabled);
            Click("AARecorder_Tutorial");smokeOptionStage=1008;
        }
        else if(smokeOptionStage==1008)
        {
            Click("AARecorder_TutorialNext");Click("AARecorder_TutorialSkip");
            CheckDlss(ui.DlssEnabled==tutorialSaved.Dlss && config.Scale.Value==tutorialSaved.Scale && config.Multiplier.Value==tutorialSaved.Multiplier && config.Neural.Value==tutorialSaved.Neural && config.Fps.Value==tutorialSaved.Fps && ui.OutputFolder==tutorialSaved.Folder,"Rewatching tutorial preserves every setting, folder and DLSS choice");
            smokeOptionStage=1009;
        }
        else
        {
            if(ui.DlssEnabled)Click("AARecorder_DLSS");smokeOptionStage=1;
        }
    }
    static void VerifyTutorialShield()
    {
        var ui=RecorderBehaviour.Instance!.Ui!;var config=RecorderPlugin.Instance;
        int page=ui.TutorialPage,fps=config.Fps.Value,scale=config.Scale.Value,multiplier=config.Multiplier.Value;
        bool neural=config.Neural.Value,dlss=ui.DlssEnabled;var folder=ui.OutputFolder;
        foreach(var name in new[]{"AARecorder_Close","AARecorder_Fps","AARecorder_Scale","AARecorder_Multiplier","AARecorder_Neural","AARecorder_DLSS","AARecorder_ChooseFolder","AARecorder_Start","AARecorder_Tutorial"})
        {
            var button=FindButton(name);var camera=UICamera.FindCameraForLayer(button.gameObject.layer);
            bool hit=UICamera.Raycast(camera.cachedCamera.WorldToScreenPoint(button.transform.position));
            var target=UICamera.lastHit.collider?.name;
            CheckDlss(hit && target is "TutorialInputShield" or "AARecorder_TutorialTarget","Page "+(page+1)+": input shield intercepts "+name);
            UIEventListener.Get(button.gameObject).OnClick();
        }
        CheckDlss(ui.TutorialVisible && ui.TutorialPage==page && !RecorderBehaviour.Instance.Busy && config.Fps.Value==fps && config.Scale.Value==scale && config.Multiplier.Value==multiplier && config.Neural.Value==neural && ui.DlssEnabled==dlss && ui.OutputFolder==folder,"Background actions cannot change settings, dismiss guide, open picker or start recording");
        CheckDlss(Object.FindObjectsOfType<UICamera>(true).Where(x=>x.name!="AARecorder_ScreenCamera").All(x=>!x.enabled),"Other AA input cameras disabled during tutorial");
        var skip=FindButton("AARecorder_TutorialSkip");
        CheckDlss(!skip.disabled && skip.GetComponent<UIWidget>().depth>=100 && skip.GetComponentInChildren<UISprite>(true).color.a>.9f,"Skip stays bright above the overlay on page "+(page+1));
        Click("AARecorder_TutorialSkip",false);
    }
    static void CheckDlss(bool passed,string description)
    {
        File.AppendAllText(Path.Combine(Root,"smoke-ui-dlss-checks.jsonl"),System.Text.Json.JsonSerializer.Serialize(new{check=description,passed})+"\n");
        if(!passed)throw new InvalidOperationException("DLSS gate regression: "+description);
    }
    static void VerifyDlssControls(bool enabled,string phase)
    {
        var ui=RecorderBehaviour.Instance!.Ui!;var config=RecorderPlugin.Instance;
        CheckDlss(ui.DlssEnabled==enabled,phase+": effective master state");
        foreach(var name in new[]{"AARecorder_Scale","AARecorder_Multiplier","AARecorder_Neural"})
            CheckDlss(FindButton(name).disabled==!enabled,phase+": "+name+" enabled state");
        if(enabled)return;
        var scale=config.Scale.Value;var multiplier=config.Multiplier.Value;var neural=config.Neural.Value;
        // Exercise the actual native click listener even if the control is disabled.
        // The production handler must guard its state, rather than rely on appearance.
        foreach(var name in new[]{"AARecorder_Scale","AARecorder_Multiplier","AARecorder_Neural"})
            UIEventListener.Get(FindButton(name).gameObject).OnClick();
        CheckDlss(config.Scale.Value==scale && config.Multiplier.Value==multiplier && config.Neural.Value==neural,phase+": disabled clicks preserve every DLSS setting");
    }
    static void VerifyMissingComponents(string phase)
    {
        var owner=RecorderBehaviour.Instance!;
        CheckDlss(owner.EnhancementSupported && !owner.EnhancementAvailable,phase+": supported GPU is gated by missing installed components");
        VerifyDlssControls(false,phase);
        foreach(var name in new[]{"AARecorder_DLSS","AARecorder_Scale","AARecorder_Multiplier","AARecorder_Neural"})
        {
            var button=FindButton(name);var color=button.GetComponentInChildren<UISprite>(true).color;
            CheckDlss(button.disabled && Math.Abs(color.r-color.g)<.08f && Math.Abs(color.g-color.b)<.08f,phase+": "+name+" is gray and disabled");
            UIEventListener.Get(button.gameObject).OnClick();
        }
        CheckDlss(Object.FindObjectsOfType<UILabel>().Any(label=>label.text==DlssComponents.MissingRuntimeMessage && label.color.r>.6f && label.color.g<.3f && label.color.b<.3f),phase+": exact missing-component message is red");
        owner.StartFromOptions(true,smokeOutputFolder);
        CheckDlss(!owner.Busy && owner.Status==DlssComponents.MissingRuntimeMessage,phase+": direct enhanced-recording request is rejected before starting any task");
        SaveFrame(Path.Combine(Root,"smoke-ui-dlss-missing"+(componentLossExercised?"-after-opt-in":"")+".ppm"));
    }
    internal static void Click(string name,bool dispatch=true)
    {
        var button=FindButton(name);
        var cam=UICamera.FindCameraForLayer(button.gameObject.layer);
        var point=cam.cachedCamera.WorldToScreenPoint(button.transform.position);
        bool hit=UICamera.Raycast(point);
        var target=UICamera.lastHit.collider?.GetComponentInParent<UI.MXButton>();
        File.AppendAllText(Path.Combine(Root,"ui-raycasts.txt"),$"{name}: checkCursor={button.checkCursor}, raycast={hit}, hit={target?.name}, camera={cam.name}\n");
        File.AppendAllText(Path.Combine(Root,"ui-raycasts.txt"),$"point={point}, collider={UICamera.lastHit.collider?.name}; rootWidget={button.GetComponent<UIWidget>()?.isVisible}, alpha={button.GetComponent<UIWidget>()?.finalAlpha}; physics={string.Join(",",Physics.RaycastAll(cam.cachedCamera.ScreenPointToRay(point),100,1<<button.gameObject.layer).Select(h=>h.collider.name))}\n");
        // Validate real NGUI hit testing before dispatching its click message.
        if(!hit || target==null || target.Pointer!=button.Pointer)throw new InvalidOperationException("UI raycast missed "+name);
        if(dispatch)UIEventListener.Get(button.gameObject).OnClick();
    }
    static void AuthoringMcpPanelHide()=>AzureArchive.Automation.AuthoringMcpPanel.HideCurrent();
    internal static void SaveFrame(string path)
    {
        var rt=new RenderTexture(Screen.width,Screen.height,24,RenderTextureFormat.ARGB32);
        rt.Create();
        var tex=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);
        var previous=RenderTexture.active;
        try
        {
            RenderTexture.active=rt; GL.Clear(true,true,Color.black);
            foreach(var cam in Camera.allCameras.Where(c=>c.enabled && c.targetTexture==null).OrderBy(c=>c.depth))
            { try {cam.targetTexture=rt;cam.Render();} finally {cam.targetTexture=null;} }
            RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0,false);
            var raw=tex.GetRawTextureData(); var bytes=new byte[raw.Length];
            System.Runtime.InteropServices.Marshal.Copy(raw.Pointer+4*IntPtr.Size,bytes,0,bytes.Length);
            using var file=File.Create(path);
            file.Write(Encoding.ASCII.GetBytes($"P6\n{Screen.width} {Screen.height}\n255\n")); file.Write(bytes);
        }
        finally { RenderTexture.active=previous;rt.Release();Object.Destroy(rt);Object.Destroy(tex); }
    }
    internal static string PathOf(Transform? t)=>t==null?"(null)":(t.parent==null?t.name:PathOf(t.parent)+"/"+t.name);
}


