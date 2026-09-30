"""Run one real source-bridge GPU stage in a new, evidence-preserving directory."""
import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("--tool", required=True, type=Path)
    parser.add_argument("--runtime", required=True, type=Path)
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--stage", required=True)
    parser.add_argument("--scale", type=int, default=1)
    parser.add_argument("--multiplier", type=int, default=1)
    parser.add_argument("--neural", action="store_true")
    parser.add_argument("--timeout", type=float, default=600)
    parser.add_argument("--cancel-after", type=float)
    args = parser.parse_args()
    recorder = Path(__file__).resolve().parents[1]
    devices = subprocess.check_output([
        "nvidia-smi", "--query-gpu=name,driver_version,memory.total,pci.bus_id",
        "--format=csv,noheader"], text=True).strip()
    rows = devices.splitlines()
    if len(rows) != 1:
        raise RuntimeError("This evidence runner requires one NVIDIA GPU; inspect multi-GPU selection separately.")
    gpu = rows[0].split(",")[0].strip()
    match = re.search(r"\bRTX\s*(30|40|50)\d{2}\b", gpu)
    if not match:
        raise RuntimeError("Unsupported GPU: " + gpu)
    label = re.sub(r"[^a-zA-Z0-9_-]", "_", args.stage)
    directory = args.evidence.resolve() / (dt.datetime.now().strftime("%Y%m%d-%H%M%S-") + label + "-" + uuid.uuid4().hex[:6])
    directory.mkdir(parents=True, exist_ok=False)
    job = dict(input=str(args.input.resolve(strict=True)), output=str(directory / "enhanced.mp4"),
               tool_directory=str(args.tool.resolve(strict=True)), runtime=str(args.runtime.resolve(strict=True)),
               series=int(match.group(1)), gpu=gpu, scale=args.scale, multiplier=args.multiplier, neural=args.neural)
    path = directory / "enhancement-job.json"
    path.write_text(json.dumps(job, ensure_ascii=False, indent=2), encoding="utf-8")
    command = [sys.executable, "-u", str(recorder / "bridge" / "enhance.py"), str(path)]
    with (directory / "precheck.log").open("w", encoding="utf-8") as log:
        check = subprocess.run(command + ["--check"], stdout=log, stderr=subprocess.STDOUT, timeout=60)
    if check.returncode:
        print("Precheck failed: " + str(directory), flush=True)
        return check.returncode
    meta = {"device": devices, "python": sys.version, "command": command,
            "input_sha256": hashlib.file_digest(open(job["input"], "rb"), "sha256").hexdigest(),
            "runtime_sha256": hashlib.file_digest(open(job["runtime"], "rb"), "sha256").hexdigest(),
            "mode": "actual GPU inference via source bridge (not frozen AA integration)"}
    print("Real GPU stage: " + str(directory), flush=True)
    started = time.monotonic()
    cancellation_sent = False
    peak_memory = 0
    with (directory / "enhancement.log").open("wb") as log, (directory / "gpu-samples.csv").open("w", encoding="utf-8") as samples:
        samples.write("seconds,memory_used_mib,gpu_utilization_percent\n")
        proc = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
        while proc.poll() is None:
            elapsed = time.monotonic() - started
            if args.cancel_after is not None and elapsed >= args.cancel_after and not cancellation_sent:
                Path(str(path) + ".cancel").write_text("cancel", encoding="utf-8")
                cancellation_sent = True
                meta["cancel_requested_seconds"] = elapsed
            if elapsed > args.timeout:
                Path(str(path) + ".cancel").write_text("timeout", encoding="utf-8")
                try:
                    proc.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
                meta["harness_timeout"] = True
                break
            raw = subprocess.run(["nvidia-smi", "--query-gpu=memory.used,utilization.gpu", "--format=csv,noheader,nounits"],
                                 capture_output=True, text=True, timeout=10)
            if raw.returncode == 0:
                memory, usage = [int(x.strip()) for x in raw.stdout.strip().split(",")]
                peak_memory = max(peak_memory, memory)
                samples.write(f"{elapsed:.3f},{memory},{usage}\n")
                samples.flush()
            time.sleep(1)
        meta.update(elapsed_seconds=time.monotonic()-started, exit_code=proc.poll(),
                    peak_device_memory_mib=peak_memory, cancelled=cancellation_sent)
    (directory / "run-metadata.json").write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"directory": str(directory), **{k: meta[k] for k in ("elapsed_seconds", "exit_code", "peak_device_memory_mib")}}, ensure_ascii=False), flush=True)
    return proc.returncode if proc.returncode is not None else 124


if __name__ == "__main__":
    raise SystemExit(main())
