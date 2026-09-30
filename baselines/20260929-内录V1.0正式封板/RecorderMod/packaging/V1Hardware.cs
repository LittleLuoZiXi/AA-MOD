using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

public sealed class V1GpuDevice
{
    public string Name;
    public uint VendorId;
    public ulong Memory;
}

public sealed class V1GpuStatus
{
    public string Name, Reason;
    public int Series;
    public bool Supported, RejectDlssStage;
}

public static class V1Hardware
{
    public static int Classify(string name,uint vendor)
    {
        if(vendor!=0x10DE)return 0;
        var match=Regex.Match(name??"",@"\bRTX\s*(20|30|40|50)\d{2}(?:\s?D)?\b",RegexOptions.IgnoreCase);
        return match.Success?Int32.Parse(match.Groups[1].Value):-1;
    }
    public static V1GpuStatus FromDevices(IEnumerable<V1GpuDevice> devices)
    {
        var all=devices.ToArray();
        var cards=all.Where(x=>x.VendorId==0x10DE).ToArray();
        var result=new V1GpuStatus{Name=String.Join(" / ",all.Select(x=>x.Name).Distinct()),Reason=""};
        if(cards.Length==0){result.Reason="非 NVIDIA 显卡，DLSS功能不可用；普通内录可安装。";return result;}
        var series=cards.Select(x=>Classify(x.Name,x.VendorId)).Distinct().ToArray();
        if(series.Length!=1){result.Reason="检测到不同系列的 NVIDIA 显卡，无法安全自动选择DLSS组件；普通内录可安装。";return result;}
        result.Series=series[0];
        if(result.Series==20){result.RejectDlssStage=true;result.Reason="暂时不支持20系";return result;}
        if(!new[]{30,40,50}.Contains(result.Series)){result.Reason="此 NVIDIA 显卡型号暂不支持DLSS增强；普通内录可安装。";return result;}
        result.Supported=true;
        result.Name=String.Join(" / ",cards.Select(x=>x.Name).Distinct());
        return result;
    }

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Name;
        public uint Vendor,Device,Subsystem,Revision;
        public UIntPtr VideoMemory,SystemMemory,SharedMemory;
        public uint LuidLow;public int LuidHigh;public uint Flags;
    }
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid id,out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters(IntPtr self,uint index,out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDescription(IntPtr self,out Description description);
    public static V1GpuStatus Detect()
    {
        try {
            var devices=new List<V1GpuDevice>();var iid=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");IntPtr factory;
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid,out factory));
            try {
                var enumerate=(EnumAdapters)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory),12*IntPtr.Size),typeof(EnumAdapters));
                for(uint i=0;i<32;i++){
                    IntPtr adapter;var code=enumerate(factory,i,out adapter);
                    if(code==unchecked((int)0x887A0002))break;
                    Marshal.ThrowExceptionForHR(code);
                    try {
                        var describe=(GetDescription)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter),10*IntPtr.Size),typeof(GetDescription));
                        Description value;Marshal.ThrowExceptionForHR(describe(adapter,out value));
                        if((value.Flags&2)==0)devices.Add(new V1GpuDevice{Name=value.Name.Trim(),VendorId=value.Vendor,Memory=value.VideoMemory.ToUInt64()});
                    } finally {Marshal.Release(adapter);}
                }
            } finally {Marshal.Release(factory);}
            return FromDevices(devices);
        } catch(Exception ex) {
            return new V1GpuStatus{Name="显卡检测未完成",Reason="无法读取显卡信息，DLSS功能不可用；普通内录可安装。\r\n"+ex.Message};
        }
    }
}
