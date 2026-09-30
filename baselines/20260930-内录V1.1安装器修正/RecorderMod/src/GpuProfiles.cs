using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Net.Http;
using System.IO.Compression;

namespace AzureArchive.Recorder;

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public uint VendorId { get; set; }
    public ulong Memory { get; set; }
    public int Series => GpuProfiles.Classify(Name, VendorId);
}

public static class GpuProfiles
{
    public static readonly Dictionary<int,string> Hashes = new()
    {
        [30] = "6eb209e764f39872625debd6abaf45e2bb6322f6f270f781f70c059ae30b3927",
        [40] = "ceb6432f6fbdf44d886014bcd47241932bf8b67439feef9bbdd0961436662650",
        [50] = "e16bcf15e16e13f527491cdf7845b2fe6521a738d8f7c9c721866a8496e1fc8e"
    };
    public static int Classify(string name, uint vendor)
    {
        if (vendor != 0x10DE) return 0;
        var m = Regex.Match(name, @"\bRTX\s*(20|30|40|50)\d{2}(?:\s?D)?\b", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }
    public static string ResolveRuntime(string tool, string profiles, int series)
    {
        if (series == 20) throw new InvalidOperationException("已识别 RTX 20 系，但上游 zip Release 未提供 20 系 DLSS5 运行库；此配置禁用 DLSS5，普通录制可用。");
        if (!Hashes.TryGetValue(series, out var expected)) throw new InvalidOperationException("此显卡型号暂不支持增强，普通 MP4 录制可用。");
        foreach (var path in new[] { Path.Combine(profiles, "rtx"+series, "nvngx_dlssnr.dll"),
            Path.Combine(tool, "mods", "dlss", "rtx"+series, "nvngx_dlssnr.dll"),
            Path.Combine(tool, "mods", "nvngx_dlssnr.dll"), Path.Combine(tool,"_internal","nvngx_dlssnr.dll") })
        {
            if (!File.Exists(path)) continue;
            using var file = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(file));
            if (hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) return path;
        }
        throw new FileNotFoundException($"RTX {series} 系对应运行库缺失或哈希不匹配，请运行 RecorderMod/install-gpu-runtime.ps1 -Series {series}。");
    }

    public static async Task<string> EnsureRuntimeAsync(string tool, string profiles, int series)
    {
        try { return ResolveRuntime(tool, profiles, series); }
        catch(FileNotFoundException) { }
        var (asset, zipHash) = series switch
        {
            30 => ("30.-310.8.SF-v2.zip", "01626f7ffe14c54928e9b2eaa09baf1886fa9200b247bbb51895f935f301886c"),
            40 => ("40.zip", "3fdeb4f3b44165bfd31d98e288a46dc16eedd40168819c8ed6f73c45fc92c7a1"),
            50 => ("50.zip", "e730e1ea95b0a4f6420b9b1bbb1c2948cbb1bc9241aebfdeb10efef8a3bc906a"),
            _ => throw new InvalidOperationException("此显卡暂不支持。")
        };
        var dest=Path.Combine(profiles,"rtx"+series);
        Directory.CreateDirectory(dest);
        var final=Path.Combine(dest,"nvngx_dlssnr.dll");
        if(File.Exists(final)) throw new IOException("已有对应系列 DLL 校验失败，未覆盖："+final);
        var zip=Path.Combine(dest,Guid.NewGuid().ToString("N")+".zip.partial");
        var extracted=Path.Combine(dest,Guid.NewGuid().ToString("N")+".dll.partial");
        using var client=new HttpClient { Timeout=TimeSpan.FromMinutes(15) };
        using(var source=await client.GetStreamAsync("https://github.com/banbanzhige/DLSS5Tool/releases/download/zip/"+Uri.EscapeDataString(asset)))
        using(var target=new FileStream(zip,FileMode.CreateNew,FileAccess.Write,FileShare.None)) await source.CopyToAsync(target);
        static string Hash(string path) { using var s=File.OpenRead(path); using var h=SHA256.Create(); return Convert.ToHexString(h.ComputeHash(s)); }
        if(!Hash(zip).Equals(zipHash,StringComparison.OrdinalIgnoreCase)) throw new IOException("下载的 GPU 运行库压缩包校验失败。");
        using(var archive=ZipFile.OpenRead(zip))
        {
            var entries=archive.Entries.Where(x=>Path.GetFileName(x.FullName).Equals("nvngx_dlssnr.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(entries.Length!=1) throw new IOException("GPU 运行库压缩包格式不符。");
            entries[0].ExtractToFile(extracted,false);
        }
        if(!Hash(extracted).Equals(Hashes[series],StringComparison.OrdinalIgnoreCase)) throw new IOException("GPU 运行库 DLL 校验失败。");
        File.Move(extracted,final,false); File.Delete(zip);
        return final;
    }

    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct Desc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Name;
        public uint Vendor, Device, Subsystem, Revision;
        public UIntPtr VideoMemory, SystemMemory, SharedMemory;
        public uint LuidLow; public int LuidHigh; public uint Flags;
    }
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid id, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc(IntPtr self, out Desc desc);
    static T Method<T>(IntPtr obj, int slot) where T: Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));
    public static List<GpuInfo> Detect()
    {
        var result = new List<GpuInfo>();
        var id = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref id, out var factory));
        try
        {
            var enumerate = Method<EnumAdapters>(factory,12);
            for (uint i=0; i<32; i++)
            {
                int hr = enumerate(factory,i,out var adapter);
                if (hr == unchecked((int)0x887A0002)) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetDesc>(adapter,10)(adapter,out var d));
                    if ((d.Flags & 2) == 0) result.Add(new GpuInfo { Name=d.Name.Trim(),VendorId=d.Vendor,Memory=d.VideoMemory.ToUInt64() });
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }
    public static GpuInfo Select(List<GpuInfo> devices)
    {
        var nvidia = devices.Where(x=>x.VendorId==0x10DE).ToArray();
        if (nvidia.Length == 0) throw new InvalidOperationException("非 NVIDIA 显卡暂不支持增强；普通 MP4 录制可用。");
        // Upstream VSR and DLSSG have their own adapter selection. Do not select
        // a DLL for one generation then silently run it on a different generation.
        if (nvidia.Select(x=>x.Series).Distinct().Count()>1)
            throw new InvalidOperationException("检测到不同系列的多张 NVIDIA 显卡，上游超分/补帧不统一支持选卡；请先禁用其他 NVIDIA 适配器后再增强。");
        return nvidia.OrderByDescending(x=>x.Memory).First();
    }
}
