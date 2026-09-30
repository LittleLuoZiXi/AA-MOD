using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Core fixtures use tiny synthetic payloads and a deliberately identified encoder
// process stub. They verify installation transactions, not video/GPU performance.
public static class V1InstallerTests
{
    static Dictionary<string,string> options;
    const string RecorderVersion="1.1.0";
    static string workspace,game,root,encoder;
    static int passed;
    static readonly List<Dictionary<string,object>> results=new List<Dictionary<string,object>>();
    static string Opt(string key){string value;return options.TryGetValue(key,out value)?value:"";}
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Run(string name,Action action)
    {
        if(Opt("TestFilter").Length>0&&name.IndexOf(Opt("TestFilter"),StringComparison.OrdinalIgnoreCase)<0)return;
        Console.WriteLine("START: "+name);
        var time=Stopwatch.StartNew();
        try{action();passed++;results.Add(new Dictionary<string,object>{{"name",name},{"passed",true},{"milliseconds",time.ElapsedMilliseconds}});Console.WriteLine("PASS "+passed+": "+name);}
        catch(Exception error){results.Add(new Dictionary<string,object>{{"name",name},{"passed",false},{"error",error.ToString()}});throw;}
    }
    static void MustFail(Action action,string message)
    {
        bool failed=false;try{action();}catch(Exception error){failed=true;Console.WriteLine("Expected rejection: "+error.GetBaseException().Message);}
        Check(failed,message+" was accepted");
    }
    static string P(string relative){return InstallCore.Within(root,relative);}
    static void Put(string relative,string text){var path=P(relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,text,new UTF8Encoding(false));}
    static void Bytes(string relative,byte[] bytes){var path=P(relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,bytes);}
    static string HashBytes(byte[] bytes){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","");}
    static Dictionary<string,string> Snapshot(string directory)
    {
        var snapshot=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if(!Directory.Exists(directory))return snapshot;
        snapshot.Add("D:","");
        foreach(var path in Directory.GetDirectories(directory,"*",SearchOption.AllDirectories)){InstallCore.NoLinks(path);snapshot.Add("D:"+path.Substring(directory.Length),"");}
        foreach(var path in Directory.GetFiles(directory,"*",SearchOption.AllDirectories)){InstallCore.NoLinks(path);snapshot.Add("F:"+path.Substring(directory.Length),InstallCore.Hash(path));}
        return snapshot;
    }
    static void Same(Dictionary<string,string> before,string directory,string message)
    {
        var after=Snapshot(directory);
        var changed=before.Keys.Union(after.Keys).Where(k=>!before.ContainsKey(k)||!after.ContainsKey(k)||before[k]!=after[k]).ToArray();
        Check(changed.Length==0,message+": "+String.Join(", ",changed.Take(16)));
    }
    static void Fixture(string name)
    {
        root=Path.Combine(workspace,name);
        Check(!Directory.Exists(root),"Fixture already exists");Directory.CreateDirectory(root);
        File.Copy(Path.Combine(game,"AzureArchive.exe"),P("AzureArchive.exe"));Directory.CreateDirectory(P("AzureArchive_Data"));
        Put("ActiveProfile.txt","Active");
        Put("profiles/Active/modconfig.json","{\"EnabledMods\":[{\"name\":\"OtherMod\",\"version\":\"7.0\",\"custom\":42}],\"note\":\"保留\"}");
        Put("profiles/Disabled/modconfig.json","{ \"EnabledMods\": [{\"name\":\"OnlyOther\",\"version\":\"2\"}], \"custom\": 91 }\n");
        Put("profiles/Active/configs/azurearchive.recorder.cfg","[Recording]\nFrameRate = 60\nFFmpegPath = "+encoder+"\n[DLSS]\nToolDirectory = C:\\user-selected-tool\n[Tutorial]\nSeenVersion = 1\n");
        Put("profiles/Active/configs/azurearchive.recorder-state/export-sequence.json","{\"date\":\"20260929\",\"sequence\":27}");
        Put("profiles/Active/configs/other-mod.cfg","OTHER SETTINGS");
        Put("mods/OtherMod/keep.dll","OTHER MOD");Put("用户剧情/保留.aap2","USER PROJECT");Put("用户视频/保留.mp4","USER VIDEO");
        Put("RecorderMod/src/keep.cs","DEVELOPMENT SOURCE");Put("mods/AzureArchiveDLSS/runtime/user-component.bin","EXISTING INDEPENDENT DLSS");
    }
    static Dictionary<string,string> Sentinels()
    {
        return new[]{"AzureArchive.exe","ActiveProfile.txt","profiles/Disabled/modconfig.json","profiles/Active/configs/azurearchive.recorder.cfg","profiles/Active/configs/azurearchive.recorder-state/export-sequence.json","profiles/Active/configs/other-mod.cfg","mods/OtherMod/keep.dll","用户剧情/保留.aap2","用户视频/保留.mp4","RecorderMod/src/keep.cs","mods/AzureArchiveDLSS/runtime/user-component.bin"}.ToDictionary(x=>x,x=>InstallCore.Hash(P(x)));
    }
    static void Keep(Dictionary<string,string> sentinels)
    {
        foreach(var item in sentinels)Check(File.Exists(P(item.Key))&&InstallCore.Hash(P(item.Key))==item.Value,"Sentinel changed: "+item.Key);
    }
    static Dictionary<string,byte[]> PayloadFiles(string version,string variant)
    {
        var files=new Dictionary<string,byte[]>();
        Action<string,string> text=delegate(string name,string value){files.Add(InstallCore.Mod+name,Encoding.UTF8.GetBytes(value));};
        text(version+"/AzureArchive.Recorder.dll","SYNTHETIC "+version+" "+variant+" DLL - not an executable assembly");
        text(version+"/manifest.json","{\"name\":\"AzureArchiveRecorder\",\"version_number\":\""+version+"\"}");
        text("runtime/EnhanceHost.exe","SYNTHETIC ENHANCER "+variant);text("卸载内录MOD.exe","SYNTHETIC UNINSTALLER "+variant);
        text("使用说明.txt","GUIDE "+variant);text("第三方许可.txt","LICENSE "+variant);
        files.Add(InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe",File.ReadAllBytes(encoder));
        files.Add(InstallCore.Mod+"runtime/ffmpeg/ffprobe.exe",File.ReadAllBytes(encoder));
        return files;
    }
    static Receipt ReceiptFor(string version,Dictionary<string,byte[]> files)
    {
        return new Receipt{Product=InstallCore.Product,Version=version,Root=root,Files=files.Select(x=>new OwnedFile{Path=x.Key,Sha256=HashBytes(x.Value)}).ToList(),LegacyFiles=new List<OwnedFile>(),GeneratedFiles=new List<OwnedFile>()};
    }
    static MemoryStream Payload(string variant,Action<Receipt> adjust=null,bool corrupt=false,string version=RecorderVersion)
    {
        var files=PayloadFiles(version,variant);var receipt=ReceiptFor(version,files);receipt.Root="";
        if(adjust!=null)adjust(receipt);
        var stream=new MemoryStream();
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true)){
            using(var writer=new StreamWriter(zip.CreateEntry("payload-manifest.json").Open(),new UTF8Encoding(false)))writer.Write(InstallCore.Json.Serialize(receipt));
            foreach(var file in files)using(var output=zip.CreateEntry(file.Key).Open()){
                var value=corrupt&&file.Key.EndsWith("AzureArchive.Recorder.dll")?new byte[]{1,2,3}:file.Value;output.Write(value,0,value.Length);
            }
        }
        stream.Position=0;return stream;
    }
    static Receipt SeedOld(string variant,string version="0.2.1")
    {
        var files=PayloadFiles(version,variant);
        files.Add(InstallCore.Mod+"runtime/obsolete-owned.txt",Encoding.UTF8.GetBytes("OLD KNOWN FILE"));
        foreach(var file in files)Bytes(file.Key,file.Value);
        var receipt=ReceiptFor(version,files);Put(InstallCore.ReceiptName,InstallCore.Json.Serialize(receipt));
        Put("profiles/Active/modconfig.json","{\"EnabledMods\":[{\"name\":\"OtherMod\",\"version\":\"7.0\",\"custom\":42},{\"name\":\"AzureArchiveRecorder\",\"version\":\""+version+"\",\"userTag\":\"preserve\"}],\"note\":\"保留\"}");
        Put("profiles/AlsoEnabled/modconfig.json","{\"EnabledMods\":[{\"name\":\"AnotherMod\",\"version\":\"4\"},{\"name\":\"AzureArchiveRecorder\",\"version\":\""+version+"\"}],\"keep\":77}");
        Put("profiles/AlsoEnabled/configs/azurearchive.recorder.cfg","[Tutorial]\nSeenVersion = 1\n[Recording]\nFrameRate = 24\n");
        return receipt;
    }
    static void Install(string variant,Action<Receipt> adjust=null)
    {
        using(var stream=Payload(variant,adjust))Check(InstallCore.Install(root,stream,delegate{},delegate{return true;},delegate{return true;},new[]{encoder},false,delegate{return true;},delegate{return true;}),"Installation unexpectedly cancelled");
    }
    static void CheckEnabled(string profile,bool shouldBeEnabled)
    {
        var data=InstallCore.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(P("profiles/"+profile+"/modconfig.json")));
        var list=(IEnumerable)data["EnabledMods"];var own=new List<Dictionary<string,object>>();
        foreach(var raw in list){var item=(Dictionary<string,object>)raw;if((string)item["name"]==InstallCore.Product)own.Add(item);}
        Check(own.Count==(shouldBeEnabled?1:0),"Enabled count differs in profile "+profile);
        if(shouldBeEnabled)Check((string)own[0]["version"]==RecorderVersion,"Old version remains selected in "+profile);
    }
    static void CoreTests()
    {
        Run("V1.1 product and current-path whitelist are 1.1.0",delegate{
            Check(InstallCore.Version==RecorderVersion,"Wrong product version");
            Check(InstallCore.AllowedOwned(InstallCore.Mod+RecorderVersion+"/AzureArchive.Recorder.dll"),"Current DLL not allowed");
            Check(!InstallCore.AllowedOwned(InstallCore.Mod+"0.2.1/AzureArchive.Recorder.dll"),"Old version allowed in current payload");
            Check(!InstallCore.AllowedOwned(InstallCore.Mod+"1.0.0/AzureArchive.Recorder.dll"),"V1.0 version allowed in current payload");
            Check(!InstallCore.AllowedOwned("mods/AzureArchiveDLSS/runtime/a.dll"),"Independent DLSS ownership leaked into recorder");
        });
        foreach(var previous in new[]{"0.2.1","1.0.0"})foreach(var oldVariant in new[]{"development","protected"}){
            var variant=oldVariant;var oldVersion=previous;
            Run(oldVersion+" "+variant+" migrates all enabled profiles and cleans only receipted unchanged old files",delegate{
                Fixture("upgrade-"+oldVersion+"-"+variant);SeedOld(variant,oldVersion);var keep=Sentinels();var secondaryConfig=InstallCore.Hash(P("profiles/AlsoEnabled/configs/azurearchive.recorder.cfg"));
                Install(variant=="development"?"protected":"development");
                Check(!File.Exists(P(InstallCore.Mod+oldVersion+"/AzureArchive.Recorder.dll"))&&!File.Exists(P(InstallCore.Mod+oldVersion+"/manifest.json")),"Unchanged old DLL/manifest remain");
                Check(!File.Exists(P(InstallCore.Mod+"runtime/obsolete-owned.txt")),"Unchanged obsolete runtime file remains");
                CheckEnabled("Active",true);CheckEnabled("AlsoEnabled",true);CheckEnabled("Disabled",false);Keep(keep);
                Check(File.ReadAllText(P("profiles/Active/modconfig.json")).Contains("preserve")&&File.ReadAllText(P("profiles/Active/modconfig.json")).Contains("42"),"Profile custom fields lost");
                Check(InstallCore.Hash(P("profiles/AlsoEnabled/configs/azurearchive.recorder.cfg"))==secondaryConfig,"Inactive recorder settings modified");
                var receipt=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(P(InstallCore.ReceiptName)));Check(receipt.Version==RecorderVersion&&receipt.LegacyFiles.Count==0,"Migrated receipt incorrect");
            });
            Run("V1.1 "+variant+" upgraded from "+oldVersion+" can switch variants twice without losing settings",delegate{
                var keep=Sentinels();Install("development");Install("protected");
                Check(File.ReadAllText(P(InstallCore.Mod+RecorderVersion+"/AzureArchive.Recorder.dll")).Contains("protected"),"Final variant did not replace DLL");Keep(keep);CheckEnabled("AlsoEnabled",true);
            });
        }
        Run("Modified old DLL and manifest survive upgrade and later uninstall without staying enabled",delegate{
            Fixture("modified-old-files");SeedOld("protected");
            Put(InstallCore.Mod+"0.2.1/AzureArchive.Recorder.dll","USER MODIFIED OLD DLL");Put(InstallCore.Mod+"0.2.1/manifest.json","USER MODIFIED OLD MANIFEST");
            var oldDll=InstallCore.Hash(P(InstallCore.Mod+"0.2.1/AzureArchive.Recorder.dll"));var oldManifest=InstallCore.Hash(P(InstallCore.Mod+"0.2.1/manifest.json"));var keep=Sentinels();
            Install("development",delegate(Receipt next){next.LegacyFiles.Add(new OwnedFile{Path=InstallCore.Mod+"0.2.1/AzureArchive.Recorder.dll",Sha256=oldDll});});
            CheckEnabled("Active",true);CheckEnabled("AlsoEnabled",true);
            Check(InstallCore.Hash(P(InstallCore.Mod+"0.2.1/AzureArchive.Recorder.dll"))==oldDll&&InstallCore.Hash(P(InstallCore.Mod+"0.2.1/manifest.json"))==oldManifest,"Modified legacy files overwritten/deleted");
            InstallCore.Uninstall(root,delegate{});Check(!File.Exists(P(InstallCore.ReceiptName)),"Receipt remains after uninstall");
            Check(InstallCore.Hash(P(InstallCore.Mod+"0.2.1/AzureArchive.Recorder.dll"))==oldDll&&InstallCore.Hash(P(InstallCore.Mod+"0.2.1/manifest.json"))==oldManifest,"A build-machine legacy hash incorrectly claimed user modifications");
            CheckEnabled("Active",false);CheckEnabled("AlsoEnabled",false);Keep(keep);
        });
        Run("Unreceipted old version is retained while profiles select only V1",delegate{
            Fixture("unreceipted-old");Put(InstallCore.Mod+"0.2.1/AzureArchive.Recorder.dll","UNKNOWN OLD DLL");Put(InstallCore.Mod+"0.2.1/manifest.json","{\"name\":\"AzureArchiveRecorder\",\"version_number\":\"0.2.1\"}");
            Put("profiles/Active/modconfig.json","{\"EnabledMods\":[{\"name\":\"AzureArchiveRecorder\",\"version\":\"0.2.1\"}]}");var oldHash=InstallCore.Hash(P(InstallCore.Mod+"0.2.1/AzureArchive.Recorder.dll"));
            Install("protected");CheckEnabled("Active",true);Check(InstallCore.Hash(P(InstallCore.Mod+"0.2.1/AzureArchive.Recorder.dll"))==oldHash,"Unreceipted old DLL removed");
        });
        Run("Cancelling old-version replacement produces no file or directory changes",delegate{
            Fixture("cancel-upgrade");SeedOld("development");var before=Snapshot(root);int prompts=0;
            using(var payload=Payload("protected"))Check(!InstallCore.Install(root,payload,delegate{},delegate{return true;},delegate(List<string> versions){prompts++;Check(versions.Contains("0.2.1"),"Old installation not detected");return false;},new[]{encoder},false,delegate{return true;}),"Cancellation was ignored");
            Check(prompts==1,"Incorrect replacement prompt count");Same(before,root,"Cancelled migration changed target");
        });
        var invalidReceipts=new Dictionary<string,Action<Receipt>>{
            {"unknown-version",delegate(Receipt r){r.Version="0.9.9";}},
            {"wrong-version-owned-path",delegate(Receipt r){r.Files[0].Path=InstallCore.Mod+RecorderVersion+"/AzureArchive.Recorder.dll";}},
            {"foreign-product-path",delegate(Receipt r){r.Files[0].Path="mods/AzureArchiveDLSS/runtime/user-component.bin";}},
            {"path-traversal",delegate(Receipt r){r.Files[0].Path=InstallCore.Mod+"runtime/../../OtherMod/keep.dll";}},
            {"duplicate-path",delegate(Receipt r){r.Files.Add(new OwnedFile{Path=r.Files[0].Path,Sha256=r.Files[0].Sha256});}},
            {"non-hex-hash",delegate(Receipt r){r.Files[0].Sha256=new string('Z',64);}},
            {"foreign-root",delegate(Receipt r){r.Root=Path.Combine(workspace,"another-game");}}
        };
        foreach(var item in invalidReceipts){var test=item;Run("Malformed old receipt rejected without mutation: "+test.Key,delegate{
            Fixture("bad-receipt-"+test.Key);var receipt=SeedOld("protected");test.Value(receipt);Put(InstallCore.ReceiptName,InstallCore.Json.Serialize(receipt));var before=Snapshot(root);
            MustFail(delegate{Install("development");},test.Key);Same(before,root,"Malformed old receipt modified target");
        });}
        Run("Malformed inactive profile blocks migration before first file write",delegate{
            Fixture("bad-inactive-profile");SeedOld("development");Put("profiles/AlsoEnabled/modconfig.json","{broken");var before=Snapshot(root);
            MustFail(delegate{Install("protected");},"Inactive malformed profile");Same(before,root,"Bad inactive profile partially migrated");
        });
        Run("Payload hash mismatch leaves the old installation completely intact",delegate{
            Fixture("bad-payload-hash");SeedOld("protected");var before=Snapshot(root);
            using(var payload=Payload("development",null,true))MustFail(delegate{InstallCore.Install(root,payload,delegate{},delegate{return true;},delegate{return true;},new[]{encoder},false,delegate{return true;});},"Payload hash mismatch");
            Same(before,root,"Corrupt payload changed old installation");
        });
        Run("Failed receipt commit rolls back new files, all profiles and deleted legacy files",delegate{
            Fixture("rollback-final-receipt");SeedOld("development");var before=Snapshot(root);
            using(var hold=new FileStream(P(InstallCore.ReceiptName),FileMode.Open,FileAccess.ReadWrite,FileShare.Read))MustFail(delegate{Install("protected");},"Locked receipt commit");
            Same(before,root,"Upgrade transaction did not roll back every change");
        });
        Run("Unknown V1 destination conflicts are not overwritten",delegate{
            Fixture("unknown-current-conflict");Put(InstallCore.Mod+RecorderVersion+"/AzureArchive.Recorder.dll","USER FOREIGN V1 DLL");var before=Snapshot(root);
            MustFail(delegate{Install("development");},"Unknown V1 conflict");Same(before,root,"Unknown current file changed");
        });
        Run("V1 uninstall retains settings, tutorial state, naming counter and modified/unknown files",delegate{
            Fixture("uninstall-preserves-settings");var keep=Sentinels();Install("protected");
            Put(InstallCore.Mod+"user-note.txt","UNKNOWN USER FILE");Put(InstallCore.Mod+"使用说明.txt","USER EDITED GUIDE");
            var retained=InstallCore.Uninstall(root,delegate{});Keep(keep);CheckEnabled("Active",false);
            Check(!File.Exists(P(InstallCore.Mod+RecorderVersion+"/AzureArchive.Recorder.dll"))&&!File.Exists(P(InstallCore.ReceiptName)),"Owned install still present");
            Check(File.ReadAllText(P(InstallCore.Mod+"使用说明.txt"))=="USER EDITED GUIDE"&&File.ReadAllText(P(InstallCore.Mod+"user-note.txt"))=="UNKNOWN USER FILE","User file removed on uninstall");
            Check(retained.Contains(P(InstallCore.Mod+"使用说明.txt")),"Modified file not reported as retained");
        });
        Run("Failed uninstall deletion restores profiles and earlier deleted files",delegate{
            Fixture("uninstall-rollback");Install("development");var before=Snapshot(root);
            using(var hold=new FileStream(P(InstallCore.Mod+"runtime/EnhanceHost.exe"),FileMode.Open,FileAccess.ReadWrite,FileShare.Read))MustFail(delegate{InstallCore.Uninstall(root,delegate{});},"Locked uninstall target");
            Same(before,root,"Uninstall failure left partial state");
        });
        Run("Modified V1.0 DLL/manifest remain after V1.1 upgrade and uninstall without staying enabled",delegate{
            Fixture("v10-modified");SeedOld("protected","1.0.0");
            Put(InstallCore.Mod+"1.0.0/AzureArchive.Recorder.dll","USER MODIFIED V1.0 DLL");Put(InstallCore.Mod+"1.0.0/manifest.json","USER MODIFIED V1.0 MANIFEST");
            var dll=InstallCore.Hash(P(InstallCore.Mod+"1.0.0/AzureArchive.Recorder.dll"));var manifest=InstallCore.Hash(P(InstallCore.Mod+"1.0.0/manifest.json"));var keep=Sentinels();
            Install("development");CheckEnabled("Active",true);CheckEnabled("AlsoEnabled",true);InstallCore.Uninstall(root,delegate{});
            CheckEnabled("Active",false);CheckEnabled("AlsoEnabled",false);Keep(keep);
            Check(InstallCore.Hash(P(InstallCore.Mod+"1.0.0/AzureArchive.Recorder.dll"))==dll&&InstallCore.Hash(P(InstallCore.Mod+"1.0.0/manifest.json"))==manifest,"Modified V1.0 files were claimed or removed");
        });
        Run("V1.0 upgrade receipt-write failure rolls back both profiles and prior file cleanup",delegate{
            Fixture("v10-rollback");SeedOld("development","1.0.0");var before=Snapshot(root);
            using(var hold=new FileStream(P(InstallCore.ReceiptName),FileMode.Open,FileAccess.ReadWrite,FileShare.Read))MustFail(delegate{Install("protected");},"V1.0 final receipt failure");Same(before,root,"V1.0 upgrade did not fully roll back");
        });
        foreach(var previous in new[]{"0.2.1","1.0.0"})foreach(var legacy in new[]{false,true}){
            var old=previous;var legacyPath=legacy;
            Run(old+" receipt cannot claim V1.1 paths through "+(legacy?"LegacyFiles":"Files"),delegate{
                Fixture("forged-"+old+"-"+legacyPath);var receipt=SeedOld("protected",old);
                if(legacyPath)receipt.LegacyFiles.Add(new OwnedFile{Path=InstallCore.Mod+RecorderVersion+"/AzureArchive.Recorder.dll",Sha256=new string('A',64)});
                else receipt.Files[0].Path=InstallCore.Mod+RecorderVersion+"/AzureArchive.Recorder.dll";
                Put(InstallCore.ReceiptName,InstallCore.Json.Serialize(receipt));var before=Snapshot(root);MustFail(delegate{Install("development");},"Cross-version ownership");Same(before,root,"Rejected version claim changed files");
            });
        }
        foreach(var previous in new[]{"0.2.1","1.0.0"}){var version=previous;Run("Current installer rejects a "+version+" payload without mutation",delegate{
            Fixture("reject-payload-"+version);var before=Snapshot(root);
            using(var payload=Payload("development",null,false,version))MustFail(delegate{InstallCore.Install(root,payload,delegate{},delegate{return true;},delegate{return true;},new[]{encoder},false,delegate{return true;},delegate{return true;});},"Old payload accepted as current");Same(before,root,"Old payload changed files");
        });}
    }
    sealed class HttpRecord
    {
        public string Range,IfRange;public int Number;
    }
    // A real HTTP server on loopback only. TcpListener avoids machine-wide URL ACL changes.
    sealed class HttpFixture : IDisposable
    {
        readonly TcpListener listener;readonly Thread accept;readonly List<HttpRecord> requests=new List<HttpRecord>();
        public readonly byte[] Data;public readonly string Url;public string Mode="normal";public int Delay=2;bool disposed;int counter;
        public HttpRecord[] Requests{get{lock(requests)return requests.ToArray();}}
        public HttpFixture()
        {
            Data=new byte[4*1024*1024];new Random(12019).NextBytes(Data);
            listener=new TcpListener(IPAddress.Loopback,0);listener.Start();Url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/asset.zip";
            accept=new Thread(Accept);accept.IsBackground=true;accept.Start();
        }
        void Accept()
        {
            while(!disposed)try{var client=listener.AcceptTcpClient();ThreadPool.QueueUserWorkItem(delegate{Serve(client);});}
            catch(SocketException){if(!disposed)throw;}catch(ObjectDisposedException){break;}
        }
        void Serve(TcpClient client)
        {
            try{using(client)using(var stream=client.GetStream()){
                client.ReceiveTimeout=5000;client.SendTimeout=5000;var header=new StringBuilder();
                while(header.Length<32768&&!header.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal)){int value=stream.ReadByte();if(value<0)return;header.Append((char)value);}
                var fields=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(var line in header.ToString().Split(new[]{"\r\n"},StringSplitOptions.RemoveEmptyEntries).Skip(1)){int colon=line.IndexOf(':');if(colon>0)fields[line.Substring(0,colon)]=line.Substring(colon+1).Trim();}
                string range,ifRange;fields.TryGetValue("Range",out range);fields.TryGetValue("If-Range",out ifRange);
                var record=new HttpRecord{Number=Interlocked.Increment(ref counter),Range=range,IfRange=ifRange};lock(requests)requests.Add(record);
                long offset=String.IsNullOrEmpty(range)?0:Int64.Parse(range.Substring("bytes=".Length).TrimEnd('-'));
                if(Mode=="offline"){SendHeader(stream,"503 Service Unavailable",0,"");return;}
                if(Mode=="not-found"){SendHeader(stream,"404 Not Found",0,"");return;}
                bool partial=offset>0&&Mode!="ignore-range";if(!partial)offset=0;
                var etag=Mode=="changed-etag"&&record.Number>1?"\"fixture-v2\"":"\"fixture-v1\"";
                var extras="ETag: "+etag+"\r\n";
                if(partial){long first=Mode=="wrong-range"?offset+1:offset;extras+="Content-Range: bytes "+first+"-"+(Data.Length-1)+"/"+Data.Length+"\r\n";}
                long count=Data.Length-offset;SendHeader(stream,partial?"206 Partial Content":"200 OK",count,extras);
                if(Mode=="disconnect-always"||(Mode=="disconnect-once"&&record.Number==1))count=Math.Min(count,128*1024);
                int position=(int)offset,end=position+(int)count;
                while(position<end&&!disposed){int size=Math.Min(16*1024,end-position);stream.Write(Data,position,size);stream.Flush();position+=size;if(Delay>0)Thread.Sleep(Delay);}
            }}catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}
        }
        static void SendHeader(Stream output,string status,long length,string extra)
        {
            var bytes=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+"\r\nContent-Length: "+length+"\r\nConnection: close\r\n"+extra+"\r\n");output.Write(bytes,0,bytes.Length);output.Flush();
        }
        public void Dispose(){disposed=true;listener.Stop();accept.Join(1000);}
        public OnlineDlssAsset Asset(bool knownSize=true){return new OnlineDlssAsset{Name="loopback-fixture",Url=Url,Size=knownSize?Data.Length:0,Sha256=HashBytes(Data)};}
    }
    static void CompleteTask(Task task,string label,int timeout=15000)
    {
        var watch=Stopwatch.StartNew();while(!task.IsCompleted&&watch.ElapsedMilliseconds<timeout)Thread.Sleep(10);
        Check(task.IsCompleted,label+" did not finish before timeout");task.GetAwaiter().GetResult();
    }
    static string TransportRoot(string name)
    {
        var path=Path.Combine(workspace,"transport-"+name);Directory.CreateDirectory(path);return Path.Combine(path,"download.zip");
    }
    static void NoDownload(string target)
    {
        Check(!File.Exists(target)&&!File.Exists(target+".part"),"Failed/cancelled download left owned files");
    }
    static void PausedTransfer(string mode,bool success)
    {
        var target=TransportRoot(mode);using(var server=new HttpFixture{Mode=mode})using(var control=new OnlineDlssControl())using(var paused=new ManualResetEventSlim()){
            int pausing=0;
            var task=Task.Run(delegate{OnlineDlssInstall.DownloadVerifiedAsset(server.Asset(),target,control,delegate(OnlineDlssProgress p){
                if(p.Phase=="Downloading"&&p.BytesReceived>=128*1024&&Interlocked.CompareExchange(ref pausing,1,0)==0){control.Pause();paused.Set();}
            });});
            Check(paused.Wait(5000),"No pause point reached");Thread.Sleep(70);var length=new FileInfo(target+".part").Length;var requests=server.Requests.Length;
            Thread.Sleep(180);Check(control.IsPaused&&new FileInfo(target+".part").Length==length&&server.Requests.Length==requests&&!task.IsCompleted,"Paused download continued writing or reconnected");
            control.Resume();if(success){CompleteTask(task,"Resumed transfer");Check(InstallCore.Hash(target)==HashBytes(server.Data),"Resumed bytes differ");}
            else {MustFail(delegate{CompleteTask(task,"Invalid resume");},"Invalid range");NoDownload(target);}
            var ranged=server.Requests.Where(r=>!String.IsNullOrEmpty(r.Range)).ToArray();Check(ranged.Length>=1,"Resume did not send Range");Check(ranged[0].IfRange=="\"fixture-v1\"","Resume omitted entity validator");
            if(mode=="changed-etag")Check(server.Requests.Length>=3&&String.IsNullOrEmpty(server.Requests[2].Range),"Changed entity was not restarted from byte zero");
        }
    }
    static void NetworkTests()
    {
        Run("HTTP success validates hash and removes only its temporary part",delegate{
            var target=TransportRoot("success");var neighbor=target+".user";File.WriteAllText(neighbor,"KEEP");
            using(var server=new HttpFixture())using(var control=new OnlineDlssControl()){
                OnlineDlssInstall.DownloadVerifiedAsset(server.Asset(),target,control,delegate{});
                Check(InstallCore.Hash(target)==HashBytes(server.Data)&&!File.Exists(target+".part"),"Success bytes or cleanup invalid");Check(File.ReadAllText(neighbor)=="KEEP","Neighbour file changed");
            }
        });
        Run("Unknown download size is learned from Content-Length",delegate{
            var target=TransportRoot("unknown-size");long total=0;
            using(var server=new HttpFixture())using(var control=new OnlineDlssControl()){
                OnlineDlssInstall.DownloadVerifiedAsset(server.Asset(false),target,control,delegate(OnlineDlssProgress p){total=Math.Max(total,p.TotalBytes);});
                Check(total==server.Data.Length&&InstallCore.Hash(target)==HashBytes(server.Data),"Inferred size/hash differs");
            }
        });
        foreach(var mode in new[]{"normal","ignore-range","changed-etag"}){var name=mode;Run("Pause is quiescent and Range resume succeeds: "+name,delegate{PausedTransfer(name,true);});}
        Run("An inconsistent Content-Range is rejected and cleaned",delegate{PausedTransfer("wrong-range",false);});
        Run("Dropped HTTP connection retries by Range and preserves exact bytes",delegate{
            var target=TransportRoot("connection-drop");using(var server=new HttpFixture{Mode="disconnect-once"})using(var control=new OnlineDlssControl()){
                OnlineDlssInstall.DownloadVerifiedAsset(server.Asset(),target,control,delegate{});Check(InstallCore.Hash(target)==HashBytes(server.Data)&&server.Requests.Any(r=>!String.IsNullOrEmpty(r.Range)),"Retry did not resume safely");
            }
        });
        foreach(var mode in new[]{"disconnect-always","offline","not-found"}){var name=mode;Run("Network failure is bounded and leaves no part: "+name,delegate{
            var target=TransportRoot(name);using(var server=new HttpFixture{Mode=name})using(var control=new OnlineDlssControl()){
                MustFail(delegate{OnlineDlssInstall.DownloadVerifiedAsset(server.Asset(),target,control,delegate{});},name);NoDownload(target);
                Check(server.Requests.Length<=(name=="not-found"?1:3),"Retry count exceeds bounded policy");
            }
        });}
        Run("A corrupted SHA-256 is rejected before publishing the destination",delegate{
            var target=TransportRoot("bad-hash");using(var server=new HttpFixture())using(var control=new OnlineDlssControl()){
                var asset=server.Asset();asset.Sha256=new string('0',64);MustFail(delegate{OnlineDlssInstall.DownloadVerifiedAsset(asset,target,control,delegate{});},"Corrupt asset");NoDownload(target);
            }
        });
        foreach(var paused in new[]{false,true}){bool pauseFirst=paused;Run("Cancel "+(paused?"while paused":"while downloading")+" cleans owned part and never installs",delegate{
            var target=TransportRoot(paused?"cancel-paused":"cancel-active");using(var server=new HttpFixture())using(var control=new OnlineDlssControl())using(var reached=new ManualResetEventSlim()){
                int once=0;var task=Task.Run(delegate{OnlineDlssInstall.DownloadVerifiedAsset(server.Asset(),target,control,delegate(OnlineDlssProgress p){if(p.BytesReceived>=128*1024&&Interlocked.CompareExchange(ref once,1,0)==0){if(pauseFirst)control.Pause();reached.Set();}});});
                Check(reached.Wait(5000),"Download did not reach cancel point");control.Cancel();
                bool cancelled=false;try{CompleteTask(task,"Cancelled transfer");}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"Cancellation exception not preserved");NoDownload(target);
            }
        });}
        Run("Existing destination and unrelated existing .part are never replaced",delegate{
            var target=TransportRoot("existing");using(var server=new HttpFixture())using(var control=new OnlineDlssControl()){
                File.WriteAllText(target,"MY DOWNLOAD");MustFail(delegate{OnlineDlssInstall.DownloadVerifiedAsset(server.Asset(),target,control,delegate{});},"Existing target");Check(File.ReadAllText(target)=="MY DOWNLOAD","Existing target overwritten");
                var second=target+"2";File.WriteAllText(second+".part","MY OLD PART");MustFail(delegate{OnlineDlssInstall.DownloadVerifiedAsset(server.Asset(),second,control,delegate{});},"Existing part");Check(File.ReadAllText(second+".part")=="MY OLD PART"&&server.Requests.Length==0,"Existing part touched or network started");
            }
        });
        Run("A destination appearing during verification remains intact",delegate{
            var target=TransportRoot("publish-race");using(var server=new HttpFixture())using(var control=new OnlineDlssControl()){
                MustFail(delegate{OnlineDlssInstall.DownloadVerifiedAsset(server.Asset(),target,control,delegate(OnlineDlssProgress p){if(p.Phase=="Verifying")File.WriteAllText(target,"CONCURRENT USER FILE");});},"Destination race");
                Check(File.ReadAllText(target)=="CONCURRENT USER FILE"&&!File.Exists(target+".part"),"Race clobbered someone else's file or left part");
            }
        });
        Run("Failure in final progress callback also cleans the just-published owned file",delegate{
            var target=TransportRoot("progress-failure");using(var server=new HttpFixture())using(var control=new OnlineDlssControl()){
                MustFail(delegate{OnlineDlssInstall.DownloadVerifiedAsset(server.Asset(),target,control,delegate(OnlineDlssProgress p){if(p.Phase=="Verified")throw new InvalidOperationException("injected consumer failure");});},"Final progress exception");NoDownload(target);
            }
        });
        Run("Plain HTTP outside loopback is refused before connecting",delegate{
            var target=TransportRoot("http-not-loopback");using(var control=new OnlineDlssControl())MustFail(delegate{OnlineDlssInstall.DownloadVerifiedAsset(new OnlineDlssAsset{Url="http://192.0.2.1/not-allowed",Name="forbidden",Sha256=new string('0',64)},target,control,delegate{});},"Non-loopback HTTP");NoDownload(target);
        });
    }
    static Dictionary<string,string> Prepared(int series)
    {
        var stage=Path.GetFullPath(Opt("DlssFixtureStage"));Check(Directory.Exists(stage),"Real DLSS fixture stage missing");InstallCore.NoLinks(stage);
        var prepared=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in Directory.GetFiles(Path.Combine(stage,"mods","AzureArchiveDLSS"),"*",SearchOption.AllDirectories)){
            var relative=file.Substring(stage.TrimEnd('\\').Length+1).Replace('\\','/');
            if(new[]{30,40,50}.Where(s=>s!=series).Any(s=>relative.Contains("/rtx"+s+"/")))continue;
            prepared.Add(relative,file);
        }
        Check(prepared.Count==25,"Unexpected native fixture count");return prepared;
    }
    static void ArtifactTests()
    {
        Dictionary<string,string> prepared=null;Dictionary<string,string> sentinels=null;
        Run("Genuine native fixture installs only the selected GPU series with a separate receipt",delegate{
            Fixture("dlss-transactions");Install("development");sentinels=Sentinels();prepared=Prepared(50);
            using(var control=new OnlineDlssControl()){
                var result=OnlineDlssInstall.CommitPrepared(root,50,prepared,control,delegate{});Check(result.Completed&&!result.AlreadyInstalled&&result.Series==50,"DLSS installation result incorrect");
            }
            Check(OnlineDlssInstall.IsInstalled(root,50)&&!OnlineDlssInstall.IsInstalled(root,40),"Per-series integrity check incorrect");
            Check(!Directory.Exists(P("mods/AzureArchiveDLSS/runtime/mods/dlss/rtx30"))&&!Directory.Exists(P("mods/AzureArchiveDLSS/runtime/mods/dlss/rtx40")),"Unselected series installed");Keep(sentinels);
        });
        Run("Already-valid R6 identity is reused without a network request",delegate{
            var r6=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(Path.Combine(Opt("DlssFixtureStage"),"payload-manifest.json")));
            Check(r6.Product==OnlineDlssInstall.Product&&r6.Version==OnlineDlssInstall.Version,"R6 supplemental identity changed");
            var before=Snapshot(root);using(var control=new OnlineDlssControl()){
                var result=OnlineDlssInstall.RunAsync(root,50,null,delegate{},control).GetAwaiter().GetResult();Check(result.Completed&&result.AlreadyInstalled,"Existing native bytes triggered a repair/download");
            }Same(before,root,"Already-installed path changed files");
        });
        Run("DLSS commit cancellation rolls back writes and preserves the recorder",delegate{
            var before=Snapshot(root);using(var control=new OnlineDlssControl()){
                int count=0;bool cancelled=false;try{OnlineDlssInstall.CommitPrepared(root,50,prepared,control,delegate(OnlineDlssProgress p){if(p.Phase=="Installing"&&++count==4)control.Cancel();});}catch(OperationCanceledException){cancelled=true;}
                Check(cancelled&&count==4,"Commit cancellation was ignored");
            }Same(before,root,"Cancelled native transaction changed old files");Keep(sentinels);
        });
        Run("DLSS write-phase exception rolls back all previous component writes",delegate{
            var before=Snapshot(root);using(var control=new OnlineDlssControl()){
                int count=0;MustFail(delegate{OnlineDlssInstall.CommitPrepared(root,50,prepared,control,delegate(OnlineDlssProgress p){if(p.Phase=="Installing"&&++count==4)throw new IOException("injected DLSS write phase failure");});},"Native commit failure");
            }Same(before,root,"Failed native transaction changed old files");
        });
        Run("Modified DLSS owned files block repair and are retained by independent uninstall",delegate{
            Put(OnlineDlssInstall.Mod+"使用说明.txt","USER MODIFIED DLSS GUIDE");var before=Snapshot(root);
            using(var control=new OnlineDlssControl())MustFail(delegate{OnlineDlssInstall.CommitPrepared(root,50,prepared,control,delegate{});},"Modified component conflict");Same(before,root,"Repair overwrote user component");
            var keep=OnlineDlssInstall.UninstallProduct(root);Check(keep.Contains(P(OnlineDlssInstall.Mod+"使用说明.txt")),"Modified component was not retained");
            Check(File.ReadAllText(P(OnlineDlssInstall.Mod+"使用说明.txt"))=="USER MODIFIED DLSS GUIDE","Modified DLSS guide deleted");Keep(sentinels);
            Check(File.Exists(P(InstallCore.Mod+RecorderVersion+"/AzureArchive.Recorder.dll"))&&File.Exists(P(InstallCore.ReceiptName)),"DLSS uninstall deleted recorder");CheckEnabled("Active",true);
        });
        Run("RTX20 is rejected only by DLSS backend and leaves installed recorder unchanged",delegate{
            Fixture("rtx20-dlss-stage");Install("protected");var before=Snapshot(root);var temp=Directory.GetDirectories(Path.GetTempPath(),"AARecorder-DLSS-*").OrderBy(x=>x).ToArray();
            using(var control=new OnlineDlssControl()){
                bool expected=false;try{OnlineDlssInstall.RunAsync(root,20,null,delegate{},control).GetAwaiter().GetResult();}catch(NotSupportedException error){expected=error.Message=="暂时不支持20系";}
                Check(expected,"RTX20 backend did not report exact reason");
            }
            Check(temp.SequenceEqual(Directory.GetDirectories(Path.GetTempPath(),"AARecorder-DLSS-*").OrderBy(x=>x)),"RTX20 left new DLSS temp directory");Same(before,root,"RTX20 rejection removed recorder or user files");
        });
    }
    static V1GpuStatus Gpu(string name,uint vendor=0x10DE)
    {
        return V1Hardware.FromDevices(new[]{new V1GpuDevice{Name=name,VendorId=vendor}});
    }
    static void PumpUntil(Func<bool> condition,string message,int timeout=15000)
    {
        var watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds<timeout){Application.DoEvents();if(condition())return;Thread.Sleep(10);}
        Check(condition(),message+" (timeout)");
    }
    static void PumpFor(int milliseconds)
    {
        var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(10);}
    }
    sealed class WizardFixture : IDisposable
    {
        public readonly V1SetupWindow Window;public readonly List<string> Messages=new List<string>();
        public readonly ManualResetEventSlim DownloadReady=new ManualResetEventSlim();
        public readonly ManualResetEventSlim HardwareReady=new ManualResetEventSlim(true),CoreReady=new ManualResetEventSlim(true);
        public int CoreCalls,DlssCalls,ProgressEvents,DetectionCalls,Inspections;public bool CoreCancel,CoreFail,InspectFail,IncompleteResult,DetectionFails,ExistingComponentsReport;public string DownloadError;
        public V1GpuStatus NextGpu;public Action<string> MessageCheck;
        public OnlineDlssControl LastControl;public Task<OnlineDlssResult> LastTask;
        public WizardFixture(string name,V1GpuStatus gpu)
        {
            Fixture(name);var target=root;V1Program.ExitCode=0;NextGpu=gpu;
            var services=new V1SetupServices();
            services.DetectHardware=delegate{
                Interlocked.Increment(ref DetectionCalls);HardwareReady.Wait();if(DetectionFails)throw new IOException("fixture hardware detection failure");return NextGpu;
            };
            services.Inspect=delegate(string location,V1GpuStatus hardware){
                Interlocked.Increment(ref Inspections);if(InspectFail)throw new IOException("fixture preflight failure");var report=V1SetupServices.InspectComputer(location,hardware);
                if(ExistingComponentsReport){report.DlssInstalled=true;foreach(var item in report.Parts.Where(p=>p.Dlss)){item.Missing=false;item.Status="已完整安装";item.Detail="Local fixture reports all components already installed.";}}
                return report;
            };
            services.InstallRecorder=delegate(string location,Action<string> p,Func<List<DependencyIssue>,bool> d,Func<List<string>,bool> r,Func<bool> f){
                Interlocked.Increment(ref CoreCalls);CoreReady.Wait();if(CoreFail)throw new IOException("fixture core failure");if(CoreCancel)return false;
                using(var payload=Payload("development"))return InstallCore.Install(location,payload,p,delegate{return true;},delegate{return true;},new[]{encoder},false,delegate{return true;},delegate{return true;});
            };
            services.InstallDlss=delegate(string location,int series,Action<OnlineDlssProgress> progress,OnlineDlssControl control){
                Interlocked.Increment(ref DlssCalls);LastControl=control;
                LastTask=Task.Run(delegate{
                    while(!DownloadReady.IsSet){control.Check();Interlocked.Increment(ref ProgressEvents);progress(new OnlineDlssProgress{Phase="Downloading",Asset="fixture",BytesReceived=ProgressEvents*32768,TotalBytes=100*1024*1024,Message="正在下载"});Thread.Sleep(20);}
                    control.Check();if(DownloadError!=null)throw new IOException(DownloadError);
                    return new OnlineDlssResult{Completed=!IncompleteResult,Series=series,RuntimeDirectory=Path.Combine(target,"mods","AzureArchiveDLSS","runtime")};
                });return LastTask;
            };
            services.ShowMessage=delegate(IWin32Window owner,string text,string title,MessageBoxIcon icon){Messages.Add(title+": "+text);if(MessageCheck!=null)MessageCheck(text);};
            Window=new V1SetupWindow(root,services){ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(40,40),Opacity=0};
            Window.Text+=" · 隔离自动验证";
        }
        public T Control<T>(string name) where T:Control
        {
            var found=Window.Controls.Find(name,true);Check(found.Length==1,"Named control missing or duplicated: "+name);return (T)found[0];
        }
        public void Show(){Console.WriteLine("UI: Show");Window.Show();PumpUntil(()=>Window.Stage==V1SetupStage.Preflight||Window.Stage==V1SetupStage.Selecting,"Preflight completion");Console.WriteLine("UI: Shown "+Window.Stage);}
        public void SelectDlss()
        {
            var check=Control<CheckBox>("DlssAvailability");Check(check.Enabled&&check.AutoCheck,"DLSS checkbox cannot be selected");check.Checked=true;
            PumpUntil(()=>DetectionCalls>0&&Window.Stage!=V1SetupStage.HardwareChecking,"Explicit hardware detection completion");
        }
        public void Start(){Console.WriteLine("UI: Start");Control<Button>("StartInstall").PerformClick();Console.WriteLine("UI: Started "+Window.Stage);}
        public void CheckLockedDownloadChoice()
        {
            var text=Control<Label>("DlssReason").Text;
            Check(text.Contains("选择已锁定")&&!text.Contains("显卡支持。可取消勾选")&&!text.Contains("开始安装前"),"Download-stage copy still offers changing the locked DLSS choice");
            Check(Control<CheckBox>("DlssAvailability").Checked&&!Control<CheckBox>("DlssAvailability").Enabled,"Download-stage copy and checkbox lock disagree");
        }
        public void Capture(string name)
        {
            using(var bitmap=new Bitmap(Window.Width,Window.Height)){Window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(workspace,name+".png"));}
        }
        public void Dispose()
        {
            HardwareReady.Set();CoreReady.Set();
            if(!Window.IsDisposed){Window.Close();PumpUntil(()=>Window.IsDisposed,"Test window cleanup");}Window.Dispose();
            if(LastTask!=null&&!LastTask.IsCompleted){if(LastControl!=null)LastControl.Cancel();try{CompleteTask(LastTask,"Test download cleanup");}catch(OperationCanceledException){}}
            DownloadReady.Dispose();HardwareReady.Dispose();CoreReady.Dispose();
        }
    }
    static void UiTests()
    {
        Run("GPU names classify RTX laptop, Ti, D, Intel and unsupported NVIDIA explicitly",delegate{
            foreach(var pair in new Dictionary<string,int>{{"NVIDIA GeForce RTX 3060 Laptop GPU",30},{"NVIDIA GeForce RTX 4060 Ti",40},{"NVIDIA GeForce RTX 5060",50},{"NVIDIA GeForce RTX 5080 D",50}}){var gpu=Gpu(pair.Key);Check(gpu.Supported&&gpu.Series==pair.Value&&!gpu.RejectDlssStage,"Supported GPU misclassified: "+pair.Key);}
            var rtx20=Gpu("NVIDIA GeForce RTX 2060 SUPER");Check(!rtx20.Supported&&rtx20.RejectDlssStage&&rtx20.Reason=="暂时不支持20系","RTX20 stage-specific classification incorrect");
            var intel=Gpu("Intel(R) Arc(TM) B390 GPU",0x8086);Check(!intel.Supported&&!intel.RejectDlssStage&&intel.Reason.Contains("不是英伟达（NVIDIA）显卡"),"Intel classification omitted the non-NVIDIA reason");
            Check(!Gpu("NVIDIA GeForce GTX 1660").Supported&&Gpu("NVIDIA GeForce GTX 1660").Reason.Contains("该系列英伟达（NVIDIA）显卡暂不支持")&&!Gpu("NVIDIA RTX A4000").Supported,"Unsupported NVIDIA model enabled DLSS or omitted its precise reason");
        });
        Run("Mixed NVIDIA generations do not choose a potentially wrong runtime",delegate{
            var mixed=V1Hardware.FromDevices(new[]{new V1GpuDevice{Name="NVIDIA GeForce RTX 3060",VendorId=0x10DE},new V1GpuDevice{Name="NVIDIA GeForce RTX 5080",VendorId=0x10DE}});
            Check(!mixed.Supported&&!mixed.RejectDlssStage&&mixed.Reason.Contains("不同系列"),"Mixed generation warning missing");
            var empty=V1Hardware.FromDevices(new V1GpuDevice[0]);Check(!empty.Supported&&!empty.RejectDlssStage,"No GPU incorrectly supported");
        });
        Run("V1.1 preflight defaults DLSS off without detecting hardware",delegate{
            using(var wizard=new WizardFixture("ui-preflight",Gpu("NVIDIA GeForce RTX 5080"))){wizard.Show();
                var rows=wizard.Control<ListView>("MissingParts").Items.Cast<ListViewItem>().ToArray();
                Check(rows.Any(x=>x.Text.Contains("内录 MOD V1.1"))&&rows.Any(x=>x.Text.Contains("DLSS"))&&rows.Any(x=>x.SubItems[1].Text.Contains("缺少")),"Preflight did not list missing parts");
                Check(wizard.Control<Button>("StartInstall").Enabled&&!wizard.Control<Button>("ContinueInstall").Enabled,"Preflight button gating incorrect");
                var selected=wizard.Control<CheckBox>("DlssAvailability");Check(!selected.Checked&&selected.Enabled&&selected.AutoCheck,"Default DLSS checkbox is not an available unchecked opt-in");
                Check(wizard.DetectionCalls==0&&wizard.DlssCalls==0&&wizard.Messages.Count==0,"Startup detected hardware or started DLSS without opt-in");wizard.Capture("ui-preflight-default-off");
                Check(wizard.Control<ListView>("MissingParts").ShowItemToolTips&&rows.All(x=>!String.IsNullOrEmpty(x.ToolTipText)),"Full preflight descriptions are not available as tooltips");
            }
        });
        var defaults=new[]{Gpu("NVIDIA GeForce RTX 5080"),Gpu("Intel(R) Arc(TM) B390 GPU",0x8086),Gpu("NVIDIA GeForce RTX 2060")};
        for(int i=0;i<defaults.Length;i++){var hardware=defaults[i];var index=i;Run("Unchecked DLSS installs ordinary recorder with zero hardware/download calls: "+hardware.Name,delegate{
            using(var wizard=new WizardFixture("ui-default-"+index,hardware)){var keep=Sentinels();wizard.Show();wizard.Start();
                PumpUntil(()=>wizard.Window.Stage==V1SetupStage.Ready,"Unchecked ordinary install");
                Check(wizard.CoreCalls==1&&wizard.DetectionCalls==0&&wizard.DlssCalls==0&&wizard.Messages.Count==0,"Default path detected/rejected hardware or entered DLSS");
                Check(wizard.Window.RecorderInstalled&&!wizard.Window.DownloadComplete&&wizard.Control<Button>("ContinueInstall").Enabled,"Unchecked path cannot finish ordinary installation");
                wizard.Control<Button>("ContinueInstall").PerformClick();Check(wizard.Window.Stage==V1SetupStage.Finished&&V1Program.ExitCode==0,"Unchecked path did not finish successfully");Keep(keep);
            }
        });}
        Run("Supported GPU is detected only on opt-in and can be deselected before installing",delegate{
            using(var wizard=new WizardFixture("ui-deselect",Gpu("NVIDIA GeForce RTX 5080"))){wizard.Show();Check(wizard.DetectionCalls==0,"Detected before selection");wizard.SelectDlss();
                var check=wizard.Control<CheckBox>("DlssAvailability");Check(check.Checked&&check.Enabled&&wizard.DetectionCalls==1&&wizard.Messages.Count==0,"Supported detection did not keep a reversible selection");
                Check(wizard.Control<Label>("DlssReason").Text.Contains("可在开始安装前取消勾选"),"Supported preflight copy does not limit deselection to before installation");wizard.Capture("ui-selected-supported");
                check.Checked=false;PumpFor(50);Check(!check.Checked&&check.Enabled,"Supported user cannot deselect DLSS");
                var status=wizard.Control<Label>("SetupStatus");
                Check(status.Text.Contains("本次仅安装内录 MOD")&&!status.Text.Contains("已选择 DLSS")&&status.ForeColor.G>status.ForeColor.R,"Deselection left the bottom status announcing selected DLSS or an error");
                Check(wizard.Control<Label>("DlssReason").Text.Contains("未选择 DLSS")&&wizard.Control<ListView>("MissingParts").Items.Cast<ListViewItem>().Single(x=>x.Text.StartsWith("DLSS")).SubItems[1].Text=="未选择","Deselection hints disagree with the checkbox and bottom status");
                wizard.Capture("ui-deselected-supported");wizard.Start();PumpUntil(()=>wizard.Window.Stage==V1SetupStage.Ready,"Deselected core install");
                Check(wizard.DetectionCalls==1&&wizard.DlssCalls==0&&wizard.Window.RecorderInstalled,"Deselection did not suppress download");
            }
        });
        var denied=new[]{Gpu("Intel(R) Arc(TM) B390 GPU",0x8086),Gpu("NVIDIA GeForce GTX 1660"),Gpu("NVIDIA GeForce RTX 2060")};
        for(int i=0;i<denied.Length;i++){var hardware=denied[i];var index=i;Run("Unsupported opt-in shows reason then locks only DLSS while ordinary installation remains available: "+hardware.Name,delegate{
            using(var wizard=new WizardFixture("ui-denied-"+index,hardware)){var keep=Sentinels();wizard.Show();
                wizard.MessageCheck=delegate(string text){Check(!wizard.Control<Button>("StartInstall").Enabled&&!wizard.Control<Button>("ContinueInstall").Enabled&&wizard.CoreCalls==0,"Installation was allowed before rejection confirmation");};
                wizard.SelectDlss();var check=wizard.Control<CheckBox>("DlssAvailability");
                Check(wizard.DetectionCalls==1&&wizard.Messages.Count==1&&wizard.Messages[0].Contains(hardware.Reason),"Unsupported hardware modal omitted the precise reason");
                Check(!check.Checked&&!check.Enabled&&!wizard.Window.IsDisposed&&wizard.Control<Button>("StartInstall").Enabled,"Rejection did not leave ordinary install usable with permanently disabled checkbox");
                Check(wizard.Control<Label>("DlssReason").ForeColor.R>wizard.Control<Label>("DlssReason").ForeColor.G,"Rejection reason is not red");
                int inspections=wizard.Inspections;wizard.Control<TextBox>("RootPath").Text=root+Path.DirectorySeparatorChar+".";wizard.Control<Button>("Inspect").PerformClick();
                PumpUntil(()=>wizard.Inspections>inspections&&wizard.Window.Stage==V1SetupStage.Preflight,"Directory reinspection after rejection");
                Check(!check.Checked&&!check.Enabled&&wizard.DetectionCalls==1,"Root reinspection re-enabled a rejected checkbox or detected hardware again");
                wizard.Capture("ui-rejected-"+index);wizard.Start();PumpUntil(()=>wizard.Window.Stage==V1SetupStage.Ready,"Rejected hardware core completion");
                Check(wizard.CoreCalls==1&&wizard.DlssCalls==0&&wizard.Window.RecorderInstalled&&!wizard.Window.IsDisposed,"Unsupported/RTX20 rejection exited the installer or prevented ordinary core");
                wizard.Control<Button>("ContinueInstall").PerformClick();Check(wizard.Window.Stage==V1SetupStage.Finished&&V1Program.ExitCode==0,"Rejected hardware did not complete normal installation");Keep(keep);CheckEnabled("Active",true);
            }
        });}
        Run("A failed hardware probe is explained and permanently disables only DLSS",delegate{
            using(var wizard=new WizardFixture("ui-probe-error",Gpu("NVIDIA GeForce RTX 5080"))){wizard.DetectionFails=true;wizard.Show();Check(wizard.DetectionCalls==0,"Failed probe ran at startup");wizard.SelectDlss();
                var check=wizard.Control<CheckBox>("DlssAvailability");Check(!check.Checked&&!check.Enabled&&wizard.Messages.Count==1&&wizard.Messages[0].Contains("fixture hardware detection failure"),"Probe exception was hidden or left DLSS usable");
                Check(wizard.Control<Button>("StartInstall").Enabled,"Probe failure disabled ordinary recording installation");wizard.Start();PumpUntil(()=>wizard.Window.Stage==V1SetupStage.Ready,"Probe-failure ordinary install");Check(wizard.DlssCalls==0,"Probe error started download");
            }
        });
        Run("Hardware checking disables Start and Continue until the asynchronous result arrives",delegate{
            using(var wizard=new WizardFixture("ui-probe-pending",Gpu("NVIDIA GeForce RTX 5080"))){wizard.HardwareReady.Reset();wizard.Show();wizard.Control<CheckBox>("DlssAvailability").Checked=true;
                PumpUntil(()=>wizard.DetectionCalls==1&&wizard.Window.Stage==V1SetupStage.HardwareChecking,"Pending hardware stage");
                Check(!wizard.Control<Button>("StartInstall").Enabled&&!wizard.Control<Button>("ContinueInstall").Enabled&&!wizard.Control<CheckBox>("DlssAvailability").Enabled,"Pending hardware state still permits start/continue/toggling");
                wizard.Start();Check(wizard.CoreCalls==0&&wizard.DlssCalls==0,"Click while checking started installation");wizard.Capture("ui-hardware-checking");wizard.HardwareReady.Set();
                PumpUntil(()=>wizard.Window.Stage==V1SetupStage.Preflight&&wizard.Control<Button>("StartInstall").Enabled,"Hardware check restores preflight");Check(wizard.Control<CheckBox>("DlssAvailability").Checked,"Successful async check lost selection");
            }
        });
        foreach(var deselect in new[]{false,true}){var cancelled=deselect;Run("Supported hardware followed by a failed directory refresh requires reinspection without rejecting DLSS: "+(cancelled?"deselect":"keep selected"),delegate{
            using(var wizard=new WizardFixture("ui-env-refresh-"+(cancelled?"off":"on"),Gpu("NVIDIA GeForce RTX 5080"))){wizard.Show();wizard.InspectFail=true;wizard.SelectDlss();
                var check=wizard.Control<CheckBox>("DlssAvailability");
                Check(wizard.Window.Stage==V1SetupStage.Selecting&&check.Checked&&check.Enabled&&wizard.DetectionCalls==1&&wizard.Messages.Count==0,"An environment refresh failure was mistaken for a permanent hardware rejection");
                Check(!wizard.Control<Button>("StartInstall").Enabled&&!wizard.Control<Button>("ContinueInstall").Enabled&&wizard.Control<ListView>("MissingParts").Items.Count==0,"Stale preflight data remained usable after environment failure");
                Check(wizard.Control<Label>("SetupStatus").Text.Contains("fixture preflight failure"),"Directory refresh failure is not visible");
                wizard.Start();Check(wizard.CoreCalls==0&&wizard.DlssCalls==0,"Failed refresh allowed installation");
                if(cancelled){var errorText=wizard.Control<Label>("SetupStatus").Text;check.Checked=false;Check(!check.Checked&&check.Enabled&&!wizard.Control<Button>("StartInstall").Enabled,"Deselecting after environment failure bypassed directory inspection");
                    Check(wizard.Control<Label>("SetupStatus").Text==errorText&&wizard.Control<Label>("SetupStatus").ForeColor.R>wizard.Control<Label>("SetupStatus").ForeColor.G,"Deselecting without a valid report hid the pending directory error");}
                wizard.Capture("ui-directory-refresh-failed-"+(cancelled?"off":"on"));wizard.InspectFail=false;int inspections=wizard.Inspections;wizard.Control<Button>("Inspect").PerformClick();
                PumpUntil(()=>wizard.Inspections>inspections&&wizard.Window.Stage==V1SetupStage.Preflight,"Directory reinspection restores a valid report");
                Check(wizard.Control<Button>("StartInstall").Enabled&&check.Enabled&&check.Checked==!cancelled&&wizard.DetectionCalls==1,"Directory recovery lost the user choice or repeated hardware detection");
                wizard.DownloadReady.Set();wizard.Start();PumpUntil(()=>wizard.Window.Stage==V1SetupStage.Ready,"Recovered directory installation completes");
                Check(wizard.CoreCalls==1&&wizard.DlssCalls==(cancelled?0:1),"Recovered directory ignored the explicit DLSS selection");
            }
        });}
        Run("Already-complete component report never makes DLSS selected by default",delegate{
            using(var wizard=new WizardFixture("ui-existing-off",Gpu("NVIDIA GeForce RTX 5080"))){wizard.ExistingComponentsReport=true;wizard.Show();
                Check(!wizard.Control<CheckBox>("DlssAvailability").Checked&&wizard.DetectionCalls==0,"Complete existing components implicitly selected or detected hardware");
                wizard.Start();PumpUntil(()=>wizard.Window.Stage==V1SetupStage.Ready,"Existing but unselected ordinary install");Check(wizard.DlssCalls==0&&wizard.DetectionCalls==0,"Existing components caused unrequested DLSS processing");
            }
        });
        Run("DLSS choice is locked during ordinary installation after a supported selection",delegate{
            using(var wizard=new WizardFixture("ui-core-choice-lock",Gpu("NVIDIA GeForce RTX 5080"))){wizard.Show();wizard.SelectDlss();wizard.CoreReady.Reset();wizard.Start();
                PumpUntil(()=>wizard.CoreCalls==1&&wizard.Window.Stage==V1SetupStage.CoreInstalling,"Core waiting point");Check(!wizard.Control<CheckBox>("DlssAvailability").Enabled,"Core installation allows the selection to change");
                wizard.DownloadReady.Set();wizard.CoreReady.Set();PumpUntil(()=>wizard.Window.Stage==V1SetupStage.Ready,"Selected installation completion");Check(wizard.DlssCalls==1,"Selected supported install skipped DLSS");
            }
        });
        Run("Closing during pending hardware detection never starts installation",delegate{
            using(var wizard=new WizardFixture("ui-close-probe",Gpu("NVIDIA GeForce RTX 5080"))){wizard.HardwareReady.Reset();wizard.Show();wizard.Control<CheckBox>("DlssAvailability").Checked=true;
                PumpUntil(()=>wizard.DetectionCalls==1,"Probe entered before close");wizard.Window.Close();wizard.HardwareReady.Set();PumpUntil(()=>wizard.Window.IsDisposed,"Pending-probe close completes");
                Check(wizard.CoreCalls==0&&wizard.DlssCalls==0,"Closing the probe started an installation");
            }
        });
        Run("Supported GPU cannot Continue until download completes; pause, cancel and retry retain recorder",delegate{
            using(var wizard=new WizardFixture("ui-download-controls",Gpu("NVIDIA GeForce RTX 5080"))){wizard.Show();wizard.SelectDlss();wizard.Start();wizard.Start();
                PumpUntil(()=>wizard.Window.Stage==V1SetupStage.DlssDownloading&&wizard.ProgressEvents>2,"Download stage started");
                Check(wizard.CoreCalls==1&&wizard.DlssCalls==1&&wizard.Window.RecorderInstalled,"Repeated click started concurrent installation");
                var recorderRow=wizard.Control<ListView>("MissingParts").Items.Cast<ListViewItem>().Single(x=>x.Text.Contains("内录 MOD V1.1"));
                Check(recorderRow.SubItems[1].Text.Contains("已安装")&&!recorderRow.SubItems[1].Text.Contains("未安装"),"Installed recorder row still says uninstalled during DLSS stage");
                Check(!wizard.Control<Button>("ContinueInstall").Enabled&&wizard.Control<Button>("PauseDownload").Enabled&&!wizard.Control<TextBox>("RootPath").Enabled,"Download gate or input lock incorrect");
                wizard.CheckLockedDownloadChoice();
                Check(wizard.Control<Label>("NetworkHint").Visible&&wizard.Control<Label>("NetworkHint").Text.Contains("github"),"Initial network hint missing");
                wizard.Capture("ui-download-progress");PumpUntil(()=>wizard.Control<Label>("NetworkHint").Text.Contains("暂停后排查"),"Rotating network hint",5500);
                wizard.Control<Button>("PauseDownload").PerformClick();PumpUntil(()=>wizard.Window.Stage==V1SetupStage.DlssPaused,"Paused stage");
                Check(wizard.LastControl.IsPaused&&wizard.Control<Button>("PauseDownload").Text.Contains("恢复")&&!wizard.Control<Button>("ContinueInstall").Enabled,"Pause controls incorrect");
                wizard.CheckLockedDownloadChoice();
                wizard.Capture("ui-download-paused");
                wizard.Control<Button>("PauseDownload").PerformClick();PumpUntil(()=>wizard.Window.Stage==V1SetupStage.DlssDownloading,"Resume button stage");
                Check(!wizard.LastControl.IsPaused&&wizard.Control<Button>("PauseDownload").Text.Contains("暂停"),"Resume did not reopen the download gate");wizard.Control<Button>("PauseDownload").PerformClick();
                wizard.Control<Button>("CancelInstall").PerformClick();PumpUntil(()=>wizard.Window.Stage==V1SetupStage.DlssCancelled,"Cancel stage");
                Check(!wizard.Control<Button>("ContinueInstall").Enabled&&wizard.Control<Button>("StartInstall").Enabled&&wizard.Control<Button>("CancelInstall").Enabled,"Cancelled stage is stuck or Continue was enabled");
                Check(!wizard.Control<CheckBox>("DlssAvailability").Enabled&&wizard.Control<CheckBox>("DlssAvailability").Checked,"Download cancellation allowed bypassing the chosen DLSS stage");
                Check(File.Exists(P(InstallCore.ReceiptName)),"Cancel removed successfully installed recorder");wizard.Capture("ui-download-cancelled");
                wizard.DownloadReady.Set();wizard.Start();PumpUntil(()=>wizard.Window.Stage==V1SetupStage.Ready,"Retry download completion");
                Check(wizard.CoreCalls==1&&wizard.DlssCalls==2&&wizard.Window.DownloadComplete&&wizard.Control<Button>("ContinueInstall").Enabled,"Retry reinstalled recorder or failed gating");
                PumpFor(100);Check(wizard.Window.Stage==V1SetupStage.Ready,"Queued progress regressed completed stage");
                var completedRows=wizard.Control<ListView>("MissingParts").Items.Cast<ListViewItem>().Where(x=>x.Text.Contains("内录 MOD V1.1")||x.Text=="FFmpeg"||x.Text.StartsWith("DLSS")).ToArray();
                Check(completedRows.All(x=>!x.SubItems[1].Text.Contains("未安装")&&!x.SubItems[1].Text.Contains("未发现")&&!x.SubItems[1].Text.Contains("缺少")&&!x.SubItems[1].Text.Contains("待下载")),"Completed component table retains stale missing statuses");
                Check(wizard.Control<ProgressBar>("DownloadProgress").Value==wizard.Control<ProgressBar>("DownloadProgress").Maximum&&wizard.Control<Label>("TransferStatus").Text.Contains("全部完成")&&!wizard.Control<Label>("TransferStatus").Text.Contains("0.0 MB"),"Completed transfer still shows empty progress/size");
                Check(!wizard.Control<Label>("NetworkHint").Visible,"Network warning is still shown after successful completion");wizard.Capture("ui-download-ready-complete");
                wizard.Control<Button>("ContinueInstall").PerformClick();Check(wizard.Window.Stage==V1SetupStage.Finished,"Continue did not advance to final page");wizard.Capture("ui-finished-supported");
            }
        });
        Run("Offline DLSS failure keeps recorder and Continue disabled, while retry succeeds",delegate{
            using(var wizard=new WizardFixture("ui-offline",Gpu("NVIDIA GeForce RTX 4060"))){wizard.DownloadError="fixture offline: GitHub unreachable";wizard.DownloadReady.Set();wizard.Show();wizard.SelectDlss();wizard.Start();
                PumpUntil(()=>wizard.Window.Stage==V1SetupStage.DlssFailed,"Offline failure stage");
                Check(wizard.Window.RecorderInstalled&&!wizard.Window.DownloadComplete&&!wizard.Control<Button>("ContinueInstall").Enabled&&wizard.Control<Button>("StartInstall").Enabled,"Failure continuation improperly enabled");
                Check(!wizard.Control<CheckBox>("DlssAvailability").Enabled&&wizard.Control<CheckBox>("DlssAvailability").Checked,"Download failure reopened hardware selection");
                wizard.CheckLockedDownloadChoice();
                Check(wizard.Messages.Any(x=>x.Contains("GitHub unreachable"))&&File.Exists(P(InstallCore.ReceiptName)),"Failure lost recorder or hid error");
                wizard.Capture("ui-download-failed");
                wizard.DownloadError=null;wizard.Start();PumpUntil(()=>wizard.Window.Stage==V1SetupStage.Ready,"Offline retry");Check(wizard.CoreCalls==1&&wizard.DlssCalls==2,"Offline retry repeated core install");
            }
        });
        Run("A backend result without Completed cannot unlock Continue",delegate{
            using(var wizard=new WizardFixture("ui-incomplete",Gpu("NVIDIA GeForce RTX 3060"))){wizard.IncompleteResult=true;wizard.DownloadReady.Set();wizard.Show();wizard.SelectDlss();wizard.Start();
                PumpUntil(()=>wizard.Window.Stage==V1SetupStage.DlssFailed,"Incomplete result rejected");Check(!wizard.Control<Button>("ContinueInstall").Enabled&&!wizard.Window.DownloadComplete,"Incomplete backend result enabled continuation");
            }
        });
        Run("Cancelling recorder confirmation closes without DLSS and preserves a zero-write target",delegate{
            using(var wizard=new WizardFixture("ui-core-cancel",Gpu("NVIDIA GeForce RTX 5060"))){wizard.CoreCancel=true;var before=Snapshot(root);wizard.Show();wizard.Start();
                PumpUntil(()=>wizard.Window.IsDisposed&&wizard.CoreCalls==1,"Core confirmation cancellation");
                Check(wizard.DlssCalls==0&&!wizard.Window.RecorderInstalled&&V1Program.ExitCode==2,"Core cancellation started next stage");Same(before,root,"Core cancelled wizard changed files");
            }
        });
        Run("Recorder failure never starts DLSS and keeps a retry action available",delegate{
            using(var wizard=new WizardFixture("ui-core-error",Gpu("NVIDIA GeForce RTX 5060"))){wizard.CoreFail=true;var before=Snapshot(root);wizard.Show();wizard.Start();
                PumpUntil(()=>wizard.Window.Stage==V1SetupStage.CoreFailed,"Core failure stage");Check(wizard.DlssCalls==0&&!wizard.Window.RecorderInstalled&&wizard.Control<Button>("StartInstall").Enabled&&!wizard.Control<Button>("ContinueInstall").Enabled,"Core failure buttons incorrect");Same(before,root,"Core error changed target");
            }
        });
        Run("Preflight failure cannot enable installation",delegate{
            using(var wizard=new WizardFixture("ui-inspect-error",Gpu("NVIDIA GeForce RTX 5080"))){wizard.InspectFail=true;wizard.Show();
                Check(wizard.Window.Stage==V1SetupStage.Selecting&&!wizard.Control<Button>("StartInstall").Enabled&&!wizard.Control<Button>("ContinueInstall").Enabled,"Preflight error enabled installation");
            }
        });
        Run("Closing during DLSS download waits for cancellation and retains installed recorder",delegate{
            using(var wizard=new WizardFixture("ui-close-downloading",Gpu("NVIDIA GeForce RTX 5080"))){wizard.Show();wizard.SelectDlss();wizard.Start();PumpUntil(()=>wizard.DlssCalls==1&&wizard.ProgressEvents>0,"Download before close");
                wizard.Window.Close();PumpUntil(()=>wizard.Window.IsDisposed,"Window cancellation exit");
                var cancelled=wizard.LastTask.IsCanceled||(wizard.LastTask.IsFaulted&&wizard.LastTask.Exception.GetBaseException() is OperationCanceledException);
                Check(cancelled&&File.Exists(P(InstallCore.ReceiptName))&&V1Program.ExitCode==2,"Close failed to cancel or removed recorder");
            }
        });
    }
    [STAThread] public static int Main(string[] args)
    {
        bool success=false;
        try{
            options=InstallCore.Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(args[0]));game=Path.GetFullPath(Opt("GameRoot"));workspace=Path.GetFullPath(Opt("Workspace"));encoder=Path.GetFullPath(Opt("EncoderStub"));
            var allowed=Path.GetFullPath(Path.Combine(Directory.GetParent(game).FullName,"V1.1安装测试"))+Path.DirectorySeparatorChar;
            Check(workspace.StartsWith(allowed,StringComparison.OrdinalIgnoreCase),"Fixture workspace must be under V1.1安装测试");
            Check(!Directory.Exists(workspace),"Test fixture workspace already exists");Directory.CreateDirectory(workspace);InstallCore.NoLinks(workspace);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            if(Opt("Suite")=="Core"||Opt("Suite")=="All")CoreTests();
            if(Opt("Suite")=="Network"||Opt("Suite")=="All")NetworkTests();
            if(Opt("Suite")=="Artifacts"||Opt("Suite")=="All")ArtifactTests();
            if(Opt("Suite")=="Ui"||Opt("Suite")=="All"){
                bool started=false;Exception uiError=null;
                EventHandler startUi=delegate{if(started)return;started=true;try{UiTests();}catch(Exception error){uiError=error;}finally{Application.ExitThread();}};
                Application.Idle+=startUi;Application.Run();Application.Idle-=startUi;if(uiError!=null)throw uiError;
            }
            success=true;Console.WriteLine("ALL "+passed+" V1.1 "+Opt("Suite")+" CHECKS PASSED: "+workspace);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{
            if(!String.IsNullOrEmpty(workspace)&&Directory.Exists(workspace)){
                File.WriteAllText(Path.Combine(workspace,"results.json"),InstallCore.Json.Serialize(new Dictionary<string,object>{{"passed",success},{"count",passed},{"cases",results},{"fixture_note","Core uses synthetic recorder payloads and an identified encoder process stub. Network uses real loopback HTTP and the production downloader. DLSS commit uses genuine native fixtures. WinForms uses the real wizard with source-level test services. No AA launched and no GPU inference claimed."}}),new UTF8Encoding(false));
                if(success)File.WriteAllText(Path.Combine(workspace,"PASS.txt"),"ALL "+passed+" CHECKS PASSED\n"+DateTime.UtcNow.ToString("O"));
            }
        }
    }
}
