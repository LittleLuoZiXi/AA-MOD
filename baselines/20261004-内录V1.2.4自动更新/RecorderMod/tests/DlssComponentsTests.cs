using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AzureArchive.Recorder;

public static class DlssComponentsTests
{
    static int passed;
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Pass(string message){Console.WriteLine("PASS "+(++passed)+": "+message);}
    public static void Run(string output,string referenceTool)
    {
        output=Path.GetFullPath(output);Check(!Directory.Exists(output),"Test output must be new");Directory.CreateDirectory(output);
        string game=Path.Combine(output,"AA"),tool=Path.Combine(game,DlssComponents.ToolRelative.Replace('/',Path.DirectorySeparatorChar)),profiles=Path.Combine(output,"profiles");
        Directory.CreateDirectory(Path.Combine(tool,"_internal"));
        foreach(var name in DlssComponents.RequiredFiles)File.WriteAllText(Path.Combine(tool,"_internal",name),"NONEMPTY PRESENCE FIXTURE");
        var empty=DlssComponents.InspectForRuntime(tool,profiles,50);Check(empty.Supported&&!empty.Ready&&empty.Missing.Any(x=>x.Contains("RTX 50")),"Missing current-generation runtime accepted");Pass("Common presence alone cannot enable DLSS without the selected generation's verified runtime");
        string runtime=Path.Combine(tool,"mods","dlss","rtx50","nvngx_dlssnr.dll");Directory.CreateDirectory(Path.GetDirectoryName(runtime));
        var reference=Path.Combine(referenceTool,"mods","dlss","rtx50","nvngx_dlssnr.dll");Check(DlssComponents.MatchesRuntime(reference,50),"Reference RTX 50 DLL is not the pinned fixture");File.Copy(reference,runtime);
        var valid=DlssComponents.InspectForRuntime(tool,profiles,50);Check(valid.Ready&&valid.Runtime==runtime,"Valid selected-generation runtime rejected");
        Check(DlssComponents.MissingForInstall(game,tool).Count==2,"Existing installer completeness API must still require other two generations");Pass("Runtime admits exactly the selected GPU generation while existing installation completeness semantics stay unchanged");
        foreach(var series in new[]{0,-1,20,60})Check(!DlssComponents.InspectForRuntime(tool,profiles,series).Supported&&!DlssComponents.InspectForRuntime(tool,profiles,series).Ready,"Unsupported series admitted: "+series);
        Check(!DlssComponents.InspectForRuntime(tool,profiles,30).Ready&&!DlssComponents.InspectForRuntime(tool,profiles,40).Ready,"A different supported generation reused RTX 50 DLL");Pass("RTX 20, unknown/non-NVIDIA generations and a mismatched supported generation remain unavailable");
        foreach(var name in DlssComponents.RequiredFiles) {
            var file=Path.Combine(tool,"_internal",name);var bytes=File.ReadAllBytes(file);var fingerprint=DlssComponents.RuntimeFingerprint(tool,profiles,50);
            try {
                File.WriteAllBytes(file,new byte[0]);var check=DlssComponents.InspectForRuntime(tool,profiles,50);Check(!check.Ready&&check.Missing.Contains(name),"Empty common file accepted: "+name);Check(fingerprint!=DlssComponents.RuntimeFingerprint(tool,profiles,50),"Empty file did not invalidate UI fingerprint");
                File.Delete(file);check=DlssComponents.InspectForRuntime(tool,profiles,50);Check(!check.Ready&&check.Missing.Contains(name),"Deleted common file accepted: "+name);
            } finally {File.WriteAllBytes(file,bytes);}
        }Pass("Every common component is required and both deletion and zero-length files invalidate readiness");
        var originalTime=File.GetLastWriteTimeUtc(runtime);var stamp=DlssComponents.RuntimeFingerprint(tool,profiles,50);byte original;
        using(var stream=new FileStream(runtime,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){original=(byte)stream.ReadByte();stream.Position=0;stream.WriteByte((byte)(original^1));}
        File.SetLastWriteTimeUtc(runtime,originalTime);
        try {
            Check(stamp==DlssComponents.RuntimeFingerprint(tool,profiles,50),"Metadata-preserving corruption fixture is not valid");
            Check(!DlssComponents.InspectForRuntime(tool,profiles,50).Ready,"Fresh preflight trusted cached metadata instead of hashing runtime contents");
        } finally {using(var stream=new FileStream(runtime,FileMode.Open,FileAccess.Write,FileShare.None))stream.WriteByte(original);File.SetLastWriteTimeUtc(runtime,originalTime);}
        Check(DlssComponents.InspectForRuntime(tool,profiles,50).Ready,"Restored authentic runtime rejected");Pass("Fresh preflight rejects byte corruption even if length and timestamps are preserved; authentic restoration passes");
        var legacy=Path.Combine(tool,"mods","nvngx_dlssnr.dll");File.Move(runtime,legacy);
        Check(DlssComponents.InspectForRuntime(tool,profiles,50).Ready,"Existing legacy tools stopped working");
        var cached=Path.Combine(profiles,"rtx50","nvngx_dlssnr.dll");Directory.CreateDirectory(Path.GetDirectoryName(cached));File.Move(legacy,cached);
        Check(DlssComponents.InspectForRuntime(tool,profiles,50).Ready,"Existing profile cache stopped working");
        Check(!DlssComponents.InspectForRuntime(tool,profiles,40).Ready,"Cached RTX 50 runtime incorrectly admitted RTX 40");Pass("Compatible legacy/profile locations remain supported only when the actual generation hash matches");
        var before=Directory.GetFileSystemEntries(output,"*",SearchOption.AllDirectories).OrderBy(x=>x).ToArray();
        var absent=Path.Combine(output,"never-created");var unavailable=DlssComponents.InspectForRuntime(absent,Path.Combine(absent,"profiles"),50);DlssComponents.RuntimeFingerprint(absent,Path.Combine(absent,"profiles"),50);
        Check(!unavailable.Ready&&!Directory.Exists(absent)&&before.SequenceEqual(Directory.GetFileSystemEntries(output,"*",SearchOption.AllDirectories).OrderBy(x=>x)),"Read-only inspection created/downloaded files");
        Check(DlssComponents.MissingRuntimeMessage=="缺少DLSS组件，相关DLSS功能不可用。","Required UI message changed");Pass("Missing-component inspection is read-only and keeps the exact user-facing message");
        File.WriteAllText(Path.Combine(output,"PASS.txt"),"ALL "+passed+" DLSS COMPONENT CHECKS PASSED\n"+DateTime.Now.ToString("O"));Console.WriteLine("ALL "+passed+" DLSS COMPONENT CHECKS PASSED: "+output);
    }
}
