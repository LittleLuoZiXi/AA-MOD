using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace AzureArchive.Recorder;

// Unity messages and generated coroutine names can differ between interop caches.
// Missing optional hooks must not abort registration of the recorder's UI.
internal static class OptionalHooks
{
    internal static bool Patch(Harmony harmony, Type? targetType, string methodName,
        Type patchType, Action<string> warning)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
        var target=targetType?.GetMethods(flags).SingleOrDefault(m=>m.Name==methodName && m.GetParameters().Length==0);
        if(target==null)
        {
            warning($"Optional recorder hook unavailable: {targetType?.FullName??"<missing type>"}.{methodName}; continuing without it.");
            return false;
        }
        var prefix=patchType.GetMethod("Prefix",flags);
        var postfix=patchType.GetMethod("Postfix",flags);
        harmony.Patch(target,prefix==null?null:new HarmonyMethod(prefix),postfix==null?null:new HarmonyMethod(postfix));
        return true;
    }
}
