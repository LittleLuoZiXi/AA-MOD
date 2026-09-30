using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AzureArchive.Recorder;

// A job object keeps native GPU workers and encoders owned by this one task.
// Closing AA, cancelling, timing out, or a failed bridge cannot orphan them.
internal sealed class EnhancementProcessTree : IDisposable
{
    IntPtr handle;
    internal EnhancementProcessTree(Process process)
    {
        handle=CreateJobObjectW(IntPtr.Zero,null);
        if(handle==IntPtr.Zero)
        {var error=Marshal.GetLastWin32Error();try{process.Kill(true);}catch{}throw new Win32Exception(error);}
        try
        {
            var limits=new ExtendedLimits();limits.Basic.LimitFlags=0x2000; // KILL_ON_JOB_CLOSE
            if(!SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>()) ||
                !AssignProcessToJobObject(handle,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch {Dispose();try{process.Kill(true);}catch{}throw;}
    }
    public void Dispose()
    {
        var owned=Interlocked.Exchange(ref handle,IntPtr.Zero);
        if(owned!=IntPtr.Zero)CloseHandle(owned);
    }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits
    {
        public long PerProcessUserTimeLimit,PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass,SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters
    {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits
    {
        public BasicLimits Basic;public IoCounters Io;
        public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObjectW(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(IntPtr job,int info,ref ExtendedLimits limits,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
}

internal static class EnhancementMonitor
{
    internal static async Task RunAsync(Process process,string job,TimeSpan timeout,TimeSpan cancelGrace,Action<string,float>? progress=null)
    {
        using var tree=new EnhancementProcessTree(process);
        var directory=Path.GetDirectoryName(job)!;
        using var errors=new FileStream(Path.Combine(directory,"enhancement.log"),FileMode.Create,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
        using var output=new FileStream(Path.Combine(directory,"enhancement-stdout.log"),FileMode.Create,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
        var stderr=process.StandardError.BaseStream.CopyToAsync(errors);
        var stdout=process.StandardOutput.BaseStream.CopyToAsync(output);
        var exited=process.WaitForExitAsync();
        var clock=Stopwatch.StartNew();
        TimeSpan? stoppingAt=null;
        bool timedOut=false,cancelled=false,forced=false;
        try
        {
            while(!exited.IsCompleted)
            {
                if(stoppingAt==null)
                {
                    timedOut=clock.Elapsed>=timeout;
                    cancelled=File.Exists(job+".cancel");
                    if(timedOut || cancelled)
                    {
                        stoppingAt=clock.Elapsed;
                        File.WriteAllText(job+".cancel",timedOut?"timeout":"cancel");
                        progress?.Invoke(timedOut?"增强超过时限，正在结束任务；原始 MP4 已保留。":"正在取消增强；原始 MP4 已保留。",-1);
                    }
                }
                if(stoppingAt!=null && clock.Elapsed-stoppingAt.Value>=cancelGrace)
                {forced=true;tree.Dispose();break;}
                if(stoppingAt==null)ReadProgress(Path.Combine(directory,"enhancement-progress.json"),progress);
                await Task.WhenAny(exited,Task.Delay(200));
            }
            // Includes the case where a bridge exits while a descendant holds a log pipe.
            tree.Dispose();
            if(await Task.WhenAny(exited,Task.Delay(5000))!=exited)
                throw new IOException("增强进程未能退出；原始 MP4 已保留。请查看增强诊断日志。");
            await exited;
            await WaitForLogs(stderr,stdout);
            if(timedOut)throw new TimeoutException("增强超过配置时限，已结束相关进程；原始 MP4 已保留。");
            cancelled|=File.Exists(job+".cancel");
            if(cancelled)throw new OperationCanceledException("增强已取消，原始 MP4 已保留。");
            if(process.ExitCode!=0)throw new IOException("增强失败，原始 MP4 已保留；详情见 enhancement.log。");
            using var result=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"enhancement-result.json")));
            var data=result.RootElement;
            if(!data.TryGetProperty("ok",out var ok) || ok.ValueKind!=JsonValueKind.True ||
                !data.TryGetProperty("upstream",out var upstream) || !upstream.TryGetProperty("status",out var state) || state.GetString()!="complete")
                throw new IOException("增强没有返回完整成功结果；原始 MP4 已保留。");
        }
        finally
        {
            tree.Dispose();
            File.WriteAllText(Path.Combine(directory,"enhancement-process.json"),JsonSerializer.Serialize(new
            {elapsedSeconds=clock.Elapsed.TotalSeconds,timeoutSeconds=timeout.TotalSeconds,cancelGraceSeconds=cancelGrace.TotalSeconds,timedOut,cancelled,forced,
             exitCode=process.HasExited?(int?)process.ExitCode:null}));
        }
    }
    static async Task WaitForLogs(Task stderr,Task stdout)
    {
        var drains=Task.WhenAll(stderr,stdout);
        if(await Task.WhenAny(drains,Task.Delay(5000))!=drains)throw new IOException("增强日志管道未关闭；原始 MP4 已保留。");
        await drains;
    }
    static void ReadProgress(string path,Action<string,float>? progress)
    {
        if(progress==null)return;
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length>65536)return;
            using var data=JsonDocument.Parse(stream);
            if(data.RootElement.TryGetProperty("message",out var message) && message.ValueKind==JsonValueKind.String &&
                data.RootElement.TryGetProperty("fraction",out var fraction) && fraction.ValueKind==JsonValueKind.Number &&
                fraction.TryGetSingle(out var value) && float.IsFinite(value))progress(message.GetString()??"正在增强…",Math.Clamp(value,0,1));
        }
        catch(IOException){}catch(UnauthorizedAccessException){}catch(JsonException){}
    }
}
