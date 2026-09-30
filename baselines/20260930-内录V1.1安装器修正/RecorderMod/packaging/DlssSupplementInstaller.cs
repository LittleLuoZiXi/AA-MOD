using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using AzureArchive.Recorder;

// This installer deliberately has no entry point into InstallCore.Install/Uninstall.
// The shared core supplies only path checks, host validation, hashes and rollback.
public static class DlssSupplementCore
{
    public const string Product="AzureArchiveDLSSSupplement", Version="1.0.0";
    public const string Mod="mods/AzureArchiveDLSS/";
    public const string ReceiptName=Mod+"installed-files.json";
    public const string Uninstaller=Mod+"卸载DLSS补充MOD.exe";
    public static readonly string[] LicenseFiles={
        "DLSS5Tool-MIT.txt", "DLSS5Tool-THIRD_PARTY_NOTICES.md", "NVIDIA-DLSS-SDK-LICENSE.txt",
        "NVIDIA-RTX-Video-SDK-LICENSE.pdf", "NVIDIA-Optical-Flow-Headers-LICENSE.txt", "RTX40MFG-Unlock-LICENSE.txt",
        "FFmpeg-COPYING.GPLv3.txt", "FFmpeg-UPSTREAM-README.txt", "FFmpeg-VERSION.txt",
        "FFmpeg-BUILD-CONFIG.txt", "FFmpeg-PROVENANCE.json"
    };
    public static readonly Dictionary<string,string> CommonHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){
        {"ffmpeg.exe","B1383F5D07470D503EDECDAEE4BDDC5891E986E916A698299B357F79CFE445FD"},
        {"ffprobe.exe","012BDDDED3CBC5204055210D7FF4F0B3F7521BCA441A694939856D01909F5756"},
        {"vsr_host.dll","5E66DDC8B4C56F2B77DAED782CC8429B607D32BBA358EFB488FBD4C03AE515B7"},
        {"nvngx_vsr.dll","C3D88EEA5FF7A548EDEFA66414CF6E77464D0947277C904F324DD23ABF58A1ED"},
        {"dlssg_video_worker.exe","7E6C281608BE2E8A6D63A574A3EAE8621C6C38635260E150571EF20F422C4659"},
        {"nvngx_dlssg.dll","135EAF0733C1E37381A8C28ABCF7A862404A54132B81787C04E35D09EFC5E36F"},
        {"dlssnr_host_v2.dll","C8AD631F8F78B2DEDEC6AEC418C7A570D6FC5BF1B9FFA9F3514BCB0EDD58FC13"}
    };
    public static HashSet<string> AllowedPaths()
    {
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Uninstaller,Mod+"使用说明.txt",Mod+"第三方许可.txt",Mod+"组件来源.json"};
        foreach(var name in DlssComponents.RequiredFiles)paths.Add(Mod+"runtime/_internal/"+name);
        paths.Add(Mod+"runtime/_internal/locales/zh_CN.json");paths.Add(Mod+"runtime/_internal/locales/en_US.json");
        foreach(var series in new[]{30,40,50})paths.Add(Mod+"runtime/mods/dlss/rtx"+series+"/nvngx_dlssnr.dll");
        foreach(var name in LicenseFiles)paths.Add(Mod+"许可证/"+name);
        return paths;
    }
    public static void ValidateReceipt(string root,Receipt receipt)
    {
        if(receipt==null||receipt.Product!=Product||receipt.Version!=Version||String.IsNullOrWhiteSpace(receipt.Root)
            ||!String.Equals(InstallCore.FullRoot(receipt.Root),root,StringComparison.OrdinalIgnoreCase)||receipt.Files==null)
            throw new IOException("DLSS 补充包清单身份不匹配，已停止。");
        if((receipt.LegacyFiles!=null&&receipt.LegacyFiles.Count!=0)||(receipt.GeneratedFiles!=null&&receipt.GeneratedFiles.Count!=0))
            throw new IOException("DLSS 补充包清单包含不允许的扩展项目。");
        var allowed=AllowedPaths();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in receipt.Files) {
            if(file==null||file.Path==null||!allowed.Contains(file.Path)||!seen.Add(file.Path)
                ||file.Sha256==null||!Regex.IsMatch(file.Sha256,"\\A[0-9A-Fa-f]{64}\\z"))
                throw new IOException("DLSS 补充包清单包含越界、重复或无效项目。");
            InstallCore.Within(root,file.Path);
        }
        if(!seen.SetEquals(allowed))throw new IOException("DLSS 补充包清单不完整。");
        foreach(var item in CommonHashes)RequireHash(receipt,Mod+"runtime/_internal/"+item.Key,item.Value);
        foreach(var item in DlssComponents.Hashes)RequireHash(receipt,Mod+"runtime/mods/dlss/rtx"+item.Key+"/nvngx_dlssnr.dll",item.Value);
    }
    static void RequireHash(Receipt receipt,string path,string expected)
    {
        var file=receipt.Files.Single(f=>String.Equals(f.Path,path,StringComparison.OrdinalIgnoreCase));
        if(!String.Equals(file.Sha256,expected,StringComparison.OrdinalIgnoreCase))throw new IOException("固定组件 SHA-256 不匹配："+path);
    }
    static Receipt ReadReceipt(string root)
    {
        var path=InstallCore.Within(root,ReceiptName);if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>1024*1024)throw new IOException("DLSS 补充包清单大小异常。");
        var receipt=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(path));ValidateReceipt(root,receipt);return receipt;
    }
    public static bool Install(string root,Stream payload,Action<string> progress,Func<bool> confirmReinstall=null)
    {
        root=InstallCore.FullRoot(root);InstallCore.ValidateGame(root,true);
        if(payload==null)throw new IOException("DLSS 补充安装负载缺失。");
        var old=ReadReceipt(root);var receiptPath=InstallCore.Within(root,ReceiptName);
        if(old!=null&&(confirmReinstall==null||!confirmReinstall()))return false;
        using(var archive=new ZipArchive(payload,ZipArchiveMode.Read,true)) {
            var manifest=archive.GetEntry("payload-manifest.json");
            if(manifest==null||manifest.Length>1024*1024)throw new IOException("DLSS 补充安装清单缺失或大小异常。");
            Receipt next;using(var reader=new StreamReader(manifest.Open(),Encoding.UTF8))next=InstallCore.Json.Deserialize<Receipt>(reader.ReadToEnd());
            if(next==null||!String.IsNullOrEmpty(next.Root))throw new IOException("安装负载清单根目录无效。");
            next.Root=root;ValidateReceipt(root,next);
            var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in archive.Entries) {
                if(entries.ContainsKey(entry.FullName))throw new IOException("DLSS 补充安装负载含重复路径。");
                entries.Add(entry.FullName,entry);
            }
            if(entries.Count!=next.Files.Count+1)throw new IOException("DLSS 补充安装负载文件数不符。");
            progress("正在校验全部组件；尚未写入安装文件…");
            foreach(var file in next.Files) {
                ZipArchiveEntry entry;if(!entries.TryGetValue(file.Path,out entry)||entry.Length<1||entry.Length>512L*1024*1024)
                    throw new IOException("DLSS 补充安装负载缺失或大小异常："+file.Path);
                using(var input=entry.Open())using(var hash=SHA256.Create())
                    if(!BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))
                        throw new IOException("安装负载 SHA-256 校验失败："+file.Path);
                var target=InstallCore.Within(root,file.Path);
                if(Directory.Exists(target))throw new IOException("组件目标被同名文件夹占用："+target);
                if(File.Exists(target)) {
                    var previous=old==null?null:old.Files.FirstOrDefault(f=>String.Equals(f.Path,file.Path,StringComparison.OrdinalIgnoreCase));
                    if(previous==null||!InstallCore.Hash(target).Equals(previous.Sha256,StringComparison.OrdinalIgnoreCase))
                        throw new IOException("目标文件未知或曾被修改，未覆盖："+target);
                }
            }
            InstallCore.ValidateGame(root,true);
            using(var tx=new InstallCore.Transaction()) {
                int index=0;
                foreach(var file in next.Files) {
                    progress("正在安装 DLSS 补充组件 "+(++index)+" / "+next.Files.Count);
                    var target=InstallCore.Within(root,file.Path);
                    // Recheck immediately before writing, including after a confirmation dialog.
                    if(File.Exists(target)) {
                        var previous=old==null?null:old.Files.FirstOrDefault(f=>String.Equals(f.Path,file.Path,StringComparison.OrdinalIgnoreCase));
                        if(previous==null||!InstallCore.Hash(target).Equals(previous.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("安装期间目标发生变化，已停止："+target);
                    }
                    using(var input=entries[file.Path].Open())using(var memory=new MemoryStream()) {input.CopyTo(memory);tx.Write(target,memory.ToArray());}
                    if(!InstallCore.Hash(target).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("写入后校验失败："+target);
                }
                var tool=InstallCore.Within(root,DlssComponents.ToolRelative);
                var missing=DlssComponents.MissingForInstall(root,tool);
                if(missing.Count>0)throw new IOException("安装后的组件自检失败："+String.Join("、",missing));
                tx.Write(receiptPath,Encoding.UTF8.GetBytes(InstallCore.Json.Serialize(next)));tx.Commit();
            }
        }
        return true;
    }
    public static List<string> Uninstall(string root,Action<string> progress)
    {
        root=InstallCore.FullRoot(root);InstallCore.ValidateGame(root,true);
        var receipt=ReadReceipt(root);if(receipt==null)throw new IOException("DLSS 补充包的卸载清单缺失，未删除任何文件。");
        var retained=new List<string>();var deletes=new List<string>();var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in receipt.Files) {
            var path=InstallCore.Within(root,file.Path);
            if(Directory.Exists(path))throw new IOException("文件位置已变为文件夹，未删除任何文件："+path);
            if(!File.Exists(path))continue;
            if(!InstallCore.Hash(path).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase)){retained.Add(path);continue;}
            deletes.Add(path);
        }
        var receiptPath=InstallCore.Within(root,ReceiptName);
        InstallCore.ValidateGame(root,true);
        using(var tx=new InstallCore.Transaction()) {
            foreach(var path in deletes) {
                InstallCore.NoLinks(path);
                var file=receipt.Files.Single(f=>String.Equals(InstallCore.Within(root,f.Path),path,StringComparison.OrdinalIgnoreCase));
                if(!File.Exists(path))continue;
                if(!InstallCore.Hash(path).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase)){retained.Add(path);continue;}
                progress("正在卸载 DLSS 补充组件："+Path.GetFileName(path));
                tx.Track(path);File.Delete(path);
                var dir=Path.GetDirectoryName(path);var owned=InstallCore.Within(root,Mod.TrimEnd('/'));
                while(dir.StartsWith(owned+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)){directories.Add(dir);dir=Path.GetDirectoryName(dir);}
            }
            InstallCore.NoLinks(receiptPath);tx.Track(receiptPath);File.Delete(receiptPath);tx.Commit();
        }
        directories.Add(InstallCore.Within(root,Mod.TrimEnd('/')));
        foreach(var dir in directories.OrderByDescending(d=>d.Length)) {
            InstallCore.NoLinks(dir);
            if(Directory.Exists(dir)&&!Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir,false);
        }
        return retained;
    }
}

public static class DlssProgram
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool MoveFileEx(string a,string b,int flags);
    static string Self {get{return Assembly.GetExecutingAssembly().Location;}}
    static string Arg(string[] args,string key){var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:null;}
    public static int ExitCode;
    [STAThread]public static int Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try {
#if UNINSTALL
            if(args.Contains("--cleanup"))return Cleanup(args);
            var root=Arg(args,"--root")??Directory.GetParent(Directory.GetParent(Path.GetDirectoryName(Self)).FullName).FullName;
            InstallCore.ValidateGame(root,true);
            if(!args.Contains("--quiet")&&MessageBox.Show("卸载 DLSS 补充 MOD？\n\n仅删除本补充包的清单文件。内录 MOD、配置、剧情及已导出视频保留。\n被修改或未知的文件将保留。","卸载 DLSS 补充 MOD",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return 2;
            var helper=Path.Combine(Path.GetTempPath(),"AADLSS-Uninstall-"+Guid.NewGuid().ToString("N")+".exe");File.Copy(Self,helper,false);
            Process.Start(new ProcessStartInfo(helper,"--cleanup --root \""+root+"\" --parent "+Process.GetCurrentProcess().Id+(args.Contains("--quiet")?" --quiet":"")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});return 0;
#else
            var root=Arg(args,"--root")??Path.GetDirectoryName(Self);
            if(args.Contains("--quiet"))using(var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("dlss-supplement.payload"))
                return DlssSupplementCore.Install(root,payload,delegate{},delegate{return ConfirmReinstall(null);})?0:2;
            Application.Run(new DlssSetupWindow(root));return ExitCode;
#endif
        }catch(Exception error){Fail(error);return 1;}
    }
    static int Cleanup(string[] args)
    {
        var root=InstallCore.FullRoot(Arg(args,"--root"));var original=InstallCore.Within(root,DlssSupplementCore.Uninstaller);
        var temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!Path.GetFullPath(Self).StartsWith(temporary,StringComparison.OrdinalIgnoreCase)||!Regex.IsMatch(Path.GetFileName(Self),"\\AAADLSS-Uninstall-[0-9a-f]{32}\\.exe\\z")
            ||!File.Exists(original)||InstallCore.Hash(Self)!=InstallCore.Hash(original))throw new IOException("DLSS 补充卸载器身份校验失败。");
        int pid;if(!Int32.TryParse(Arg(args,"--parent"),out pid))throw new IOException("卸载调用无效。");
        try{using(var parent=Process.GetProcessById(pid))if(!parent.WaitForExit(10000))throw new IOException("卸载程序尚未退出，请重试。");}catch(ArgumentException){}
        var kept=DlssSupplementCore.Uninstall(root,delegate{});
        if(!args.Contains("--quiet"))MessageBox.Show(kept.Count==0?"DLSS 补充 MOD 已卸载。内录 MOD 保留。":"DLSS 补充 MOD 已卸载。以下被修改的文件已保留：\n"+String.Join("\n",kept),"卸载完成",MessageBoxButtons.OK,MessageBoxIcon.Information);
        MoveFileEx(Self,null,4);return 0;
    }
    public static bool ConfirmReinstall(IWin32Window owner)
    {
        return MessageBox.Show(owner,"检测到 DLSS 补充 MOD 1.0.0，是否覆盖重新安装？\n\n仅更新本补充包的清单文件。若已安装文件被修改，安装会报错停止。\n内录 MOD、配置、剧情与已导出视频保留。\n取消不会写入任何安装文件。","DLSS 补充 MOD · 重复安装确认",MessageBoxButtons.OKCancel,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.OK;
    }
    public static void Fail(Exception error)
    {
        ExitCode=1;MessageBox.Show("操作失败，程序即将终止。\n\n"+error.Message,"DLSS 补充 MOD 安装错误",MessageBoxButtons.OK,MessageBoxIcon.Error);
    }
}

public sealed class DlssSetupWindow : Form
{
    readonly TextBox path=new TextBox();readonly Button install=new Button(),browse=new Button();readonly Label status=new Label();bool working;
    public DlssSetupWindow(string root)
    {
        Text="AzureArchive DLSS 补充 MOD 1.0.0";Font=new Font("Microsoft YaHei UI",10);ClientSize=new Size(660,345);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
        Controls.Add(new Label{Text="安装 DLSS 补充 MOD",Font=new Font(Font.FontFamily,17,FontStyle.Bold),Location=new Point(24,20),Size=new Size(610,36)});
        Controls.Add(new Label{Text="包含 RTX 30 / 40 / 50 系组件，安装时无需联网下载。\n与内录 MOD 安装顺序任意，进入内录后自动连接；DLSS 仍需主动开启。",Location=new Point(24,66),Size=new Size(610,54)});
        Controls.Add(new Label{Text="选择包含 AzureArchive.exe 的 AA 1.0 fix4 根目录",Location=new Point(24,132),Size=new Size(610,28)});
        path.Text=root;path.SetBounds(24,167,510,30);Controls.Add(path);browse.Text="浏览…";browse.SetBounds(545,165,91,34);Controls.Add(browse);
        browse.Click+=delegate{using(var dialog=new FolderBrowserDialog()){dialog.Description="选择 AzureArchive 根目录";dialog.SelectedPath=path.Text;if(dialog.ShowDialog(this)==DialogResult.OK)path.Text=dialog.SelectedPath;}};
        status.Text="组件安装到 mods\\AzureArchiveDLSS，附独立卸载 EXE。\n仅补齐依赖；实际功能仍受显卡、驱动与增强参数影响。";status.SetBounds(24,216,612,53);Controls.Add(status);
        install.Text="安装";install.SetBounds(490,290,145,36);Controls.Add(install);install.Click+=Install;
        FormClosing+=delegate(object sender,FormClosingEventArgs e){if(working)e.Cancel=true;};
    }
    async void Install(object sender,EventArgs e)
    {
        working=true;install.Enabled=browse.Enabled=path.Enabled=false;var root=path.Text;
        try {
            var success=await Task.Run(delegate{using(var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("dlss-supplement.payload"))
                return DlssSupplementCore.Install(root,payload,delegate(string message){BeginInvoke((Action)delegate{status.Text=message;});},delegate{return (bool)Invoke((Func<bool>)delegate{return DlssProgram.ConfirmReinstall(this);});});});
            working=false;if(!success){DlssProgram.ExitCode=2;Close();return;}
            MessageBox.Show(this,"DLSS 补充 MOD 已安装。\n\n与本次内录 MOD 配合使用时会自动连接，无需修改配置。\n在内录设置中主动开启 DLSS 后，受支持显卡才能使用增强。\n\n卸载程序：mods\\AzureArchiveDLSS\\卸载DLSS补充MOD.exe","安装完成",MessageBoxButtons.OK,MessageBoxIcon.Information);Close();
        }catch(Exception error){working=false;DlssProgram.Fail(error);Close();}
    }
}
