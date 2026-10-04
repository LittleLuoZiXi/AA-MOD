"""Headless adapter to DLSS5Tool v2.3.3; --check never initializes a GPU.

Only owned per-job state is written. The portable app's settings, queue,
_internal DLLs and user mods are never modified.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import time
import traceback

HERE = Path(__file__).resolve().parent
PROFILES = json.loads((HERE / "gpu_profiles.json").read_text(encoding="utf-8"))["profiles"]


def configure_paths():
    # Also runs in multiprocessing spawn children, before they import native hosts.
    tool = os.environ.get("AA_RECORDER_DLSS_TOOL")
    work = os.environ.get("AA_RECORDER_DLSS_WORK")
    if not tool or not work:
        return
    sys.path.insert(0, str(HERE / "vendor"))
    from dlss5tool import paths
    root = Path(tool)
    state = Path(work)
    paths.app_root = lambda: root
    paths.resource_root = lambda: root / "_internal"
    paths.runtime_root = lambda: root / "_internal"
    paths.state_path = lambda filename: state / filename
    os.environ["FFMPEG_EXE"] = str(root / "_internal" / "ffmpeg.EXE")
    probe = root / "_internal" / "ffprobe.exe"
    if probe.is_file():
        os.environ["FFPROBE_EXE"] = str(probe)
    # Preserve handles for the lifetime of the process.
    if hasattr(os, "add_dll_directory"):
        global DLL_DIRECTORIES
        DLL_DIRECTORIES = [os.add_dll_directory(str(root / "_internal"))]


configure_paths()


def validate_job(job: dict, *, check_dependencies: bool = True) -> dict:
    required = {"input", "output", "tool_directory", "runtime", "series", "gpu", "scale", "multiplier", "neural"}
    if set(job) != required:
        raise ValueError("任务字段缺失或未知：" + str(set(job) ^ required))
    if type(job["series"]) is not int:
        raise ValueError("显卡系列必须是整数。")
    profile = PROFILES.get(str(job["series"]))
    if not profile or not profile["supported"]:
        raise ValueError("此显卡系列暂不支持 DLSS5Tool 增强。")
    if type(job["scale"]) is not int or job["scale"] not in (1, 2, 4):
        raise ValueError("超分只支持 1× / 2× / 4×。")
    if type(job["multiplier"]) is not int or job["multiplier"] not in (1, 2, 3, 4):
        raise ValueError("补帧只支持 1× / 2× / 3× / 4×。")
    if type(job["neural"]) is not bool:
        raise ValueError("neural 必须为布尔值。")
    source = Path(job["input"]).resolve(strict=True)
    output = Path(job["output"]).resolve()
    tool = Path(job["tool_directory"]).resolve(strict=True)
    runtime = Path(job["runtime"]).resolve(strict=True)
    if output == source or output.exists():
        raise ValueError("禁止覆盖源视频或已有输出。")
    if source.suffix.lower() != ".mp4" or output.suffix.lower() != ".mp4":
        raise ValueError("录制桥接仅接受 MP4。")
    if not output.parent.is_dir():
        raise ValueError("输出目录不存在。")
    if hashlib.sha256(runtime.read_bytes()).hexdigest() != profile["sha256"]:
        raise ValueError("显卡运行库 SHA-256 不匹配；禁止借用其他系列配置。")
    components = ["ffmpeg.EXE"]
    if job["scale"] > 1:
        components += ["vsr_host.dll", "nvngx_vsr.dll"]
    if job["multiplier"] > 1:
        components += ["dlssg_video_worker.exe", "nvngx_dlssg.dll"]
    if job["neural"]:
        components += ["dlssnr_host_v2.dll"]
    for component in components:
        if not (tool / "_internal" / component).is_file():
            raise FileNotFoundError("DLSS5Tool 组件缺失：" + component)
    if check_dependencies:
        missing = [name for name in ("numpy", "cv2", "av", "imageio_ffmpeg") if importlib.util.find_spec(name) is None]
        if missing:
            raise RuntimeError("桥接环境缺少 " + ", ".join(missing) + "；请运行 setup-dlss.ps1。")
    return {**job, "input": str(source), "output": str(output), "tool_directory": str(tool), "runtime": str(runtime)}


def publish(path: Path, payload: dict, *, required: bool = True) -> bool:
    # On Windows a reader without FILE_SHARE_DELETE can briefly block replace.
    # Progress is advisory; losing an update must not discard successful GPU work.
    # Results are mandatory and still fail explicitly after a bounded retry.
    temp = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                                         prefix=path.name + ".", suffix=".tmp", delete=False) as file:
            temp = Path(file.name)
            json.dump(payload, file, ensure_ascii=False, indent=2)
        deadline = time.monotonic() + (2.0 if required else 0.05)
        while True:
            try:
                os.replace(temp, path)
                return True
            except PermissionError:
                if time.monotonic() >= deadline:
                    if required:
                        raise
                    return False
                time.sleep(0.01 if not required else 0.05)
    finally:
        if temp is not None:
            temp.unlink(missing_ok=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("job", type=Path)
    parser.add_argument("--check", action="store_true", help="只做文件/配置/依赖检查，不加载 GPU 运行库")
    args = parser.parse_args(argv)
    report_path = args.job.with_name("enhancement-result.json")
    try:
        job = validate_job(json.loads(args.job.read_text(encoding="utf-8-sig")))
        if args.check:
            print(json.dumps({"validated": True, "gpu_executed": False, "series": job["series"]}))
            return 0
        os.environ["AA_RECORDER_DLSS_TOOL"] = job["tool_directory"]
        os.environ["AA_RECORDER_DLSS_WORK"] = str(args.job.resolve().parent)
        configure_paths()
        from dlss5tool.frame_generation import export_video
        from dlss5tool.app_settings import DEFAULTS
        # Fresh settings snapshot: do not inherit unrelated GUI/user preferences.
        config = dict(DEFAULTS)
        config.update(dlss_runtime=job["runtime"], host_backend="v2", host_auto_fallback=False,
                      guidance_mode=0, frame_generation_multiplier=job["multiplier"],
                      super_resolution_scale=job["scale"], hdr_mode=False,
                      output_view=0, output_mix=1.0, output_container="mp4",
                      quality_profile="high", rate_control="quality", render_gpu="auto")
        progress_path = args.job.with_name("enhancement-progress.json")
        cancel = threading.Event()
        done = threading.Event()
        cancel_path = Path(str(args.job) + ".cancel")
        def watch_cancel():
            while not done.wait(0.25):
                if cancel_path.exists():
                    cancel.set()
                    return
        threading.Thread(target=watch_cancel, name="recorder-cancel", daemon=True).start()
        last_progress = 0.0
        def progress(message, fraction, *extra):
            nonlocal last_progress
            now = time.monotonic()
            if fraction >= 1 or now - last_progress >= 0.25:
                publish(progress_path, {"message": message, "fraction": fraction}, required=False)
                last_progress = now
        try:
            result = export_video(job["input"], job["output"], multiplier=job["multiplier"], scale=job["scale"],
                                  enhance=job["neural"], settings=config, cancel=cancel,
                                  progress=progress, log_dir=args.job.with_name("dlss-diagnostics"))
        finally:
            done.set()
        if result.get("status") != "complete" or not Path(job["output"]).is_file():
            raise RuntimeError("上游未返回完成状态或缺少视频文件。")
        publish(report_path, {"ok": True, "job": job, "upstream": result})
        return 0
    except Exception as error:
        publish(report_path, {"ok": False, "error": str(error), "traceback": traceback.format_exc()})
        print(traceback.format_exc(), file=sys.stderr)
        return 1


if __name__ == "__main__":
    import multiprocessing
    multiprocessing.freeze_support()
    raise SystemExit(main())
