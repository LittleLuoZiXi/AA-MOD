using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

public sealed class UpdateApplyJob
{
    public string Root,Version,PayloadPath,PayloadSha256,Token;
    public int ParentPid;
    public long ParentStartUtcTicks,PayloadSize;
}
public sealed class UpdateApplyStatus { public string State,Error,Message; public bool InstallationUncertain; }
public sealed class UpdateRestartRequest { public string Token; }

public static class UpdateHelperProgram
{
    public static string ValidateJobLocation(string jobPath,string hostPath)
    {
        jobPath=Path.GetFullPath(jobPath);hostPath=Path.GetFullPath(hostPath);var directory=Path.GetDirectoryName(jobPath);
        var parent=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"AzureArchiveRecorderApply")).TrimEnd(Path.DirectorySeparatorChar);
        Guid id;
        if(Path.GetFileName(jobPath)!="job.json"||!String.Equals(Path.GetDirectoryName(directory),parent,StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(Path.GetFileName(directory),"N",out id)||!String.Equals(hostPath,Path.Combine(directory,InstallCore.UpdateHelperName),StringComparison.OrdinalIgnoreCase))throw new IOException("更新助手只能从指定临时任务目录运行。");
        InstallCore.NoLinks(jobPath);InstallCore.NoLinks(hostPath);return directory;
    }
    public static UpdateApplyJob ReadJob(string directory)
    {
        var file=Path.Combine(directory,"job.json");InstallCore.NoLinks(file);
        if(!File.Exists(file)||new FileInfo(file).Length>16384)throw new IOException("更新任务缺失或过大。");
        var job=InstallCore.Json.Deserialize<UpdateApplyJob>(File.ReadAllText(file));
        if(job==null||job.Version!=InstallCore.Version||job.ParentPid<=0||job.ParentStartUtcTicks<=0||job.PayloadSize<=0||job.PayloadSize>8L*1024*1024*1024||!Regex.IsMatch(job.PayloadSha256??"","^[0-9a-fA-F]{64}$")||!Regex.IsMatch(job.Token??"","^[0-9a-fA-F]{64}$"))throw new IOException("更新任务字段无效。");
        if(String.IsNullOrWhiteSpace(job.Root)||!Path.IsPathRooted(job.Root)||!String.Equals(InstallCore.FullRoot(job.Root),job.Root.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)||String.IsNullOrWhiteSpace(job.PayloadPath)||!String.Equals(Path.GetFullPath(job.PayloadPath),Path.Combine(directory,"payload.zip"),StringComparison.OrdinalIgnoreCase))throw new IOException("更新任务路径无效。");
        job.Root=InstallCore.FullRoot(job.Root);InstallCore.NoLinks(job.PayloadPath);InstallCore.NoLinks(job.Root);return job;
    }
    public static void WriteStatus(string directory,string state,string error,string message,bool uncertain=false)
    {
        var path=Path.Combine(directory,"status.json");InstallCore.NoLinks(path);
        if(error!=null&&error.Length>2000)error=error.Substring(0,2000);
        using(var tx=new InstallCore.Transaction()){tx.Write(path,Encoding.UTF8.GetBytes(InstallCore.Json.Serialize(new UpdateApplyStatus{State=state,Error=error,Message=message,InstallationUncertain=uncertain})));tx.Commit();}
    }
    public static bool HasRestartRequest(string directory,string token,Process parent)
    {
        var path=Path.Combine(directory,"restart.json");InstallCore.NoLinks(path);
        if(!File.Exists(path))return false;
        if(new FileInfo(path).Length>1024)throw new IOException("重启确认文件过大。");
        UpdateRestartRequest request;
        try{request=InstallCore.Json.Deserialize<UpdateRestartRequest>(File.ReadAllText(path));}catch(ArgumentException){return false;}
        if(request==null||!String.Equals(request.Token,token,StringComparison.Ordinal))return false;
        // The OK acknowledgement may be observed after the parent has already exited.
        // Its recorded write must still predate that process's exit.
        if(parent.HasExited&&File.GetLastWriteTimeUtc(path)>parent.ExitTime.ToUniversalTime())return false;
        return true;
    }
    public static bool TryDeleteVerifiedPayload(string directory,UpdateApplyJob job)
    {
        try {
            ValidateJobLocation(Path.Combine(directory,"job.json"),Path.Combine(directory,InstallCore.UpdateHelperName));
            var path=Path.Combine(directory,"payload.zip");
            if(job==null||!String.Equals(Path.GetFullPath(job.PayloadPath),path,StringComparison.OrdinalIgnoreCase))return false;
            InstallCore.NoLinks(path);
            if(!File.Exists(path)||new FileInfo(path).Length!=job.PayloadSize||!String.Equals(InstallCore.Hash(path),job.PayloadSha256,StringComparison.OrdinalIgnoreCase))return false;
            File.Delete(path);return true;
        }catch{return false;} // Cleanup cannot turn a successful install into an error.
    }
    public static ProcessStartInfo CreateRestartStartInfo(string root)
    {
        var start=new ProcessStartInfo(InstallCore.Within(root,"AzureArchive.exe")){UseShellExecute=false,WorkingDirectory=root,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Normal};
        // Doorstop sets this process-local guard after loading BepInEx. A restarted
        // AA must initialize Doorstop afresh; even an empty inherited value disables it.
        // Change only the child's environment, preserving the helper and all other keys.
        start.EnvironmentVariables.Remove("DOORSTOP_DISABLE");
        return start;
    }
    static string StreamHash(Stream stream){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
    [STAThread] public static int Main(string[] args)
    {
        string directory=null;bool applied=false,cleanupAllowed=false;UpdateApplyJob verifiedJob=null;
        try {
            if(args.Length!=2||args[0]!="--job")throw new IOException("更新助手参数无效。");
            var host=AppDomain.CurrentDomain.GetData("AARecorder.InstallerHost") as string;
            if(String.IsNullOrEmpty(host))host=Assembly.GetExecutingAssembly().Location;
            var validatedDirectory=ValidateJobLocation(args[1],host);
            if(File.Exists(Path.Combine(validatedDirectory,"restart.json"))||File.Exists(Path.Combine(validatedDirectory,"status.json")))return 1;
            directory=validatedDirectory;var job=ReadJob(directory);
            // A stale request can never count as a post-completion OK click.
            if(File.Exists(Path.Combine(directory,"restart.json"))||File.Exists(Path.Combine(directory,"status.json")))throw new IOException("更新任务目录已被使用，请重新发起更新。");
            string mutexName;using(var hash=SHA256.Create())mutexName="Local\\AARecorderUpdate-"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(job.Root.ToUpperInvariant()))).Replace("-","");
            using(var mutex=new Mutex(false,mutexName)) {
                bool owns=false;
                try {
                    try{owns=mutex.WaitOne(0);}catch(AbandonedMutexException){owns=true;}
                    if(!owns)throw new IOException("此 AA 正在处理其他内录 MOD 更新。");
                    using(var parent=InstallCore.ValidateUpdateParent(job.Root,job.ParentPid,job.ParentStartUtcTicks))
                    using(var payload=new FileStream(job.PayloadPath,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                        if(payload.Length!=job.PayloadSize||!String.Equals(StreamHash(payload),job.PayloadSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("更新包大小或 SHA-256 不匹配。");
                        payload.Position=0;
                        using(var archive=new ZipArchive(payload,ZipArchiveMode.Read,true)) {
                            var next=InstallCore.ValidateUpdateArchive(job.Root,archive);
                            var own=next.Files.Single(f=>f.Path==InstallCore.Mod+job.Version+"/"+InstallCore.UpdateHelperName);
                            if(!String.Equals(InstallCore.Hash(host),own.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("更新助手与已验证更新包不匹配。");
                        }
                        verifiedJob=job;payload.Position=0;WriteStatus(directory,"Applying",null,"正在替换内录 MOD 文件…");
                        InstallCore.ApplyUpdate(job.Root,payload,job.ParentPid,job.ParentStartUtcTicks,delegate(string message){WriteStatus(directory,"Applying",null,message);});
                        applied=true;cleanupAllowed=true;WriteStatus(directory,"Completed",null,"内录MOD更新完成，AA即将重启");
                        var timer=Stopwatch.StartNew();bool acknowledged=false;
                        while(timer.Elapsed.TotalMinutes<10) {
                            if(HasRestartRequest(directory,job.Token,parent)){acknowledged=true;break;}
                            if(parent.HasExited)return 0;
                            Thread.Sleep(200);
                        }
                        if(!acknowledged)return 0;
                        while(!parent.WaitForExit(200)&&timer.Elapsed.TotalMinutes<10){}
                        if(!parent.HasExited)return 0;
                        if(parent.ExitCode!=0)throw new IOException("AA 未正常退出，自动重启已停止，请手动启动 AA。");
                        InstallCore.ValidateGame(job.Root,true);
                        WriteStatus(directory,"Restarting",null,"正在重新启动 AA…");
                        var start=CreateRestartStartInfo(job.Root);
                        using(var restarted=Process.Start(start)){if(restarted==null)throw new IOException("AA 未能启动，请手动启动。");}
                    }
                }finally{if(owns)mutex.ReleaseMutex();}
            }
            return 0;
        }catch(Exception ex) {
            cleanupAllowed=verifiedJob!=null&&!applied&&!(ex is InstallRollbackException);
            if(directory!=null)try{WriteStatus(directory,"Failed",ex.Message,applied?"内录 MOD 已更新，但重启未完成，请手动启动 AA。":"内录 MOD 更新失败，已停止安装。",applied || ex is InstallRollbackException);}catch{cleanupAllowed=false;}
            return 1;
        }finally{if(cleanupAllowed&&verifiedJob!=null)TryDeleteVerifiedPayload(directory,verifiedJob);}
    }
}