using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using TextAnimation=ScenarioAnimation.TextTypewriterAnimation;

namespace AzureArchive.Recorder;

// Purely observational and opt-in: no HarmonyPatch attributes, no changes to
// yielded waits, return values, settings, captureFramerate or native playback.
internal static class TimingDiagnostics
{
    static Action<string>? warning;
    static readonly HashSet<string> errors=new();
    static readonly Dictionary<IntPtr,int> sampleCounts=new();
    static readonly HashSet<IntPtr> settingsPlayers=new();
    static readonly Stopwatch wall=Stopwatch.StartNew();

    internal static void Install(Harmony harmony,Action<string> warn)
    {
        if(!Environment.GetCommandLineArgs().Contains("--aa-recorder-choice-probe"))return;
        warning=warn;
        try
        {
            var task=typeof(TextAnimation).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic)
                .FirstOrDefault(type=>type.Name.StartsWith("_TextFadeInTask_d__",StringComparison.Ordinal));
            var target=task==null?null:AccessTools.Method(task,"MoveNext");
            if(target==null)throw new MissingMethodException("TextTypewriterAnimation._TextFadeInTask.MoveNext");
            harmony.Patch(target,postfix:new HarmonyMethod(((Action<object,bool>)TextTaskAfter).Method));
        }
        catch(Exception error){Report("install",error);}

    }

    static void Report(string scope,Exception error)
    {
        try
        {
            if(errors.Add(scope+":"+error.GetType().Name))warning?.Invoke($"Timing diagnostic skipped ({scope}): {error.Message}");
        }
        catch { }
    }

    static string F(double value)=>value.ToString("0.000000",CultureInfo.InvariantCulture);

    static void LogSettings(Test owner,string mode)
    {
        try
        {
            if(settingsPlayers.Contains(owner.Pointer))return;
            if(settingsPlayers.Count>=4096)return;
            settingsPlayers.Add(owner.Pointer);
            // Find an existing component; never invoke the singleton getter or
            // write/reload shared user settings while measuring native behavior.
            var settings=UnityEngine.Object.FindObjectOfType<UserSettings>();
            RecorderPlugin.Instance.Log.LogInfo($"TIMING SETTINGS mode={mode} player={owner.Pointer} popupInterval={F(Settings.textPopupInterval)} newLineInterval={F(Settings.textNewLineInterval)} userAutoDelay={settings?.data?.autoDelay} userFpsTier={settings?.data?.fpsTier} playerAutoDelay={F(owner.autoDelayInSeconds)} auto={owner.auto}/{owner.IsAutoEnabled} captureFps={Time.captureFramerate} targetFps={Application.targetFrameRate} vsync={QualitySettings.vSyncCount} timeScale={F(Time.timeScale)}");
        }
        catch(Exception error){Report("settings",error);}
    }

    internal static void TextTaskAfter(object __instance,bool __result)
    {
        try
        {
            if(__instance is not Il2CppObjectBase iterator)return;
            var type=__instance.GetType();
            object? Read(string name)=>type.GetProperty(name)?.GetValue(__instance);
            var animation=Read("__4__this") as TextAnimation;
            var owner=animation?.owner;
            if(owner==null || owner.previewMode)return;
            var recorder=RecorderBehaviour.Instance;
            bool offline=recorder?.Capturing==true;
            if(offline && !recorder!.OwnsPlayer(owner))return;
            string mode=offline?"offline":"native";
            LogSettings(owner,mode);

            int count=sampleCounts.TryGetValue(iterator.Pointer,out var existing)?existing:0;
            if(count>=12 || (count==0 && sampleCounts.Count>=4096))return;
            int index=Read("_i_5__2") is int value?value:-1;
            if(index>=12)return;
            sampleCounts[iterator.Pointer]=count+1;

            var current=Read("__2__current") as Il2CppSystem.Object;
            var scaled=current?.TryCast<WaitForSeconds>();
            var realtime=current?.TryCast<WaitForSecondsRealtime>();
            string text=Read("txt") as string??animation!.targetTxt??"";
            string character=index>=0 && index<text.Length?"U+"+((int)text[index]).ToString("X4",CultureInfo.InvariantCulture):"<none>";
            string playback=offline?F(recorder!.PlaybackTime):"<native>";
            RecorderPlugin.Instance.Log.LogInfo($"TIMING TEXT mode={mode} player={owner.Pointer} row={owner.cur} iterator={iterator.Pointer} animation={animation!.Pointer} sample={count+1}/12 state={Read("__1__state")} i={index} character={character} length={text.Length} result={__result} current={current?.GetIl2CppType().FullName??"<null>"} waitSeconds={(scaled==null?"<none>":F(scaled.m_Seconds))} realtimeWait={(realtime==null?"<none>":F(realtime.waitTime))} frame={Time.frameCount} delta={F(Time.deltaTime)} unscaledDelta={F(Time.unscaledDeltaTime)} unityTime={F(Time.time)} realtime={F(Time.realtimeSinceStartup)} uiTime={F(RealTime.time)} playback={playback} wall={F(wall.Elapsed.TotalSeconds)} popupInterval={F(Settings.textPopupInterval)} newLineInterval={F(Settings.textNewLineInterval)}");
        }
        catch(Exception error){Report("text-task",error);}
    }
}
