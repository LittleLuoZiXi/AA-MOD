$ErrorActionPreference='Stop'
$game=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$harmony=Join-Path $game 'BepInEx/core/0Harmony.dll'
$null=[Reflection.Assembly]::LoadFrom($harmony)
$source=Get-Content (Join-Path $game 'RecorderMod/src/OptionalHooks.cs') -Raw
$source+=@'

public static class MissingHookRegression
{
    sealed class HostWithoutDestroy { public void Start(){} }
    sealed class HostWithUnrelatedOverload { public void OnDestroy(int value){} }
    sealed class PatchMethods { static void Prefix(){} }
    public static string Run()
    {
        int warnings=0;
        foreach(var host in new Type?[]{null,typeof(HostWithoutDestroy),typeof(HostWithUnrelatedOverload)})
            if(OptionalHooks.Patch(null!,host,"OnDestroy",typeof(PatchMethods),message=>warnings++))
                throw new Exception("Missing optional hook was incorrectly registered.");
        if(warnings!=3)throw new Exception("Missing hook diagnostics were lost.");
        return "PASS: absent type, absent OnDestroy method, and unrelated overload are skipped with diagnostics.";
    }
}
'@
$references=@($harmony)+@(Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
Add-Type -TypeDefinition $source -ReferencedAssemblies $references -CompilerOptions '/nullable:enable'
[AzureArchive.Recorder.MissingHookRegression]::Run()
