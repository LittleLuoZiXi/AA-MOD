using System;
using System.Reflection;
using HarmonyLib;

namespace AzureArchive.Recorder;

// An offscreen MXButton can throw in OnPress when AUTO simulates a click.
// Route that click through OnSelect and retain a guarded completion fallback.
// AA finishes the AUTO countdown while an offscreen MXButton can still reject
// its simulated pointer click. Complete that same default through OnSelect:
// AA keeps control of button disabling, exit tweens and the scenario callback.
internal static class RecordingChoices
{
    internal static void Install(Harmony harmony,Action<string> warning)
    {
        foreach(var press in typeof(UI.MXButton).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic))
            if(press.Name.StartsWith("_CoSimulatePress_d__",StringComparison.Ordinal))
            {OptionalHooks.Patch(harmony,press,"MoveNext",typeof(RecordingChoicePress),warning);break;}
        foreach(var type in typeof(SelectionManager).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic))
            if(type.Name.StartsWith("_CoAutoSelect_d__",StringComparison.Ordinal))
            {
                foreach(var name in new[]{"__4__this","index","_elem_5__2","_elapsed_5__3"})
                    if(type.GetProperty(name)==null)
                    {warning("Optional recorder AUTO hook unavailable: coroutine field "+name+"; continuing without it.");return;}
                OptionalHooks.Patch(harmony,type,"MoveNext",typeof(RecordingChoices),warning);
                return;
            }
        warning("Optional recorder AUTO hook unavailable: SelectionManager.CoAutoSelect; continuing without it.");
    }

    static void Postfix(object __instance,bool __result)
    {
        var recorder=RecorderBehaviour.Instance;
        if(__result || recorder?.Capturing!=true)return;
        try
        {
            var type=__instance.GetType();
            if(type.GetProperty("__4__this")?.GetValue(__instance) is not SelectionManager manager ||
                !recorder.OwnsSelectionManager(manager) || !manager.isSelectionActive || !manager.autoModeEnabled)return;
            if(type.GetProperty("_elapsed_5__3")?.GetValue(__instance) is not float elapsed ||
                !(elapsed>=manager.autoSelectDelaySeconds))return;
            if(type.GetProperty("index")?.GetValue(__instance) is not int index ||
                index<0 || index!=manager.defaultSelectionIndex || manager.elements==null || index>=manager.elements.Count)return;
            var element=manager.elements[index];
            var original=type.GetProperty("_elem_5__2")?.GetValue(__instance) as SelectionElement;
            // Reject cancelled/replaced choices and clicks already accepted by AA.
            if(element==null || original==null || element.Pointer!=original.Pointer ||
                !element.gameObject.activeInHierarchy || element.button==null || element.button.disabled)return;
            recorder.LogChoice(index,elapsed);
            element.OnSelect();
        }
        catch(Exception error)
        {
            RecorderPlugin.Instance.Log.LogError(error);
            recorder.Stop("AUTO 选项提交失败，已停止并保留当前录像");
        }
    }
}
// Intercept the generated iterator rather than SimulateClick: IL2CPP may inline
// that small method. Only the current recorded AUTO element bypasses the pointer
// animation; every unrelated button and normal playback keeps AA's code.
internal static class RecordingChoicePress
{
    static bool Prefix(object __instance,ref bool __result)
    {
        var recorder=RecorderBehaviour.Instance;
        if(recorder?.Capturing!=true)return true;
        var type=__instance.GetType();
        if(type.GetProperty("__1__state")?.GetValue(__instance) is not int state || state!=0 ||
            type.GetProperty("__4__this")?.GetValue(__instance) is not UI.MXButton button)return true;
        var element=recorder.FindAutoChoice(button);
        if(element==null)return true;
        __result=false;
        try
        {
            type.GetProperty("__1__state")!.SetValue(__instance,-1);
            recorder.LogAutoChoice();
            element.OnSelect();
        }
        catch(Exception error)
        {
            RecorderPlugin.Instance.Log.LogError(error);
            recorder.Stop("AUTO 选项提交失败，已停止并保留当前录像");
        }
        return false;
    }
}