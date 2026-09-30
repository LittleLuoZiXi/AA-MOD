using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using Object=UnityEngine.Object;
namespace AzureArchive.Recorder;

// Reuse the visual shell and controls of AA Settings; never clone SettingPanel
// or its callbacks. The persistent screen camera is excluded from video capture.
internal sealed partial class NativeRecorderUi
{
    readonly RecorderBehaviour owner;
    readonly List<(CatalogFileInfo Info,UI.MXButton Button,float Offset)> bindings=new();
    readonly List<(Camera Camera,int Mask)> cameraMasks=new();
    float nextScan,nextDisplay;
    GameObject? host,canvas,dialog,options,progress;
    Camera? camera;
    UICamera? inputCamera;
    UILabel title=null!,resolution=null!,gpu=null!,output=null!,status=null!,progressText=null!,progressDetail=null!;
    UILabel scaleTitle=null!,multiplierTitle=null!,neuralTitle=null!,dlssHint=null!;
    UITexture bar=null!;
    UI.MXButton fps=null!,scale=null!,multiplier=null!,neural=null!,dlss=null!,start=null!,choose=null!,close=null!,stop=null!;
    bool open,showProgress,choosing,dlssEnabled;
    Color activeButtonColor;
    internal string OutputFolder=FolderPicker.Videos;
    static readonly Color Normal=new(.16f,.26f,.36f,1),Red=new(.78f,.13f,.16f,1);
    static readonly Color DisabledText=new(.48f,.51f,.54f,1),DisabledButton=new(.69f,.71f,.73f,1);
    RecorderPlugin Config=>RecorderPlugin.Instance;
    internal NativeRecorderUi(RecorderBehaviour owner)=>this.owner=owner;
    internal Camera? ScreenCamera=>camera;
    internal bool IsVisible=>camera!=null && camera.enabled;
    internal bool DlssEnabled=>dlssEnabled && owner.EnhancementSupported;
    internal void ToggleSettings()
    {
        if(owner.Busy || choosing)return;
        EnsureDialog();open=!open;showProgress=false;
        if(open)
        {
            OutputFolder=FolderPicker.Videos;dlssEnabled=false;RefreshDlssControls();
            tutorialVisible=Config.TutorialSeenVersion.Value<TutorialVersion;
            if(tutorialVisible){tutorialPage=0;RefreshTutorial();}
        }
        else tutorialVisible=false;
        UpdateVisibility();
    }
    internal void ShowProgress(){EnsureDialog();tutorialVisible=false;showProgress=true;open=true;UpdateVisibility();}
    internal void Update()
    {
        if(Time.realtimeSinceStartup>=nextScan)
        {
            nextScan=Time.realtimeSinceStartup+1;bindings.RemoveAll(x=>x.Info==null);
            foreach(var info in Object.FindObjectsOfType<CatalogFileInfo>(true))
            {
                if(info.btn==null || bindings.Any(x=>x.Info.Pointer==info.Pointer))continue;
                var offset=info.bg!=null && info.bg.width>1600?-324:280;
                var button=Button(info.btn,info.btn.transform.parent,"AARecorder_Entry","内录",info.btn.transform.localPosition-new Vector3(offset,0,0),270,150,50,ToggleSettings);
                bindings.Add((info,button,offset));
            }
            if(dialog==null && bindings.Count>0)EnsureDialog();
        }
        bindings.RemoveAll(x=>x.Info==null || x.Button==null || x.Info.btn==null);
        foreach(var b in bindings)
        {
            b.Button.gameObject.SetActive(b.Info.btn.gameObject.activeInHierarchy && !owner.Busy);
            b.Button.transform.localPosition=b.Info.btn.transform.localPosition-new Vector3(b.Offset,0,0);
        }
        if(dialog==null)return;
        UpdateVisibility();if(!open)return;
        title.text=showProgress?owner.Busy?"正在内录":"内录结果":"内录设置";
        close.gameObject.SetActive(!owner.Busy && !choosing);
        if(showProgress)
        {
            progressText.text=owner.ProgressTitle;progressDetail.text=owner.ProgressDetail;
            bar.width=Math.Max(1,(int)(1800*owner.ProgressRatio));
            stop.label.text=owner.Busy?owner.Capturing||owner.Armed?"停止并保存":"取消增强":"完成";
            stop.disabled=owner.Busy && !owner.Capturing && !owner.Armed && !owner.Enhancing;return;
        }
        if(Time.realtimeSinceStartup>=nextDisplay)
        {
            nextDisplay=Time.realtimeSinceStartup+2;var d=MonitorResolution.Detect(Display.main.systemWidth,Display.main.systemHeight);
            resolution.text=$"{d.Width} × {d.Height}（本机显示器）";
        }
        fps.label.text=$"{Config.Fps.Value} fps";scale.label.text=$"{Config.Scale.Value} 倍";multiplier.label.text=$"{Config.Multiplier.Value} 倍";
        neural.label.text=Config.Neural.Value?"开启":"关闭";
        RefreshDlssControls();
        gpu.text=owner.GpuDescription;gpu.color=owner.EnhancementSupported?Normal:Red;
        output.text=OutputFolder;status.text=owner.Status;status.color=owner.Status.Contains("不支持")?Red:Normal;
        start.disabled=choosing;choose.disabled=choosing;choose.label.text=choosing?"选择中…":"选择文件夹";
        if(tutorialVisible)UpdateTutorial();
    }
    void UpdateVisibility()
    {
        if(camera==null || dialog==null)return;
        cameraMasks.RemoveAll(x=>x.Camera==null);
        if(open)
        {
            foreach(var cam in Camera.allCameras)
                if(cam!=camera && !cameraMasks.Any(x=>x.Camera.Pointer==cam.Pointer))
                {cameraMasks.Add((cam,cam.cullingMask));cam.cullingMask&=~(1<<31);}
        }
        else
        {
            foreach(var item in cameraMasks)item.Camera.cullingMask=item.Mask;
            cameraMasks.Clear();
        }
        camera.enabled=open;if(inputCamera!=null)inputCamera.enabled=open;dialog.SetActive(open);camera.clearFlags=showProgress?CameraClearFlags.SolidColor:CameraClearFlags.Depth;
        camera.backgroundColor=new Color(.90f,.96f,1f,1);
        close.gameObject.SetActive(!owner.Busy && !choosing);
        if(canvas!=null)canvas.transform.localScale=Vector3.one*Math.Min(2f/1440f,2f*Screen.width/Screen.height/2560f);
        options!.SetActive(!showProgress);progress!.SetActive(showProgress);
        tutorial?.SetActive(!showProgress && tutorialVisible);
        SetTutorialInputLock(TutorialVisible);
    }
    void EnsureDialog()
    {
        if(dialog!=null)return;
        var original=Object.FindObjectsOfType<Transform>(true).FirstOrDefault(x=>x.name=="UIPopup_Settings");
        if(original==null)throw new InvalidOperationException("未找到 AA 原生设置面板，请在剧情列表打开内录。");
        var sourceBase=original.Find("Center/Popup/Base");
        var sourceButton=original.Find("Center/Popup/BG/List/MainTable/Table/NicknameEntry/EditBtn").GetComponent<UI.MXButton>();
        activeButtonColor=sourceButton.GetComponentInChildren<UISprite>(true).color;
        var font=sourceButton.GetComponentInChildren<UILabel>(true).trueTypeFont;
        host=new GameObject("AARecorder_Display");Object.DontDestroyOnLoad(host);
        var camGo=new GameObject("AARecorder_ScreenCamera");camGo.transform.SetParent(host.transform,false);camGo.transform.position=new Vector3(0,0,-10);
        camera=camGo.AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=1;camera.nearClipPlane=.1f;camera.farClipPlane=100;
        camera.depth=100000;camera.cullingMask=1<<31;camera.enabled=false;camera.allowHDR=false;camera.allowMSAA=false;
        inputCamera=camGo.AddComponent<UICamera>();inputCamera.eventType=UICamera.EventType.UI_3D;inputCamera.eventReceiverMask=1<<31;inputCamera.enabled=false;
        canvas=new GameObject("AARecorder_Canvas");canvas.transform.SetParent(host.transform,false);canvas.layer=31;
        var panel=canvas.AddComponent<UIPanel>();panel.depth=10000;
        var body=canvas.AddComponent<Rigidbody>();body.isKinematic=true;body.useGravity=false;
        dialog=new GameObject("AARecorder_Dialog");dialog.transform.SetParent(canvas.transform,false);dialog.layer=31;dialog.SetActive(false);
        var dim=Texture(dialog.transform,"Dim",0,0,20000,20000,new Color(0,0,0,.5f),0);
        var block=dim.gameObject.AddComponent<BoxCollider>();block.size=new Vector3(20000,20000,1);
        var shell=Object.Instantiate(sourceBase.gameObject,dialog.transform);shell.name="SettingsShell";shell.transform.localPosition=Vector3.zero;shell.transform.localScale=Vector3.one;
        foreach(var tr in shell.GetComponentsInChildren<Transform>(true))tr.gameObject.layer=31;
        foreach(var rect in shell.GetComponentsInChildren<UIRect>(true))ClearAnchors(rect);
        foreach(var widget in shell.GetComponentsInChildren<UIWidget>(true))widget.depth=5;
        title=shell.transform.Find("Label_Title").GetComponent<UILabel>();title.trueTypeFont=font;title.text="内录设置";title.width=1800;title.depth=8;title.transform.localPosition=new Vector3(0,540,0);
        close=shell.transform.Find("Btn_Close").GetComponent<UI.MXButton>();close.name="AARecorder_Close";Bind(close,()=>{open=false;showProgress=false;});
        foreach(var widget in close.GetComponentsInChildren<UIWidget>(true))widget.depth=10;
        options=Child(dialog.transform,"Options");progress=Child(dialog.transform,"Progress");
        Label(options.transform,font,"录制分辨率",-1050,415,420,70,46);resolution=Label(options.transform,font,"",-560,415,1100,70,46);
        Button(sourceButton,options.transform,"AARecorder_Tutorial","新手教程",new Vector3(780,390,0),410,80,36,ShowTutorial);
        Label(options.transform,font,"视频帧率",-1050,285,360,70,46);
        fps=Button(sourceButton,options.transform,"AARecorder_Fps","",new Vector3(-480,260,0),330,90,40,()=>Cycle(Config.Fps,new[]{24,25,30,50,60}));
        Label(options.transform,font,"DLSS 增强",130,285,440,70,46);
        dlss=Button(sourceButton,options.transform,"AARecorder_DLSS","DLSS：关闭",new Vector3(780,260,0),410,90,40,()=>
        {if(owner.EnhancementSupported){dlssEnabled=!dlssEnabled;RefreshDlssControls();}});
        scaleTitle=Label(options.transform,font,"超分倍率",-1050,155,360,70,46);
        scale=Button(sourceButton,options.transform,"AARecorder_Scale","",new Vector3(-480,130,0),330,90,40,()=>{if(DlssEnabled)Cycle(Config.Scale,new[]{1,2,4});});
        multiplierTitle=Label(options.transform,font,"补帧倍率",130,155,400,70,46);
        multiplier=Button(sourceButton,options.transform,"AARecorder_Multiplier","",new Vector3(780,130,0),410,90,40,()=>{if(DlssEnabled)Cycle(Config.Multiplier,new[]{1,2,3,4});});
        neuralTitle=Label(options.transform,font,"神经渲染",-1050,25,360,70,46);
        neural=Button(sourceButton,options.transform,"AARecorder_Neural","",new Vector3(-480,0,0),330,90,40,()=>{if(DlssEnabled)Config.Neural.Value=!Config.Neural.Value;});
        dlssHint=Label(options.transform,font,"开启 DLSS 后可设置增强选项",130,20,1000,60,34);
        gpu=Label(options.transform,font,"",-1050,-75,2100,70,34);
        Label(options.transform,font,"本次输出文件夹",-1050,-175,650,70,46);
        choose=Button(sourceButton,options.transform,"AARecorder_ChooseFolder","选择文件夹",new Vector3(780,-200,0),450,90,38,ChooseFolder);
        output=Label(options.transform,font,"",-1050,-265,2100,80,35);
        Label(options.transform,font,"默认 Windows 视频文件夹 · 后台逐帧导出，视频保持正常时长",-1050,-350,2100,60,31);
        status=Label(options.transform,font,"",-1050,-420,1470,100,30);
        start=Button(sourceButton,options.transform,"AARecorder_Start","开始内录",new Vector3(780,-460,0),460,110,44,()=>owner.StartFromOptions(DlssEnabled,OutputFolder));
        progressText=Label(progress.transform,font,"准备内录…",-900,230,1800,100,60);
        Texture(progress.transform,"Track",0,55,1800,34,new Color(.77f,.83f,.88f,1),20);
        bar=Texture(progress.transform,"Bar",-900,55,1,34,new Color(.18f,.73f,.91f,1),21);bar.pivot=UIWidget.Pivot.Left;bar.transform.localPosition=new Vector3(-900,55,0);
        progressDetail=Label(progress.transform,font,"",-900,-65,1800,220,40);
        stop=Button(sourceButton,progress.transform,"AARecorder_Stop","停止并保存",new Vector3(0,-360,0),550,110,44,()=>{if(owner.Capturing||owner.Armed)owner.Stop("手动停止");else if(owner.Busy)owner.CancelEnhancement();else{open=false;showProgress=false;}});
        EnsureTutorial(sourceButton,font);
        RefreshDlssControls();UpdateVisibility();
    }
    void RefreshDlssControls()
    {
        bool supported=owner.EnhancementSupported,enabled=DlssEnabled;
        if(!supported)dlssEnabled=false;
        dlss.disabled=!supported;
        dlss.label.text=supported?(enabled?"DLSS：开启":"DLSS：关闭"):"DLSS：暂不支持";
        dlss.label.color=supported?Normal:Red;
        foreach(var button in new[]{scale,multiplier,neural})
        {
            button.disabled=!enabled;
            button.label.color=enabled?Normal:DisabledText;
            button.GetComponentInChildren<UISprite>(true).color=enabled?activeButtonColor:DisabledButton;
        }
        foreach(var label in new[]{scaleTitle,multiplierTitle,neuralTitle,dlssHint})label.color=enabled?Normal:DisabledText;
        dlssHint.text=enabled?"3 / 4 倍补帧为实验模式":supported?"开启 DLSS 后可设置增强选项":"当前显卡暂不支持 DLSS 增强";
    }
    async void ChooseFolder()
    {
        if(choosing || owner.Busy)return;choosing=true;
        try{var selected=await FolderPicker.ChooseAsync(OutputFolder);owner.OnMainThread(()=>{if(!string.IsNullOrEmpty(selected))OutputFolder=selected;choosing=false;});}
        catch(Exception ex){owner.OnMainThread(()=>{choosing=false;owner.SetStatus("选择文件夹失败："+ex.Message);});}
    }
    void Cycle(BepInEx.Configuration.ConfigEntry<int> config,int[] choices)=>config.Value=choices[(Array.IndexOf(choices,config.Value)+1)%choices.Length];
    static GameObject Child(Transform parent,string name){var go=new GameObject(name);go.layer=31;go.transform.SetParent(parent,false);return go;}
    static void ClearAnchors(UIRect r){r.leftAnchor.target=null;r.rightAnchor.target=null;r.topAnchor.target=null;r.bottomAnchor.target=null;r.ResetAnchors();}
    static void Bind(UI.MXButton b,Action click)
    {
        b.onClicked=null;b.onPressed=null;b.onClick=new UnityEvent();b.onPress=new UnityEvent();b.onLongPress=new UnityEvent();b.disabled=false;
        // Keep native press animation/sound but use NGUI's release-inside click
        // event. MXButton's custom cursor check assumes the original UI camera.
        var listener=UIEventListener.Get(b.gameObject);listener.Clear();
        listener.onClick=(UIEventListener.VoidDelegate)(Action<GameObject>)(_=>
        {
            if(b.disabled || !b.gameObject.activeInHierarchy)return;
            if(RecorderBehaviour.Instance?.Ui?.TutorialVisible==true &&
                b.name is not ("AARecorder_TutorialTarget" or "AARecorder_TutorialPrevious" or "AARecorder_TutorialNext" or "AARecorder_TutorialSkip"))return;
            try{RecorderPlugin.Instance.Log.LogInfo("Recorder control: "+b.name);click();}catch(Exception ex){RecorderPlugin.Instance.Log.LogError(ex);}
        });
    }
    static UI.MXButton Button(UI.MXButton template,Transform parent,string name,string text,Vector3 pos,int width,int height,int size,Action click)
    {
        var go=Object.Instantiate(template.gameObject,parent);go.name=name;go.SetActive(false);
        foreach(var tr in go.GetComponentsInChildren<Transform>(true))tr.gameObject.layer=parent.gameObject.layer;
        foreach(var r in go.GetComponentsInChildren<UIRect>(true))ClearAnchors(r);
        foreach(var drag in go.GetComponentsInChildren<UIDragScrollView>(true))drag.enabled=false;
        var b=go.GetComponent<UI.MXButton>();Bind(b,click);
        // NGUI ranks a collider by the widget on that same object, not by its
        // visible child sprites. Leaving the cloned root at depth zero ties it
        // with the modal blocker and makes input depend on physics hit order.
        var root=go.GetComponent<UIWidget>();
        if(root!=null){root.depth=22;root.width=width;root.height=height;root.autoResizeBoxCollider=false;}
        var bg=go.GetComponentInChildren<UISprite>(true);bg.width=width;bg.height=height;bg.depth=22;
        foreach(var detail in bg.GetComponentsInChildren<UISprite>(true))if(detail.Pointer!=bg.Pointer)detail.gameObject.SetActive(false);
        var box=go.GetComponent<BoxCollider>();box.size=new Vector3(width,height+10,1);box.center=Vector3.zero;
        var label=go.GetComponentInChildren<UILabel>(true);label.text=text;label.width=width-20;label.height=height;label.maxLineCount=1;label.fontSize=size;label.supportEncoding=false;label.depth=24;b.label=label;
        go.transform.localPosition=pos;go.transform.localScale=Vector3.one;go.SetActive(true);return b;
    }
    static UILabel Label(Transform parent,Font font,string text,float x,float y,int width,int height,int size)
    {
        var go=Child(parent,"RecorderText");var label=go.AddComponent<UILabel>();label.trueTypeFont=font;label.pivot=UIWidget.Pivot.TopLeft;label.alignment=NGUIText.Alignment.Left;
        label.width=width;label.height=height;label.fontSize=size;label.color=Normal;label.overflowMethod=UILabel.Overflow.ShrinkContent;label.supportEncoding=false;label.depth=24;label.text=text;go.transform.localPosition=new Vector3(x,y,0);return label;
    }
    static UITexture Texture(Transform parent,string name,float x,float y,int width,int height,Color color,int depth)
    {var go=Child(parent,name);var t=go.AddComponent<UITexture>();t.mainTexture=Texture2D.whiteTexture;t.width=width;t.height=height;t.color=color;t.depth=depth;go.transform.localPosition=new Vector3(x,y,0);return t;}
}
