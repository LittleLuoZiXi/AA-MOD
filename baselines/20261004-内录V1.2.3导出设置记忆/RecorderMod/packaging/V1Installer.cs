using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using AzureArchive.Recorder;

public enum V1SetupStage { Selecting,Inspecting,HardwareChecking,Preflight,CoreInstalling,CoreFailed,DlssDownloading,DlssPaused,DlssCancelled,DlssFailed,Ready,Finished }
public sealed class V1PartStatus
{
    public string Name,Status,Detail;
    public bool Missing,Dlss;
}
public sealed class V1PreflightReport
{
    public string Root;
    public List<V1PartStatus> Parts=new List<V1PartStatus>();
    public bool DlssInstalled;
}
public sealed class V1SetupServices
{
    public Func<V1GpuStatus> DetectHardware;
    public Func<string,V1GpuStatus,V1PreflightReport> Inspect;
    public Func<string,Action<string>,Func<List<DependencyIssue>,bool>,Func<List<string>,bool>,Func<bool>,bool> InstallRecorder;
    public Func<string,int,Action<OnlineDlssProgress>,OnlineDlssControl,Task<OnlineDlssResult>> InstallDlss;
    public Action<IWin32Window,string,string,MessageBoxIcon> ShowMessage;
    public V1SetupServices()
    {
        DetectHardware=V1Hardware.Detect;
        Inspect=InspectComputer;
        InstallRecorder=delegate(string root,Action<string> progress,Func<List<DependencyIssue>,bool> dependencies,Func<List<string>,bool> reinstall,Func<bool> ffmpeg){
            using(var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("installer.payload"))
                return InstallCore.Install(root,payload,progress,dependencies,reinstall,null,true,ffmpeg,delegate{return true;});
        };
        InstallDlss=delegate(string root,int series,Action<OnlineDlssProgress> progress,OnlineDlssControl control){
            using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("installer.dlss-uninstaller")) {
                if(source==null)throw new IOException("DLSS卸载程序资源缺失，未开始下载安装。");
                using(var memory=new MemoryStream()){source.CopyTo(memory);return OnlineDlssInstall.RunAsync(root,series,memory.ToArray(),progress,control);}
            }
        };
        ShowMessage=delegate(IWin32Window owner,string text,string title,MessageBoxIcon icon){MessageBox.Show(owner,text,title,MessageBoxButtons.OK,icon);};
    }
    public static V1PreflightReport InspectComputer(string root,V1GpuStatus gpu)
    {
        root=InstallCore.FullRoot(root);InstallCore.ValidateGame(root,true);
        var report=new V1PreflightReport{Root=root};
        var versions=InstallCore.FindExistingInstallation(root,null);
        report.Parts.Add(new V1PartStatus{Name="内录 MOD V1.2.3",Status=versions.Count==0?"未安装":versions.Contains("1.2.3")?"已安装 V1.2.3 / 可重新安装":"已检测到旧安装 / 可更新",Detail=versions.Count==0?"将安装内录、教程和卸载程序。":"现有版本："+String.Join("、",versions)+"；配置和导出视频保留。",Missing=versions.Count==0});
        var missing=InstallCore.DetectDependencies(root).Where(x=>!x.OptionalDlss).ToArray();
        if(missing.Length==0)report.Parts.Add(new V1PartStatus{Name="AA / MOD 加载依赖",Status="已具备",Detail="本体、加载框架和启动配置已通过文件检查。"});
        foreach(var item in missing)report.Parts.Add(new V1PartStatus{Name=item.Name,Status="缺少 / 需处理",Detail=item.Detail+" "+item.Impact,Missing=true});
        var ffmpeg=InstallCore.FindFfmpeg(root);
        report.Parts.Add(new V1PartStatus{Name="FFmpeg",Status=ffmpeg==null?"未发现可用版本":"已有可用版本",Detail=ffmpeg==null?"安装时按原规则确认是否安装内置版本。":ffmpeg,Missing=ffmpeg==null});
        if(gpu==null){
            report.Parts.Add(new V1PartStatus{Name="DLSS（可选）",Status="未选择",Detail="勾选后检查显卡；未勾选时仅安装内录，不下载 DLSS。",Dlss=true});
        } else if(gpu.Supported){
            report.DlssInstalled=OnlineDlssInstall.IsInstalled(root,gpu.Series);
            report.Parts.Add(new V1PartStatus{Name="DLSS · RTX "+gpu.Series+" 系",Status=report.DlssInstalled?"已完整安装":"缺少 / 待下载安装",Detail=report.DlssInstalled?"现有组件校验通过，无需重复下载。":"内录安装完成后，从 GitHub 下载共同组件和本机显卡对应文件。",Missing=!report.DlssInstalled,Dlss=true});
        } else report.Parts.Add(new V1PartStatus{Name="DLSS",Status="不可用",Detail=gpu.Reason,Missing=true,Dlss=true});
        return report;
    }
}

public static class V1Program
{
    public static int ExitCode;
    [STAThread] public static int Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try {
            var host=AppDomain.CurrentDomain.GetData("AARecorder.InstallerHost") as string??Assembly.GetExecutingAssembly().Location;
            var root=Path.GetDirectoryName(host);var index=Array.IndexOf(args,"--root");
            if(index>=0&&index+1<args.Length)root=args[index+1];
            using(var window=new V1SetupWindow(root,new V1SetupServices()))Application.Run(window);
            return ExitCode;
        } catch(Exception ex){Program.Fail(ex);return 1;}
    }
}

public sealed class V1SetupWindow : Form
{
    V1GpuStatus gpu;
    readonly V1SetupServices services;
    readonly TextBox rootPath=new TextBox();readonly Button browse=new Button();readonly Button inspect=new Button();
    readonly ListView parts=new ListView();readonly Button start=new Button();readonly Button pause=new Button();readonly Button cancel=new Button();readonly Button next=new Button();
    readonly Label status=new Label();readonly Label dlssReason=new Label();readonly Label networkHint=new Label();readonly Label transfer=new Label();
    readonly Label gpuLabel=new Label();readonly CheckBox dlssAvailability=new CheckBox();
    readonly ProgressBar progress=new ProgressBar();readonly Timer networkTimer=new Timer();
    readonly Color blue=Color.FromArgb(28,135,179);readonly Color red=Color.FromArgb(188,48,61);
    OnlineDlssControl download;V1PreflightReport report;bool working,closeRequested;int hintIndex;
    bool dlssRequested,dlssRejected,suppressSelection;
    readonly object progressSync=new object();OnlineDlssProgress pendingProgress;bool progressQueued;
    public V1SetupStage Stage{get;private set;}
    public bool RecorderInstalled{get;private set;}
    public bool DownloadComplete{get;private set;}
    public V1SetupWindow(string root,V1SetupServices operations)
    {
        services=operations;Stage=V1SetupStage.Selecting;
        Text="AA 内录 V1.2.3 · 安装向导";Name="AARecorderV1Setup";ClientSize=new Size(920,710);MinimumSize=new Size(936,749);
        Font=new Font("Microsoft YaHei UI",10F);BackColor=Color.FromArgb(247,251,253);StartPosition=FormStartPosition.CenterScreen;AutoScaleMode=AutoScaleMode.Dpi;
        var title=new Label{Text="内录 V1.2.3",Font=new Font(Font.FontFamily,24F,FontStyle.Bold),ForeColor=Color.FromArgb(37,69,87)};title.SetBounds(26,18,700,48);Controls.Add(title);
        var subtitle=new Label{Text="01 检查环境   →   02 安装内录   →   03 DLSS（可选）",ForeColor=blue};subtitle.SetBounds(30,70,850,26);Controls.Add(subtitle);
        var pathLabel=new Label{Text="AA 根目录"};pathLabel.SetBounds(30,108,95,26);Controls.Add(pathLabel);
        rootPath.Name="RootPath";rootPath.SetBounds(127,105,545,30);rootPath.Text=root??"";Controls.Add(rootPath);
        browse.Text="选择目录";browse.SetBounds(684,103,96,34);Controls.Add(browse);
        inspect.Name="Inspect";inspect.Text="重新检测";inspect.SetBounds(791,103,100,34);Controls.Add(inspect);
        parts.Name="MissingParts";parts.SetBounds(30,150,861,227);parts.View=View.Details;parts.FullRowSelect=true;parts.MultiSelect=false;parts.GridLines=false;parts.ShowItemToolTips=true;
        parts.Columns.Add("组件",205);parts.Columns.Add("状态",185);parts.Columns.Add("检测结果 / 后续处理",435);Controls.Add(parts);
        gpuLabel.Name="GpuStatus";gpuLabel.Text="显卡：勾选 DLSS 后检测";gpuLabel.AutoEllipsis=true;gpuLabel.SetBounds(30,389,860,29);Controls.Add(gpuLabel);
        dlssAvailability.Name="DlssAvailability";dlssAvailability.Text="安装 DLSS 组件（可选）";dlssAvailability.AutoCheck=true;dlssAvailability.Checked=false;dlssAvailability.SetBounds(30,425,258,30);Controls.Add(dlssAvailability);
        dlssReason.Name="DlssReason";dlssReason.Text="未选择 DLSS；本次仅安装内录 MOD。";
        dlssReason.ForeColor=blue;dlssReason.SetBounds(300,425,590,47);Controls.Add(dlssReason);
        networkHint.Name="NetworkHint";networkHint.Text="请确保网络可以连接至github。";networkHint.ForeColor=blue;networkHint.SetBounds(30,476,860,27);networkHint.Visible=false;Controls.Add(networkHint);
        progress.Name="DownloadProgress";progress.SetBounds(30,510,860,21);progress.Maximum=1000;progress.Enabled=false;Controls.Add(progress);
        transfer.Name="TransferStatus";transfer.SetBounds(30,539,860,30);transfer.AutoEllipsis=true;Controls.Add(transfer);
        status.Name="SetupStatus";status.SetBounds(30,576,860,53);status.ForeColor=Color.FromArgb(52,78,91);Controls.Add(status);
        start.Name="StartInstall";start.Text="开始安装内录";start.SetBounds(30,650,172,38);start.Enabled=false;Controls.Add(start);
        pause.Name="PauseDownload";pause.Text="暂停下载";pause.SetBounds(453,650,130,38);pause.Enabled=false;Controls.Add(pause);
        cancel.Name="CancelInstall";cancel.Text="取消安装";cancel.SetBounds(596,650,138,38);Controls.Add(cancel);
        next.Name="ContinueInstall";next.Text="继续";next.SetBounds(746,650,144,38);next.Enabled=false;Controls.Add(next);
        browse.Click+=delegate{using(var picker=new FolderBrowserDialog{Description="选择包含 AzureArchive.exe 的 AA 根目录",SelectedPath=rootPath.Text})if(picker.ShowDialog(this)==DialogResult.OK){rootPath.Text=picker.SelectedPath;InspectNow();}};
        inspect.Click+=delegate{InspectNow();};start.Click+=BeginInstall;pause.Click+=TogglePause;cancel.Click+=CancelOrClose;next.Click+=ContinueOrFinish;
        dlssAvailability.CheckedChanged+=ChangeDlssSelection;
        rootPath.TextChanged+=delegate{if(!working){report=null;start.Enabled=false;next.Enabled=false;Stage=V1SetupStage.Selecting;status.Text="目录已更改，请重新检测。";}};
        networkTimer.Interval=4000;networkTimer.Tick+=delegate{hintIndex++;networkHint.Text=hintIndex%2==0?"请确保网络可以连接至github。":"如遇下载卡顿需暂停后排查网络。";};
        Shown+=delegate{InspectNow();};FormClosing+=OnClosing;
    }
    void Ui(Action action){if(IsDisposed||Disposing)return;if(InvokeRequired){try{BeginInvoke(action);}catch(InvalidOperationException){}}else action();}
    void Inputs(bool enabled){rootPath.Enabled=enabled;browse.Enabled=enabled;inspect.Enabled=enabled;dlssAvailability.Enabled=enabled&&!dlssRejected&&!RecorderInstalled&&!closeRequested;}
    void SetDlssChecked(bool value){suppressSelection=true;try{dlssAvailability.Checked=value;}finally{suppressSelection=false;}}
    void RefreshDlssPart()
    {
        if(report==null)return;
        var item=report.Parts.FirstOrDefault(x=>x.Dlss);
        if(item==null){item=new V1PartStatus{Dlss=true};report.Parts.Add(item);}
        item.Name="DLSS（可选）";
        if(dlssRejected){item.Status="不可用 / 已取消选择";item.Detail=gpu.Reason;item.Missing=true;}
        else if(!dlssRequested){item.Status="未选择";item.Detail="本次只安装内录；不会下载或改动已有 DLSS 组件。";item.Missing=false;}
        else {item.Name="DLSS · RTX "+gpu.Series+" 系";item.Status=report.DlssInstalled?"已完整安装":"缺少 / 待下载安装";item.Detail=report.DlssInstalled?"现有组件校验通过，无需重复下载。":"已选择；内录完成后下载共同组件和本机系列运行库。";item.Missing=!report.DlssInstalled;}
    }
    async void ChangeDlssSelection(object sender,EventArgs e)
    {
        if(suppressSelection)return;
        if(working||RecorderInstalled||dlssRejected){SetDlssChecked(dlssRequested);return;}
        if(!dlssAvailability.Checked){
            dlssRequested=false;dlssReason.ForeColor=blue;dlssReason.Text="未选择 DLSS；本次仅安装内录 MOD。";
            RefreshDlssPart();if(report!=null){ShowParts();status.ForeColor=Color.FromArgb(52,78,91);status.Text="本次仅安装内录 MOD；点击开始安装内录。";}return;
        }
        working=true;Stage=V1SetupStage.HardwareChecking;Inputs(false);start.Enabled=false;next.Enabled=false;status.ForeColor=Color.FromArgb(52,78,91);
        gpuLabel.Text="正在检测显卡…";dlssReason.ForeColor=blue;dlssReason.Text="正在检查此显卡是否支持 DLSS…";status.Text="正在检查 DLSS 安装条件，请稍候。";
        string environmentError=null;
        try {
            var detected=await Task.Run(delegate{return services.DetectHardware();});
            if(IsDisposed||closeRequested)return;
            if(detected==null)throw new IOException("未能读取显卡检测结果。");
            gpu=detected;gpuLabel.Text="检测到的显卡："+(String.IsNullOrWhiteSpace(gpu.Name)?"未发现":gpu.Name);
            if(!gpu.Supported){RejectDlss(gpu.Reason);return;}
            dlssRequested=true;
            if(report!=null){
                var requested=report.Root;report=null;
                try {report=await Task.Run(delegate{return services.Inspect(requested,gpu);});}
                catch(Exception ex){
                    environmentError="AA 环境检查未完成，请排查后重新检测："+ex.Message;parts.Items.Clear();
                    dlssReason.Text="显卡支持 DLSS；请先完成 AA 目录检查，也可取消勾选。";return;
                }
            }
            if(IsDisposed||closeRequested)return;
            dlssReason.Text="显卡支持。可在开始安装前取消勾选；保持勾选则在内录完成后安装 DLSS。";
            RefreshDlssPart();if(report!=null)ShowParts();
        } catch(Exception ex){
            if(!IsDisposed&&!closeRequested){gpu=new V1GpuStatus{Name="DLSS 检查未完成",Reason="无法完成 DLSS 检查："+ex.Message};RejectDlss(gpu.Reason);}
        } finally {
            working=false;
            if(!IsDisposed){Stage=report==null?V1SetupStage.Selecting:V1SetupStage.Preflight;Inputs(true);start.Enabled=report!=null;status.Text=environmentError??(report==null?"请选择有效的 AA 目录并重新检测。":dlssRequested?"已选择 DLSS；点击开始安装内录，完成后安装 DLSS。":"本次仅安装内录 MOD；点击开始安装内录。");if(environmentError!=null)status.ForeColor=red;}
            CloseIfRequested();
        }
    }
    void RejectDlss(string reason)
    {
        if(String.IsNullOrWhiteSpace(reason))reason="该系列英伟达（NVIDIA）显卡暂不支持 DLSS 组件。";
        gpu.Reason=reason;dlssReason.Text=reason;dlssReason.ForeColor=red;
        services.ShowMessage(this,reason+"\r\n\r\n确认后将取消 DLSS 选择并禁用此选项，随后仅安装内录 MOD。","无法安装 DLSS",MessageBoxIcon.Information);
        dlssRequested=false;dlssRejected=true;SetDlssChecked(false);dlssAvailability.Enabled=false;
        RefreshDlssPart();if(report!=null)ShowParts();
    }
    void ShowParts()
    {
        parts.BeginUpdate();
        try {
            parts.Items.Clear();
            foreach(var item in report.Parts){var row=new ListViewItem(new[]{item.Name,item.Status,item.Detail});row.ToolTipText=item.Detail;row.ForeColor=item.Missing?red:Color.FromArgb(38,109,79);if(item.Dlss&&dlssRejected)row.BackColor=Color.FromArgb(235,238,240);parts.Items.Add(row);}
        } finally {parts.EndUpdate();}
    }
    void RecorderPartsInstalled()
    {
        foreach(var item in report.Parts){
            if(item.Name=="内录 MOD V1.2.3"){item.Missing=false;item.Status="已安装 V1.2.3";item.Detail="内录、教程和卸载程序已安装完成，配置和导出视频保留。";}
            else if(item.Name=="FFmpeg"){
                if(item.Missing)item.Detail="内置版本已安装并通过 MP4 编码校验。";
                item.Missing=false;item.Status="已具备 / 编码校验通过";
            }
        }
        ShowParts();
    }
    async void InspectNow()
    {
        if(working)return;working=true;Inputs(false);start.Enabled=false;Stage=V1SetupStage.Inspecting;status.ForeColor=Color.FromArgb(52,78,91);status.Text="正在检查本机和所选 AA 目录…";parts.Items.Clear();
        var requested=rootPath.Text;
        try {
            var found=await Task.Run(delegate{return services.Inspect(requested,dlssRequested?gpu:null);});if(IsDisposed)return;report=found;
            RefreshDlssPart();
            ShowParts();
            Stage=V1SetupStage.Preflight;start.Enabled=true;
            status.Text=dlssRequested?"检查完成，已选择 DLSS；点击开始安装内录。":dlssRejected?"检查完成。DLSS 已禁用，本次仅安装内录 MOD。":"检查完成。默认仅安装内录；如需 DLSS，请先勾选。";
        } catch(Exception ex){if(!IsDisposed){Stage=V1SetupStage.Selecting;status.Text=ex.Message;status.ForeColor=red;report=null;}}
        finally {working=false;if(!IsDisposed)Inputs(true);CloseIfRequested();}
    }
    async void BeginInstall(object sender,EventArgs e)
    {
        if(working)return;
        if(RecorderInstalled){await DownloadDlss();return;}
        if(report==null)return;
        working=true;Stage=V1SetupStage.CoreInstalling;Inputs(false);start.Enabled=false;cancel.Enabled=false;next.Enabled=false;
        progress.Enabled=true;progress.Style=ProgressBarStyle.Marquee;status.ForeColor=Color.FromArgb(52,78,91);status.Text="正在安装内录 V1.2.3…";
        try {
            var root=report.Root;
            var installed=await Task.Run(delegate{return services.InstallRecorder(root,
                delegate(string text){Ui(delegate{transfer.Text=text;});},
                delegate(List<DependencyIssue> issues){return (bool)Invoke((Func<bool>)delegate{return DependencyDialog.Confirm(this,issues);});},
                delegate(List<string> versions){return (bool)Invoke((Func<bool>)delegate{return ReinstallDialog.Confirm(this,versions);});},
                delegate{return (bool)Invoke((Func<bool>)delegate{return FfmpegInstallDialog.Confirm(this);});});});
            if(!installed){Stage=V1SetupStage.Preflight;status.Text="已取消内录安装，目标未写入安装文件。";V1Program.ExitCode=2;closeRequested=true;return;}
            RecorderInstalled=true;RecorderPartsInstalled();V1Program.ExitCode=0;transfer.Text="内录 V1.2.3 已安装完成。";progress.Style=ProgressBarStyle.Blocks;progress.Value=1000;
        } catch(Exception ex){Stage=V1SetupStage.CoreFailed;V1Program.ExitCode=1;status.Text="内录安装未完成："+ex.Message;status.ForeColor=red;start.Text="重试内录安装";start.Enabled=true;Inputs(true);services.ShowMessage(this,ex.Message,"内录安装未完成",MessageBoxIcon.Error);return;}
        finally {working=false;if(!IsDisposed){cancel.Enabled=true;progress.Style=ProgressBarStyle.Blocks;}CloseIfRequested();}
        if(IsDisposed||closeRequested)return;
        if(!dlssRequested){Ready(false);return;}
        await DownloadDlss();
    }
    async Task DownloadDlss()
    {
        if(working||!RecorderInstalled||!dlssRequested||gpu==null||!gpu.Supported)return;
        working=true;Stage=V1SetupStage.DlssDownloading;DownloadComplete=false;start.Enabled=false;next.Enabled=false;Inputs(false);
        dlssReason.Text="本次已选择 DLSS，选择已锁定。下载过程中可使用下方的暂停或取消按钮。";
        pause.Enabled=true;pause.Text="暂停下载";cancel.Text="取消下载";cancel.Enabled=true;status.ForeColor=Color.FromArgb(52,78,91);status.Text="内录已安装，正在下载并安装 DLSS 组件…";
        networkHint.Visible=true;networkHint.Text="请确保网络可以连接至github。";hintIndex=0;networkTimer.Start();progress.Enabled=true;progress.Value=0;progress.Style=ProgressBarStyle.Marquee;
        using(var control=new OnlineDlssControl()){
            download=control;
            try {
                var result=await services.InstallDlss(report.Root,gpu.Series,QueueProgress,control);
                if(!result.Completed)throw new IOException("DLSS 安装未报告完成。");
                DownloadComplete=true;V1Program.ExitCode=0;Ready(true);
            } catch(OperationCanceledException){
                Stage=V1SetupStage.DlssCancelled;V1Program.ExitCode=2;status.Text="已取消 DLSS 下载并清理本次暂存。内录 V1.2.3 保留；可重试下载，或退出安装。";start.Text="重试 DLSS 下载";start.Enabled=true;next.Enabled=false;
            } catch(Exception ex){
                Stage=V1SetupStage.DlssFailed;V1Program.ExitCode=1;status.ForeColor=red;status.Text="DLSS 下载或安装未完成。内录已安装，可排查网络后重试。";transfer.Text=ex.Message;start.Text="重试 DLSS 下载";start.Enabled=true;next.Enabled=false;
                if(!closeRequested)services.ShowMessage(this,ex.Message+"\r\n\r\n内录 V1.2.3 已安装。继续按钮会在 DLSS 下载和安装完成后开放。","DLSS 安装未完成",MessageBoxIcon.Warning);
            } finally {
                download=null;working=false;networkTimer.Stop();pause.Enabled=false;progress.Style=ProgressBarStyle.Blocks;cancel.Enabled=true;cancel.Text=Stage==V1SetupStage.Ready?"退出":"退出安装";CloseIfRequested();
            }
        }
    }
    void QueueProgress(OnlineDlssProgress value)
    {
        lock(progressSync){pendingProgress=value;if(progressQueued)return;progressQueued=true;}
        Ui(delegate{OnlineDlssProgress latest;lock(progressSync){latest=pendingProgress;progressQueued=false;}ShowProgress(latest);});
    }
    void ShowProgress(OnlineDlssProgress value)
    {
        if(!working||download==null||DownloadComplete)return;
        var percent=value.TotalBytes>0?(int)Math.Min(1000,Math.Max(0,value.BytesReceived*1000.0/value.TotalBytes)):0;
        progress.Style=value.TotalBytes>0?ProgressBarStyle.Blocks:ProgressBarStyle.Marquee;progress.Value=percent;
        var size=value.TotalBytes>0?String.Format("{0:0.0} / {1:0.0} MB",value.BytesReceived/1048576.0,value.TotalBytes/1048576.0):String.Format("{0:0.0} MB",value.BytesReceived/1048576.0);
        transfer.Text=value.Message+"  "+value.Asset+"  "+size;
        if(download.IsPaused){Stage=V1SetupStage.DlssPaused;status.Text="下载已暂停。请排查网络，完成后点击恢复下载。";}
        else {Stage=V1SetupStage.DlssDownloading;status.Text="内录已安装；"+value.Message+"。下载、校验和安装全部完成后才能继续。";}
    }
    void TogglePause(object sender,EventArgs e)
    {
        if(download==null||!working)return;
        if(download.IsPaused){download.Resume();pause.Text="暂停下载";Stage=V1SetupStage.DlssDownloading;status.Text="已恢复下载，正在连接 GitHub…";}
        else {download.Pause();pause.Text="恢复下载";Stage=V1SetupStage.DlssPaused;status.Text="下载已暂停。请排查网络，完成后点击恢复下载。";}
    }
    void CancelOrClose(object sender,EventArgs e)
    {
        if(download!=null&&working){cancel.Enabled=false;pause.Enabled=false;status.Text="正在取消下载并清理本次临时文件…";download.Cancel();return;}
        if(!RecorderInstalled)V1Program.ExitCode=2;Close();
    }
    void Ready(bool dlss)
    {
        Stage=V1SetupStage.Ready;start.Enabled=false;next.Enabled=true;next.Text="继续";cancel.Text="退出";pause.Enabled=false;progress.Style=ProgressBarStyle.Blocks;progress.Value=1000;
        status.Text=dlss?"内录 V1.2.3 与本机 DLSS 组件均安装完成，可继续。":"内录 V1.2.3 已安装。本次未选择 DLSS，已跳过其下载和安装，可继续。";
        networkHint.Visible=false;
        if(dlss){
            foreach(var item in report.Parts)if(item.Dlss){item.Missing=false;item.Status="已完整安装";item.Detail="本机 RTX "+gpu.Series+" 系组件已通过校验并安装完成。";}
            ShowParts();transfer.Text="DLSS 组件下载、校验和安装已全部完成。";
            dlssReason.Text="DLSS 组件校验与安装完成。进入内录设置后，需主动开启 DLSS 总开关。";dlssReason.ForeColor=Color.FromArgb(38,109,79);
        }
    }
    void ContinueOrFinish(object sender,EventArgs e)
    {
        if(working)return;
        if(Stage==V1SetupStage.Finished){Close();return;}
        if(Stage!=V1SetupStage.Ready||!RecorderInstalled||(dlssRequested&&!DownloadComplete))return;
        Stage=V1SetupStage.Finished;networkHint.Visible=false;transfer.Text="内录卸载：mods\\AzureArchiveRecorder\\卸载内录MOD.exe";
        status.Text="安装完成。启动 AA 后，在剧情“入场”旁打开“内录”。\r\n"+(DownloadComplete?"DLSS 独立卸载：mods\\AzureArchiveDLSS\\卸载DLSS补充MOD.exe":"本次仅安装内录 MOD；已有 DLSS 组件保持原样。");next.Text="完成";cancel.Visible=false;V1Program.ExitCode=0;
    }
    void OnClosing(object sender,FormClosingEventArgs e)
    {
        if(!working)return;e.Cancel=true;closeRequested=true;cancel.Enabled=false;pause.Enabled=false;
        if(download!=null){status.Text="正在取消并清理下载，完成后退出；已安装内录保留。";download.Cancel();}
        else status.Text="正在结束当前操作，完成后退出…";
    }
    void CloseIfRequested(){if(closeRequested&&!working&&!IsDisposed)Close();}
    protected override void Dispose(bool disposing){if(disposing){networkTimer.Dispose();if(download!=null)download.Cancel();}base.Dispose(disposing);}
}
