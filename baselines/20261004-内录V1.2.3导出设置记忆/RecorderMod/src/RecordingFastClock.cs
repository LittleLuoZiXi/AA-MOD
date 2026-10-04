using System;
using Il2CppInterop.Runtime.Attributes;
namespace AzureArchive.Recorder;
public sealed partial class RecorderBehaviour
{
    readonly System.Collections.Generic.Dictionary<IntPtr,UnityEngine.Camera> fastHeldCameras=new();
    [HideFromIl2Cpp]
    void HoldFastCamera(UnityEngine.Camera camera)
    {
        if(FastSimulationHz==0 || camera==null)return;
        nativeUi?.ExcludeFromCapture(camera);
        if(!camera.enabled)return;
        if(!fastHeldCameras.ContainsKey(camera.Pointer))fastHeldCameras.Add(camera.Pointer,camera);
        camera.enabled=false;
    }
    [HideFromIl2Cpp]
    void RestoreFastCameras()
    {
        foreach(var camera in fastHeldCameras.Values)if(camera!=null)camera.enabled=true;
        fastHeldCameras.Clear();
    }
    static readonly int? requestedFastSimulationHz=ReadFastSimulationHz();
    int frozenFastSimulationHz;
    int FastSimulationHz { [HideFromIl2Cpp] get=>frozenFastSimulationHz; }
    string TimingMode { [HideFromIl2Cpp] get=>measureTotalRequested?"native-preflight-replay":FastSimulationHz>0?"accelerated":"native-realtime"; }
    [HideFromIl2Cpp]
    void FreezeCaptureTiming()
    {
        // Keep the selected source clock unchanged until the next recording.
        // Measured playback always retains its native preflight/replay cadence.
        frozenFastSimulationHz=measureTotalRequested?0:requestedFastSimulationHz??(Config.AcceleratedRecording.Value?120:0);
    }
    static int? ReadFastSimulationHz()
    {
        var args=Environment.GetCommandLineArgs();
        int i=Array.IndexOf(args,"--aa-recorder-fast-hz");
        if(i<0)return null;
        if(i+1>=args.Length || !int.TryParse(args[i+1],out int hz) || (hz!=0 && (hz<100 || hz>2000)))
            throw new ArgumentException("Diagnostic simulation frequency must be 0 or 100..2000 Hz.");
        return hz;
    }
}
