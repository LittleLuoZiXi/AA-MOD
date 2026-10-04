using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AzureArchive.Recorder;

public static class DailyExportNamingTests
{
    public static void Run(string root)
    {
        Directory.CreateDirectory(root);
        var input=Path.Combine(root,"input.mp4");File.WriteAllText(input,"original test video bytes");
        var enhanced=Path.Combine(root,"enhanced.mp4");File.WriteAllText(enhanced,"enhanced test video bytes");
        var folder=Path.Combine(root,"输出一");var other=Path.Combine(root,"输出二");var state=Path.Combine(root,"state");
        var day=new DateTime(2000,1,1);
        void Check(bool passed,string message){if(!passed)throw new Exception(message);Console.WriteLine("PASS: "+message);}
        var first=DailyExportNaming.Begin(input,folder,state,false,day);
        Check(Path.GetFileName(first.FinalPath)=="20000101-1.mp4","First export uses date-1.mp4");
        Check(Path.GetFileName(DailyExportNaming.Begin(input,folder,state,false,day).FinalPath)=="20000101-2.mp4","Same-day sequence increases");
        Check(Path.GetFileName(DailyExportNaming.Begin(input,other,state,false,day).FinalPath)=="20000101-3.mp4","Sequence persists across output folders");
        Check(Path.GetFileName(DailyExportNaming.Begin(input,folder,state,false,day.AddDays(1)).FinalPath)=="20000102-1.mp4","New local day starts at 1");
        var existing=Path.Combine(folder,"20000102-2.mp4");File.WriteAllText(existing,"keep user video");
        Check(Path.GetFileName(DailyExportNaming.Begin(input,folder,state,false,day.AddDays(1)).FinalPath)=="20000102-3.mp4" && File.ReadAllText(existing)=="keep user video","Existing video is preserved and its number skipped");
        var withEnhancement=DailyExportNaming.Begin(input,folder,state,true,day.AddDays(2));
        Check(Path.GetFileName(withEnhancement.FinalPath)=="20000103-1.mp4" && Path.GetFileName(Path.GetDirectoryName(withEnhancement.OriginalPath))=="原始视频" && !File.Exists(withEnhancement.FinalPath),"Enhanced export preserves named original separately");
        DailyExportNaming.FinishEnhancement(enhanced,withEnhancement);
        Check(File.ReadAllText(withEnhancement.OriginalPath)==File.ReadAllText(input) && File.ReadAllText(withEnhancement.FinalPath)==File.ReadAllText(enhanced),"Final enhanced video and original retain separate exact bytes");
        bool collisionRejected=false;
        try{DailyExportNaming.FinishEnhancement(input,withEnhancement);}catch(IOException){collisionRejected=true;}
        Check(collisionRejected && File.ReadAllText(withEnhancement.FinalPath)==File.ReadAllText(enhanced),"Late destination collision never overwrites a video");
        var jobs=Enumerable.Range(0,8).Select(_=>Task.Run(()=>DailyExportNaming.Begin(input,folder,state,false,day).FinalPath)).ToArray();
        Task.WaitAll(jobs);
        Check(jobs.Select(t=>t.Result).Distinct(StringComparer.OrdinalIgnoreCase).Count()==8,"Concurrent exports allocate unique names");
        var counterBefore=File.ReadAllText(Path.Combine(state,"export-sequence.json"));
        try{DailyExportNaming.Begin(Path.Combine(root,"absent.mp4"),folder,state,false,day);throw new Exception("Missing source accepted");}catch(FileNotFoundException){}
        Check(File.ReadAllText(Path.Combine(state,"export-sequence.json"))==counterBefore,"Missing source does not consume a sequence");
        Check(!Directory.GetFiles(root,"*.partial",SearchOption.AllDirectories).Any() && !Directory.GetFiles(root,"*.tmp",SearchOption.AllDirectories).Any(),"Successful and failed publication leave no partial files");
        Check(File.ReadAllText(first.FinalPath)==File.ReadAllText(input),"Earlier exports remain unchanged");
    }
}
