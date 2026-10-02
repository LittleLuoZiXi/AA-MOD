using System;
using UnityEngine;

namespace AzureArchive.Recorder;

internal sealed partial class NativeRecorderUi
{
    internal const string TimingConfirmationPrompt="统计总时长需要完整的预演一次，会极大的拖慢内录的耗时时长，是否开启？";
    bool measureTotal,timingConfirmationVisible;
    GameObject? timingConfirmation;
    UI.MXButton measureTotalButton=null!;
    UILabel timingConfirmationBody=null!;

    internal bool MeasureTotal=>measureTotal;
    internal bool TimingConfirmationVisible=>timingConfirmationVisible && open && !showProgress;
    internal string TimingButtonText=>measureTotalButton?.label?.text??"";
    internal string TimingConfirmationText=>timingConfirmationBody?.text??"";

    void ResetTimingChoice()
    {
        // A recording choice belongs only to this panel visit, never to Config.
        measureTotal=false;timingConfirmationVisible=false;RefreshTimingChoice();
    }

    void ToggleTimingChoice()
    {
        if(owner.Busy || choosing || !open || showProgress || TutorialVisible || TimingConfirmationVisible)return;
        if(measureTotal){measureTotal=false;RefreshTimingChoice();return;}
        timingConfirmationVisible=true;UpdateVisibility();
    }

    internal void DismissTimingConfirmation()=>ResolveTimingConfirmation(false);

    void ResolveTimingConfirmation(bool accepted)
    {
        if(!TimingConfirmationVisible)return;
        measureTotal=accepted;timingConfirmationVisible=false;
        RefreshTimingChoice();UpdateVisibility();
    }

    void RefreshTimingChoice()
    {
        if(measureTotalButton==null)return;
        measureTotalButton.label.text=measureTotal?"开启":"关闭";
    }

    void UpdateTimingChoice()
    {
        measureTotalButton.disabled=choosing || owner.Busy;
        if(TimingConfirmationVisible && Input.GetKeyDown(KeyCode.Escape))DismissTimingConfirmation();
    }

    static bool TimingSubmitKeyActive()=>Input.GetKey(KeyCode.Return) || Input.GetKeyUp(KeyCode.Return) ||
        Input.GetKey(KeyCode.KeypadEnter) || Input.GetKeyUp(KeyCode.KeypadEnter);

    void EnsureTimingOptions(UI.MXButton template,Font font)
    {
        // Match the native FPS and neural-rendering buttons, including their
        // left-column position and dimensions. The choice stays per visit.
        Label(options!.transform,font,"开始前统计总时长",-1050,-360,400,70,38);
        measureTotalButton=Button(template,options.transform,"AARecorder_MeasureTotal","关闭",
            new Vector3(-480,-385,0),330,90,40,ToggleTimingChoice);
        Label(options.transform,font,"默认关闭 · 仅本次有效",130,-357,900,40,31);

        timingConfirmation=Child(dialog!.transform,"TimingConfirmation");var parent=timingConfirmation.transform;
        var shield=Texture(parent,"TimingConfirmationShield",0,0,20000,20000,new Color(.02f,.04f,.06f,.68f),130);
        shield.gameObject.AddComponent<BoxCollider>().size=new Vector3(20000,20000,1);
        var shieldListener=UIEventListener.Get(shield.gameObject);shieldListener.Clear();
        shieldListener.onClick=(UIEventListener.VoidDelegate)(Action<GameObject>)(_=>ResolveTimingConfirmation(false));
        Texture(parent,"TimingConfirmationShadow",8,-9,1520,620,new Color(0,0,0,.24f),134);
        var body=Texture(parent,"TimingConfirmationPanel",0,0,1520,620,new Color(.97f,.99f,1,1),135);
        // The panel itself intercepts clicks, so only clicks outside it cancel.
        body.gameObject.AddComponent<BoxCollider>().size=new Vector3(1520,620,1);
        Texture(parent,"TimingConfirmationAccent",-754,0,12,620,Accent,136);
        var heading=Label(parent,font,"开始前统计总时长",-650,245,1220,80,48);heading.depth=140;
        timingConfirmationBody=Label(parent,font,TimingConfirmationPrompt,-650,118,1300,190,42);
        timingConfirmationBody.name="AARecorder_TimingConfirmationPrompt";timingConfirmationBody.depth=140;
        timingConfirmationBody.spacingY=14;
        var hint=Label(parent,font,"仅影响本次内录",-650,-88,1300,55,30);hint.depth=140;hint.color=DisabledText;
        var no=Button(template,parent,"AARecorder_TimingNo","否",new Vector3(-325,-215,0),400,100,44,()=>ResolveTimingConfirmation(false));
        var yes=Button(template,parent,"AARecorder_TimingYes","是",new Vector3(325,-215,0),400,100,44,()=>ResolveTimingConfirmation(true));
        var dismiss=Button(template,parent,"AARecorder_TimingClose","×",new Vector3(663,242,0),90,80,50,()=>ResolveTimingConfirmation(false));
        foreach(var button in new[]{no,yes,dismiss})RaiseTutorialButton(button,150);
        timingConfirmation.SetActive(false);RefreshTimingChoice();
    }
}

