using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AzureArchive.Recorder;

public static class EnhancementMonitorTests
{
    const string Helper=@"
param([string]$Mode,[string]$Job)
$ErrorActionPreference='Stop'
$directory=Split-Path -Parent $Job
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
if($Mode -eq 'child'){while($true){Start-Sleep -Milliseconds 100}}
if($Mode -in @('cancel','timeout','orphan')){
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden'
    foreach($arg in @('-NoProfile','-File',$PSCommandPath,'child',$Job)){$start.ArgumentList.Add($arg)}
    $child=[Diagnostics.Process]::Start($start)
    [IO.File]::WriteAllText((Join-Path $directory 'child.pid'),$child.Id.ToString())
    if($Mode -ne 'orphan'){while($true){Start-Sleep -Milliseconds 100}}
}
if($Mode -eq 'invalid'){exit 0}
for($i=0;$i -lt 3000;$i++){[Console]::Error.WriteLine('增强日志中文-'+$i);[Console]::Out.WriteLine('worker output '+$i)}
[IO.File]::WriteAllText((Join-Path $directory 'enhancement-result.json'),'{""ok"":true,""upstream"":{""status"":""complete""}}')
exit 0
";
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static bool Alive(int id){try{using var process=Process.GetProcessById(id);return !process.HasExited;}catch(ArgumentException){return false;}}
    public static async Task Run(string powershell,string directory)
    {
        Directory.CreateDirectory(directory);
        var helper=Path.Combine(directory,"fixture.ps1");File.WriteAllText(helper,Helper);
        foreach(var mode in new[]{"normal","cancel","timeout","orphan","invalid"})
        {
            var root=Path.Combine(directory,mode);Directory.CreateDirectory(root);
            var job=Path.Combine(root,"enhancement-job.json");File.WriteAllText(job,"{}");
            var original=Path.Combine(root,"original.mp4");File.WriteAllText(original,"ORIGINAL VIDEO SENTINEL");
            var start=new ProcessStartInfo(powershell){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardInput=true,RedirectStandardError=true,RedirectStandardOutput=true};
            foreach(var arg in new[]{"-NoProfile","-File",helper,mode,job})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)!;process.StandardInput.Close();
            var clock=Stopwatch.StartNew();
            var monitor=EnhancementMonitor.RunAsync(process,job,TimeSpan.FromSeconds(mode=="timeout"?2:15),TimeSpan.FromMilliseconds(300));
            if(mode=="cancel")
            {
                while(!File.Exists(Path.Combine(root,"child.pid")) && clock.Elapsed.TotalSeconds<5 && !monitor.IsCompleted)await Task.Delay(20);
                Check(File.Exists(Path.Combine(root,"child.pid")),"Cancel fixture did not start its descendant.");
                File.WriteAllText(job+".cancel","cancel");
            }
            Exception? failure=null;try{await monitor;}catch(Exception error){failure=error;}
            if(mode=="cancel")Check(failure is OperationCanceledException,"Cancellation was not classified correctly: "+failure);
            else if(mode=="timeout")Check(failure is TimeoutException,"Timeout was not classified correctly: "+failure);
            else if(mode=="invalid")Check(failure is IOException,"Exit zero without result was accepted: "+failure);
            else Check(failure==null,"Unexpected failure: "+failure);
            Check(File.ReadAllText(original)=="ORIGINAL VIDEO SENTINEL","Original video was changed.");
            if(mode is "cancel" or "timeout" or "orphan")
            {
                var id=int.Parse(File.ReadAllText(Path.Combine(root,"child.pid")));
                for(int i=0;i<30 && Alive(id);i++)await Task.Delay(50);
                Check(!Alive(id),"Descendant survived "+mode);
            }
            if(mode is "normal" or "orphan")
            {
                Check(File.ReadAllText(Path.Combine(root,"enhancement.log")).Contains("增强日志中文-2999"),"stderr truncated or wrong encoding");
                Check(File.ReadAllText(Path.Combine(root,"enhancement-stdout.log")).Contains("worker output 2999"),"stdout pipe was not drained");
            }
            using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"enhancement-process.json")));
            if(mode is "cancel" or "timeout")Check(report.RootElement.GetProperty("forced").GetBoolean(),"Stuck process was not forced to exit.");
            Console.WriteLine("PASS: "+mode+"; original retained; logs drained; process tree contained ("+clock.Elapsed.TotalSeconds.ToString("F2")+" s)");
        }
    }
}
