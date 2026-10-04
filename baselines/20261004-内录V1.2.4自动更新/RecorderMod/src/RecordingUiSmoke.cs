using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityEngine;
using Object=UnityEngine.Object;
namespace AzureArchive.Recorder;

// Optional in-app checks of the ordinary catalog and recording UI. These do not
// enable choice probes or interact with enhancement settings.
internal static class RecordingUiSmoke
{
    static readonly bool Entry=Environment.GetCommandLineArgs().Contains("--aa-recorder-entry-smoke");
    static readonly bool Progress=Environment.GetCommandLineArgs().Contains("--aa-recorder-progress-inspect");
    static readonly bool CancelMeasurement=Environment.GetCommandLineArgs().Contains("--aa-recorder-cancel-preflight");
    static readonly bool CancelReplay=Environment.GetCommandLineArgs().Contains("--aa-recorder-cancel-before-render");
    static bool cancelSent;
    static int stage;
    static float readyAt;
    static bool savedProgress;
    static int progressReadyFrames,failureFrames;
    static void Check(bool value,string description)
    {
        if(!value)throw new InvalidOperationException(description);
        RecorderPlugin.Instance.Log.LogInfo("Recorder UI check: "+description);
    }
    internal static void Update()
    {
        if(!Entry && !Progress && !CancelMeasurement && !CancelReplay)return;
        try
        {
            Directory.CreateDirectory(UiDiagnostics.Root);
            var owner=RecorderBehaviour.Instance;
            if(!cancelSent && owner!=null && ((CancelMeasurement && owner.Measuring && owner.Capturing && owner.OfflineSeconds>=1) || (CancelReplay && owner.PreparingReplay)))
            {
                cancelSent=true;
                // During scene/resolution transition exercise the same Stop path
                // as F10, independently of a temporarily rebuilding UI collider.
                if(CancelReplay)owner.Stop("手动停止");else UiDiagnostics.Click("AARecorder_Stop");
                return;
            }
            if(Progress && owner?.HasError==true)
            {
                if(++failureFrames>=3)
                {
                    var errors=Object.FindObjectsOfType<UILabel>().Where(l=>UiDiagnostics.PathOf(l.transform).Contains("AARecorder_") && l.text.Contains("无法进行内录")).ToArray();
                    Check(errors.Any(l=>l.isVisible && l.color.r>.6f && l.color.g<.3f),"No-AUTO recording refusal is visible in red");
                    UiDiagnostics.SaveFrame(Path.Combine(UiDiagnostics.Root,"recording-refused.ppm"));
                    File.WriteAllText(Path.Combine(UiDiagnostics.Root,"recording-refused.json"),JsonSerializer.Serialize(new{message=owner.Status,labels=errors.Select(l=>new{text=l.text,visible=l.isVisible,red=l.color.r,green=l.color.g})}));
                    Application.Quit(1);
                }
                return;
            }
            if(Progress && !savedProgress && owner?.Rendering==true && owner.OfflineSeconds>=6 && ++progressReadyFrames>=3)
            {
                var labels=Object.FindObjectsOfType<UILabel>().Where(l=>l.gameObject.activeInHierarchy &&
                    UiDiagnostics.PathOf(l.transform).Contains("AARecorder_") && UiDiagnostics.PathOf(l.transform).Contains("/Progress/")).ToArray();
                Check(labels.Any(l=>l.isVisible),"Recording progress panel has visible text");
                string text=owner.ProgressTitle+"\n"+owner.ProgressDetail;
                if(owner.TotalRequested)
                {
                    Check(text.Contains("预计总时长"),"Measured recording exposes its fixed estimated duration");
                    Check(labels.Any(l=>l.isVisible && l.text.Contains("预计总时长")),"Estimated duration is present in the visible measured progress panel");
                }
                else
                {
                    string[] forbidden={"预计","总时长","百分比","%","％"};
                    Check(!forbidden.Any(text.Contains),"Unmeasured progress title/detail contain no estimate, total duration or percentage");
                    Check(!labels.Where(l=>l.isVisible).Any(l=>forbidden.Any(l.text.Contains)),"Unmeasured visible progress labels contain no estimate, total duration or percentage");
                }
                UiDiagnostics.SaveFrame(Path.Combine(UiDiagnostics.Root,"recording-progress.ppm"));
                File.WriteAllText(Path.Combine(UiDiagnostics.Root,"recording-progress.json"),JsonSerializer.Serialize(new{
                    passed=true,totalRequested=owner.TotalRequested,title=owner.ProgressTitle,detail=owner.ProgressDetail,
                    labels=labels.Select(l=>new{text=l.text,visible=l.isVisible,width=l.width,height=l.height,fontSize=l.fontSize})}));
                savedProgress=true;
            }
            if(!Entry || stage==99)return;
            var manager=Object.FindObjectOfType<ScenarioResourceManager>();
            if(manager==null || !manager.AreDbsLoaded)return;
            if(stage==0){stage=1;readyAt=Time.realtimeSinceStartup+8;}
            if(Time.realtimeSinceStartup<readyAt)return;
            if(stage==1)
            {
                var catalog=Object.FindObjectOfType<Catalog>();
                Check(catalog!=null && catalog.items.Count>0,"Catalog has a story available");
                catalog!.items[0].OnButtonClicked();
                AzureArchive.Automation.AuthoringMcpPanel.HideCurrent();
                stage=2;readyAt=Time.realtimeSinceStartup+3;return;
            }
            if(stage==2)
            {
                var button=Object.FindObjectsOfType<UI.MXButton>().FirstOrDefault(b=>b.name=="AARecorder_Entry" && b.gameObject.activeInHierarchy);
                Check(button!=null && button.label.text=="内录","Native catalog recorder button is active and correctly labeled");
                var info=Object.FindObjectsOfType<CatalogFileInfo>().FirstOrDefault(i=>i.btn!=null && i.btn.transform.parent.Pointer==button!.transform.parent.Pointer);
                Check(info!=null && info.btn.gameObject.activeInHierarchy,"Recorder button is alongside the native entrance button");
                UiDiagnostics.SaveFrame(Path.Combine(UiDiagnostics.Root,"catalog-entry.ppm"));
                UiDiagnostics.Click("AARecorder_Entry");
                stage=3;readyAt=Time.realtimeSinceStartup+2;return;
            }
            if(stage==3)
            {
                Check(owner?.Ui?.IsVisible==true,"Clicking the catalog button opens the recorder panel");
                UiDiagnostics.SaveFrame(Path.Combine(UiDiagnostics.Root,"catalog-settings.ppm"));
                UiDiagnostics.Click("AARecorder_Close");

                stage=4;readyAt=Time.realtimeSinceStartup+2;return;
            }
            Check(owner?.Ui?.IsVisible==false,"Recorder settings close normally");
            UiDiagnostics.Click("AARecorder_Entry");
            Check(owner?.Ui?.IsVisible==true,"Recorder settings reopen normally");
            File.WriteAllText(Path.Combine(UiDiagnostics.Root,"catalog-entry-result.json"),JsonSerializer.Serialize(new{passed=true,choiceProbe=false,entryLabel="内录",opened=true,reopened=true,enhancementControlsExercised=false}));
            stage=99;Application.Quit(0);
        }
        catch(Exception error)
        {
            stage=99;savedProgress=true;
            RecorderPlugin.Instance.Log.LogError(error);
            File.WriteAllText(Path.Combine(UiDiagnostics.Root,"recording-ui-error.txt"),error.ToString());
            Application.Quit(1);
        }
    }
}
