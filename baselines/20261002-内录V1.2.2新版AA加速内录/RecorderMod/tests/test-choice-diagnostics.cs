using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// Standalone registration doubles: no AA, native Unity, network or DLSS code executes.
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class|AttributeTargets.Method)]
    public sealed class HarmonyPatch : Attribute { }
    public sealed class HarmonyMethod
    {
        public MethodInfo Method;
        public HarmonyMethod(MethodInfo method) { Method=method; }
    }
    public sealed class PatchRecord
    {
        public MethodBase Target=null!;
        public HarmonyMethod? Prefix,Postfix;
    }
    public sealed class Harmony
    {
        public readonly List<PatchRecord> Records=new();
        public bool ThrowPatch;
        public int Attempts;
        public void PatchAll(Assembly assembly)
        {
            // The regression is metadata scanning, before any native hook is installed.
            foreach(var type in assembly.GetTypes())
                if(type.GetCustomAttributes(typeof(HarmonyPatch),false).Length!=0)
                    throw new InvalidOperationException("Unexpected diagnostic class reached PatchAll: "+type.FullName);
        }
        public void Patch(MethodBase target,HarmonyMethod? prefix=null,HarmonyMethod? postfix=null)
        {
            Attempts++;
            if(ThrowPatch)throw new InvalidOperationException("Injected patch failure");
            Records.Add(new PatchRecord{Target=target,Prefix=prefix,Postfix=postfix});
        }
    }
    public static class AccessTools
    {
        public static bool Missing,ThrowLookup;
        public static int Lookups;
        public static MethodInfo? Method(Type type,string name)
        {
            Lookups++;
            if(ThrowLookup)throw new AmbiguousMatchException("Injected target lookup failure");
            if(Missing)return null;
            return type.GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);
        }
        public static void Reset(){Missing=false;ThrowLookup=false;Lookups=0;}
    }
}
namespace UnityEngine
{
    public sealed class GameObject { public bool activeInHierarchy=true,activeSelf=true; }
}
namespace UI
{
    public sealed class MXButton
    {
        public bool disabled,enabled=true,checkCursor,pressed;
        public UnityEngine.GameObject gameObject=new();
    }
}
public sealed class UILabel { public string text="fixture"; }
public sealed class Toggle { public UnityEngine.GameObject gameObject=new(); }
public sealed class TaskPointer { public IntPtr Pointer=new(41); }
public sealed class Test
{
    public int cur=7;
    public bool IsAutoEnabled=true,auto=true,ready=true,locked,hasSelection=true;
    public Toggle autoToggle=new();
    public SelectionManager selectionManager=new();
    public void Update(){ }
}
public sealed class SelectionElement
{
    public IntPtr Pointer=new(23);
    public UILabel label=new();
    public UI.MXButton button=new();
    public UnityEngine.GameObject gameObject=new();
    public void SimulateClick(){ }
    public void OnSelect(){ }
    public void DisableButton(){ }
}
public sealed class SelectionManager
{
    public IntPtr Pointer=new(17);
    public bool isSelectionActive=true,autoModeEnabled=true;
    public int defaultSelectionIndex=0;
    public float autoSelectDelaySeconds=4;
    public TaskPointer autoSelectTask=new();
    public List<SelectionElement> elements=new(){new SelectionElement()};
    public void OnAutoModeChanged(bool enabled){ }
    public sealed class _CoAutoSelect_d__16
    {
        public int __1__state {get;set;}
        public float _elapsed_5__3 {get;set;}=4;
        public int index {get;set;}
        public object? __2__current {get;set;}
        public bool MoveNext()=>false;
    }
}
namespace AzureArchive.Recorder
{
    public sealed class TestLog
    {
        public readonly List<string> Lines=new();
        public void LogInfo(object value)=>Lines.Add(value.ToString()??"");
    }
    public sealed class RecorderPlugin
    {
        public static RecorderPlugin Instance=new();
        public TestLog Log=new();
    }
    public sealed class RecorderBehaviour
    {
        public static RecorderBehaviour? Instance=new();
        public bool Capturing=true;
        public float PlaybackTime=10;
        public bool OwnsPlayer(Test value)=>true;
    }
    public static class ChoiceDiagnosticsRegression
    {
        static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        public static string[] Run(bool optIn)
        {
            var checks=new List<string>();
            Check(Environment.GetCommandLineArgs().Contains("--aa-recorder-choice-probe")==optIn,"Worker opt-in argument is wrong.");
            var types=new[]{typeof(ChoiceDiagnostics),typeof(ChoiceProbe),typeof(ChoiceCoroutineProbe),typeof(ChoiceAutoModeProbe),typeof(ChoiceClickProbe)};
            Check(types.All(t=>t.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch),false).Length==0),"A diagnostic class still carries HarmonyPatch metadata.");
            Check(types.All(t=>t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).All(m=>m.Name!="TargetMethods")),"TargetMethods disable pattern has returned.");
            new HarmonyLib.Harmony().PatchAll(typeof(ChoiceDiagnostics).Assembly);
            checks.Add("No diagnostic patch-class metadata or TargetMethods; PatchAll cannot discover these classes.");
            if(!optIn)
            {
                HarmonyLib.AccessTools.Reset();HarmonyLib.AccessTools.ThrowLookup=true;
                var harmony=new HarmonyLib.Harmony{ThrowPatch=true};var warnings=new List<string>();
                ChoiceDiagnostics.Install(harmony,warnings.Add);
                bool uiLoadContinued=true;
                Check(harmony.Attempts==0&&harmony.Records.Count==0&&HarmonyLib.AccessTools.Lookups==0&&warnings.Count==0&&uiLoadContinued,"Normal startup did diagnostic work or failed before UI registration.");
                checks.Add("No opt-in: zero target lookups, zero registrations, zero warnings; startup continues.");
                return checks.ToArray();
            }
            HarmonyLib.AccessTools.Reset();
            var complete=new HarmonyLib.Harmony();var completeWarnings=new List<string>();
            ChoiceDiagnostics.Install(complete,completeWarnings.Add);
            Check(complete.Records.Count==6&&completeWarnings.Count==0,"Opt-in did not register all six known targets.");
            Check(complete.Records.All(r=>(r.Prefix==null)!=(r.Postfix==null)),"Invalid prefix/postfix registration.");
            Check(complete.Records.All(r=>(r.Prefix??r.Postfix)!.Method.IsStatic),"Delegate callback does not resolve to a static method.");
            RecorderPlugin.Instance.Log.Lines.Clear();
            foreach(var patch in complete.Records)
            {
                var callback=(patch.Prefix??patch.Postfix)!.Method;
                object[] arguments=patch.Target.DeclaringType==typeof(Test)?new object[]{new Test()}:
                    patch.Target.DeclaringType==typeof(SelectionManager)?new object[]{new SelectionManager(),true}:
                    patch.Target.DeclaringType==typeof(SelectionElement)?new object[]{new SelectionElement(),patch.Target}:
                    new object[]{new SelectionManager._CoAutoSelect_d__16(),false};
                callback.Invoke(null,arguments);
            }
            Check(RecorderPlugin.Instance.Log.Lines.Count>=6,"Registered callback method handles cannot execute after renaming.");
            checks.Add("Opt-in: six explicit method-handle registrations; all callback handles execute.");
            foreach(var fault in new[]{"missing-targets","lookup-throws","patch-throws"})
            {
                HarmonyLib.AccessTools.Reset();HarmonyLib.AccessTools.Missing=fault=="missing-targets";HarmonyLib.AccessTools.ThrowLookup=fault=="lookup-throws";
                var harmony=new HarmonyLib.Harmony{ThrowPatch=fault=="patch-throws"};var warnings=new List<string>();
                ChoiceDiagnostics.Install(harmony,warnings.Add);
                bool uiLoadContinued=true;
                Check(warnings.Count==6&&harmony.Records.Count==0&&uiLoadContinued,"Diagnostic failure prevented startup or lost warnings: "+fault);
                checks.Add("Opt-in "+fault+": six warnings, no installed patches; startup continues.");
            }
            return checks.ToArray();
        }
    }
}