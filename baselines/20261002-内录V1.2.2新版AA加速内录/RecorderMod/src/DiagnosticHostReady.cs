using UnityEngine;
namespace AzureArchive.Recorder;
// Diagnostic launchers can observe DB completion before AA finishes building
// its resource path index. Do not open a story until both are actually ready.
internal static class DiagnosticHostReady
{
    internal static bool Check(ScenarioResourceManager? manager)
    {
        if(manager==null || !manager.AreDbsLoaded || manager._manifestIndex==null || manager._manifest==null)return false;
        var characters=Object.FindObjectOfType<CharacterManager>();
        return characters!=null && characters.characterHashDict.ByteBuffer!=null;
    }
}