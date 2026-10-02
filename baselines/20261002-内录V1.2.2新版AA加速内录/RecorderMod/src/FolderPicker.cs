using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
namespace AzureArchive.Recorder;
internal static class FolderPicker
{
    internal static string Videos=>Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    internal static Task<string?> ChooseAsync(string initial)
    {
        var completion=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner=Process.GetCurrentProcess().MainWindowHandle;
        var thread=new Thread(()=>
        {
            IFileDialog? dialog=null;IShellItem? folder=null;IShellItem? result=null;
            try
            {
                dialog=(IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"))!)!;
                dialog.SetOptions(0x20|0x40|0x8|0x800);
                dialog.SetTitle("选择本次内录的视频文件夹");dialog.SetOkButtonLabel("选择文件夹");
                var iid=typeof(IShellItem).GUID;
                if(SHCreateItemFromParsingName(initial,IntPtr.Zero,ref iid,out folder)==0)dialog.SetFolder(folder);
                int hr=dialog.Show(owner);
                if(hr==unchecked((int)0x800704C7)){completion.SetResult(null);return;}
                Marshal.ThrowExceptionForHR(hr);dialog.GetResult(out result);result.GetDisplayName(0x80058000,out var path);
                try{completion.SetResult(Marshal.PtrToStringUni(path));}finally{Marshal.FreeCoTaskMem(path);}
            }
            catch(Exception ex){completion.SetException(ex);}
            finally{if(result!=null)Marshal.ReleaseComObject(result);if(folder!=null)Marshal.ReleaseComObject(folder);if(dialog!=null)Marshal.ReleaseComObject(dialog);}
        }){IsBackground=true,Name="AA recorder folder chooser"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=true)]
    static extern int SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellItem item);
    [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr context,ref Guid bhid,ref Guid iid,out IntPtr output);void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind,out IntPtr name);void GetAttributes(uint mask,out uint attributes);void Compare(IShellItem other,uint hint,out int order);
    }
    [ComImport,Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig]int Show(IntPtr owner);
        void SetFileTypes(uint count,IntPtr types);void SetFileTypeIndex(uint index);void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events,out uint cookie);void Unadvise(uint cookie);void SetOptions(uint options);void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);void SetFolder(IShellItem folder);void GetFolder(out IShellItem folder);void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)]string name);void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)]string title);void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)]string label);void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)]string label);
        void GetResult(out IShellItem item);void AddPlace(IShellItem item,int placement);void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)]string extension);
        void Close(int result);void SetClientGuid(ref Guid guid);void ClearClientData();void SetFilter(IntPtr filter);
    }
}
