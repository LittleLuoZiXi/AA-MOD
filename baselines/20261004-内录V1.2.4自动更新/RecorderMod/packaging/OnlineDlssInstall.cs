using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AzureArchive.Recorder;

public sealed class OnlineDlssAsset
{
    public string Name,Url,Sha256;
    public long Size;
}
public sealed class OnlineDlssProgress
{
    public long BytesReceived,TotalBytes;
    public string Phase,Asset,Message;
}
public sealed class OnlineDlssResult
{
    public bool Completed,AlreadyInstalled;
    public int Series;
    public string RuntimeDirectory;
}
public sealed class OnlineDlssControl : IDisposable
{
    readonly ManualResetEventSlim gate=new ManualResetEventSlim(true);
    readonly CancellationTokenSource cancellation=new CancellationTokenSource();
    readonly object sync=new object();
    HttpWebRequest active;int pauseRevision;bool paused,disposed;
    public bool IsPaused {get{lock(sync)return paused;}}
    internal int Revision {get{lock(sync)return pauseRevision;}}
    internal CancellationToken Token {get{return cancellation.Token;}}
    public void Pause()
    {
        HttpWebRequest request;
        lock(sync){if(disposed||cancellation.IsCancellationRequested||paused)return;paused=true;pauseRevision++;gate.Reset();request=active;}
        if(request!=null)request.Abort();
    }
    public void Resume(){lock(sync){if(disposed)return;paused=false;gate.Set();}}
    public void Cancel()
    {
        HttpWebRequest request;
        lock(sync){if(disposed)return;cancellation.Cancel();paused=false;gate.Set();request=active;}
        if(request!=null)request.Abort();
    }
    internal void Check(){Token.ThrowIfCancellationRequested();gate.Wait(Token);Token.ThrowIfCancellationRequested();}
    internal void Bind(HttpWebRequest request)
    {
        bool abort;lock(sync){active=request;abort=paused||cancellation.IsCancellationRequested;}
        if(abort)request.Abort();
    }
    internal void Unbind(HttpWebRequest request){lock(sync){if(Object.ReferenceEquals(active,request))active=null;}}
    public void Dispose()
    {
        lock(sync){if(disposed)return;}
        Cancel();lock(sync){disposed=true;active=null;}
        // Dispose only after the Task has finished; the installer owns this lifetime.
        gate.Dispose();cancellation.Dispose();
    }
}

public static class OnlineDlssInstall
{
    public const string Product="AzureArchiveDLSSSupplement",Version="1.0.0";
    public const string Mod="mods/AzureArchiveDLSS/",ReceiptName=Mod+"installed-files.json",Uninstaller=Mod+"卸载DLSS补充MOD.exe";
    public const string CommonUrl="https://github.com/banbanzhige/DLSS5Tool/releases/download/v2.3.3/DLSS5Tool-v2.3.3-win64.zip";
    public const string CommonZipHash="D724F986143A45293E50FAF7E2799EABAD8A35F98BFA30C3D6F9836702B056B1";
    // These identities are independently checked against the fixed public archive before release.
    public static readonly Dictionary<string,string> CommonHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){
        {"ffmpeg.exe","B1383F5D07470D503EDECDAEE4BDDC5891E986E916A698299B357F79CFE445FD"},
        {"ffprobe.exe","012BDDDED3CBC5204055210D7FF4F0B3F7521BCA441A694939856D01909F5756"},
        {"vsr_host.dll","5E66DDC8B4C56F2B77DAED782CC8429B607D32BBA358EFB488FBD4C03AE515B7"},
        {"nvngx_vsr.dll","C3D88EEA5FF7A548EDEFA66414CF6E77464D0947277C904F324DD23ABF58A1ED"},
        {"dlssg_video_worker.exe","7E6C281608BE2E8A6D63A574A3EAE8621C6C38635260E150571EF20F422C4659"},
        {"nvngx_dlssg.dll","135EAF0733C1E37381A8C28ABCF7A862404A54132B81787C04E35D09EFC5E36F"},
        {"dlssnr_host_v2.dll","C8AD631F8F78B2DEDEC6AEC418C7A570D6FC5BF1B9FFA9F3514BCB0EDD58FC13"}
    };
    static readonly string[] Licenses={"DLSS5Tool-MIT.txt","DLSS5Tool-THIRD_PARTY_NOTICES.md","NVIDIA-DLSS-SDK-LICENSE.txt",
        "NVIDIA-RTX-Video-SDK-LICENSE.pdf","NVIDIA-Optical-Flow-Headers-LICENSE.txt","RTX40MFG-Unlock-LICENSE.txt",
        "FFmpeg-COPYING.GPLv3.txt","FFmpeg-UPSTREAM-README.txt","FFmpeg-VERSION.txt","FFmpeg-BUILD-CONFIG.txt","FFmpeg-PROVENANCE.json"};
    static readonly Dictionary<int,string> ZipHashes=new Dictionary<int,string>{
        {30,"01626F7FFE14C54928E9B2EAA09BAF1886FA9200B247BBB51895F935F301886C"},
        {40,"3FDEB4F3B44165BFD31D98E288A46DC16EEDD40168819C8ED6F73C45FC92C7A1"},
        {50,"E730E1EA95B0A4F6420B9B1BBB1C2948CBB1BC9241AEBFDEB10EFEF8A3BC906A"}};
    static readonly Dictionary<int,string> ZipNames=new Dictionary<int,string>{{30,"30.-310.8.SF-v2.zip"},{40,"40.zip"},{50,"50.zip"}};

    public static OnlineDlssAsset[] GetAssets(int series)
    {
        CheckSeries(series);
        return new[]{new OnlineDlssAsset{Name="共同组件",Url=CommonUrl,Sha256=CommonZipHash,Size=579761793},
            new OnlineDlssAsset{Name="RTX "+series+" 系组件",Url="https://github.com/banbanzhige/DLSS5Tool/releases/download/zip/"+ZipNames[series],Sha256=ZipHashes[series],Size=series==30?117898662:series==40?106991529:109425424}};
    }
    static void CheckSeries(int series)
    {
        if(series==20)throw new NotSupportedException("暂时不支持20系");
        if(!DlssComponents.Hashes.ContainsKey(series))throw new NotSupportedException("此显卡暂不支持 DLSS 增强；普通内录可用。");
    }
    static bool SameHash(string path,string hash)
    {
        return File.Exists(path)&&InstallCore.Hash(path).Equals(hash,StringComparison.OrdinalIgnoreCase);
    }
    static HashSet<string> Allowed()
    {
        var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Uninstaller,Mod+"使用说明.txt",Mod+"第三方许可.txt",Mod+"组件来源.json"};
        foreach(var name in CommonHashes.Keys)set.Add(Mod+"runtime/_internal/"+name);
        foreach(var name in new[]{"zh_CN.json","en_US.json"})set.Add(Mod+"runtime/_internal/locales/"+name);
        foreach(var series in new[]{30,40,50})set.Add(Mod+"runtime/mods/dlss/rtx"+series+"/nvngx_dlssnr.dll");
        foreach(var name in Licenses)set.Add(Mod+"许可证/"+name);
        return set;
    }
    static Receipt ReadReceipt(string root,bool required)
    {
        var path=InstallCore.Within(root,ReceiptName);
        if(!File.Exists(path)){if(required)throw new IOException("DLSS 独立卸载清单缺失。");return null;}
        if(new FileInfo(path).Length>1024*1024)throw new IOException("DLSS 清单大小异常。");
        var receipt=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(path));
        ValidateReceipt(root,receipt);return receipt;
    }
    static void ValidateReceipt(string root,Receipt receipt)
    {
        if(receipt==null||receipt.Product!=Product||receipt.Version!=Version||String.IsNullOrWhiteSpace(receipt.Root)
            ||!InstallCore.FullRoot(receipt.Root).Equals(root,StringComparison.OrdinalIgnoreCase)||receipt.Files==null||receipt.Files.Count==0||receipt.Files.Count>64
            ||(receipt.LegacyFiles!=null&&receipt.LegacyFiles.Count>0)||(receipt.GeneratedFiles!=null&&receipt.GeneratedFiles.Count>0))
            throw new IOException("DLSS 独立组件清单身份无效，未更改安装文件。");
        var allowed=Allowed();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in receipt.Files) {
            if(file==null||file.Path==null||!allowed.Contains(file.Path)||!seen.Add(file.Path)||file.Sha256==null||!Regex.IsMatch(file.Sha256,"\\A[0-9a-fA-F]{64}\\z"))
                throw new IOException("DLSS 清单含未知、重复或越界路径。");
            InstallCore.Within(root,file.Path);
            foreach(var pair in CommonHashes)
                if(file.Path.Equals(Mod+"runtime/_internal/"+pair.Key,StringComparison.OrdinalIgnoreCase)&&!file.Sha256.Equals(pair.Value,StringComparison.OrdinalIgnoreCase))throw new IOException("DLSS 清单的固定共同组件摘要无效。");
            foreach(var pair in DlssComponents.Hashes)
                if(file.Path.Equals(Mod+"runtime/mods/dlss/rtx"+pair.Key+"/nvngx_dlssnr.dll",StringComparison.OrdinalIgnoreCase)&&!file.Sha256.Equals(pair.Value,StringComparison.OrdinalIgnoreCase))throw new IOException("DLSS 清单的固定代际摘要无效。");
        }
        if(!seen.Contains(Uninstaller))throw new IOException("DLSS 清单没有独立卸载器。");
    }
    public static string[] MissingComponents(string gameRoot,int series)
    {
        var result=new List<string>();
        if(!DlssComponents.Hashes.ContainsKey(series))return new[]{series==20?"暂时不支持20系":"此显卡暂不支持 DLSS"};
        try {
            var root=InstallCore.FullRoot(gameRoot);InstallCore.NoLinks(root);
            var receipt=ReadReceipt(root,false);
            if(receipt==null)result.Add("独立安装清单");
            foreach(var pair in CommonHashes) {
                var relative=Mod+"runtime/_internal/"+pair.Key;var path=InstallCore.Within(root,relative);
                if(!SameHash(path,pair.Value)||receipt==null||!receipt.Files.Any(f=>f.Path.Equals(relative,StringComparison.OrdinalIgnoreCase)&&f.Sha256.Equals(pair.Value,StringComparison.OrdinalIgnoreCase)))result.Add(pair.Key);
            }
            var runtime=Mod+"runtime/mods/dlss/rtx"+series+"/nvngx_dlssnr.dll";
            if(!SameHash(InstallCore.Within(root,runtime),DlssComponents.Hashes[series])||receipt==null||!receipt.Files.Any(f=>f.Path.Equals(runtime,StringComparison.OrdinalIgnoreCase)&&f.Sha256.Equals(DlssComponents.Hashes[series],StringComparison.OrdinalIgnoreCase)))result.Add("RTX "+series+" 系运行库");
            var uninstall=receipt==null?null:receipt.Files.FirstOrDefault(f=>f.Path.Equals(Uninstaller,StringComparison.OrdinalIgnoreCase));
            if(uninstall==null||!SameHash(InstallCore.Within(root,Uninstaller),uninstall.Sha256))result.Add("独立卸载程序");
            foreach(var name in new[]{"zh_CN.json","en_US.json"}) {
                var relative=Mod+"runtime/_internal/locales/"+name;var file=receipt==null?null:receipt.Files.FirstOrDefault(f=>f.Path.Equals(relative,StringComparison.OrdinalIgnoreCase));
                if(file==null||!SameHash(InstallCore.Within(root,relative),file.Sha256))result.Add(name);
            }
        }catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is ArgumentException))throw;result.Add("组件清单或路径无效："+error.Message);}
        return result.Distinct().ToArray();
    }
    public static bool IsInstalled(string gameRoot,int series){return MissingComponents(gameRoot,series).Length==0;}
    public static Task<OnlineDlssResult> RunAsync(string gameRoot,int series,byte[] uninstallerBytes,Action<OnlineDlssProgress> progress,OnlineDlssControl control)
    {
        if(control==null)throw new ArgumentNullException("control");
        return Task.Run(()=>Run(gameRoot,series,uninstallerBytes,progress,control));
    }
    static void Report(Action<OnlineDlssProgress> progress,string phase,string asset,string message,long received=0,long total=0)
    {
        if(progress!=null)progress(new OnlineDlssProgress{Phase=phase,Asset=asset,Message=message,BytesReceived=received,TotalBytes=total});
    }
    static OnlineDlssResult Run(string gameRoot,int series,byte[] uninstallerBytes,Action<OnlineDlssProgress> progress,OnlineDlssControl control)
    {
        CheckSeries(series);control.Check();var root=InstallCore.FullRoot(gameRoot);InstallCore.ValidateGame(root,true);
        Report(progress,"Checking","","正在检查本机代际组件…");
        if(IsInstalled(root,series)){Report(progress,"Completed","","对应组件已经完整安装，无需重复下载。");return new OnlineDlssResult{Completed=true,AlreadyInstalled=true,Series=series,RuntimeDirectory=InstallCore.Within(root,DlssComponents.ToolRelative)};}
        if(uninstallerBytes==null||uninstallerBytes.Length<1024||uninstallerBytes.Length>16*1024*1024||uninstallerBytes[0]!=77||uninstallerBytes[1]!=90)throw new IOException("独立 DLSS 卸载程序资源无效。");
        var old=ReadReceipt(root,false);
        foreach(var relative in Allowed().Where(p=>!new[]{30,40,50}.Where(s=>s!=series).Any(s=>p.Equals(Mod+"runtime/mods/dlss/rtx"+s+"/nvngx_dlssnr.dll",StringComparison.OrdinalIgnoreCase))))CheckDestination(root,relative,old);
        // The download session is separate from both installed products. No installation
        // path is created until all archive and component checks have passed.
        var session=Path.Combine(Path.GetTempPath(),"AARecorder-DLSS-"+Guid.NewGuid().ToString("N"));
        InstallCore.NoLinks(Path.GetTempPath());Directory.CreateDirectory(session);var owned=new List<string>();
        try {
            var assets=GetAssets(series);var archives=new string[assets.Length];
            long finished=0;long planned=assets.Sum(a=>a.Size);
            for(int i=0;i<assets.Length;i++) {
                int index=i;archives[i]=Path.Combine(session,"asset-"+i+".zip");owned.Add(archives[i]);
                DownloadVerifiedAsset(assets[i],archives[i],control,delegate(OnlineDlssProgress p){
                    long total=planned;if(assets[index].Size==0&&p.TotalBytes>0){assets[index].Size=p.TotalBytes;planned=assets.Sum(a=>a.Size);total=planned;}
                    p.BytesReceived+=finished;p.TotalBytes=total;if(progress!=null)progress(p);
                });finished+=new FileInfo(archives[i]).Length;
            }
            control.Check();Report(progress,"Extracting","","正在校验并提取本机所需组件…");
            var prepared=Prepare(session,owned,archives,assets,series,uninstallerBytes,control);
            return CommitPrepared(root,series,prepared,control,progress);
        }finally{CleanupSession(session,owned);}
    }

    // Same-assembly tests may supply loopback assets to this transport. The production
    // entry above obtains its entire asset list from GetAssets; no env/CLI URL override exists.
    internal static void DownloadVerifiedAsset(OnlineDlssAsset asset,string destination,OnlineDlssControl control,Action<OnlineDlssProgress> progress)
    {
        if(asset==null||control==null||String.IsNullOrEmpty(asset.Url)||asset.Sha256==null||!Regex.IsMatch(asset.Sha256,"\\A[0-9a-fA-F]{64}\\z"))throw new ArgumentException("下载描述无效。");
        var uri=new Uri(asset.Url);if(uri.Scheme!="https"&&!(uri.Scheme=="http"&&uri.IsLoopback))throw new IOException("仅允许 HTTPS 或本地测试服务器。");
        if(asset.Size<0||asset.Size>1024L*1024*1024)throw new IOException("下载大小超出限定范围。");
        InstallCore.NoLinks(destination);var part=destination+".part";
        if(File.Exists(destination)||Directory.Exists(destination)||File.Exists(part)||Directory.Exists(part))throw new IOException("下载目标已存在，未覆盖："+destination);
        bool partOwned=false,published=false,success=false;long expected=asset.Size;string identity=null;int errors=0;
        ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
        try {
            using(var initial=new FileStream(part,FileMode.CreateNew,FileAccess.Write,FileShare.Read))partOwned=true;
            while(true) {
                if(control.IsPaused)Report(progress,"Paused",asset.Name,"下载已暂停，可继续或取消。",new FileInfo(part).Length,expected);
                control.Check();long offset=new FileInfo(part).Length;
                if(expected>0&&offset==expected)break;
                var revision=control.Revision;
                var request=(HttpWebRequest)WebRequest.Create(uri);request.Method="GET";request.AllowAutoRedirect=true;request.MaximumAutomaticRedirections=10;
                request.Timeout=30000;request.ReadWriteTimeout=30000;request.UserAgent="AzureArchive-Recorder-V1.0";request.AutomaticDecompression=DecompressionMethods.None;
                if(offset>0){request.AddRange(offset);if(identity!=null)request.Headers["If-Range"]=identity;}
                control.Bind(request);
                try {
                    using(var response=(HttpWebResponse)request.GetResponse()) {
                        var currentIdentity=response.Headers["ETag"]??response.Headers["Last-Modified"];
                        long total=expected;
                        if(response.StatusCode==HttpStatusCode.PartialContent) {
                            var match=Regex.Match(response.Headers["Content-Range"]??"","\\Abytes ([0-9]+)-([0-9]+)/([0-9]+)\\z");
                            long first,last,whole;
                            if(!match.Success||!Int64.TryParse(match.Groups[1].Value,out first)||!Int64.TryParse(match.Groups[2].Value,out last)||!Int64.TryParse(match.Groups[3].Value,out whole)
                                ||first!=offset||last<first||last>=whole||(expected>0&&whole!=expected)||(response.ContentLength>=0&&response.ContentLength!=last-first+1))throw new InvalidDataException("服务器返回了不一致的续传范围。");
                            total=whole;
                            if(identity!=null&&currentIdentity!=null&&!String.Equals(identity,currentIdentity,StringComparison.Ordinal)) {
                                using(var reset=new FileStream(part,FileMode.Open,FileAccess.Write,FileShare.Read))reset.SetLength(0);
                                identity=null;Report(progress,"Downloading",asset.Name,"文件版本改变，正在从头重新下载。",0,total);continue;
                            }
                        } else if(response.StatusCode==HttpStatusCode.OK) {
                            // A server may ignore Range or invalidate If-Range. Never append
                            // a full response to an existing partial archive.
                            if(offset>0){using(var reset=new FileStream(part,FileMode.Open,FileAccess.Write,FileShare.Read))reset.SetLength(0);offset=0;}
                            if(response.ContentLength>=0)total=response.ContentLength;
                            if(expected>0&&total>0&&total!=expected)throw new InvalidDataException("下载文件长度与固定清单不一致。");
                        } else throw new WebException("下载服务器返回 HTTP "+(int)response.StatusCode);
                        if(total>1024L*1024*1024||total<0)throw new InvalidDataException("下载文件大小异常。");
                        if(expected==0&&total>0)expected=total;
                        if(identity==null)identity=currentIdentity;
                        Report(progress,"Downloading",asset.Name,"正在下载 "+asset.Name,offset,expected);
                        using(var stream=response.GetResponseStream())using(var output=new FileStream(part,FileMode.Open,FileAccess.Write,FileShare.Read)) {
                            output.Position=offset;var buffer=new byte[64*1024];
                            while(true) {
                                control.Check();int read=stream.Read(buffer,0,buffer.Length);if(read==0)break;
                                control.Check();
                                if(output.Position+read>1024L*1024*1024||(expected>0&&output.Position+read>expected))throw new InvalidDataException("服务器发送的数据超出预计长度。");
                                output.Write(buffer,0,read);output.Flush();
                                Report(progress,"Downloading",asset.Name,"正在下载 "+asset.Name,output.Position,expected);
                            }
                            if(expected>0&&output.Length!=expected)throw new EndOfStreamException("下载连接提前结束，可重试。");
                        }
                    }
                    break;
                }catch(Exception error) {
                    control.Token.ThrowIfCancellationRequested();
                    if(control.Revision!=revision){Report(progress,"Paused",asset.Name,"下载已暂停，可继续或取消。",new FileInfo(part).Length,expected);control.Check();continue;}
                    if(error is InvalidDataException)throw;
                    var web=error as WebException;
                    if(web!=null&&web.Response is HttpWebResponse) {
                        var response=(HttpWebResponse)web.Response;int status=(int)response.StatusCode;response.Close();
                        if(status>=400&&status<500)throw;
                    }
                    if(!(error is WebException)&&!(error is IOException))throw;
                    if(++errors>2)throw;
                    Report(progress,"Downloading",asset.Name,"连接中断，正在重试续传（"+errors+"/2）…",new FileInfo(part).Length,expected);
                    if(control.Token.WaitHandle.WaitOne(300))control.Token.ThrowIfCancellationRequested();
                }finally{control.Unbind(request);request.Abort();}
            }
            control.Check();Report(progress,"Verifying",asset.Name,"正在校验 "+asset.Name,new FileInfo(part).Length,expected);
            var digest=HashControlled(part,control);
            if(!digest.Equals(asset.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("下载文件 SHA-256 不匹配，未安装任何组件。");
            control.Check();InstallCore.NoLinks(destination);File.Move(part,destination);published=true;
            Report(progress,"Verified",asset.Name,"文件校验通过。",new FileInfo(destination).Length,new FileInfo(destination).Length);success=true;
        }finally {
            if(partOwned&&File.Exists(part)){InstallCore.NoLinks(part);File.Delete(part);}
            if(!success&&published&&File.Exists(destination)){InstallCore.NoLinks(destination);File.Delete(destination);}
        }
    }
    static string HashControlled(string file,OnlineDlssControl control)
    {
        using(var input=File.OpenRead(file))using(var hash=SHA256.Create()) {
            var bytes=new byte[1024*1024];int count;
            while((count=input.Read(bytes,0,bytes.Length))>0){control.Check();hash.TransformBlock(bytes,0,count,bytes,0);}
            hash.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(hash.Hash).Replace("-","");
        }
    }
    static Dictionary<string,ZipArchiveEntry> Entries(ZipArchive archive)
    {
        if(archive.Entries.Count>20000)throw new IOException("压缩包文件数量异常。");
        var result=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in archive.Entries) {
            var name=entry.FullName.Replace('\\','/');var trimmed=name.TrimEnd('/');
            if(String.IsNullOrEmpty(trimmed)||Path.IsPathRooted(name)||name.Contains(":")||trimmed.Split('/').Any(s=>s.Length==0||s=="."||s=="..")
                ||((entry.ExternalAttributes>>16)&0xF000)==0xA000||(entry.ExternalAttributes&0x400)!=0)throw new IOException("压缩包包含不安全路径或链接。");
            if(result.ContainsKey(name))throw new IOException("压缩包包含重复路径。");
            result.Add(name,entry);
        }
        return result;
    }
    static string TempFile(string session,List<string> owned)
    {
        var file=Path.Combine(session,Guid.NewGuid().ToString("N")+".bin");owned.Add(file);return file;
    }
    static string Extract(ZipArchiveEntry entry,string session,List<string> owned,OnlineDlssControl control)
    {
        if(entry==null||entry.Length<1||entry.Length>512L*1024*1024)throw new IOException("所需压缩文件缺失或大小异常。");
        var target=TempFile(session,owned);
        using(var input=entry.Open())using(var output=new FileStream(target,FileMode.CreateNew)) {
            var bytes=new byte[1024*1024];long total=0;int count;
            while((count=input.Read(bytes,0,bytes.Length))>0){control.Check();total+=count;if(total>entry.Length)throw new IOException("解压数据超出限定大小。");output.Write(bytes,0,count);}
            if(total!=entry.Length)throw new IOException("解压数据被截断。");
        }
        return target;
    }
    static string WriteTemp(string session,List<string> owned,byte[] data)
    {
        var file=TempFile(session,owned);using(var output=new FileStream(file,FileMode.CreateNew))output.Write(data,0,data.Length);return file;
    }
    static Dictionary<string,string> Prepare(string session,List<string> owned,string[] archives,OnlineDlssAsset[] assets,int series,byte[] uninstaller,OnlineDlssControl control)
    {
        var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        using(var archive=ZipFile.OpenRead(archives[0])) {
            var entries=Entries(archive);
            var roots=entries.Keys.Where(n=>n.EndsWith("/_internal/ffmpeg.EXE",StringComparison.OrdinalIgnoreCase)||n.Equals("_internal/ffmpeg.EXE",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(roots.Length!=1)throw new IOException("共同组件压缩包结构不匹配。");
            var prefix=roots[0].Substring(0,roots[0].Length-"_internal/ffmpeg.EXE".Length);
            Func<string,ZipArchiveEntry> get=delegate(string name){ZipArchiveEntry entry;if(!entries.TryGetValue(prefix+name,out entry))throw new IOException("共同组件缺少："+name);return entry;};
            foreach(var pair in CommonHashes) {
                var file=Extract(get("_internal/"+pair.Key),session,owned,control);
                if(!HashControlled(file,control).Equals(pair.Value,StringComparison.OrdinalIgnoreCase))throw new IOException("共同原生组件 SHA-256 不匹配："+pair.Key);
                files.Add(Mod+"runtime/_internal/"+pair.Key,file);
            }
            foreach(var language in new[]{"en_US","zh_CN"})files.Add(Mod+"runtime/_internal/locales/"+language+".json",Extract(get("_internal/locales/"+language+".json"),session,owned,control));
            var licenseMap=new Dictionary<string,string>{
                {"DLSS5Tool-MIT.txt","LICENSE"},{"DLSS5Tool-THIRD_PARTY_NOTICES.md","THIRD_PARTY_NOTICES.md"},
                {"NVIDIA-DLSS-SDK-LICENSE.txt","_internal/licenses/NVIDIA-DLSS/LICENSE.txt"},
                {"NVIDIA-RTX-Video-SDK-LICENSE.pdf","NVIDIA_RTX_Video_SDK_License.pdf"},
                {"NVIDIA-Optical-Flow-Headers-LICENSE.txt","_internal/licenses/NVIDIA-Optical-Flow-Headers-LICENSE.txt"},
                {"RTX40MFG-Unlock-LICENSE.txt","_internal/licenses/RTX40MFG-Unlock/LICENSE.txt"},
                {"FFmpeg-COPYING.GPLv3.txt","_internal/licenses/FFmpeg-full/LICENSE"},
                {"FFmpeg-UPSTREAM-README.txt","_internal/licenses/FFmpeg-full/README.txt"},
                {"FFmpeg-BUILD-CONFIG.txt","_internal/licenses/FFmpeg-full/README.txt"}};
            foreach(var pair in licenseMap)files.Add(Mod+"许可证/"+pair.Key,Extract(get(pair.Value),session,owned,control));
        }
        using(var archive=ZipFile.OpenRead(archives[1])) {
            var entries=Entries(archive);var selected=entries.Values.Where(e=>Path.GetFileName(e.FullName.Replace('\\','/')).Equals("nvngx_dlssnr.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(selected.Length!=1)throw new IOException("代际运行库压缩包结构不匹配。");
            var file=Extract(selected[0],session,owned,control);
            if(!HashControlled(file,control).Equals(DlssComponents.Hashes[series],StringComparison.OrdinalIgnoreCase))throw new IOException("本机代际运行库 SHA-256 不匹配。");
            files.Add(Mod+"runtime/mods/dlss/rtx"+series+"/nvngx_dlssnr.dll",file);
        }
        var utf8=new UTF8Encoding(false);
        files.Add(Uninstaller,WriteTemp(session,owned,uninstaller));
        files.Add(Mod+"使用说明.txt",WriteTemp(session,owned,utf8.GetBytes("AzureArchive 内录 V1.0 · DLSS 独立组件\r\n本次安装 RTX "+series+" 系运行库及共同组件。内录自动发现此目录，不改已有配置。\r\nDLSS 仍需在内录设置中主动开启；普通内录与增强相互独立。\r\n运行本目录的“卸载DLSS补充MOD.exe”只卸载本组件清单中未修改的文件，不移除内录、配置、剧情或成品视频。其他代际及修改文件不会在修复时被删除。\r\n")));
        files.Add(Mod+"第三方许可.txt",WriteTemp(session,owned,utf8.GetBytes("原始第三方条款见“许可证”目录。DLSS5Tool 自有代码采用 MIT；NVIDIA 与 FFmpeg 组件保留各自原始许可，未重新许可为 MIT。文件来源见组件来源.json。本安装器未宣称 NVIDIA 官方认证或已完成公共再分发许可审核。\r\n")));
        files.Add(Mod+"许可证/FFmpeg-VERSION.txt",WriteTemp(session,owned,utf8.GetBytes("Gyan FFmpeg 7.1.1 full，原始 DLL/EXE 无修改。完整版本、配置及源码信息见 FFmpeg-UPSTREAM-README.txt。\r\n")));
        var ffmpegSource=new{version="7.1.1-full_build-www.gyan.dev",source="https://github.com/FFmpeg/FFmpeg/commit/db69d06eee",license="GPL-3.0-or-later",changes="None",sourceNotes="Original license and build/source provenance retained; not a complete corresponding-source archive."};
        files.Add(Mod+"许可证/FFmpeg-PROVENANCE.json",WriteTemp(session,owned,utf8.GetBytes(InstallCore.Json.Serialize(ffmpegSource))));
        var provenance=new{product=Product,version=Version,series=series,installedAtUtc=DateTime.UtcNow.ToString("o"),assets=assets,nativeHashes=CommonHashes,runtimeSha256=DlssComponents.Hashes[series],changes="None; original upstream native bytes",excluded="GUI, updater, settings/history, Python/Tk duplicate runtime, all other GPU series and NVIDIA system drivers"};
        files.Add(Mod+"组件来源.json",WriteTemp(session,owned,utf8.GetBytes(InstallCore.Json.Serialize(provenance))));
        return files;
    }
    // Tests in this assembly can commit a real verified staged fixture and inject a
    // progress failure/cancellation. Production reaches this only after ZIP validation.
    internal static OnlineDlssResult CommitPrepared(string gameRoot,int series,IDictionary<string,string> prepared,OnlineDlssControl control,Action<OnlineDlssProgress> progress)
    {
        CheckSeries(series);control.Check();var root=InstallCore.FullRoot(gameRoot);InstallCore.ValidateGame(root,true);
        var allowed=Allowed();var expected=new HashSet<string>(allowed,StringComparer.OrdinalIgnoreCase);
        foreach(var other in new[]{30,40,50}.Where(s=>s!=series))expected.Remove(Mod+"runtime/mods/dlss/rtx"+other+"/nvngx_dlssnr.dll");
        if(prepared==null||!expected.SetEquals(prepared.Keys))throw new IOException("临时组件集合不完整或含未知路径。");
        var files=new List<OwnedFile>();
        foreach(var pair in prepared) {
            control.Check();InstallCore.Within(root,pair.Key);InstallCore.NoLinks(pair.Value);
            if(!File.Exists(pair.Value)||new FileInfo(pair.Value).Length<1)throw new IOException("临时组件缺失。");
            files.Add(new OwnedFile{Path=pair.Key,Sha256=HashControlled(pair.Value,control)});
        }
        foreach(var pair in CommonHashes)if(!files.Single(f=>f.Path.Equals(Mod+"runtime/_internal/"+pair.Key,StringComparison.OrdinalIgnoreCase)).Sha256.Equals(pair.Value,StringComparison.OrdinalIgnoreCase))throw new IOException("临时原生组件校验失败。");
        if(!files.Single(f=>f.Path.Equals(Mod+"runtime/mods/dlss/rtx"+series+"/nvngx_dlssnr.dll",StringComparison.OrdinalIgnoreCase)).Sha256.Equals(DlssComponents.Hashes[series],StringComparison.OrdinalIgnoreCase))throw new IOException("临时代际运行库校验失败。");
        var old=ReadReceipt(root,false);
        foreach(var file in files)CheckDestination(root,file.Path,old);
        var merged=old==null?new List<OwnedFile>():old.Files.Where(f=>!files.Any(n=>n.Path.Equals(f.Path,StringComparison.OrdinalIgnoreCase))).ToList();merged.AddRange(files);
        var next=new Receipt{Product=Product,Version=Version,Root=root,Files=merged,LegacyFiles=new List<OwnedFile>(),GeneratedFiles=new List<OwnedFile>()};ValidateReceipt(root,next);
        control.Check();InstallCore.ValidateGame(root,true);
        using(var tx=new InstallCore.Transaction()) {
            int index=0;
            foreach(var file in files) {
                control.Check();Report(progress,"Installing",Path.GetFileName(file.Path),"正在安装组件 "+(++index)+" / "+files.Count);
                control.Check();CheckDestination(root,file.Path,old);var target=InstallCore.Within(root,file.Path);
                tx.Write(target,File.ReadAllBytes(prepared[file.Path]));
                if(!SameHash(target,file.Sha256))throw new IOException("组件写入后校验失败。");
            }
            control.Check();var receiptPath=InstallCore.Within(root,ReceiptName);tx.Write(receiptPath,Encoding.UTF8.GetBytes(InstallCore.Json.Serialize(next)));
            if(!IsInstalled(root,series))throw new IOException("组件安装后的完整性检查未通过。");
            control.Check();tx.Commit();
        }
        Report(progress,"Completed","","DLSS 对应组件安装完成。");return new OnlineDlssResult{Completed=true,AlreadyInstalled=false,Series=series,RuntimeDirectory=InstallCore.Within(root,DlssComponents.ToolRelative)};
    }
    static void CheckDestination(string root,string relative,Receipt old)
    {
        var target=InstallCore.Within(root,relative);if(Directory.Exists(target))throw new IOException("组件位置被文件夹占用："+relative);
        if(!File.Exists(target))return;
        var known=old==null?null:old.Files.FirstOrDefault(f=>f.Path.Equals(relative,StringComparison.OrdinalIgnoreCase));
        if(known==null||!SameHash(target,known.Sha256))throw new IOException("文件未知或曾被修改，修复不会覆盖它："+relative);
    }
    static void CleanupSession(string session,IEnumerable<string> owned)
    {
        foreach(var file in owned.Distinct(StringComparer.OrdinalIgnoreCase))if(File.Exists(file)){InstallCore.NoLinks(file);File.Delete(file);}
        InstallCore.NoLinks(session);if(Directory.Exists(session)&&!Directory.EnumerateFileSystemEntries(session).Any())Directory.Delete(session,false);
    }
    public static List<string> UninstallProduct(string gameRoot)
    {
        var root=InstallCore.FullRoot(gameRoot);InstallCore.ValidateGame(root,true);var receipt=ReadReceipt(root,true);
        var retained=new List<string>();var dirs=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var owned=InstallCore.Within(root,Mod.TrimEnd('/'));
        foreach(var file in receipt.Files)if(Directory.Exists(InstallCore.Within(root,file.Path)))throw new IOException("组件文件位置已变成文件夹，未删除任何文件。");
        using(var tx=new InstallCore.Transaction()) {
            foreach(var file in receipt.Files) {
                var path=InstallCore.Within(root,file.Path);if(!File.Exists(path))continue;
                if(!SameHash(path,file.Sha256)){retained.Add(path);continue;}
                tx.Track(path);File.Delete(path);var dir=Path.GetDirectoryName(path);
                while(dir.StartsWith(owned+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)){dirs.Add(dir);dir=Path.GetDirectoryName(dir);}
            }
            var receiptPath=InstallCore.Within(root,ReceiptName);tx.Track(receiptPath);File.Delete(receiptPath);tx.Commit();
        }
        dirs.Add(owned);foreach(var dir in dirs.OrderByDescending(d=>d.Length)){InstallCore.NoLinks(dir);if(Directory.Exists(dir)&&!Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir,false);}
        return retained;
    }
}

public static class OnlineDlssUninstallerProgram
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool MoveFileEx(string a,string b,int flags);
    static string Self {get{return AppDomain.CurrentDomain.GetData("AARecorder.InstallerHost") as string??Assembly.GetExecutingAssembly().Location;}}
    static string Arg(string[] args,string key){int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:null;}
    [STAThread]public static int Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try {
            if(args.Contains("--cleanup")) {
                var root=InstallCore.FullRoot(Arg(args,"--root"));var original=InstallCore.Within(root,OnlineDlssInstall.Uninstaller);
                var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!Path.GetFullPath(Self).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Regex.IsMatch(Path.GetFileName(Self),"\\AAADLSS-Online-Uninstall-[0-9a-f]{32}\\.exe\\z")||!File.Exists(original)||InstallCore.Hash(Self)!=InstallCore.Hash(original))throw new IOException("卸载程序身份校验失败。");
                int pid;if(!Int32.TryParse(Arg(args,"--parent"),out pid))throw new IOException("卸载调用无效。");
                try{using(var parent=Process.GetProcessById(pid))if(!parent.WaitForExit(10000))throw new IOException("卸载程序尚未退出。");}catch(ArgumentException){}
                var kept=OnlineDlssInstall.UninstallProduct(root);
                if(!args.Contains("--quiet"))MessageBox.Show(kept.Count==0?"DLSS 独立组件已卸载。内录与原有数据保留。":"DLSS 独立组件已卸载；以下修改文件保留：\n"+String.Join("\n",kept),"卸载完成",MessageBoxButtons.OK,MessageBoxIcon.Information);
                MoveFileEx(Self,null,4);return 0;
            }
            var game=Arg(args,"--root")??Directory.GetParent(Directory.GetParent(Path.GetDirectoryName(Self)).FullName).FullName;InstallCore.ValidateGame(game,true);
            if(!args.Contains("--quiet")&&MessageBox.Show("卸载 DLSS 独立组件？\n内录 MOD、配置、剧情与导出视频保留。修改或未知文件不会删除。","卸载 DLSS 组件",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return 2;
            var helper=Path.Combine(Path.GetTempPath(),"AADLSS-Online-Uninstall-"+Guid.NewGuid().ToString("N")+".exe");File.Copy(Self,helper,false);
            Process.Start(new ProcessStartInfo(helper,"--cleanup --root \""+game+"\" --parent "+Process.GetCurrentProcess().Id+(args.Contains("--quiet")?" --quiet":"")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});return 0;
        }catch(Exception error){MessageBox.Show("卸载失败，已停止。\n"+error.Message,"DLSS 组件卸载错误",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
    }
}
