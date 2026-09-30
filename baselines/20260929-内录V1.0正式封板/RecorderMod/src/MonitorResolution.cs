using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AzureArchive.Recorder;

internal sealed class MonitorResolution
{
    public int Width { get; }
    public int Height { get; }
    public string Device { get; }
    public string Source { get; }
    internal int EncodedWidth=>Width&~1;
    internal int EncodedHeight=>Height&~1;
    internal MonitorResolution(int width,int height,string device,string source)
    {
        if(width<2 || height<2) throw new InvalidOperationException("无法检测显示器分辨率，录制已取消。");
        Width=width;Height=height;Device=device;Source=source;
    }
    internal static MonitorResolution Detect(int fallbackWidth,int fallbackHeight)
    {
        // EnumDisplaySettings returns physical display pixels, unaffected by
        // Windows 125%/150%/200% text scaling or the Unity window's client size.
        var handle=Process.GetCurrentProcess().MainWindowHandle;
        var monitor=MonitorFromWindow(handle,2 /* nearest monitor */);
        var info=new MonitorInfo { Size=Marshal.SizeOf<MonitorInfo>() };
        if(monitor!=IntPtr.Zero && GetMonitorInfo(monitor,ref info))
        {
            var mode=Marshal.AllocHGlobal(220);
            try
            {
                Marshal.Copy(new byte[220],0,mode,220);
                Marshal.WriteInt16(mode,68,220); // DEVMODEW.dmSize
                if(EnumDisplaySettings(info.Device,-1,mode))
                    return new MonitorResolution(Marshal.ReadInt32(mode,172),Marshal.ReadInt32(mode,176),info.Device,"EnumDisplaySettingsW");
            }
            finally { Marshal.FreeHGlobal(mode); }
        }
        return new MonitorResolution(fallbackWidth,fallbackHeight,"Unity main display","Display.systemWidth/systemHeight");
    }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct MonitorInfo
    {
        public int Size;
        public Rect Monitor,Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
    }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW",CharSet=CharSet.Unicode)]
    [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll",EntryPoint="EnumDisplaySettingsW",CharSet=CharSet.Unicode)]
    [return:MarshalAs(UnmanagedType.Bool)] static extern bool EnumDisplaySettings(string device,int mode,IntPtr devMode);
}
