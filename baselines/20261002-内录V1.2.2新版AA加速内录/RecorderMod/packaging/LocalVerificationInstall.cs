using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;

// Developer-only harness for installing the exact release payload before AA smoke tests.
// The distributed installer and its confirmation dialogs are tested separately.
public static class LocalVerificationInstall
{
    public static int Main(string[] args)
    {
        try
        {
            if(args.Length==2&&args[0]=="--exe")
            {
                // Exercise the shipped shell's default root detection, using the
                // PID-scoped dialog helpers already verified by the GUI tests.
                var helpers=Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"DependencyTests.exe")).GetType("DependencyTests");
                var flags=BindingFlags.Static|BindingFlags.NonPublic;
                using(var process=Process.Start(new ProcessStartInfo(Path.GetFullPath(args[1]),"--quiet"){UseShellExecute=false,CreateNoWindow=true}))
                {
                    try
                    {
                        Console.WriteLine("Protected installer verification PID: "+process.Id);
                        var dialog=helpers.GetMethod("FindWindow",flags).Invoke(null,new object[]{process,"重复安装确认"});
                        helpers.GetMethod("Click",flags).Invoke(null,new object[]{dialog,"覆盖重新安装"});
                        helpers.GetMethod("ExitWithoutFfmpegPrompt",flags).Invoke(null,new object[]{process,0});
                    }
                    finally {if(!process.HasExited){process.CloseMainWindow();if(!process.WaitForExit(3000))process.Kill();}}
                }
                Console.WriteLine("Protected EXE installed using its own default AA root; existing FFmpeg reused without another prompt.");
                return 0;
            }
            if(args.Length!=2)throw new ArgumentException("Expected game root and release payload ZIP.");
            using(var payload=File.OpenRead(args[1]))
                if(!InstallCore.Install(args[0],payload,delegate{},
                    issues=>{foreach(var issue in issues)Console.WriteLine(issue);return true;},
                    versions=>{Console.WriteLine("Verification update: "+String.Join(", ",versions));return true;},
                    null,true,()=>{Console.WriteLine("Verification approves bundled FFmpeg installation.");return true;}))
                    throw new Exception("Verification install was cancelled.");
            Console.WriteLine("Release payload installed for local verification.");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
