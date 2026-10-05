using System;
using System.Linq;
using Studio.Scripts;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace AzureArchive.RevisionCompare;

// NGUI controls share AA's font, sprites, camera and input routing. Only the
// floating window's rectangle intercepts clicks; the editor remains usable.
internal sealed class NativeCompareUi : IDisposable
{
    readonly Action compareRequested, restore, closeRequested;
    readonly Action<string> report;
    readonly ComparisonPreview preview;
    ScriptNodeInspector? inspector;
    UI.MXButton? originalPlay, entry, restoreButton;
    GameObject? window;
    UITexture? screen;
    UILabel? status;
    UIWidget? originalViewport;
    bool visible;
    float nextScan;
    const int Depth = 9200;
    static readonly Color Ink = new(.16f,.26f,.36f,1);

    internal NativeCompareUi(Action compareRequested, Action restore, Action closeRequested, Action<string> report)
    { this.compareRequested=compareRequested; this.restore=restore; this.closeRequested=closeRequested; this.report=report; preview=new ComparisonPreview(report); }
    internal bool IsVisible => visible && window!=null;
    internal UI.MXButton? NativePlayButton => originalPlay;
    internal RenderTexture? PreviewTexture => preview.Texture;
    internal string PreviewText => preview.PreviewText;
    internal bool PreviewReady => visible && preview.PreviewReady;
    internal bool IsPlayingPrevious => visible && preview.IsPlayingPrevious;

    internal void Update(ScriptNodeInspector? value, bool hasSnapshot, string caption)
    {
        if(inspector!=value)
        {
            Hide(); DestroyEntry(); inspector=value; nextScan=0;
            if(window!=null){Object.Destroy(window);window=null;}
        }
        if(inspector==null || !inspector.gameObject.activeInHierarchy)
        { if(entry!=null)entry.gameObject.SetActive(false); Hide(); return; }
        if(entry==null && Time.unscaledTime>=nextScan)
        { nextScan=Time.unscaledTime+1; TryAttach(); }
        if(entry!=null && originalPlay!=null)
        {
            entry.gameObject.SetActive(originalPlay.gameObject.activeInHierarchy);
            var root=originalPlay.GetComponent<UIWidget>();
            var gap=(root!=null?root.width:100)+16;
            entry.transform.localPosition=originalPlay.transform.localPosition-new Vector3(gap,0,0);
            entry.disabled=!hasSnapshot;
        }
        if(!visible)return;
        if(restoreButton!=null)restoreButton.disabled=!hasSnapshot;
        preview.Update();
        RefreshPreviewDisplay();
    }

    void TryAttach()
    {
        if(inspector==null)return;
        var anchor=inspector.transform.Find("Preview/PlayPreviewBtn");
        if(anchor==null)return;
        originalPlay=anchor.GetComponent<UI.MXButton>();
        if(originalPlay==null)return;
        originalViewport=anchor.parent.GetComponent<UIWidget>();
        entry=CloneButton(originalPlay,anchor.parent,"AARevisionCompare_Entry",()=>compareRequested());
        foreach(var w in entry.GetComponentsInChildren<UIWidget>(true))w.depth+=4;
        var icon=entry.transform.Find("Icon");if(icon!=null)icon.gameObject.SetActive(false);
        if(entry.label!=null)entry.label.gameObject.SetActive(false);
        var root=entry.GetComponent<UIWidget>();var depth=(root!=null?root.depth:10)+2;
        // Two overlapping pages are legible at the same size as AA's play icon.
        Stroke(entry.transform,"CompareBack",-7,7,32,36,3,depth);
        Stroke(entry.transform,"CompareFront",7,-7,32,36,3,depth+2);
        var listener=UIEventListener.Get(entry.gameObject);
        listener.onTooltip=(UIEventListener.BoolDelegate)(Action<GameObject,bool>)((_,show)=>{if(show)UITooltip.Show("查看对比");else UITooltip.Hide();});
        listener.onHover=(UIEventListener.BoolDelegate)(Action<GameObject,bool>)((_,show)=>{if(show)UITooltip.Show("查看对比");else UITooltip.Hide();});
    }

    internal void Show(Test originalPreview, FlatData.IScenarioScriptExcel oldLine)
    {
        if(inspector==null || originalPlay==null)throw new InvalidOperationException("请先选中一条对话。");
        EnsureWindow();
        visible=true;window!.SetActive(true);
        status!.text="播放修改前 · 右侧暂定格";
        try
        {
            var ratio=originalViewport!=null && originalViewport.height>0?(float)originalViewport.width/originalViewport.height:16f/9f;
            preview.Play(originalPreview,oldLine,ratio);
            RefreshPreviewDisplay();
        }
        catch{Hide();throw;}
    }

    internal void PlayCurrent()
    {
        if(!IsVisible)return;
        try{preview.PlayCurrent();RefreshPreviewDisplay();}
        catch{Hide();throw;}
    }

    void RefreshPreviewDisplay()
    {
        if(screen!=null)screen.mainTexture=preview.Texture;
        if(status!=null)status.text=preview.IsPlayingPrevious?"播放修改前 · 右侧暂定格":"修改前已定格 · 右侧播放中";
    }

    internal void Hide()
    {
        visible=false;
        if(window!=null)window.SetActive(false);
        preview.Stop();
        UITooltip.Hide();
    }

    void EnsureWindow()
    {
        if(window!=null)return;
        var editorRoot=inspector!.GetComponentInParent<UIRoot>();
        if(editorRoot==null)throw new InvalidOperationException("未找到 AA 编辑器界面。");
        var font=inspector.contentInput?.label?.trueTypeFont ?? inspector.GetComponentInChildren<UILabel>(true)?.trueTypeFont;
        if(font==null)throw new InvalidOperationException("未找到 AA 原生字体。");
        float ratio=originalViewport!=null && originalViewport.height>0?(float)originalViewport.width/originalViewport.height:16f/9f;
        const int width=1040, imageWidth=976;
        int imageHeight=(int)Math.Round(imageWidth/ratio), height=imageHeight+190;
        window=New(editorRoot.transform,"AARevisionCompare_Window");
        window.layer=inspector.gameObject.layer;
        window.SetActive(false);
        var panel=window.AddComponent<UIPanel>();panel.depth=Depth;
        window.transform.localPosition=new Vector3(-350,-30,-20);

        var source=Object.FindObjectsOfType<Transform>(true).FirstOrDefault(t=>t.name=="UIPopup_Settings")?.Find("Center/Popup/Base");
        // Toolbar sprites are intentionally skewed. Stretching one into a
        // dialog makes its slanted edges overlap the title and preview. Only
        // reuse a large sliced native panel, with a plain AA-colour fallback.
        var shellTemplate=source?.GetComponentsInChildren<UISprite>(true).FirstOrDefault(s=>s.type==UIBasicSprite.Type.Sliced && s.width>900 && s.height>500);
        if(shellTemplate!=null)
        {
            var shell=Object.Instantiate(shellTemplate.gameObject,window.transform);shell.name="NativeShell";
            foreach(var tr in shell.GetComponentsInChildren<Transform>(true))tr.gameObject.layer=window.layer;
            foreach(var c in shell.GetComponentsInChildren<Collider>(true))c.enabled=false;
            for(int i=0;i<shell.transform.childCount;i++)shell.transform.GetChild(i).gameObject.SetActive(false);
            foreach(var a in shell.GetComponents<Animation>())a.enabled=false;
            foreach(var b in shell.GetComponents<UI.MXButton>())b.enabled=false;
            foreach(var rect in shell.GetComponents<UIRect>())ClearAnchors(rect);
            shell.transform.localPosition=Vector3.zero;shell.transform.localScale=Vector3.one;
            var sprite=shell.GetComponent<UISprite>();sprite.width=width;sprite.height=height;sprite.depth=1;sprite.color=new Color(.94f,.98f,1,1);
        }
        else
        {
            Fill(window.transform,"ShellShadow",7,-9,width+12,height+12,new Color(.08f,.18f,.28f,.32f),0);
            Fill(window.transform,"NativeShell",0,0,width,height,new Color(.94f,.98f,1,1),1);
        }
        var collider=window.AddComponent<BoxCollider>();collider.size=new Vector3(width,height,1);
        var hit=window.AddComponent<UIWidget>();hit.width=width;hit.height=height;hit.depth=1;hit.autoResizeBoxCollider=false;
        Fill(window.transform,"TitleRule",0,height/2f-83,width-64,3,new Color(.18f,.73f,.91f,1),3);
        Label(window.transform,font,"修改前",-width/2f+34,height/2f-29,690,46,34,5);
        var close=TextButton(window.transform,font,"AARevisionCompare_Close","×",new Vector3(width/2f-51,height/2f-40,0),68,62,38,closeRequested);
        screen=Fill(window.transform,"PreviousPreview",0,4,imageWidth,imageHeight,Color.white,4);
        status=Label(window.transform,font,"播放修改前 · 右侧暂定格",-width/2f+34,-height/2f+61,480,38,23,6);
        TextButton(window.transform,font,"AARevisionCompare_Replay","重新播放",new Vector3(93,-height/2f+49,0),205,65,27,()=>
        {try{preview.Replay();RefreshPreviewDisplay();}catch(Exception ex){Hide();report(ex.Message);}});
        restoreButton=TextButton(window.transform,font,"AARevisionCompare_Restore","恢复原来修改",new Vector3(352,-height/2f+49,0),285,65,27,()=>restore());
        var drag=New(window.transform,"DragHeader");drag.transform.localPosition=new Vector3(-55,height/2f-40,0);
        var dragWidget=drag.AddComponent<UIWidget>();dragWidget.width=width-150;dragWidget.height=80;dragWidget.depth=7;
        var dragBox=drag.AddComponent<BoxCollider>();dragBox.size=new Vector3(width-150,80,1);
        var listener=UIEventListener.Get(drag);
        listener.onDrag=(UIEventListener.VectorDelegate)(Action<GameObject,Vector2>)((_,delta)=>
        {
            if(window==null)return;
            var eventCamera=UICamera.currentCamera;
            if(eventCamera==null)return;
            var p=window.transform.position;
            float z=eventCamera.WorldToScreenPoint(p).z;
            var shift=eventCamera.ScreenToWorldPoint(new Vector3(delta.x,delta.y,z))-eventCamera.ScreenToWorldPoint(new Vector3(0,0,z));
            window.transform.position=p+shift;
        });
    }

    UI.MXButton TextButton(Transform parent,Font font,string name,string text,Vector3 pos,int width,int height,int size,Action click)
    {
        var button=CloneButton(originalPlay!,parent,name,click);
        foreach(var child in button.GetComponentsInChildren<UIWidget>(true))if(child.gameObject!=button.gameObject)child.gameObject.SetActive(false);
        var root=button.GetComponent<UIWidget>();if(root!=null){root.width=width;root.height=height;root.depth=9;root.autoResizeBoxCollider=false;}
        var sprite=button.GetComponent<UISprite>();if(sprite!=null){sprite.width=width;sprite.height=height;sprite.depth=9;}
        var collider=button.GetComponent<BoxCollider>();if(collider!=null){collider.size=new Vector3(width,height,1);collider.center=Vector3.zero;}
        var label=Label(button.transform,font,text,0,0,width-14,height,size,10);label.pivot=UIWidget.Pivot.Center;label.alignment=NGUIText.Alignment.Center;
        label.transform.localPosition=Vector3.zero;button.label=label;button.transform.localPosition=pos;return button;
    }

    UI.MXButton CloneButton(UI.MXButton source,Transform parent,string name,Action click)
    {
        var go=Object.Instantiate(source.gameObject,parent);go.SetActive(false);go.name=name;
        foreach(var tr in go.GetComponentsInChildren<Transform>(true))tr.gameObject.layer=parent.gameObject.layer;
        foreach(var r in go.GetComponentsInChildren<UIRect>(true))ClearAnchors(r);
        var button=go.GetComponent<UI.MXButton>();button.onClicked=null;button.onPressed=null;button.onClick=new UnityEvent();button.onPress=new UnityEvent();button.onLongPress=new UnityEvent();button.disabled=false;
        var listener=UIEventListener.Get(go);listener.Clear();
        listener.onClick=(UIEventListener.VoidDelegate)(Action<GameObject>)(_=>
        {
            if(button.disabled || !go.activeInHierarchy)return;
            try{click();}
            catch(Exception ex){report("对比操作失败："+ex.Message);}
        });
        go.transform.localScale=Vector3.one;go.SetActive(true);return button;
    }
    static GameObject New(Transform parent,string name){var go=new GameObject(name);go.layer=parent.gameObject.layer;go.transform.SetParent(parent,false);return go;}
    static void ClearAnchors(UIRect r){r.leftAnchor.target=null;r.rightAnchor.target=null;r.topAnchor.target=null;r.bottomAnchor.target=null;r.ResetAnchors();}
    static UITexture Fill(Transform parent,string name,float x,float y,int width,int height,Color color,int depth)
    {var go=New(parent,name);var t=go.AddComponent<UITexture>();t.mainTexture=Texture2D.whiteTexture;t.width=width;t.height=height;t.color=color;t.depth=depth;go.transform.localPosition=new Vector3(x,y,0);return t;}
    static UILabel Label(Transform parent,Font font,string text,float x,float y,int width,int height,int size,int depth)
    {var go=New(parent,"CompareLabel");var label=go.AddComponent<UILabel>();label.trueTypeFont=font;label.pivot=UIWidget.Pivot.TopLeft;label.alignment=NGUIText.Alignment.Left;label.width=width;label.height=height;label.fontSize=size;label.maxLineCount=1;label.color=Ink;label.overflowMethod=UILabel.Overflow.ShrinkContent;label.supportEncoding=false;label.depth=depth;label.text=text;go.transform.localPosition=new Vector3(x,y,0);return label;}
    static void Stroke(Transform parent,string name,float x,float y,int width,int height,int thickness,int depth)
    {
        var go=New(parent,name);go.transform.localPosition=new Vector3(x,y,0);
        Fill(go.transform,"Paper",0,0,width,height,new Color(.91f,.97f,1,1),depth);
        Fill(go.transform,"Top",0,height/2f,width,thickness,Ink,depth+1);Fill(go.transform,"Bottom",0,-height/2f,width,thickness,Ink,depth+1);
        Fill(go.transform,"Left",-width/2f,0,thickness,height,Ink,depth+1);Fill(go.transform,"Right",width/2f,0,thickness,height,Ink,depth+1);
    }
    void DestroyEntry(){if(entry!=null)Object.Destroy(entry.gameObject);entry=null;originalPlay=null;}
    public void Dispose(){Hide();DestroyEntry();if(window!=null)Object.Destroy(window);window=null;preview.Dispose();}
}
