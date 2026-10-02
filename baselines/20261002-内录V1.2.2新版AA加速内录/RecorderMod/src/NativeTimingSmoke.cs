using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using UnityEngine;
using AzureArchive.Automation;

namespace AzureArchive.Recorder;

// Explicit local diagnostic only. No HarmonyPatch attributes, recorder arming,
// capture clock, RT assignment, user settings writes or fabricated readiness.
internal static class NativeTimingSmoke
{
    static bool enabled,opened,started,finished,partial,stopPending,rateHeld;
    static bool nativeClockValid=true;
    static int stopRow,nativeFps,lastRow=int.MinValue,startFrame,endFrame,oldTarget,oldVsync;
    static float startUnity,startRealtime;
    static string source="",evidence="",sourceHash="";
    static Test? player;
    static Action<string>? warning;
    static readonly Stopwatch wall=new();
    static readonly Stopwatch startupWall=new();
    static readonly HashSet<int> visited=new();
    static readonly HashSet<string> errors=new();
    static int readyEvents,rowEvents;
    static object? initialSettings;
    internal static bool Enabled=>enabled;

    internal static void Install(Harmony harmony,Action<string> warn)
    {
        var args=Environment.GetCommandLineArgs();
        int mode=Array.IndexOf(args,"--aa-recorder-native-timing");
        if(mode<0)return;
        warning=warn;
        try
        {
            string Value(string name)
            {
                int index=Array.IndexOf(args,name);
                if(index<0 || index+1>=args.Length || args[index+1].StartsWith("--",StringComparison.Ordinal))
                    throw new ArgumentException("Missing diagnostic argument: "+name);
                return args[index+1];
            }
            source=Path.GetFullPath(Value("--aa-recorder-native-timing"));
            evidence=Path.GetFullPath(Value("--aa-recorder-smoke-evidence"));
            if(!source.EndsWith(".aas",StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
                throw new ArgumentException("Native timing requires an existing .aas input.");
            if(args.Contains("--aa-recorder-smoke"))throw new ArgumentException("Native timing cannot also start recorder smoke.");
            if(args.Contains("--aa-recorder-native-stop-row") && (!int.TryParse(Value("--aa-recorder-native-stop-row"),out stopRow) || stopRow<0))
                throw new ArgumentException("Native stop row must be nonnegative.");
            if(args.Contains("--aa-recorder-native-fps") && (!int.TryParse(Value("--aa-recorder-native-fps"),out nativeFps) || (nativeFps!=0 && nativeFps!=30 && nativeFps!=60)))
                throw new ArgumentException("Native FPS must be 0, 30 or 60.");
            Directory.CreateDirectory(evidence);
            sourceHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            void Patch(string name,Delegate callback,bool prefix=false)
            {
                var target=AccessTools.Method(typeof(Test),name)??throw new MissingMethodException("Test."+name);
                var hook=new HarmonyMethod(callback.Method);
                harmony.Patch(target,prefix?hook:null,prefix?null:hook);
            }
            Patch(nameof(Test.Start),(Action<Test>)Started);
            Patch(nameof(Test.Update),(Action<Test>)PlayerUpdated);
            Patch("OnReady",(Action<Test>)Ready);
            Patch(nameof(Test.End),(Action<Test>)Ending,true);
            enabled=true;startupWall.Restart();
        }
        catch(Exception error){Report("install",error);}
    }

    internal static void Update()
    {
        if(!enabled)return;
        try
        {
            if(finished)
            {
                if(Time.frameCount>endFrame){enabled=false;Application.Quit(0);}
                return;
            }
            if(!started && startupWall.Elapsed.TotalSeconds>120)
            {
                Write("native-timing-failure.json",new{completed=false,opened,reason="Native player did not start within 120 seconds of diagnostic startup.",seconds=startupWall.Elapsed.TotalSeconds});
                enabled=false;Application.Quit(1);return;
            }
            if(!opened)
            {
                var manager=UnityEngine.Object.FindObjectOfType<ScenarioResourceManager>();
                if(!DiagnosticHostReady.Check(manager))return;
                opened=true;
                AuthoringWorkbench.OpenScenario(source);
            }
            // End outside the Test.Update postfix stack, so diagnostic truncation
            // cannot destroy the player underneath the remaining native update.
            if(stopPending && player!=null)
            {
                stopPending=false;partial=true;
                player.End();
            }
        }
        catch(Exception error){Report("update",error);}
    }

    static bool Owns(Test value)=>enabled && started && !finished && value!=null && player!=null && value.Pointer==player.Pointer;
    static void Report(string scope,Exception error)
    {
        try{if(errors.Add(scope+":"+error.GetType().Name))warning?.Invoke("Native timing diagnostic skipped ("+scope+"): "+error.Message);}catch { }
    }
    static void Write(string name,object value)=>File.WriteAllText(Path.Combine(evidence,name),JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));

    static object SettingsSnapshot(Test value)
    {
        var settings=UnityEngine.Object.FindObjectOfType<UserSettings>();
        return new {
            captureFramerate=Time.captureFramerate,targetFrameRate=Application.targetFrameRate,
            vSyncCount=QualitySettings.vSyncCount,timeScale=Time.timeScale,
            deltaTime=Time.deltaTime,unscaledDeltaTime=Time.unscaledDeltaTime,
            runInBackground=Application.runInBackground,
            autoDelayInSeconds=value.autoDelayInSeconds,
            userAutoDelay=Convert.ToString(settings?.data?.autoDelay,CultureInfo.InvariantCulture),
            userFpsTier=Convert.ToString(settings?.data?.fpsTier,CultureInfo.InvariantCulture),
            textPopupInterval=Settings.textPopupInterval,textNewLineInterval=Settings.textNewLineInterval,
            auto=value.auto,autoEnabled=value.IsAutoEnabled
        };
    }

    static void Event(string kind,Test value)
    {
        nativeClockValid &= Time.captureFramerate==0;
        var sample=new {kind,wallSeconds=wall.Elapsed.TotalSeconds,unityTime=Time.time,
            unitySeconds=Time.time-startUnity,realtimeSeconds=Time.realtimeSinceStartup-startRealtime,
            frame=Time.frameCount,frames=Time.frameCount-startFrame,cur=value.cur,ready=value.ready,
            locked=value.locked,auto=value.auto,autoEnabled=value.IsAutoEnabled,
            autoDelayInSeconds=value.autoDelayInSeconds,hasSelection=value.hasSelection,
            deltaTime=Time.deltaTime,unscaledDeltaTime=Time.unscaledDeltaTime,
            captureFramerate=Time.captureFramerate,targetFrameRate=Application.targetFrameRate,
            vSyncCount=QualitySettings.vSyncCount,timeScale=Time.timeScale};
        File.AppendAllText(Path.Combine(evidence,"native-timing-events.jsonl"),JsonSerializer.Serialize(sample)+Environment.NewLine);
    }

    static void Started(Test __instance)
    {
        try
        {
            if(!enabled || !opened || started || __instance==null || __instance.previewMode)return;
            player=__instance;started=true;startUnity=Time.time;startRealtime=Time.realtimeSinceStartup;
            startFrame=Time.frameCount;wall.Restart();
            if(Time.captureFramerate!=0)Report("capture-clock",new InvalidOperationException("Native timing is invalid because captureFramerate is not zero; no clock changes applied."));
            initialSettings=SettingsSnapshot(__instance);
            if(nativeFps!=0)
            {
                oldTarget=Application.targetFrameRate;oldVsync=QualitySettings.vSyncCount;rateHeld=true;
                Application.targetFrameRate=nativeFps;QualitySettings.vSyncCount=0;
            }
            // Preserve native AUTO if already enabled; never select a branch here.
            if(!__instance.IsAutoEnabled)__instance.ToggleAuto();
            Write("native-timing-start.json",new {source,sourceSha256=sourceHash,stopRow,nativeFps,
                initialSettings,effectiveSettings=SettingsSnapshot(__instance),startUnity,startRealtime,startFrame,
                capturesVideo=false,changesCaptureFramerate=false,changesTimeScale=false});
            Event("start",__instance);ObserveRow(__instance);
        }
        catch(Exception error){Report("start",error);}
    }
    static void ObserveRow(Test value)
    {
        if(value.cur==lastRow)return;
        lastRow=value.cur;visited.Add(value.cur);rowEvents++;Event("row",value);
        if(stopRow>0 && value.cur>=stopRow)stopPending=true;
    }
    static void PlayerUpdated(Test __instance)
    {
        try{if(Owns(__instance))ObserveRow(__instance);}catch(Exception error){Report("row",error);}
    }
    static void Ready(Test __instance)
    {
        try{if(Owns(__instance)){readyEvents++;Event("ready",__instance);}}catch(Exception error){Report("ready",error);}
    }
    static void Ending(Test __instance)
    {
        try{if(!Owns(__instance))return;}catch(Exception error){Report("end-owner",error);return;}
        try
        {
            Event(partial?"partial-end":"natural-end",__instance);
            double elapsed=wall.Elapsed.TotalSeconds;int frames=Time.frameCount-startFrame;
            Write("native-timing-result.json",new {mode="native-auto",completed=!partial,partial,
                termination=partial?"diagnostic-stop-row":"native-Test.End",stopRow,lastRow,nativeFps,
                sourceSha256=sourceHash,wallSeconds=elapsed,unitySeconds=Time.time-startUnity,
                realtimeSeconds=Time.realtimeSinceStartup-startRealtime,frames,
                observedFramesPerSecond=elapsed>0?frames/elapsed:0,visitedRows=visited.Count,rowEvents,readyEvents,
                initialSettings,finalSettings=SettingsSnapshot(__instance),
                validNativeClock=nativeClockValid && Time.captureFramerate==0,capturesVideo=false,
                diagnosticErrors=errors.ToArray()});
            // Generic result alias makes this diagnostic easy to consume without
            // confusing it with recorder completed.json or preflight.json.
            File.Copy(Path.Combine(evidence,"native-timing-result.json"),Path.Combine(evidence,"result.json"),true);
        }
        catch(Exception error){Report("end",error);}
        finally
        {
            if(rateHeld)
            {
                try{Application.targetFrameRate=oldTarget;QualitySettings.vSyncCount=oldVsync;}
                catch(Exception error){Report("restore-rate",error);}
                rateHeld=false;
            }
            finished=true;endFrame=Time.frameCount;wall.Stop();
        }
    }
}
