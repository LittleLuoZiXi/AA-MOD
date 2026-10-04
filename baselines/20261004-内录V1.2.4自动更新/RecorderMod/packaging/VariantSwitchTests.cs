using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

public static class VariantSwitchTests
{
    static Receipt Manifest(string payload)
    {
        using(var zip=ZipFile.OpenRead(payload))using(var reader=new StreamReader(zip.GetEntry("payload-manifest.json").Open()))return InstallCore.Json.Deserialize<Receipt>(reader.ReadToEnd());
    }
    static void Install(string root,string payload,bool replacing)
    {
        int repeated=0;
        using(var stream=File.OpenRead(payload))if(!InstallCore.Install(root,stream,delegate{},delegate{return true;},delegate{repeated++;return true;},new string[0],false,delegate{return true;}))throw new Exception("Installation was cancelled.");
        if(repeated!=(replacing?1:0))throw new Exception("Unexpected overwrite-confirmation count.");
        var wanted=Manifest(payload);
        var actual=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(InstallCore.Within(root,InstallCore.ReceiptName)));
        if(actual.Files.Count!=wanted.Files.Count)throw new Exception("Receipt file count mismatch.");
        foreach(var file in wanted.Files)if(!actual.Files.Any(x=>x.Path==file.Path&&x.Sha256==file.Sha256)||InstallCore.Hash(InstallCore.Within(root,file.Path))!=file.Sha256)throw new Exception("Variant file did not update: "+file.Path);
    }
    public static int Main(string[] args)
    {
        try {
            var game=Path.GetFullPath(args[0]);var release=Path.GetFullPath(args[1]);var development=Path.GetFullPath(args[2]);
            var root=Path.Combine(Directory.GetParent(game).FullName,"R6安装测试","switch-"+DateTime.Now.ToString("HHmmss"),"中文 AA");
            Directory.CreateDirectory(root);File.Copy(Path.Combine(game,"AzureArchive.exe"),Path.Combine(root,"AzureArchive.exe"));Directory.CreateDirectory(Path.Combine(root,"AzureArchive_Data"));
            Directory.CreateDirectory(Path.Combine(root,"profiles","Test"));File.WriteAllText(Path.Combine(root,"ActiveProfile.txt"),"Test");
            var profile=Path.Combine(root,"profiles","Test","modconfig.json");File.WriteAllText(profile,"{\"EnabledMods\":[{\"name\":\"OtherMod\",\"version\":\"7\"}],\"custom\":42}");
            var source=Path.Combine(root,"RecorderMod","src","preserve.cs");Directory.CreateDirectory(Path.GetDirectoryName(source));File.WriteAllText(source,"KEEP ORIGINAL SOURCE");
            if(!Manifest(release).Files.Select(x=>x.Path).OrderBy(x=>x).SequenceEqual(Manifest(development).Files.Select(x=>x.Path).OrderBy(x=>x)))throw new Exception("Variant file sets differ; switching would orphan files.");
            Install(root,release,false);Console.WriteLine("PASS 1: Protected release freshly installed in isolated fixture.");
            Install(root,development,true);Console.WriteLine("PASS 2: Protected release switches to readable development build with explicit overwrite confirmation.");
            Install(root,release,true);Console.WriteLine("PASS 3: Development build switches back to protected release; every installed hash matches the protected receipt.");
            if(File.ReadAllText(source)!="KEEP ORIGINAL SOURCE"||!File.ReadAllText(profile).Contains("OtherMod")||!File.ReadAllText(profile).Contains("42"))throw new Exception("Unrelated source/profile was changed.");
            Console.WriteLine("PASS 4: Source and unrelated MOD/profile data survive both variant switches.");
            File.WriteAllText(Path.Combine(root,"PASS.txt"),"ALL 4 VARIANT SWITCH CHECKS PASSED");Console.WriteLine("Evidence: "+root);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
