using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Object=UnityEngine.Object;

namespace AzureArchive.Recorder;

internal sealed partial class NativeRecorderUi
{
    const int TutorialVersion=1;
    GameObject? tutorial,tutorialBubble;
    bool tutorialVisible,tutorialInputLocked;
    int tutorialPage;
    UILabel tutorialHeading=null!,tutorialBody=null!,tutorialCount=null!,tutorialArrow=null!,tutorialHint=null!;
    UI.MXButton tutorialPrevious=null!,tutorialNext=null!,tutorialSkip=null!,tutorialTarget=null!;
    UITexture? tutorialPortrait;
    UITexture[] tutorialShade=Array.Empty<UITexture>(),tutorialOutline=Array.Empty<UITexture>();
    readonly List<(UICamera Camera,bool Enabled)> tutorialInputs=new();
    readonly Texture2D?[] tutorialArt=new Texture2D?[6];
    static readonly Color Accent=new(.13f,.67f,.85f,1);
    internal bool TutorialVisible=>tutorialVisible && open && !showProgress;
    internal int TutorialPage=>tutorialPage;
    internal int TutorialPageCount=>TutorialPages.Length;
    internal bool TutorialArtLoaded=>Array.TrueForAll(tutorialArt,x=>x!=null);
    internal string TutorialArtName=>tutorialPortrait?.mainTexture?.name??"";
    internal int TutorialArtId=>tutorialPortrait?.mainTexture?.GetInstanceID()??0;
    static readonly (string Heading,string Body,Rect Target)[] TutorialPages=
    {
        ("老师，从画面设置开始吧",
         "分辨率会自动匹配你的显示器。\n导出前先选中剧情，并保存编辑器里的改动。",
         new Rect(-1090,355,1580,110)),
        ("在这里选择视频帧率",
         "这里可以切换 24 / 25 / 30 / 50 / 60 fps。\n第一次使用可先选 30 fps，普通录制无需 DLSS。",
         new Rect(-665,205,370,110)),
        ("决定视频放在哪里",
         "选择本次保存位置；默认是 Windows「视频」文件夹。\n成片如 20000101-1.mp4，按当天序号递增，次日从 1 开始。",
         new Rect(535,-257,490,118)),
        ("需要增强时，再打开 DLSS",
         "显卡支持、组件齐全后手动开启；缺组件请运行安装器补齐。\n3 / 4 倍补帧为实验模式；神经渲染可能改变画风。",
         new Rect(552,200,455,120)),
        ("设置好后，从这里开始",
         "录制时可停止并保存，增强时可取消；进度不会进入成片。\n开启 DLSS 时，原片另存于所选文件夹下的「原始视频」。",
         new Rect(520,-525,520,135)),
        ("随时可以回来看看",
         "以后想再看，点击这里即可重温教程。\n点击高亮位置完成引导，再按自己的需要开始录制吧。",
         new Rect(550,340,460,110))
    };
    void EnsureTutorial(UI.MXButton template,Font font)
    {
        tutorial=Child(dialog!.transform,"Tutorial");var parent=tutorial.transform;
        tutorialShade=new UITexture[4];
        for(int i=0;i<4;i++)tutorialShade[i]=Texture(parent,"TutorialShade"+i,0,0,1,1,new Color(.02f,.04f,.06f,.70f),60);
        // Keep an input shield even through the visual hole. Only the higher
        // tutorial proxy/navigation receives clicks; originals never activate.
        var shield=Texture(parent,"TutorialInputShield",0,0,20000,20000,new Color(0,0,0,.01f),70);
        shield.gameObject.AddComponent<BoxCollider>().size=new Vector3(20000,20000,1);
        tutorialOutline=new UITexture[8];
        for(int i=0;i<8;i++)tutorialOutline[i]=Texture(parent,"TutorialOutline"+i,0,0,1,1,i<4?Accent:Color.white,76+i/4);
        tutorialTarget=Button(template,parent,"AARecorder_TutorialTarget","",Vector3.zero,100,100,30,()=>MoveTutorial(1));
        RaiseTutorialButton(tutorialTarget,80);
        tutorialArrow=Label(parent,font,"↓",0,0,120,120,108);tutorialArrow.alignment=NGUIText.Alignment.Center;
        tutorialArrow.fontStyle=FontStyle.Bold;tutorialArrow.color=new Color(1,.94f,.32f,1);tutorialArrow.depth=90;
        tutorialArrow.effectStyle=UILabel.Effect.Outline;tutorialArrow.effectColor=new Color(.14f,.23f,.33f,1);
        tutorialCount=Label(parent,font,"",-1080,565,1300,75,38);tutorialCount.color=Color.white;tutorialCount.depth=100;
        tutorialBubble=Child(parent,"GuideSpeech");var speech=tutorialBubble.transform;
        Texture(speech,"SpeechShadow",5,-5,1640,270,new Color(0,0,0,.25f),92);
        Texture(speech,"SpeechPanel",0,0,1640,270,new Color(.97f,.99f,1,1),93);
        Texture(speech,"SpeechAccent",-816,0,8,270,Accent,94);
        var speaker=Label(speech,font,"阿罗娜",-770,114,1500,50,28);speaker.color=Accent;speaker.depth=95;
        tutorialHeading=Label(speech,font,"",-770,66,1540,65,42);tutorialHeading.depth=95;
        tutorialBody=Label(speech,font,"",-770,0,1540,116,34);tutorialBody.spacingY=12;tutorialBody.depth=95;
        LoadTutorialArt(parent);
        tutorialHint=Label(parent,font,"点击高亮位置继续 · 引导不会更改设置",-675,-591,1290,70,29);
        tutorialHint.alignment=NGUIText.Alignment.Center;tutorialHint.color=Color.white;tutorialHint.depth=100;
        tutorialPrevious=Button(template,parent,"AARecorder_TutorialPrevious","‹ 上一步",new Vector3(-980,-620,0),310,78,34,()=>MoveTutorial(-1));
        tutorialNext=Button(template,parent,"AARecorder_TutorialNext","下一步 ›",new Vector3(970,-620,0),310,78,34,()=>MoveTutorial(1));
        tutorialSkip=Button(template,parent,"AARecorder_TutorialSkip","跳过",new Vector3(1000,540,0),220,80,36,FinishTutorial);
        foreach(var button in new[]{tutorialPrevious,tutorialNext,tutorialSkip})RaiseTutorialButton(button,110);
        tutorial.SetActive(false);
    }
    static void RaiseTutorialButton(UI.MXButton button,int depth)
    {
        foreach(var widget in button.GetComponentsInChildren<UIWidget>(true))widget.depth=depth;
        button.label.depth=depth+2;
    }
    void LoadTutorialArt(Transform parent)
    {
        tutorialPortrait=Texture(parent,"GuideArona",-1020,-385,1,1,Color.white,96);
        for(int i=0;i<tutorialArt.Length;i++)
        {
            Texture2D? art=null;
            try
            {
                string resource=$"recorder.tutorial.arona.page-{i+1:00}.png";
                using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                    ??throw new FileNotFoundException("缺少教程角色素材："+resource);
                using var buffer=new MemoryStream();stream.CopyTo(buffer);
                // AA unloads unused assets after loading a story. Managed .NET
                // references alone do not keep IL2CPP textures on later pages alive.
                art=new Texture2D(2,2,TextureFormat.RGBA32,false){name=resource,hideFlags=HideFlags.DontUnloadUnusedAsset};
                if(!ImageConversion.LoadImage(art,buffer.ToArray(),true))throw new IOException("教程角色图片无法解码。");
                tutorialArt[i]=art;
            }
            catch(Exception ex)
            {
                if(art!=null)Object.Destroy(art);
                Config.Log.LogWarning("Tutorial illustration: "+ex.Message);
            }
        }
    }
    void RefreshTutorialArt()
    {
        if(tutorialPortrait==null)return;
        var art=tutorialArt[tutorialPage];
        tutorialPortrait.gameObject.SetActive(art!=null);
        if(art==null || tutorialPortrait.mainTexture==art)return;
        tutorialPortrait.mainTexture=art;
        var ratio=Math.Min(330f/art.width,410f/art.height);
        tutorialPortrait.width=(int)(art.width*ratio);tutorialPortrait.height=(int)(art.height*ratio);
    }
    void SetTutorialInputLock(bool active)
    {
        if(active==tutorialInputLocked)return;
        tutorialInputLocked=active;
        if(active)
        {
            UICamera.selectedObject=null;
            foreach(var other in Object.FindObjectsOfType<UICamera>(true))
            {
                if(other==inputCamera)continue;
                tutorialInputs.Add((other,other.enabled));other.enabled=false;
            }
        }
        else
        {
            foreach(var saved in tutorialInputs)if(saved.Camera!=null)saved.Camera.enabled=saved.Enabled;
            tutorialInputs.Clear();
        }
    }
    void ShowTutorial()
    {
        if(owner.Busy || choosing || !open || showProgress)return;
        tutorialPage=0;tutorialVisible=true;RefreshTutorial();UpdateVisibility();
    }
    void MoveTutorial(int delta)
    {
        if(!TutorialVisible)return;
        if(delta>0 && tutorialPage==TutorialPages.Length-1){FinishTutorial();return;}
        tutorialPage=Math.Clamp(tutorialPage+delta,0,TutorialPages.Length-1);RefreshTutorial();
    }
    static void Place(UITexture texture,float x,float y,float width,float height)
    {
        texture.transform.localPosition=new Vector3(x,y,0);texture.width=Math.Max(1,(int)width);texture.height=Math.Max(1,(int)height);
    }
    void RefreshTutorial()
    {
        var page=TutorialPages[tutorialPage];var rect=page.Target;
        tutorialHeading.text=page.Heading;tutorialBody.text=page.Body;tutorialCount.text=$"新手教程  {tutorialPage+1} / {TutorialPages.Length}";
        const float edge=10000;
        Place(tutorialShade[0],0,(edge+rect.yMax)/2,edge*2,edge-rect.yMax);
        Place(tutorialShade[1],0,(-edge+rect.yMin)/2,edge*2,edge+rect.yMin);
        Place(tutorialShade[2],(-edge+rect.xMin)/2,rect.center.y,edge+rect.xMin,rect.height);
        Place(tutorialShade[3],(edge+rect.xMax)/2,rect.center.y,edge-rect.xMax,rect.height);
        for(int layer=0;layer<2;layer++)
        {
            float thick=layer==0?8:3,offset=layer==0?5:0;int at=layer*4;
            Place(tutorialOutline[at],rect.center.x,rect.yMax+offset,rect.width+2*offset,thick);
            Place(tutorialOutline[at+1],rect.center.x,rect.yMin-offset,rect.width+2*offset,thick);
            Place(tutorialOutline[at+2],rect.xMin-offset,rect.center.y,thick,rect.height+2*offset);
            Place(tutorialOutline[at+3],rect.xMax+offset,rect.center.y,thick,rect.height+2*offset);
        }
        tutorialTarget.transform.localPosition=new Vector3(rect.center.x,rect.center.y,0);
        var root=tutorialTarget.GetComponent<UIWidget>();root.width=(int)rect.width;root.height=(int)rect.height;
        tutorialTarget.GetComponent<BoxCollider>().size=new Vector3(rect.width,rect.height,1);
        var bg=tutorialTarget.GetComponentInChildren<UISprite>(true);bg.width=(int)rect.width;bg.height=(int)rect.height;bg.color=new Color(1,1,1,.01f);
        tutorialArrow.transform.localPosition=new Vector3(rect.center.x-60,rect.yMax+117,0);
        RefreshTutorialArt();
        bool above=tutorialPage==4;
        tutorialBubble!.transform.localPosition=new Vector3(80,above?50:-423,0);
        if(tutorialPortrait!=null)tutorialPortrait.transform.localPosition=new Vector3(-1020,above?70:-385,0);
        tutorialNext.label.text=tutorialPage==TutorialPages.Length-1?"完成 ✓":"下一步 ›";
        tutorialHint.text=tutorialPage==TutorialPages.Length-1?"点击高亮位置完成 · 也可以随时跳过":"点击高亮位置继续 · 引导不会更改设置";
        tutorialPrevious.disabled=tutorialPage==0;tutorialPrevious.label.color=tutorialPage==0?DisabledText:Normal;
        tutorialPrevious.GetComponentInChildren<UISprite>(true).color=tutorialPage==0?DisabledButton:activeButtonColor;
        tutorialSkip.disabled=false;tutorialSkip.label.color=Normal;
        tutorialSkip.GetComponentInChildren<UISprite>(true).color=new Color(.41f,.84f,.98f,1);
    }
    void UpdateTutorial()
    {
        RefreshTutorial();
        if(Input.GetKeyDown(KeyCode.LeftArrow))MoveTutorial(-1);
        if(Input.GetKeyDown(KeyCode.RightArrow))MoveTutorial(1);
        if(Input.GetKeyDown(KeyCode.Escape))FinishTutorial();
    }
    void FinishTutorial()
    {
        if(!TutorialVisible)return;
        Config.TutorialSeenVersion.Value=TutorialVersion;
        tutorialVisible=false;UpdateVisibility();
    }
}
