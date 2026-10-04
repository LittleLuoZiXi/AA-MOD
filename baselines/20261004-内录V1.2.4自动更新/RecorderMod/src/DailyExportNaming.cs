using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace AzureArchive.Recorder;

public sealed class SavedExport
{
    public string OriginalPath { get; }
    public string FinalPath { get; }
    public SavedExport(string originalPath,string finalPath){OriginalPath=originalPath;FinalPath=finalPath;}
}

// One sequence per local day and MOD profile, shared across chosen folders and
// AA instances. Publishing never replaces an existing user video.
public static class DailyExportNaming
{
    public static SavedExport Begin(string source,string folder,string stateDirectory,bool enhanced,DateTime? localTime=null)
    {
        if(!File.Exists(source))throw new FileNotFoundException("找不到待保存的视频。",source);
        Directory.CreateDirectory(folder);Directory.CreateDirectory(stateDirectory);
        using var guard=Lock(Path.Combine(stateDirectory,"export-sequence.lock"));
        var state=Path.Combine(stateDirectory,"export-sequence.json");
        var counters=File.Exists(state)?JsonSerializer.Deserialize<Dictionary<string,int>>(File.ReadAllText(state))
            ??throw new IOException("导出序号记录为空。") :new Dictionary<string,int>();
        var day=(localTime??DateTime.Now).ToString("yyyyMMdd",CultureInfo.InvariantCulture);
        counters.TryGetValue(day,out var number);
        if(number<0)throw new IOException("导出序号记录无效。");
        string final,original;
        do
        {
            number=checked(number+1);
            var name=day+"-"+number.ToString(CultureInfo.InvariantCulture)+".mp4";
            final=Path.Combine(folder,name);
            original=enhanced?Path.Combine(folder,"原始视频",name):final;
        }while(File.Exists(final) || Directory.Exists(final) || File.Exists(original) || Directory.Exists(original));
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var staging=original+"."+Guid.NewGuid().ToString("N")+".partial";
        var stateTemp=state+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.Copy(source,staging,false);
            counters[day]=number;File.WriteAllText(stateTemp,JsonSerializer.Serialize(counters));
            File.Move(staging,original,false);
            File.Move(stateTemp,state,true);
            return new SavedExport(original,final);
        }
        finally {if(File.Exists(staging))File.Delete(staging);if(File.Exists(stateTemp))File.Delete(stateTemp);}
    }
    public static string FinishEnhancement(string source,SavedExport export)
    {
        if(export.OriginalPath==export.FinalPath)throw new InvalidOperationException("此导出未预留增强成片。");
        var staging=export.FinalPath+"."+Guid.NewGuid().ToString("N")+".partial";
        try {File.Copy(source,staging,false);File.Move(staging,export.FinalPath,false);return export.FinalPath;}
        finally {if(File.Exists(staging))File.Delete(staging);}
    }
    static FileStream Lock(string path)
    {
        var watch=Stopwatch.StartNew();
        while(true)
        {
            try{return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException)when(watch.Elapsed.TotalSeconds<10){Thread.Sleep(40);}
        }
    }
}
