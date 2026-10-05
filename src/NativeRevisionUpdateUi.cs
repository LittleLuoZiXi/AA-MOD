using System;
using System.Linq;
using AzureArchive.Automation;
using Studio.Scripts.OperationManagement;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace AzureArchive.RevisionCompare;

// Created and driven on Unity's main thread. The caller owns downloads,
// cancellation, installation, restart policy and Escape handling.
internal sealed class NativeRevisionUpdateUi : IDisposable
{
    const int Width=1200, Height=600;
    static readonly Color Ink=new(.16f,.26f,.36f,1);
    static readonly Color Accent=new(.18f,.73f,.91f,1);
    static readonly Color ErrorInk=new(.72f,.22f,.25f,1);
    enum DisplayState { Offer, Download, Verify, WaitingForExit, Failed }
    readonly Action viewUpdate,dismiss;
    readonly Action<string> report;
    GameObject? window,body;
    UIRoot? uiRoot;
    Camera? screenCamera;
    UITexture? shield,progressTrack,progressBar;
    BoxCollider? shieldCollider;
    UILabel? heading,versions,help;
    UI.MXButton? updateButton,dismissButton;
    DisplayState state;
    string stateMessage="";
    float progress;
    bool disposed;

    internal NativeRevisionUpdateUi(Action viewUpdate,Action dismiss,Action<string> report)
    {this.viewUpdate=viewUpdate;this.dismiss=dismiss;this.report=report;}

    internal bool IsVisible=>window!=null && window.activeInHierarchy;
    internal bool IsBusy=>state is DisplayState.Download or DisplayState.Verify or DisplayState.WaitingForExit;
    internal bool CanDismiss=>state!=DisplayState.WaitingForExit;

    // Percent is 0..100. A negative/unknown value shows the status without
    // inventing a percentage. These methods perform UI work only.
    internal void SetDownloading(float percent,string status="正在下载更新…")
    {
        state=DisplayState.Download;stateMessage=status;
        progress=float.IsNaN(percent) || float.IsInfinity(percent) || percent<0?-1:Math.Clamp(percent,0,100);
        RefreshState();
    }

    internal void SetVerifying(string status="正在校验更新文件…")
    {state=DisplayState.Verify;stateMessage=status;progress=100;RefreshState();}

    internal void SetWaitingForExit(string status="更新已准备好，正在等待 AA 退出…")
    {state=DisplayState.WaitingForExit;stateMessage=status;progress=100;RefreshState();}

    internal void ShowFailure(string message)
    {state=DisplayState.Failed;stateMessage=message;RefreshState();}

    internal void SetReady()
    {state=DisplayState.Offer;stateMessage="";progress=0;RefreshState();}

    internal bool TryShow(string currentVersion,string latestVersion)
    {
        if(disposed)return false;
        if(IsVisible){Update();return true;}
        if(!ReadyForNotice())return false;
        if(!TryFindHost(out var root,out var camera,out int layer))return false;
        if(!TryFindTemplate(out var template,out var font))return false;

        if(window==null || uiRoot==null || uiRoot.Pointer!=root.Pointer || screenCamera==null || screenCamera.Pointer!=camera.Pointer)
        {
            DestroyWindow();
            uiRoot=root;screenCamera=camera;
            try{CreateWindow(template,font,layer);}
            catch{DestroyWindow();throw;}
        }
        versions!.text="本地版本  "+currentVersion+"\n最新版本  "+latestVersion;
        RefreshState();
        Layout();
        window!.SetActive(true);
        return true;
    }

    internal void Update()
    {
        if(!IsVisible)return;
        if(uiRoot==null || !uiRoot.gameObject.activeInHierarchy || screenCamera==null || !screenCamera.enabled)
        {Hide();return;}
        Layout();
    }

    static bool ReadyForNotice()
    {
        var settings=Object.FindObjectOfType<UserSettings>();
        if(settings==null || !settings.isReady)return false;
        if(Object.FindObjectsOfType<Loading>().Any(x=>x.loadingCount>0))return false;
        var resources=Object.FindObjectOfType<ScenarioResourceManager>();
        if(resources!=null && !resources.AreDbsLoaded)return false;
        if(UIPopupList.isOpen || UICamera.inputHasFocus || UICamera.isDragging)return false;
        if(Object.FindObjectsOfType<UI.UIPopup>().Any(x=>x.gameObject.activeInHierarchy))return false;
        if(Object.FindObjectsOfType<Studio.Scripts.Popup>().Any(x=>x.isShown))return false;
        var session=AuthoringEditorSession.Current;
        if(session!=null && (session.SaveInProgress || session.IsApplying || OperationManager.Working))return false;
        // Both MOD windows are independent of AA's UIPopup hierarchy.
        if(GameObject.Find("AARevisionCompare_Window")!=null || GameObject.Find("AARecorder_Dialog")!=null)return false;
        return true;
    }

    static bool TryFindHost(out UIRoot root,out Camera camera,out int layer)
    {
        // Use a visible native screen control to choose its existing NGUI
        // camera and root; this also excludes offscreen scenario preview roots.
        foreach(var button in Object.FindObjectsOfType<UI.MXButton>())
        {
            if(!button.gameObject.activeInHierarchy || !button.enabled)continue;
            var candidateRoot=button.GetComponentInParent<UIRoot>();
            if(candidateRoot==null || !candidateRoot.gameObject.activeInHierarchy)continue;
            int candidateLayer=button.gameObject.layer;
            var eventCamera=UICamera.FindCameraForLayer(candidateLayer);
            if(eventCamera==null || !eventCamera.enabled || eventCamera.eventType!=UICamera.EventType.UI_3D)continue;
            var candidateCamera=eventCamera.cachedCamera;
            if(candidateCamera==null || !candidateCamera.enabled || candidateCamera.targetTexture!=null)continue;
            root=candidateRoot;camera=candidateCamera;layer=candidateLayer;return true;
        }
        root=null!;camera=null!;layer=0;return false;
    }

    static bool TryFindTemplate(out UI.MXButton template,out Font font)
    {
        // Settings is loaded on AA's main screen, before any script editor.
        // A system-popup prefab is the fallback when a skin omits settings.
        var settings=Object.FindObjectsOfType<Transform>(true).FirstOrDefault(x=>x.name=="UIPopup_Settings");
        var preferred=settings?.Find("Center/Popup/BG/List/MainTable/Table/NicknameEntry/EditBtn")?.GetComponent<UI.MXButton>();
        if(TryUseTemplate(preferred,out template,out font))return true;
        var manager=Object.FindObjectOfType<PopupManager>();
        if(manager?.systemPopupPrefab!=null)
            foreach(var button in manager.systemPopupPrefab.GetComponentsInChildren<UI.MXButton>(true))
                if(TryUseTemplate(button,out template,out font))return true;
        foreach(var button in Object.FindObjectsOfType<UI.MXButton>())
            if(!button.name.StartsWith("AARevisionCompare_",StringComparison.Ordinal) && TryUseTemplate(button,out template,out font))return true;
        template=null!;font=null!;return false;
    }

    static bool TryUseTemplate(UI.MXButton? candidate,out UI.MXButton template,out Font font)
    {
        var nativeFont=candidate?.GetComponentsInChildren<UILabel>(true).Select(x=>x.trueTypeFont).FirstOrDefault(x=>x!=null);
        if(candidate!=null && nativeFont!=null && candidate.GetComponentInChildren<UISprite>(true)!=null)
        {template=candidate;font=nativeFont;return true;}
        template=null!;font=null!;return false;
    }

    void CreateWindow(UI.MXButton template,Font font,int layer)
    {
        window=new GameObject("AARevisionCompare_UpdateNotice");
        window.SetActive(false);window.layer=layer;window.transform.SetParent(uiRoot!.transform,false);
        var panel=window.AddComponent<UIPanel>();
        int existingDepth=uiRoot.GetComponentsInChildren<UIPanel>().Select(x=>x.depth).DefaultIfEmpty(0).Max();
        panel.depth=Math.Max(9400,existingDepth+20);
        shield=Fill(window.transform,"UpdateShield",0,0,20000,20000,new Color(.02f,.04f,.06f,.48f),0);
        shieldCollider=shield.gameObject.AddComponent<BoxCollider>();shieldCollider.size=new Vector3(20000,20000,1);
        body=Child(window.transform,"UpdateBody");
        Fill(body.transform,"UpdateShadow",7,-9,Width+12,Height+12,new Color(.04f,.12f,.20f,.24f),3);
        var background=Fill(body.transform,"UpdatePanel",0,0,Width,Height,new Color(.97f,.99f,1,1),4);
        background.gameObject.AddComponent<BoxCollider>().size=new Vector3(Width,Height,1);
        Fill(body.transform,"UpdateAccent",-Width/2f+4,0,8,Height,Accent,5);
        Fill(body.transform,"UpdateRule",0,172,Width-112,3,Accent,5);
        heading=Label(body.transform,font,"UpdateTitle","剧情改稿同步MOD有新版本",-Width/2f+58,246,Width-116,64,42,7);
        versions=Label(body.transform,font,"UpdateVersions","",-Width/2f+58,133,Width-116,112,34,7);
        versions.maxLineCount=2;versions.spacingY=15;
        help=Label(body.transform,font,"UpdateHelp","下载完成后将重启 AA 应用更新。",-Width/2f+58,-14,Width-116,98,30,7);
        help.maxLineCount=2;help.spacingY=10;
        progressTrack=Fill(body.transform,"AARevisionCompare_UpdateProgressTrack",0,-126,Width-116,18,new Color(.77f,.83f,.88f,1),7);
        progressBar=Fill(body.transform,"AARevisionCompare_UpdateProgress",-(Width-116)/2f,-126,1,18,Accent,8);
        progressBar.pivot=UIWidget.Pivot.Left;
        progressBar.transform.localPosition=new Vector3(-(Width-116)/2f,-126,0);
        dismissButton=Button(template,body.transform,font,"AARevisionCompare_DismissUpdate","暂不更新",new Vector3(-245,-207,0),dismiss);
        updateButton=Button(template,body.transform,font,"AARevisionCompare_ViewUpdate","更新版本",new Vector3(245,-207,0),viewUpdate);
        RefreshState();
    }

    void RefreshState()
    {
        if(heading==null || help==null || updateButton==null || dismissButton==null)return;
        bool offer=state==DisplayState.Offer,failed=state==DisplayState.Failed;
        heading.text=offer?"剧情改稿同步MOD有新版本":failed?"剧情改稿同步MOD更新未完成":"正在更新剧情改稿同步MOD";
        help.color=failed?ErrorInk:Ink;
        help.text=offer?"下载完成后将重启 AA 应用更新。":stateMessage;
        if(state==DisplayState.Download && progress>=0)help.text+="\n"+progress.ToString("F0")+"%";
        updateButton.disabled=IsBusy;
        dismissButton.disabled=!CanDismiss;
        updateButton.label.text=failed?"重试更新":IsBusy?"更新中…":"更新版本";
        dismissButton.label.text=state==DisplayState.WaitingForExit?"等待退出":IsBusy?"取消":failed?"关闭":"暂不更新";
        if(progressTrack!=null)progressTrack.gameObject.SetActive(IsBusy);
        if(progressBar!=null)
        {
            progressBar.gameObject.SetActive(IsBusy && progress>=0);
            progressBar.width=Math.Max(1,(int)Math.Round((Width-116)*Math.Clamp(progress,0,100)/100));
        }
    }

    void Layout()
    {
        if(window==null || body==null || screenCamera==null || uiRoot==null)return;
        var parent=uiRoot.transform;
        float distance=screenCamera.WorldToScreenPoint(parent.position).z;
        if(distance<=screenCamera.nearClipPlane)distance=screenCamera.nearClipPlane+1;
        window.transform.position=screenCamera.ViewportToWorldPoint(new Vector3(.5f,.5f,distance));
        var lower=parent.InverseTransformPoint(screenCamera.ViewportToWorldPoint(new Vector3(0,0,distance)));
        var upper=parent.InverseTransformPoint(screenCamera.ViewportToWorldPoint(new Vector3(1,1,distance)));
        float width=Math.Abs(upper.x-lower.x),height=Math.Abs(upper.y-lower.y);
        if(width<=0 || height<=0)return;
        float scale=Math.Min(1f,Math.Min(width*.88f/Width,height*.86f/Height));
        body.transform.localScale=new Vector3(scale,scale,1);
        if(shield!=null){shield.width=(int)Math.Ceiling(width)+4;shield.height=(int)Math.Ceiling(height)+4;}
        if(shieldCollider!=null)shieldCollider.size=new Vector3(width+4,height+4,1);
    }

    UI.MXButton Button(UI.MXButton template,Transform parent,Font font,string name,string text,Vector3 position,Action click)
    {
        const int width=370,height=88;
        // The parent is inactive during cloning, so old OnEnable handlers never
        // run. Clear native delegates and every copied NGUI event listener.
        var go=Object.Instantiate(template.gameObject,parent);go.SetActive(false);go.name=name;
        foreach(var child in go.GetComponentsInChildren<Transform>(true))child.gameObject.layer=parent.gameObject.layer;
        foreach(var rect in go.GetComponentsInChildren<UIRect>(true))ClearAnchors(rect);
        foreach(var listener in go.GetComponentsInChildren<UIEventListener>(true))listener.Clear();
        foreach(var oldButton in go.GetComponentsInChildren<UI.MXButton>(true))
        {
            oldButton.onClicked=null;oldButton.onPressed=null;
            oldButton.onClick=new UnityEvent();oldButton.onPress=new UnityEvent();oldButton.onLongPress=new UnityEvent();
            oldButton.disabled=false;oldButton.pressed=false;oldButton.timerOn=false;
            if(oldButton.gameObject.Pointer!=go.Pointer)oldButton.enabled=false;
        }
        foreach(var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
            if(behaviour.TryCast<UIRect>()==null && behaviour.TryCast<UI.MXButton>()==null && behaviour.TryCast<UIEventListener>()==null)
                behaviour.enabled=false;
        foreach(var collider in go.GetComponentsInChildren<Collider>(true))collider.enabled=false;
        foreach(var label in go.GetComponentsInChildren<UILabel>(true))label.enabled=false;
        var button=go.GetComponent<UI.MXButton>();button.enabled=true;
        var root=go.GetComponent<UIWidget>() ?? go.AddComponent<UIWidget>();
        root.width=width;root.height=height;root.depth=20;root.autoResizeBoxCollider=false;
        var sprite=go.GetComponentInChildren<UISprite>(true);
        foreach(var detail in go.GetComponentsInChildren<UISprite>(true))if(detail.Pointer!=sprite.Pointer)detail.enabled=false;
        sprite.enabled=true;sprite.width=width;sprite.height=height;sprite.depth=20;sprite.transform.localPosition=Vector3.zero;
        var box=go.GetComponent<BoxCollider>() ?? go.AddComponent<BoxCollider>();
        box.enabled=true;box.size=new Vector3(width,height,1);box.center=Vector3.zero;
        var newLabel=Label(go.transform,font,"UpdateButtonText",text,0,0,width-24,height,34,22);
        newLabel.pivot=UIWidget.Pivot.Center;newLabel.alignment=NGUIText.Alignment.Center;newLabel.transform.localPosition=Vector3.zero;
        button.label=newLabel;button.sprite=sprite;
        var events=UIEventListener.Get(go);events.Clear();
        events.onClick=(UIEventListener.VoidDelegate)(Action<GameObject>)(_=>
        {
            if(button.disabled || !IsVisible)return;
            try{click();}catch(Exception error){report("无法处理更新提示："+error.Message);}
        });
        go.transform.localPosition=position;go.transform.localScale=Vector3.one;go.SetActive(true);
        return button;
    }

    static GameObject Child(Transform parent,string name)
    {var go=new GameObject(name);go.layer=parent.gameObject.layer;go.transform.SetParent(parent,false);return go;}
    static void ClearAnchors(UIRect rect)
    {rect.leftAnchor.target=null;rect.rightAnchor.target=null;rect.topAnchor.target=null;rect.bottomAnchor.target=null;rect.ResetAnchors();}
    static UITexture Fill(Transform parent,string name,float x,float y,int width,int height,Color color,int depth)
    {var go=Child(parent,name);var widget=go.AddComponent<UITexture>();widget.mainTexture=Texture2D.whiteTexture;widget.width=width;widget.height=height;widget.color=color;widget.depth=depth;go.transform.localPosition=new Vector3(x,y,0);return widget;}
    static UILabel Label(Transform parent,Font font,string name,string text,float x,float y,int width,int height,int size,int depth)
    {var go=Child(parent,name);var label=go.AddComponent<UILabel>();label.trueTypeFont=font;label.pivot=UIWidget.Pivot.TopLeft;label.alignment=NGUIText.Alignment.Left;label.width=width;label.height=height;label.fontSize=size;label.maxLineCount=1;label.color=Ink;label.overflowMethod=UILabel.Overflow.ShrinkContent;label.supportEncoding=false;label.depth=depth;label.text=text;go.transform.localPosition=new Vector3(x,y,0);return label;}

    internal void Hide(){if(window!=null)window.SetActive(false);}
    void DestroyWindow()
    {
        if(window!=null){window.SetActive(false);Object.Destroy(window);}
        window=null;body=null;uiRoot=null;screenCamera=null;shield=null;shieldCollider=null;
        heading=null;versions=null;help=null;updateButton=null;dismissButton=null;progressTrack=null;progressBar=null;
    }
    public void Dispose(){if(disposed)return;disposed=true;DestroyWindow();}
}
