using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
namespace AzureArchive.Recorder;
// Diagnostic hooks are explicitly opt-in. An empty Harmony TargetMethods list is
// not a reliable way to disable a patch class: PatchAll can still treat it as a
// patch with an undefined target and abort the entire production plugin load.
internal static class ChoiceDiagnostics
{
    internal static void Install(Harmony harmony, Action<string> warning)
    {
        if(!Environment.GetCommandLineArgs().Contains("--aa-recorder-choice-probe"))return;
        void Patch(Type? type,string methodName,Delegate callback,bool prefix=false)
        {
            try
            {
                var target=type==null?null:AccessTools.Method(type,methodName);
                if(target==null){warning($"Choice diagnostic unavailable: {type?.FullName}.{methodName}");return;}
                var method=new HarmonyMethod(callback.Method);
                harmony.Patch(target,prefix?method:null,prefix?null:method);
            }
            catch(Exception error){warning($"Choice diagnostic skipped: {type?.FullName}.{methodName}: {error.Message}");}
        }
        Patch(typeof(Test),nameof(Test.Update),(Action<Test>)ChoiceProbe.Postfix);
        var coroutine=typeof(SelectionManager).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic)
            .FirstOrDefault(t=>t.Name.StartsWith("_CoAutoSelect_d__",StringComparison.Ordinal));
        Patch(coroutine,"MoveNext",(Action<object,bool>)ChoiceCoroutineProbe.Postfix);
        Patch(typeof(SelectionManager),"OnAutoModeChanged",(Action<SelectionManager,bool>)ChoiceAutoModeProbe.Postfix);
        foreach(var name in new[]{"SimulateClick","OnSelect","DisableButton"})
            Patch(typeof(SelectionElement),name,(Action<SelectionElement,MethodBase>)ChoiceClickProbe.Prefix,true);
    }
}
static class ChoiceProbe
{
    static float next;
    static int last=-999;
    internal static void Postfix(Test __instance)
    {
        var r=RecorderBehaviour.Instance;
        if(r?.Capturing!=true || !r.OwnsPlayer(__instance))return;
        if(__instance.cur==last && r.PlaybackTime<next)return;
        last=__instance.cur;next=r.PlaybackTime+1;
        var s=__instance.selectionManager;        if(s!=null)for(int i=0;i<s.elements.Count;i++){var e=s.elements[i];RecorderPlugin.Instance.Log.LogInfo($"CHOICE ELEMENT index={i} ptr={e.Pointer} label={e.label?.text} active={e.gameObject.activeInHierarchy} disabled={e.button?.disabled} enabled={e.button?.enabled} cursor={e.button?.checkCursor} pressed={e.button?.pressed}");}
        RecorderPlugin.Instance.Log.LogInfo($"CHOICE PROBE time={r.PlaybackTime} cur={last} auto={__instance.IsAutoEnabled}/{__instance.auto} ready={__instance.ready} locked={__instance.locked} hasSelection={__instance.hasSelection} toggleActive={__instance.autoToggle?.gameObject.activeSelf} manager={s?.Pointer} active={s?.isSelectionActive} autoMode={s?.autoModeEnabled} default={s?.defaultSelectionIndex} delay={s?.autoSelectDelaySeconds} task={s?.autoSelectTask?.Pointer} elements={s?.elements?.Count}");
    }
}
static class ChoiceCoroutineProbe
{
    static float next;
    internal static void Postfix(object __instance,bool __result)
    {
        var r=RecorderBehaviour.Instance;if(r?.Capturing!=true)return;
        if(__result && r.PlaybackTime<next)return;next=r.PlaybackTime+1;
        var t=__instance.GetType();
        object? Read(string name)=>t.GetProperty(name)?.GetValue(__instance);
        RecorderPlugin.Instance.Log.LogInfo($"CHOICE COROUTINE result={__result} time={r.PlaybackTime} state={Read("__1__state")} elapsed={Read("_elapsed_5__3")} index={Read("index")} current={Read("__2__current")}");
    }
}
static class ChoiceAutoModeProbe
{
    internal static void Postfix(SelectionManager __instance,bool __0)
    {
        var r=RecorderBehaviour.Instance;if(r?.Capturing!=true)return;
        RecorderPlugin.Instance.Log.LogInfo($"CHOICE AUTO CHANGED enabled={__0} time={r.PlaybackTime} active={__instance.isSelectionActive}");
    }
}
static class ChoiceClickProbe
{
    internal static void Prefix(SelectionElement __instance,MethodBase __originalMethod)
    {
        var r=RecorderBehaviour.Instance;if(r?.Capturing!=true)return;
        RecorderPlugin.Instance.Log.LogInfo($"CHOICE CLICK {__originalMethod.Name} time={r.PlaybackTime} element={__instance.Pointer} active={__instance.gameObject.activeInHierarchy} label={__instance.label?.text} disabled={__instance.button?.disabled} buttonActive={__instance.button?.gameObject.activeInHierarchy} buttonEnabled={__instance.button?.enabled}");
    }
}
