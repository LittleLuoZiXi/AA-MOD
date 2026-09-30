using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using AzureArchive.Automation;
using Object = UnityEngine.Object;

namespace AzureArchive.Recorder;

[BepInPlugin("azurearchive.recorder", "AzureArchiveRecorder", "1.1.0")]
public sealed class RecorderPlugin : BasePlugin
{
    internal static RecorderPlugin Instance = null!;
    internal ConfigEntry<string> Output = null!, Ffmpeg = null!, Tool = null!, Python = null!;
    internal ConfigEntry<int> Fps = null!, Crf = null!, Scale = null!, Multiplier = null!, MaxMinutes = null!, EnhancementTimeout = null!;
    internal ConfigEntry<bool> Neural = null!, Hardware = null!, AsyncReadback = null!;
    internal ConfigEntry<int> TutorialSeenVersion = null!;
    public override void Load()
    {
        Instance = this;
        var tools = Deployment.ResolveDlssTool("");
        Output = Config.Bind("Recording","OutputDirectory",Path.Combine(Paths.GameRootPath,"Recordings"),"内部素材和日志目录；成品文件夹在内录面板中按次选择，默认 Windows 视频文件夹。");
        Ffmpeg = Config.Bind("Recording","FFmpegPath",Deployment.BundledFfmpeg,"安装器自动检测并配置；原路径不存在时回退本 MOD 内置 FFmpeg。");
        Fps = Config.Bind("Recording","FrameRate",30,new ConfigDescription("固定录制帧率。",new AcceptableValueList<int>(24,25,30,50,60)));
        Crf = Config.Bind("Recording","QualityCRF",18,new ConfigDescription("H.264 质量，越小越清晰。",new AcceptableValueRange<int>(0,35)));
        Hardware=Config.Bind("Recording","HardwareEncoding",true,"自动探测普通 H.264 硬件编码；不可用时回退 CPU。这与 DLSS 增强支持情况独立。");
        AsyncReadback=Config.Bind("Recording","AsyncReadback",true,"GPU 异步读取，减少逐帧等待；若显卡驱动不支持可关闭。");
        MaxMinutes = Config.Bind("Recording","MaxMinutes",120,new ConfigDescription("防止循环剧情无限录制，超时保留已录内容。",new AcceptableValueRange<int>(1,600)));
        Tool = Config.Bind("DLSS","ToolDirectory",tools,"DLSS5Tool v2.3.3 组件目录；优先自动使用安装器已校验的 DLSS 组件。");
        Python = Config.Bind("DLSS","PythonPath",Path.Combine(Paths.GameRootPath,"RecorderMod",".venv","Scripts","python.exe"),"运行 setup-dlss.ps1 安装桥接环境。");
        Scale = Config.Bind("DLSS","SuperResolution",2,new ConfigDescription("RTX Video 超分倍率。",new AcceptableValueList<int>(1,2,4)));
        Multiplier = Config.Bind("DLSS","FrameMultiplier",2,new ConfigDescription("补帧倍率，3/4 为上游实验模式。",new AcceptableValueList<int>(1,2,3,4)));
        Neural = Config.Bind("DLSS","NeuralRendering",false,"可选 DLSS5 神经渲染；可能改变二次元画风，默认关闭。");
        EnhancementTimeout = Config.Bind("DLSS","TimeoutMinutes",120,new ConfigDescription("增强任务总时限（分钟）；超时或取消后保留原片并结束本任务的子进程。取消最多等待 10 秒。",new AcceptableValueRange<int>(1,1440)));
        TutorialSeenVersion = Config.Bind("Tutorial","SeenVersion",0,"已查看或跳过的新手教程版本；首次进入内录设置自动显示，可在设置中随时回看。");
        var harmony=new Harmony("azurearchive.recorder");
        try
        {
            harmony.PatchAll(typeof(RecorderPlugin).Assembly);
            Action<string> warning=message=>Log.LogWarning(message);
            // Exercise the reported missing-method path in an opt-in verification instance.
            bool missingDestroy=Environment.GetCommandLineArgs().Contains("--aa-recorder-test-missing-destroy");
            if(missingDestroy)Log.LogWarning("Compatibility diagnostic: simulating absent Test.OnDestroy.");
            OptionalHooks.Patch(harmony,missingDestroy?null:typeof(Test),"OnDestroy",typeof(PlaybackDestroyed),warning);
            OptionalHooks.Patch(harmony,typeof(Test),"OnReady",typeof(PlaybackReady),warning);
            var delayType=typeof(Test).GetNestedTypes(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(t=>t.Name.StartsWith("_DelayedAdvance_d__",StringComparison.Ordinal));
            OptionalHooks.Patch(harmony,delayType,"MoveNext",typeof(RecordingDelays),warning);
            AddComponent<RecorderBehaviour>();
        }
        catch
        {
            harmony.UnpatchSelf();
            throw;
        }
        Log.LogInfo("Recorder ready (1.1.0; explicit DLSS opt-in, verified installed components, bounded cancellation and progress). Native catalog controls / F8 or F9 settings / F10 stop.");
    }
}

public sealed class RecorderBehaviour : MonoBehaviour
{
    internal static RecorderBehaviour? Instance;
    internal bool Armed, Capturing;
    bool busy, enhance, audioStarted, settingsHeld, smokeStarted, smokeProjectPending, displayHeld;
    string smokeSource = "";
    bool smokeEnhance;
    string status = "就绪：选中剧情后可一键录制。", directory = "", sourceProject = "", runtime = "";
    string gpuDescription = "检测中…", selectedGpu = "";
    int selectedSeries, oldCaptureFps, oldTargetFps, oldVsync, channels, width, height;
    float oldTimeScale;
    bool oldBackground;
    bool enhancementSupported;
    bool enhancementComponentsReady;
    string enhancementUnsupportedReason="当前显卡暂不支持 DLSS 增强。";
    string componentFingerprint="",availableRuntime="";
    float nextComponentProbe;
    int oldWidth,oldHeight;
    FullScreenMode oldFullScreen;
    MonitorResolution? monitor;
    NativeRecorderUi? nativeUi;
    Texture2D? texture;
    RenderTexture? renderTarget;
    Encoder? encoder;
    FrameReadback? readback;
    string selectedEncoder="libx264";
    Test? player;
    string enhancementJob = "";
    Process? enhancementProcess;
    Stopwatch waitClock = new();
    readonly Stopwatch captureClock=new(),jobClock=new();
    readonly List<Camera> recordingCameras=new();
    string destinationFolder=FolderPicker.Videos,finalOutput="";
    float scriptProgress;
    float enhancementProgress;
    long capturedFrames;
    int scriptLength;
    int lastScriptIndex=-1;
    int diagnosticOriginalFps;
    double audioStartDsp;
    float playbackClockOrigin,unityClockOrigin;
    internal float PlaybackTime { [HideFromIl2Cpp] get=>playbackClockOrigin+Time.time-unityClockOrigin; }
    [HideFromIl2Cpp] internal bool OwnsPlayer(Test value)=>player!=null && player.Pointer==value.Pointer;
    [HideFromIl2Cpp] internal void LogDelay(string kind,float seconds)=>File.AppendAllText(Path.Combine(directory,"script-events.jsonl"),JsonSerializer.Serialize(new{delay=kind,seconds,frame=capturedFrames,videoTime=(double)capturedFrames/Config.Fps.Value})+Environment.NewLine);
    bool succeeded;
    readonly System.Collections.Concurrent.ConcurrentQueue<Action> actions = new();
    RecorderPlugin Config { [HideFromIl2Cpp] get => RecorderPlugin.Instance; }
    public RecorderBehaviour(IntPtr pointer):base(pointer) { }

    public void Awake()
    {
        Instance = this;
        var args=Environment.GetCommandLineArgs();
        int testFps=Array.IndexOf(args,"--aa-recorder-test-fps");
        if(testFps>=0 && testFps+1<args.Length && int.TryParse(args[testFps+1],out var fps) && new[]{24,25,30,50,60}.Contains(fps))
        {diagnosticOriginalFps=Config.Fps.Value;Config.Fps.Value=fps;}
        int smoke=Array.IndexOf(args,"--aa-recorder-smoke");
        if(smoke>=0 && smoke+1<args.Length)
        {
            smokeSource=Path.GetFullPath(args[smoke+1]);
            smokeEnhance=args.Contains("--aa-recorder-smoke-enhance");
            int output=Array.IndexOf(args,"--aa-recorder-smoke-output");
            if(output>=0 && output+1<args.Length) destinationFolder=Path.GetFullPath(args[output+1]);
        }
        try
        {
            var devices=GpuProfiles.Detect();
            gpuDescription=string.Join(" / ",devices.Select(x=>x.Name));
            try
            {
                var selected=GpuProfiles.Select(devices);
                selectedGpu=selected.Name;selectedSeries=selected.Series;
                enhancementSupported=GpuProfiles.Hashes.ContainsKey(selected.Series);
                if(!enhancementSupported)enhancementUnsupportedReason=$"已识别 {selected.Name}，当前显卡系列暂不支持 DLSS 增强；普通内录可用。";
                gpuDescription+=enhancementSupported?$"（RTX {selected.Series} 系配置）":"（增强暂不支持）";
            }
            catch(InvalidOperationException ex) {gpuDescription+="（增强暂不支持）";enhancementUnsupportedReason=ex.Message;}
        }
        catch(Exception ex) { gpuDescription = "显卡检测失败："+ex.Message;enhancementUnsupportedReason=gpuDescription; }
        if(args.Contains("--aa-recorder-smoke-ui") && args.Contains("--aa-recorder-test-unsupported-gpu"))
        {
            enhancementSupported=false;gpuDescription="测试模拟：当前显卡暂不支持 DLSS 增强";
            enhancementUnsupportedReason=gpuDescription;
            Config.Log.LogWarning("UI diagnostic only: simulating an unsupported GPU; ordinary recording remains available.");
        }
        nativeUi=new NativeRecorderUi(this);
    }
    public void Update()
    {
        while(actions.TryDequeue(out var action)) action();
        nativeUi?.Update();
        UiDiagnostics.Update();
        if(smokeSource.Length>0 && !smokeStarted)
        {
            var manager=Object.FindObjectOfType<ScenarioResourceManager>();
            if(manager!=null && manager.AreDbsLoaded)
            {
                smokeStarted=true;
                try
                {
                    directory=CreateOutputDirectory(Path.GetFileNameWithoutExtension(smokeSource));
                    jobClock.Restart();nativeUi!.ShowProgress();
                    sourceProject=smokeSource; busy=true; Armed=true; waitClock.Restart();
                    if(smokeSource.EndsWith(".aap2",StringComparison.OrdinalIgnoreCase))
                    { Armed=false; smokeProjectPending=true; AuthoringWorkbench.OpenProject(smokeSource); }
                    else
                    {
                        if(smokeEnhance && !PrepareEnhancement(smokeSource))
                            throw new InvalidOperationException("增强诊断需要已校验的运行库，请先完成依赖预检。");
                        enhance=smokeEnhance;
                        StartCoroutine(OpenAtMonitorResolution(smokeSource).WrapToIl2Cpp());
                    }
                }
                catch(Exception ex) { Fail(ex); }
            }
        }
        if(smokeProjectPending && AuthoringEditorSession.Current!=null && waitClock.Elapsed.TotalSeconds>3)
        {
            smokeProjectPending=false;busy=false;
            if(Environment.GetCommandLineArgs().Contains("--aa-recorder-smoke-ui"))UiDiagnostics.StartSmokeOptions(smokeEnhance,destinationFolder);
            else Begin(smokeEnhance);
        }
        if(nativeUi?.TutorialVisible==true)return;
        if (Input.GetKeyDown(KeyCode.F8) && !Capturing) nativeUi?.ToggleSettings();
        if (Input.GetKeyDown(KeyCode.F9) && !busy) nativeUi?.ToggleSettings();
        if (Input.GetKeyDown(KeyCode.F10) && (Capturing||Armed)) Stop("手动停止");
        // Covers scene teardown even when the host has no OnDestroy message.
        if (Capturing && player==null) Stop("剧情播放器已退出");
        if (Armed && waitClock.Elapsed.TotalSeconds>120) Fail(new TimeoutException("120 秒未进入剧情，录制已取消。"));
        if (Capturing && encoder != null && encoder.Frames >= (long)Config.MaxMinutes.Value * 60 * Config.Fps.Value)
            Stop("达到最长录制时长");
    }

    [HideFromIl2Cpp] internal void OnMainThread(Action action)=>actions.Enqueue(action);
    [HideFromIl2Cpp] internal void SetStatus(string value)=>status=value;
    internal bool Enhancing { [HideFromIl2Cpp] get=>enhancementJob.Length>0; }
    internal NativeRecorderUi? Ui { [HideFromIl2Cpp] get=>nativeUi; }
    internal float ProgressRatio { [HideFromIl2Cpp] get=>succeeded?1:Capturing?.05f+.85f*scriptProgress:Armed?.03f:Enhancing?.90f+.09f*enhancementProgress:busy?.90f:0; }
    internal string ProgressTitle { [HideFromIl2Cpp] get=>succeeded?"内录完成":Capturing?$"正在内录 · 剧情指令进度 {scriptProgress*100:0}%":busy?Armed || capturedFrames==0?"正在准备剧情…":Enhancing?"正在增强视频…":"正在封装视频…":"内录已停止"; }
    internal string ProgressDetail
    {
        [HideFromIl2Cpp] get
        {
            if(!busy)return status;
            double seconds=(double)capturedFrames/Config.Fps.Value;
            return Capturing?$"已生成 {seconds:0.0} 秒视频 · 实际耗时 {captureClock.Elapsed.TotalSeconds:0.0} 秒\n处理速度 {seconds/Math.Max(.01,captureClock.Elapsed.TotalSeconds):0.00} 倍 · {width} × {height}\n进度按剧情指令估算；视频按正常时长导出。":status;
        }
    }
    [HideFromIl2Cpp]
    internal void StartFromOptions(bool withEnhancement,string folder)
    {
        if(busy)return;
        try
        {
            if(withEnhancement && !RefreshEnhancementAvailability(true))throw new InvalidOperationException(EnhancementUnavailableReason);
            destinationFolder=Path.GetFullPath(string.IsNullOrWhiteSpace(folder)?FolderPicker.Videos:folder);
            Directory.CreateDirectory(destinationFolder);
            var probe=Path.Combine(destinationFolder,".aa-recorder-write-"+Guid.NewGuid().ToString("N"));
            using(var file=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None))file.WriteByte(0);
            File.Delete(probe);
            succeeded=false;capturedFrames=0;scriptProgress=0;finalOutput="";directory="";jobClock.Restart();
            nativeUi!.ShowProgress();busy=true;status="正在准备内录…";
            StartCoroutine(BeginNextFrame(withEnhancement).WrapToIl2Cpp());
        }
        catch(Exception ex){status=ex.Message;Config.Log.LogError(ex);}
    }
    [HideFromIl2Cpp]
    IEnumerator BeginNextFrame(bool withEnhancement)
    {yield return null;yield return new WaitForEndOfFrame();busy=false;Begin(withEnhancement);}

    public void LateUpdate()
    {
        if(!Capturing || renderTarget==null)return;
        foreach(var cam in Camera.allCameras)
        {
            if(cam==null || cam==nativeUi?.ScreenCamera || !cam.enabled || cam.targetTexture!=null)continue;
            cam.targetTexture=renderTarget;recordingCameras.Add(cam);
        }
        if(player!=null && scriptLength>0)
            scriptProgress=Math.Max(scriptProgress,Math.Min(.99f,(float)Math.Max(0,player.cur)/scriptLength));
        if(player!=null && player.cur!=lastScriptIndex)
        {
            lastScriptIndex=player.cur;
            File.AppendAllText(Path.Combine(directory,"script-events.jsonl"),JsonSerializer.Serialize(new{index=lastScriptIndex,frame=capturedFrames,videoTime=(double)capturedFrames/Config.Fps.Value})+Environment.NewLine);
        }
    }

    [HideFromIl2Cpp]
    void EnsureFfmpeg()
    {
        var resolved=Deployment.ResolveFfmpeg(Config.Ffmpeg.Value);
        if(!string.Equals(resolved,Config.Ffmpeg.Value,StringComparison.OrdinalIgnoreCase))
        {
            Config.Ffmpeg.Value=resolved;
            Config.Log.LogInfo("FFmpeg automatically configured: "+resolved);
        }
    }

    [HideFromIl2Cpp]
    internal void Begin(bool withEnhancement)
    {
        if(busy) return;
        try
        {
            nativeUi!.ShowProgress();
            EnsureFfmpeg();
            var session=AuthoringEditorSession.Current;
            if(session==null)
            {
                var selected=CurrentSource();
                if(selected.Length==0) throw new InvalidOperationException("请先在剧情列表选择一项，或在编辑器打开已保存工程。");
                if(withEnhancement && !PrepareEnhancement(selected)) return;
                if(selected.EndsWith(".aap2",StringComparison.OrdinalIgnoreCase) || selected.EndsWith(".aap",StringComparison.OrdinalIgnoreCase))
                {
                    busy=true;status="正在打开已保存工程…";
                    AuthoringWorkbench.OpenProject(selected);
                    StartCoroutine(RecordOpenedProject(selected,withEnhancement).WrapToIl2Cpp());
                    return;
                }
                if(!selected.EndsWith(".aas",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("当前项目不是可录制的剧情文件。");
                sourceProject=selected;
                directory=CreateOutputDirectory(Path.GetFileNameWithoutExtension(selected));
                enhance=withEnhancement; busy=true; Armed=true;waitClock.Restart();
                status="正在载入所选剧情…";AuthoringMcpPanel.HideCurrent();
                StartCoroutine(OpenAtMonitorResolution(selected).WrapToIl2Cpp());
                return;
            }
            session.Refresh(false);
            if(session.Dirty || session.SaveInProgress || session.IsApplying)
                throw new InvalidOperationException("工程尚未保存或正在编辑，请先保存后再录制。");
            if(withEnhancement && !PrepareEnhancement(session.FilePath)) return;
            sourceProject=session.FilePath;
            var name=string.Concat(session.Capture().ProjectName.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
            directory=CreateOutputDirectory(name);
            // Compile a snapshot into an exclusive job folder. Never overwrite
            // the user's AAP, AAS, resources or previous videos.
            var buildDir=Path.Combine(directory,"source");
            Config.Log.LogInfo("Compiling recording snapshot: "+buildDir);
            AuthoringCompiler.Compile(session.Capture(),buildDir,session.FileVersion,AuthoringResourceCatalog.CatalogVersion);
            Config.Log.LogInfo("Snapshot compilation returned.");
            var aas=Directory.GetFiles(buildDir,"*.aas",SearchOption.AllDirectories).Single();
            enhance=withEnhancement; busy=true; Armed=true;
            waitClock.Restart(); status="正在载入剧情…";
            AuthoringMcpPanel.HideCurrent();
            Config.Log.LogInfo("Opening compiled scenario: "+aas);
            if(Studio.Scripts.StudioCommon.instance!=null)
            {
                Studio.Scripts.StudioCommon.instance.Exit();
                StartCoroutine(OpenAfterStudioExit(aas).WrapToIl2Cpp());
            }
            else StartCoroutine(OpenAtMonitorResolution(aas).WrapToIl2Cpp());
            Config.Log.LogInfo("Waiting for scenario player.");
        }
        catch(Exception ex) { Fail(ex); }
    }

    [HideFromIl2Cpp]
    internal string CurrentSource()
    {
        var session=AuthoringEditorSession.Current;
        if(session!=null) return session.FilePath;
        return Object.FindObjectsOfType<CatalogFileInfo>().FirstOrDefault(x=>x.btn!=null && x.btn.gameObject.activeInHierarchy)?.block?.path??"";
    }
    [HideFromIl2Cpp]
    string CreateOutputDirectory(string name)
    {
        name=string.Concat(name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
        var path=Path.Combine(Path.GetFullPath(Config.Output.Value),$"{name}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);return path;
    }
    [HideFromIl2Cpp]
    IEnumerator RecordOpenedProject(string path,bool withEnhancement)
    {
        var clock=Stopwatch.StartNew();
        while(AuthoringEditorSession.Current==null && clock.Elapsed.TotalSeconds<120) yield return null;
        for(int i=0;i<5;i++) yield return null;
        busy=false;
        if(SamePath(AuthoringEditorSession.Current?.FilePath,path)) Begin(withEnhancement);
        else Fail(new IOException("工程未能打开，录制已取消。"));
    }
    [HideFromIl2Cpp]
    static bool SamePath(string? a,string? b)=>!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);

    [HideFromIl2Cpp]
    IEnumerator OpenAtMonitorResolution(string aas)
    {
        try
        {
            monitor=MonitorResolution.Detect(Display.main.systemWidth,Display.main.systemHeight);
            width=monitor.EncodedWidth;height=monitor.EncodedHeight;
            oldWidth=Screen.width;oldHeight=Screen.height;oldFullScreen=Screen.fullScreenMode;displayHeld=true;
            Config.Log.LogInfo($"Monitor capture: {monitor.Width}x{monitor.Height}, {monitor.Device}, {monitor.Source}; previous window {oldWidth}x{oldHeight}.");
            // Borderless desktop size gives both cameras and NGUI the correct
            // aspect ratio before loading the player. Restore the window later.
            Screen.SetResolution(monitor.Width,monitor.Height,FullScreenMode.FullScreenWindow);
        }
        catch(Exception ex) { Fail(ex); }
        var clock=Stopwatch.StartNew();
        while(Armed && clock.Elapsed.TotalSeconds<15 && (Screen.width!=monitor!.Width || Screen.height!=monitor.Height)) yield return null;
        for(int i=0;i<5 && Armed;i++) yield return null;
        if(!Armed) yield break;
        try
        {
            EnsureFfmpeg();
            if(Screen.width!=monitor!.Width || Screen.height!=monitor.Height)
                throw new IOException($"无法设置显示器录制尺寸 {monitor.Width}×{monitor.Height}，当前 {Screen.width}×{Screen.height}。请检查显示模式后重试。");
            Config.Log.LogInfo("Recording FFmpeg: "+Config.Ffmpeg.Value);
            selectedEncoder=Config.Hardware.Value?Encoder.SelectHardwareEncoder(Config.Ffmpeg.Value,width,height,Config.Fps.Value,Config.Crf.Value,directory):"libx264";
            AuthoringWorkbench.OpenScenario(aas);
        }
        catch(Exception ex) { Fail(ex); }
    }

    internal bool Busy { [HideFromIl2Cpp] get=>busy; }
    internal string Status { [HideFromIl2Cpp] get=>status; }
    internal string GpuDescription { [HideFromIl2Cpp] get=>gpuDescription; }
    internal bool EnhancementSupported { [HideFromIl2Cpp] get=>enhancementSupported; }
    internal bool EnhancementAvailable { [HideFromIl2Cpp] get=>RefreshEnhancementAvailability(); }
    internal string EnhancementUnavailableReason { [HideFromIl2Cpp] get=>enhancementSupported?DlssComponents.MissingRuntimeMessage:enhancementUnsupportedReason; }
    [HideFromIl2Cpp]
    bool EnhancementBridgeAvailable()=>File.Exists(Deployment.Enhancer) ||
        (File.Exists(Config.Python.Value) && File.Exists(Path.Combine(Deployment.Root,"bridge","enhance.py")));
    [HideFromIl2Cpp]
    internal bool RefreshEnhancementAvailability(bool force=false)
    {
        if(!enhancementSupported){enhancementComponentsReady=false;return false;}
        if(!force && Time.realtimeSinceStartup<nextComponentProbe)return enhancementComponentsReady;
        nextComponentProbe=Time.realtimeSinceStartup+1;
        try
        {
            var tool=Deployment.ResolveDlssTool(Config.Tool.Value);
            var profiles=Path.Combine(Deployment.Root,"gpu-runtimes");
            bool bridge=EnhancementBridgeAvailable();
            var fingerprint=DlssComponents.RuntimeFingerprint(tool,profiles,selectedSeries)+"|bridge="+bridge;
            if(force || fingerprint!=componentFingerprint)
            {
                componentFingerprint=fingerprint;
                var inspection=DlssComponents.InspectForRuntime(tool,profiles,selectedSeries);
                enhancementComponentsReady=inspection.Ready && bridge;
                availableRuntime=inspection.Runtime;
            }
        }
        catch(Exception ex)
        {
            enhancementComponentsReady=false;availableRuntime="";componentFingerprint="";
            Config.Log.LogWarning("DLSS installed-component check: "+ex.Message);
        }
        return enhancementComponentsReady;
    }
    [HideFromIl2Cpp]
    internal void CancelEnhancement()
    { if(enhancementJob.Length>0) { File.WriteAllText(enhancementJob+".cancel","cancel");status="正在取消增强，原始 MP4 已保留。"; } }

    [HideFromIl2Cpp]
    IEnumerator OpenAfterStudioExit(string aas)
    {
        // The editor contains a Test preview player. The native OpenScenario
        // helper must be called after its scene and editor session are released.
        while(Armed && AuthoringEditorSession.Current!=null) yield return null;
        for(int i=0;i<5 && Armed;i++) yield return null;
        if(Armed)
        {
            try { StartCoroutine(OpenAtMonitorResolution(aas).WrapToIl2Cpp()); Config.Log.LogInfo("Scenario opened after leaving studio."); }
            catch(Exception ex) { Fail(ex); }
        }
    }

    [HideFromIl2Cpp]
    bool PrepareEnhancement(string requestPath)
    {
        var gpu=GpuProfiles.Select(GpuProfiles.Detect());
        selectedGpu=gpu.Name; selectedSeries=gpu.Series;
        if(!GpuProfiles.Hashes.ContainsKey(gpu.Series))
            throw new InvalidOperationException($"已识别 {gpu.Name}，上游未提供此系列配置，暂不支持增强；普通 MP4 录制可用。");
        enhancementSupported=true;
        if(!RefreshEnhancementAvailability(true))throw new FileNotFoundException(DlssComponents.MissingRuntimeMessage);
        runtime=availableRuntime;
        return true;
    }

    [HideFromIl2Cpp]
    void HoldCaptureClock()
    {
        if(settingsHeld)return;
        oldCaptureFps=Time.captureFramerate;oldTargetFps=Application.targetFrameRate;
        oldVsync=QualitySettings.vSyncCount;oldBackground=Application.runInBackground;oldTimeScale=Time.timeScale;settingsHeld=true;
        Time.timeScale=1;Time.captureFramerate=Config.Fps.Value;
        QualitySettings.vSyncCount=0;Application.targetFrameRate=UiDiagnostics.RealtimeTest?Config.Fps.Value:-1;Application.runInBackground=true;
    }
    [HideFromIl2Cpp]
    internal void PlayerStarting(Test value)
    {
        if(!Armed || value.previewMode) return;
        try
        {
            player=value;
            HoldCaptureClock();
            channels=AudioSettings.speakerMode switch { AudioSpeakerMode.Mono=>1, AudioSpeakerMode.Stereo=>2,
                AudioSpeakerMode.Quad=>4, AudioSpeakerMode.Surround=>5, AudioSpeakerMode.Mode5point1=>6, AudioSpeakerMode.Mode7point1=>8,
                _=>throw new InvalidOperationException("当前音频声道模式不受支持。") };
            if(monitor==null) throw new InvalidOperationException("未准备显示器分辨率，录制已取消。");
            if(!AudioRenderer.Start()) throw new InvalidOperationException("Unity AudioRenderer 启动失败，已取消录制。");
            audioStarted=true;
            audioStartDsp=AudioSettings.dspTime;
            bool asynchronous=Config.AsyncReadback.Value && SystemInfo.supportsAsyncGPUReadback;
            encoder=new Encoder(Config.Ffmpeg.Value,directory,width,height,Config.Fps.Value,AudioSettings.outputSampleRate,channels,Config.Crf.Value,selectedEncoder,asynchronous?"rgba":"rgb24");
            if(asynchronous)readback=new FrameReadback(encoder,width,height);
            else{texture=new Texture2D(width,height,TextureFormat.RGB24,false);texture.hideFlags=HideFlags.HideAndDontSave;}
            renderTarget=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32);
            renderTarget.hideFlags=HideFlags.HideAndDontSave;
            if(!renderTarget.Create()) throw new IOException("无法创建离屏录制目标。");
            playbackClockOrigin=RealTime.time;unityClockOrigin=Time.time;
            Armed=false; Capturing=true;
            captureClock.Restart();scriptProgress=0;capturedFrames=0;recordingCameras.Clear();
            scriptLength=0;lastScriptIndex=-1;
            File.WriteAllText(Path.Combine(directory,"recording.json"),JsonSerializer.Serialize(new{
                sourceProject,width,height,fps=Config.Fps.Value,sampleRate=AudioSettings.outputSampleRate,channels,
                monitor=new { monitor.Width,monitor.Height,monitor.Device,monitor.Source },previousWindow=new {width=oldWidth,height=oldHeight},
                capture="Uncapped fixed-step offscreen rendering + AudioRenderer",selectedEncoder,asynchronous,destinationFolder,enhance,selectedGpu,selectedSeries,runtime},new JsonSerializerOptions{WriteIndented=true}));
            StartCoroutine(CaptureLoop().WrapToIl2Cpp());
            Config.Log.LogInfo($"Capture started: {value.gameObject.name}, {width}x{height}, {Config.Fps.Value} fps, {channels} audio channels.");
        }
        catch(Exception ex) { Fail(ex); }
    }

    [HideFromIl2Cpp]
    internal void PlayerStarted(Test value)
    {
        if(!Capturing || player==null || player.Pointer!=value.Pointer) return;
        try
        {
            if(scriptLength==0 && value.scn!=null)
            {
                // Invoke on the actual boxed table type. The generated IL2CPP
                // interface accessor can dispatch to an unrelated table getter.
                var table=value.scn.Cast<Il2CppSystem.Object>();
                var property=table.GetIl2CppType().GetProperty("DataListLength");
                if(property!=null)scriptLength=property.GetValue(table,null).Unbox<int>();
            }
            if(!value.IsAutoEnabled) value.ToggleAuto();
            HideControls(value);
        }
        catch(Exception ex) { Fail(ex); }
    }

    [HideFromIl2Cpp]
    internal void HideControls(Test value)
    {
        if(!Capturing || player==null || player.Pointer!=value.Pointer) return;
        // Test.Update re-enables the menu during normal play. Apply after it,
        // before NGUI rebuilds geometry in LateUpdate.
        if(value.menuBtn!=null) value.menuBtn.gameObject.SetActive(false);
        if(value.autoToggle!=null) value.autoToggle.gameObject.SetActive(false);
    }

    [HideFromIl2Cpp]
    IEnumerator CaptureLoop()
    {
        var end=new WaitForEndOfFrame();
        while(Capturing)
        {
            yield return end;
            if(!Capturing) break;
            try { CaptureFrame(); }
            catch(Exception ex) { Fail(ex); }
        }
    }

    [HideFromIl2Cpp]
    unsafe void CaptureFrame()
    {
        if(Screen.width/2*2!=width || Screen.height/2*2!=height) throw new InvalidOperationException("录制时显示模式发生变化，已中止并保留临时数据。请保持录制显示模式固定。");
        int rate=AudioSettings.outputSampleRate,fps=Config.Fps.Value;
        int count=AudioRenderer.GetSampleCountForCaptureFrame();
        if(capturedFrames==0)Config.Log.LogInfo($"Audio budget: engine={AudioRenderer.GetSampleCountForCaptureFrame()}, requested={count}, dsp={audioStartDsp}");
        // The mixer emits whole DSP blocks. At 60 fps a video frame may be
        // shorter than one audio block: zero samples are valid, and Render
        // must still run to advance its fractional-frame accumulator.
        if(count<0 || count>rate)throw new IOException("离屏混音采样数量异常，已停止内录。");
        var samples=new float[count*channels];
        float empty=0;
        fixed(float* pointer=samples)
            if(!AudioRenderer.Internal_AudioRenderer_Render(samples.Length==0?&empty:pointer,samples.Length)) throw new IOException("Unity 混音采集失败。");
        if(capturedFrames<4)Config.Log.LogInfo($"Audio frame {capturedFrames}: samples={count}, dspElapsed={AudioSettings.dspTime-audioStartDsp:0.000000}, delta={Time.deltaTime}, unscaled={Time.unscaledDeltaTime}, ngui={RealTime.deltaTime}");
        if(capturedFrames>=fps && Math.Abs(AudioSettings.dspTime-audioStartDsp-(double)(capturedFrames+1)/fps)>.15)
            throw new IOException($"离屏音频时钟未正确推进：实际 {AudioSettings.dspTime-audioStartDsp:0.000}s / 应有 {(double)(capturedFrames+1)/fps:0.000}s，已停止以避免无声或不同步视频。");
        if(recordingCameras.Count==0) throw new IOException("剧情场景没有可录制的相机。");
        if(readback!=null)readback.Submit(renderTarget!,samples);
        else
        {
        var previous=RenderTexture.active;
        try
        {
            RenderTexture.active=renderTarget;
            // Cameras rendered directly into this target once during Unity's
            // render phase. The screen-only progress camera is never captured.
            texture!.ReadPixels(new Rect(0,0,width,height),0,0,false);
        }
        finally { RenderTexture.active=previous; }
        // Read the CPU texture buffer without per-byte IL2CPP property calls.
        var size=texture!.GetRawImageDataSize();
        if(size!=(ulong)(width*height*3)) throw new IOException("离屏纹理格式不符。");
        var raw=texture.GetRawTextureData();
        var rgb=System.Buffers.ArrayPool<byte>.Shared.Rent(raw.Length);
        System.Runtime.InteropServices.Marshal.Copy(raw.Pointer+4*IntPtr.Size,rgb,0,raw.Length);
        encoder!.WritePooledFrame(rgb,samples);
        }
        capturedFrames++;
        if(capturedFrames==1) Config.Log.LogInfo($"First frame captured; {count} audio samples per channel, cameras={recordingCameras.Count}; encoder={selectedEncoder}; async={readback!=null}; display camera excluded.");
        if(UiDiagnostics.CaptureTest && capturedFrames==60)UiDiagnostics.SaveFrame(Path.Combine(UiDiagnostics.Root,"progress-screen.ppm"));
        if(UiDiagnostics.StopTest && capturedFrames==120)OnMainThread(()=>UiDiagnostics.Click("AARecorder_Stop"));
        if(capturedFrames%Config.Fps.Value==0) status=$"正在录制 {capturedFrames/Config.Fps.Value} 秒；F10 停止";
    }

    [HideFromIl2Cpp]
    internal void OnEnd(Test value)
    {
        if(Capturing && player!=null && player.Pointer==value.Pointer) Stop("剧情播放完成");
    }

    [HideFromIl2Cpp]
    void Restore()
    {
        RecordingRealtimeWait.Restore();
        Capturing=false; Armed=false;
        readback?.Discard();readback=null;
        captureClock.Stop();
        foreach(var cam in recordingCameras)if(cam!=null && cam.targetTexture==renderTarget)cam.targetTexture=null;
        recordingCameras.Clear();
        if(audioStarted) { AudioRenderer.Stop(); audioStarted=false; }
        if(settingsHeld)
        {
            Time.captureFramerate=oldCaptureFps; Time.timeScale=oldTimeScale; Application.targetFrameRate=oldTargetFps;
            QualitySettings.vSyncCount=oldVsync; Application.runInBackground=oldBackground; settingsHeld=false;
        }
        if(displayHeld) { Screen.SetResolution(oldWidth,oldHeight,oldFullScreen);displayHeld=false; }
        if(texture!=null) { Object.Destroy(texture); texture=null; }
        if(renderTarget!=null) { renderTarget.Release(); Object.Destroy(renderTarget); renderTarget=null; }
        if(player!=null)
        {
            try { if(player.menuBtn!=null) player.menuBtn.gameObject.SetActive(true); if(player.autoToggle!=null) player.autoToggle.gameObject.SetActive(true); }
            catch { }
        }
    }

    [HideFromIl2Cpp]
    internal void Stop(string reason)
    {
        if(!Armed&&!Capturing) return;
        try{readback?.Flush(true);}catch(Exception ex){Fail(ex);return;}
        Restore();
        if(reason!="剧情播放完成" && player!=null) { try { player.End(); } catch(Exception ex) {Config.Log.LogWarning(ex.Message);} }
        var current=encoder; encoder=null;
        if(current==null) { busy=false; status="已取消等待。"; return; }
        status=reason+"，正在封装 MP4…";
        // Snapshot configuration before using a background task.
        var python=Config.Python.Value; var tool=Deployment.ResolveDlssTool(Config.Tool.Value);
        var scale=Config.Scale.Value; var multiplier=Config.Multiplier.Value; var neural=Config.Neural.Value;
        var timeout=Config.EnhancementTimeout.Value;
        var jobDir=directory; var dll=runtime; var gpu=selectedGpu; var series=selectedSeries; var doEnhance=enhance;
        var destination=destinationFolder;var renderSeconds=captureClock.Elapsed.TotalSeconds;
        var exportState=Path.Combine(Path.GetDirectoryName(Config.Config.ConfigFilePath)!,"azurearchive.recorder-state");
        Task.Run(async()=>
        {
            SavedExport? exported=null;
            try
            {
                var output=await current.FinishAsync();
                exported=DailyExportNaming.Begin(output,destination,exportState,doEnhance);
                var published=exported.OriginalPath;
                if(doEnhance)
                {
                    var inspection=DlssComponents.InspectForRuntime(tool,Path.Combine(Deployment.Root,"gpu-runtimes"),series);
                    if(!inspection.Ready || !EnhancementBridgeAvailable())throw new FileNotFoundException(DlssComponents.MissingRuntimeMessage);
                    dll=inspection.Runtime;
                    actions.Enqueue(()=>status="原始 MP4 已保存，正在超分 / 补帧…");
                    var job=Path.Combine(jobDir,"enhancement-job.json");
                    File.WriteAllText(job,JsonSerializer.Serialize(new { input=output,output=Path.Combine(jobDir,"enhanced.mp4"),
                        tool_directory=tool,runtime=dll,series,gpu,scale,multiplier,neural },new JsonSerializerOptions{WriteIndented=true}));
                    using var bridge=Deployment.StartEnhancement(python,job);
                    enhancementProcess=bridge; enhancementJob=job;enhancementProgress=0;
                    bridge.StandardInput.Close();
                    await EnhancementMonitor.RunAsync(bridge,job,TimeSpan.FromMinutes(timeout),TimeSpan.FromSeconds(10),
                        (message,fraction)=>actions.Enqueue(()=>{status=message;if(fraction>=0)enhancementProgress=fraction;}));
                    output=Path.Combine(jobDir,"enhanced.mp4");
                    if(!File.Exists(output)) throw new IOException("增强进程未生成 MP4。");
                    published=DailyExportNaming.FinishEnhancement(output,exported);
                }
                File.WriteAllText(Path.Combine(jobDir,"completed.json"),JsonSerializer.Serialize(new { output=published,originalVideo=exported.OriginalPath,workingVideo=output,frames=current.Frames,audioSamples=current.AudioSamples,reason,enhanced=doEnhance,renderSeconds,totalSeconds=jobClock.Elapsed.TotalSeconds,videoSeconds=(double)current.Frames/Config.Fps.Value }));
                actions.Enqueue(()=>{
                    busy=false;succeeded=true;finalOutput=published;status="已保存："+published;Config.Log.LogInfo(status);
                    if(smokeSource.Length>0 || UiDiagnostics.CaptureTest)
                    {
                        File.WriteAllText(Path.Combine(jobDir,"restored-window.json"),JsonSerializer.Serialize(new {width=Screen.width,height=Screen.height,mode=Screen.fullScreenMode.ToString(),expectedWidth=oldWidth,expectedHeight=oldHeight,expectedMode=oldFullScreen.ToString()}));
                        Application.Quit();
                    }
                });
            }
            catch(OperationCanceledException ex)
            {
                File.WriteAllText(Path.Combine(jobDir,"cancelled.json"),JsonSerializer.Serialize(new{cancelled=true,originalVideo=Path.Combine(jobDir,"recording.mp4"),publishedOriginal=exported?.OriginalPath,message=ex.Message}));
                actions.Enqueue(()=>{busy=false;status=ex.Message;Config.Log.LogInfo(status);if(smokeSource.Length>0 || UiDiagnostics.CaptureTest)Application.Quit(2);});
            }
            catch(Exception ex) { actions.Enqueue(()=>Fail(ex)); }
            finally { enhancementJob=""; enhancementProcess=null; current.Dispose(); }
        });
    }

    [HideFromIl2Cpp]
    void Fail(Exception ex)
    {
        bool wasPlaying=Capturing;
        Restore(); encoder?.Abort(); encoder?.Dispose(); encoder=null; busy=false;
        if(wasPlaying && player!=null)try{player.End();}catch{}
        status=ex.Message; Config.Log.LogError(ex);
        if(directory.Length>0) try { File.WriteAllText(Path.Combine(directory,"error.txt"),ex.ToString()); } catch { }
        if(smokeSource.Length>0 || UiDiagnostics.CaptureTest) Application.Quit(1);
    }
    public void OnApplicationQuit()
    {
        if(diagnosticOriginalFps>0)Config.Fps.Value=diagnosticOriginalFps;
        Restore(); encoder?.Abort(); encoder?.Dispose(); encoder=null;
        if(enhancementJob.Length>0) try { File.WriteAllText(enhancementJob+".cancel","cancel"); } catch { }
        try { if(enhancementProcess!=null && !enhancementProcess.WaitForExit(2000)) enhancementProcess.Kill(true); } catch { }
    }
}

[HarmonyPatch(typeof(Test),nameof(Test.Start))]
static class PlaybackStart
{
    static void Prefix(Test __instance) => RecorderBehaviour.Instance?.PlayerStarting(__instance);
    static void Postfix(Test __instance) => RecorderBehaviour.Instance?.PlayerStarted(__instance);
}
[HarmonyPatch(typeof(Test),nameof(Test.End))]
static class PlaybackEnd { static void Prefix(Test __instance) => RecorderBehaviour.Instance?.OnEnd(__instance); }
static class PlaybackDestroyed { static void Prefix(Test __instance) => RecorderBehaviour.Instance?.OnEnd(__instance); }

static class PlaybackReady
{
    static void Postfix(Test __instance) => RecorderBehaviour.Instance?.PlayerStarted(__instance);
}

// NGUI's typewriter/tweens use their own wall clock. During offline rendering
// they must follow the same fixed video timeline as Unity animations and audio.
[HarmonyPatch(typeof(RealTime),"get_time")]
static class RecordingUiTime
{
    static bool Prefix(ref float __result)
    {var r=RecorderBehaviour.Instance;if(r==null || !r.Capturing)return true;__result=r.PlaybackTime;return false;}
}
[HarmonyPatch(typeof(RealTime),"get_deltaTime")]
static class RecordingUiDelta
{
    static bool Prefix(ref float __result)
    {if(RecorderBehaviour.Instance?.Capturing!=true)return true;__result=Time.deltaTime;return false;}
}

[HarmonyPatch(typeof(Time),"get_unscaledDeltaTime")]
static class RecordingUnscaledDelta
{
    static bool Prefix(ref float __result)
    {if(RecorderBehaviour.Instance?.Capturing!=true)return true;__result=Time.deltaTime;return false;}
}

static class RecordingDelays
{
    static void Postfix(object __instance,bool __result)
    {
        var r=RecorderBehaviour.Instance;
        if(!__result || r?.Capturing!=true)return;
        var type=__instance.GetType();
        var owner=type.GetProperty("__4__this")?.GetValue(__instance) as Test;
        if(owner==null || !r.OwnsPlayer(owner))return;
        var current=type.GetProperty("__2__current")?.GetValue(__instance) as Il2CppSystem.Object;
        var wait=current?.TryCast<WaitForSecondsRealtime>();
        if(wait!=null)RecordingRealtimeWait.Track(wait);
        r.LogDelay(current?.GetIl2CppType().Name??"null",wait?.waitTime??0);
    }
}
[HarmonyPatch(typeof(WaitForSecondsRealtime),"get_keepWaiting")]
static class RecordingRealtimeWait
{
    static readonly Dictionary<IntPtr,(WaitForSecondsRealtime Wait,float Deadline)> deadlines=new();
    internal static void Track(WaitForSecondsRealtime wait)
    {
        var r=RecorderBehaviour.Instance!;
        if(deadlines.ContainsKey(wait.Pointer))return;
        var remaining=wait.m_WaitUntilTime<0?wait.waitTime:Math.Max(0,wait.m_WaitUntilTime-Time.realtimeSinceStartup);
        deadlines[wait.Pointer]=(wait,r.PlaybackTime+remaining);
    }
    internal static void Restore()
    {
        var r=RecorderBehaviour.Instance;
        if(r!=null)foreach(var entry in deadlines.Values)
            entry.Wait.m_WaitUntilTime=Time.realtimeSinceStartup+Math.Max(0,entry.Deadline-r.PlaybackTime);
        deadlines.Clear();
    }
    static bool Prefix(WaitForSecondsRealtime __instance,ref bool __result)
    {
        var r=RecorderBehaviour.Instance;
        if(r?.Capturing!=true)return true;
        Track(__instance);
        __result=r.PlaybackTime<deadlines[__instance.Pointer].Deadline;
        if(!__result){deadlines.Remove(__instance.Pointer);__instance.Reset();}
        return false;
    }
}

[HarmonyPatch(typeof(Test),nameof(Test.Update))]
static class PlaybackControls { static void Postfix(Test __instance) => RecorderBehaviour.Instance?.HideControls(__instance); }

[HarmonyPatch(typeof(Studio.Scripts.NotificationManager),nameof(Studio.Scripts.NotificationManager.Notify))]
static class RecordingNotifications
{
    static bool Prefix(string __0)
    {
        var recorder=RecorderBehaviour.Instance;
        if(recorder==null || (!recorder.Armed && !recorder.Capturing)) return true;
        RecorderPlugin.Instance.Log.LogWarning("Notification during recording: "+__0);
        return false;
    }
}
