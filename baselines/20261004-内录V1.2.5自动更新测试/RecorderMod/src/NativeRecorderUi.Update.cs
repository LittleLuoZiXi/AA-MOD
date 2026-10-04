using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;
using AzureArchive.Automation;

namespace AzureArchive.Recorder;

internal sealed partial class NativeRecorderUi
{
    internal const string UpdateOfferPrompt="内录MOD有新版本，是否进行更新？";
    internal const string UpdateCompletedPrompt="内录MOD更新完成，AA即将重启";
    internal enum UpdatePanelState { None, Offer, Download, Installing, Completed, Failed, Restarting }
    UpdatePanelState updatePanelState;
    GameObject? updatePanel;
    UILabel updateHeading=null!,updateBody=null!,updateDetail=null!;
    UITexture updateTrack=null!,updateBar=null!;
    UI.MXButton updateYes=null!,updateNo=null!,updatePause=null!,updateCancel=null!,updateOk=null!;
    CancellationTokenSource? updateCheckCancellation;
    Task<RecorderUpdateCheckResult>? updateCheck;
    RecorderUpdateInfo? availableUpdate;
    RecorderUpdateDownload? updateDownload;
    RecorderUpdateSession? updateSession;
    Task? updatePrepare,updateRefresh;
    Task<bool>? updateRestart;
    float nextUpdateRefresh;
    string updateError="";
    internal bool UpdateModalVisible=>open && updatePanelState!=UpdatePanelState.None;
    internal bool UpdateBlocksRecording=>updatePanelState!=UpdatePanelState.None || updateSession?.Snapshot.BlocksRecording==true;
    internal UpdatePanelState UpdateState=>updatePanelState;

    // Only hashing and HTTPS run on the worker. Never access Unity from there.
    void BeginUpdateCheck()
    {
        if(UpdateBlocksRecording || updateCheck!=null)return;
        if(UpdateDiagnostics.TryCreateCheck(out var probeCheck)){updateCheck=probeCheck;return;}
        updateCheckCancellation?.Dispose();
        updateCheckCancellation=new CancellationTokenSource();
        var token=updateCheckCancellation.Token;
        updateCheck=Task.Run(async ()=>
        {
            try
            {
                using var stream=File.OpenRead(Path.Combine(Paths.GameRootPath,"AzureArchive.exe"));
                using var algorithm=SHA256.Create();
                string hash=Convert.ToHexString(algorithm.ComputeHash(stream));
                using var client=new RecorderUpdateClient();
                return await client.CheckAsync(RecorderPlugin.Version,hash,token).ConfigureAwait(false);
            }
            catch(Exception ex){return new RecorderUpdateCheckResult(RecorderUpdateCheckState.Failed,null,"检查更新失败："+ex.Message);}
        });
    }

    static bool UpdateProjectReady()
    {
        try
        {
            var current=AuthoringEditorSession.Current;
            if(current==null)return true;
            current.Refresh(false);
            return !current.Dirty && !current.SaveInProgress && !current.IsApplying;
        }
        catch(Exception ex){RecorderPlugin.Instance.Log.LogWarning("Cannot confirm project state for update: "+ex.Message);return false;}
    }

    void OfferUpdate(RecorderUpdateInfo info)
    {
        if(owner.Busy || choosing || !open || showProgress || TutorialVisible || TimingConfirmationVisible || UpdateBlocksRecording)return;
        availableUpdate=info;updateError="";updatePanelState=UpdatePanelState.Offer;
        RefreshUpdatePanel();UpdateVisibility();
    }

    void AcceptUpdate()
    {
        if(updatePanelState!=UpdatePanelState.Offer || availableUpdate==null || owner.Busy)return;
        if(!UpdateProjectReady()){FailUpdate("当前工程有未保存的修改或正在保存，请先保存工程，再打开内录面板更新。");return;}
        try
        {
            updateDownload?.Dispose();updateDownload=UpdateDiagnostics.CreateDownload(availableUpdate);
            updatePanelState=UpdatePanelState.Download;updateDownload.Start();RefreshUpdatePanel();UpdateVisibility();
        }
        catch(Exception ex){FailUpdate("无法开始下载："+ex.Message);}
    }

    void DismissUpdate()
    {
        if(updatePanelState is not (UpdatePanelState.Offer or UpdatePanelState.Failed))return;
        updateDownload?.Dispose();updateDownload=null;availableUpdate=null;
        updatePanelState=UpdatePanelState.None;updateError="";
        if(updateSession?.Snapshot.BlocksRecording==true)owner.SetStatus("更新安装状态未确认，请关闭 AA 后重新启动；如无法启动，请用安装包修复。");
        else {updateSession?.Dispose();updateSession=null;}
        UpdateVisibility();
    }

    void ToggleUpdatePause()
    {
        if(updatePanelState!=UpdatePanelState.Download || updateDownload==null)return;
        var state=updateDownload.Snapshot.State;
        if(state==RecorderDownloadState.Downloading)updateDownload.Pause();
        else if(state is RecorderDownloadState.Paused or RecorderDownloadState.Failed)updateDownload.Resume();
        RefreshUpdatePanel();
    }

    void CancelUpdate()
    {
        if(updatePanelState==UpdatePanelState.Download)updateDownload?.Cancel();
    }

    void FailUpdate(string message)
    {
        updateError=message;updatePanelState=UpdatePanelState.Failed;
        Config.Log.LogWarning("Recorder update: "+message);
        RefreshUpdatePanel();UpdateVisibility();
    }

    void ConfirmUpdateRestart()
    {
        if(UpdateDiagnostics.TryConfirmRestart(this))return;
        if(updatePanelState!=UpdatePanelState.Completed || updateSession==null || updateRestart!=null)return;
        if(!UpdateProjectReady())
        {
            // Do not silently close unsaved work if an external editor/API changed it.
            FailUpdate("工程状态发生变化，暂不重启。请返回并保存工程后，手动重启 AA。");return;
        }
        updateRestart=updateSession.RequestRestartAsync();
        updatePanelState=UpdatePanelState.Restarting;RefreshUpdatePanel();
    }

    void UpdateAutomaticUpdate()
    {
        if(updateCheck?.IsCompleted==true)
        {
            var task=updateCheck;updateCheck=null;
            try
            {
                var result=task.GetAwaiter().GetResult();
                if(result.State==RecorderUpdateCheckState.Available)availableUpdate=result.Update;
                else if(result.State==RecorderUpdateCheckState.Failed)
                {
                    Config.Log.LogWarning(result.Error);
                    if(open && !owner.Busy && (owner.Status.StartsWith("就绪",StringComparison.Ordinal) || owner.Status.StartsWith("暂无法检查更新",StringComparison.Ordinal)))owner.SetStatus("暂无法检查更新，可继续内录。"+result.Error);
                }
            }
            catch(Exception ex){Config.Log.LogWarning("Update check failed: "+ex.Message);}
        }
        if(availableUpdate!=null && updatePanelState==UpdatePanelState.None)
            OfferUpdate(availableUpdate);
        if(updatePanelState==UpdatePanelState.Download && updateDownload!=null)
        {
            var snapshot=updateDownload.Snapshot;
            if(snapshot.State==RecorderDownloadState.Cancelled)
            {
                updateDownload.Dispose();updateDownload=null;availableUpdate=null;updatePanelState=UpdatePanelState.None;
                owner.SetStatus(string.IsNullOrEmpty(snapshot.Error)?"已终止更新，本次下载已清理。":"已终止更新。"+snapshot.Error);UpdateVisibility();
            }
            else if(snapshot.State==RecorderDownloadState.Completed && updatePrepare==null)
            {
                if(!UpdateProjectReady()){FailUpdate("工程有未保存的修改，请先保存，再重新尝试更新。");return;}
                if(UpdateDiagnostics.TryBeginInstall(this))return;
                updateSession=new RecorderUpdateSession();updatePanelState=UpdatePanelState.Installing;
                updatePrepare=updateSession.PrepareAndStartAsync(availableUpdate!,snapshot.VerifiedFilePath!);
                UpdateVisibility();
            }
        }
        if(updatePrepare?.IsCompleted==true)
        {
            var task=updatePrepare;updatePrepare=null;
            try{task.GetAwaiter().GetResult();updateDownload?.Dispose();updateDownload=null;}
            catch(Exception ex){FailUpdate("准备更新失败："+ex.Message);}
        }
        if(updateSession!=null && updatePanelState==UpdatePanelState.Installing && updatePrepare==null)
        {
            if(updateRefresh?.IsCompleted==true)
            {
                var task=updateRefresh;updateRefresh=null;
                try{task.GetAwaiter().GetResult();}
                catch(Exception ex){FailUpdate("读取更新结果失败："+ex.Message);}
            }
            if(updateRefresh==null && Time.realtimeSinceStartup>=nextUpdateRefresh)
            {nextUpdateRefresh=Time.realtimeSinceStartup+.25f;updateRefresh=updateSession.RefreshAsync();}
            var snapshot=updateSession.Snapshot;
            if(snapshot.State==RecorderUpdateSessionState.Completed){updatePanelState=UpdatePanelState.Completed;UpdateVisibility();}
            else if(snapshot.State==RecorderUpdateSessionState.Failed)FailUpdate(snapshot.Error);
        }
        if(updateRestart?.IsCompleted==true)
        {
            var task=updateRestart;updateRestart=null;
            try
            {
                if(task.GetAwaiter().GetResult()){Application.Quit();return;}
                FailUpdate(string.IsNullOrEmpty(updateSession?.Snapshot.Error)?"未能提交重启请求，请保存工程并手动重启 AA。":updateSession!.Snapshot.Error);
            }
            catch(Exception ex){FailUpdate("重启请求失败，请保存工程并手动重启 AA："+ex.Message);}
        }
        if(UpdateModalVisible)
        {
            if(Input.GetKeyDown(KeyCode.Escape) && updatePanelState is UpdatePanelState.Offer or UpdatePanelState.Failed)DismissUpdate();
            RefreshUpdatePanel();
        }
    }

    void RefreshUpdatePanel()
    {
        if(updatePanel==null)return;
        bool offer=updatePanelState==UpdatePanelState.Offer,download=updatePanelState==UpdatePanelState.Download;
        bool completed=updatePanelState==UpdatePanelState.Completed,failed=updatePanelState==UpdatePanelState.Failed;
        updateYes.gameObject.SetActive(offer);updateNo.gameObject.SetActive(offer);
        updatePause.gameObject.SetActive(download);updateCancel.gameObject.SetActive(download);
        updateOk.gameObject.SetActive(completed || failed);
        updateTrack.gameObject.SetActive(download);updateBar.gameObject.SetActive(download);
        updateDetail.color=Normal;updateBody.color=failed?Red:Normal;
        updateHeading.text=download?"下载内录更新":completed?"更新完成":failed?"更新未完成":"内录 MOD 更新";
        updateBody.text=offer?UpdateOfferPrompt:completed?UpdateCompletedPrompt:failed?updateError:
            updatePanelState==UpdatePanelState.Restarting?"正在重启 AA…":download?"正在下载新版本…":"正在校验并安装更新…";
        updateDetail.text=offer?$"当前 V{RecorderPlugin.Version}  →  V{availableUpdate?.Version}\n更新后需要重启 AA。":completed?updateError:
            updatePanelState==UpdatePanelState.Installing?"请稍候，完成后会提示重启。":failed?"当前更新已停止，请按提示处理后重试。":"";
        if(download && updateDownload!=null)
        {
            var snapshot=updateDownload.Snapshot;var state=snapshot.State;
            updateBody.text=state==RecorderDownloadState.Paused?"下载已暂停":state==RecorderDownloadState.Pausing?"正在暂停…":
                state==RecorderDownloadState.Verifying?"正在校验下载文件…":state==RecorderDownloadState.Cancelling?"正在终止下载…":
                state==RecorderDownloadState.Failed?"下载中断，可继续或终止":"正在下载新版本…";
            double ratio=snapshot.Total>0?(double)snapshot.Bytes/snapshot.Total:0;
            updateBar.width=Math.Max(1,(int)(1300*Math.Clamp(ratio,0,1)));
            updateDetail.text=$"{snapshot.Bytes/1048576d:F1} / {snapshot.Total/1048576d:F1} MB    {Math.Clamp(ratio,0,1):P0}";
            if(state==RecorderDownloadState.Failed){updateDetail.text+="\n"+snapshot.Error;updateDetail.color=Red;}
            updatePause.label.text=state is RecorderDownloadState.Paused or RecorderDownloadState.Failed?"继续":"暂停";
            updatePause.disabled=state is not (RecorderDownloadState.Downloading or RecorderDownloadState.Paused or RecorderDownloadState.Failed);
            updateCancel.disabled=state is RecorderDownloadState.Completed or RecorderDownloadState.Cancelled or RecorderDownloadState.Cancelling;
        }
        updateOk.label.text=completed?"OK":"返回";
    }

    void EnsureUpdatePanel(UI.MXButton template,Font font)
    {
        updatePanel=Child(dialog!.transform,"UpdateConfirmation");var parent=updatePanel.transform;
        var shield=Texture(parent,"UpdateShield",0,0,20000,20000,new Color(.02f,.04f,.06f,.68f),160);
        shield.gameObject.AddComponent<BoxCollider>().size=new Vector3(20000,20000,1);
        Texture(parent,"UpdateShadow",8,-9,1560,720,new Color(0,0,0,.24f),164);
        var body=Texture(parent,"UpdatePanel",0,0,1560,720,new Color(.97f,.99f,1,1),165);
        body.gameObject.AddComponent<BoxCollider>().size=new Vector3(1560,720,1);
        Texture(parent,"UpdateAccent",-774,0,12,720,Accent,166);
        updateHeading=Label(parent,font,"内录 MOD 更新",-670,295,1340,80,48);updateHeading.depth=170;
        updateBody=Label(parent,font,"",-670,165,1340,160,42);updateBody.depth=170;updateBody.spacingY=12;
        updateTrack=Texture(parent,"UpdateTrack",0,-18,1300,28,new Color(.77f,.83f,.88f,1),169);
        updateBar=Texture(parent,"UpdateBar",-650,-18,1,28,Accent,170);
        updateBar.pivot=UIWidget.Pivot.Left;updateBar.transform.localPosition=new Vector3(-650,-18,0);
        updateDetail=Label(parent,font,"",-670,-58,1340,125,32);updateDetail.depth=170;updateDetail.spacingY=10;
        updateYes=Button(template,parent,"AARecorder_UpdateYes","更新",new Vector3(340,-262,0),430,100,42,AcceptUpdate);
        updateNo=Button(template,parent,"AARecorder_UpdateNo","暂不更新",new Vector3(-340,-262,0),430,100,42,DismissUpdate);
        updatePause=Button(template,parent,"AARecorder_UpdatePause","暂停",new Vector3(-340,-262,0),430,100,42,ToggleUpdatePause);
        updateCancel=Button(template,parent,"AARecorder_UpdateCancel","终止",new Vector3(340,-262,0),430,100,42,CancelUpdate);
        updateOk=Button(template,parent,"AARecorder_UpdateOK","OK",new Vector3(0,-262,0),430,100,42,
            ()=>{if(updatePanelState==UpdatePanelState.Completed)ConfirmUpdateRestart();else DismissUpdate();});
        foreach(var button in new[]{updateYes,updateNo,updatePause,updateCancel,updateOk})RaiseTutorialButton(button,180);
        updatePanel.SetActive(false);RefreshUpdatePanel();
    }

    internal void StopUpdateWork()
    {
        updateCheckCancellation?.Cancel();
        updateDownload?.Dispose();updateSession?.Dispose();
        // Do not kill an applying helper. Its transaction completes/rolls back,
        // and it never restarts AA unless our explicit OK token was written.
    }
}
