#!/usr/bin/env python3
"""Detect the known AA MENU label in every decoded video frame.

Uses only Python's standard library and an existing CPU FFmpeg/ffprobe pair.
The template is extracted at runtime from ReferenceVideo; no story pixels are
embedded in this source. Defaults are calibrated for the 3120x2080 AA capture
whose known MENU flash is zero-based frame 162 (5.4 seconds at 30 fps).

Example (PowerShell):
  & .venv/Scripts/python.exe tests/check-menu-frames.py `
      --ReferenceVideo old.mp4 --InputVideo new.mp4 --OutputReport menu.json

Exit codes: 0 = scan completed (inspect matched_frames), 1 = processing error,
2 = argument error. This measures pixels, not game object activation state.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
from fractions import Fraction
import hashlib
import heapq
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import time

DEFAULT_FFMPEG = r"D:\test2\work\mods\AzureArchiveRecorder\runtime\ffmpeg\ffmpeg.exe"
DEFAULT_ROI = (2840, 68, 190, 62)  # x, y, width, height; MENU letters and padding


def positive_float(value: str) -> float:
    parsed = float(value)
    if not math.isfinite(parsed) or parsed <= 0:
        raise argparse.ArgumentTypeError("must be a positive finite number")
    return parsed


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--ReferenceVideo", "--reference-video", required=True, type=Path)
    parser.add_argument("--InputVideo", "--input-video", required=True, type=Path)
    parser.add_argument("--OutputReport", "--output-report", required=True, type=Path)
    parser.add_argument("--FFmpeg", "--ffmpeg", default=DEFAULT_FFMPEG, type=Path)
    parser.add_argument("--ReferenceFrame", "--reference-frame", default=162, type=int,
                        help="zero-based decoded reference frame containing MENU (default: 162)")
    parser.add_argument("--ROI", "--roi", default=DEFAULT_ROI, nargs=4, type=int,
                        metavar=("X", "Y", "WIDTH", "HEIGHT"))
    parser.add_argument("--firstNseconds", "--FirstNSeconds", "--first-n-seconds", type=positive_float,
                        help="scan only the first N seconds, without seeking or skipping frames")
    parser.add_argument("--correlation-threshold", default=0.90, type=float)
    parser.add_argument("--mae-threshold", default=22.0, type=positive_float,
                        help="maximum mean absolute grayscale error, on a 0..255 scale")
    parser.add_argument("--contrast-threshold", default=25.0, type=positive_float,
                        help="minimum grayscale standard deviation")
    parser.add_argument("--decode-threads", default=4, type=int)
    args = parser.parse_args()
    if args.ReferenceFrame < 0 or args.decode_threads < 1:
        parser.error("ReferenceFrame must be nonnegative and decode-threads must be positive")
    if not math.isfinite(args.correlation_threshold) or not 0 < args.correlation_threshold <= 1:
        parser.error("correlation-threshold must be in (0, 1]")
    if min(args.ROI[:2]) < 0 or min(args.ROI[2:]) < 4:
        parser.error("ROI coordinates must be nonnegative and dimensions at least four pixels")
    return args


def run_options() -> dict:
    return {"creationflags": subprocess.CREATE_NO_WINDOW} if sys.platform == "win32" else {}


def probe(ffprobe: Path, video: Path) -> dict:
    completed = subprocess.run(
        [str(ffprobe), "-v", "error", "-select_streams", "v:0", "-show_entries",
         "stream=index,codec_name,width,height,avg_frame_rate,r_frame_rate,nb_frames,duration,start_time:format=duration",
         "-of", "json", str(video)], capture_output=True, check=True, **run_options())
    result = json.loads(completed.stdout)
    if len(result.get("streams", [])) != 1:
        raise ValueError(f"Expected one selected video stream in {video}")
    stream = result["streams"][0]
    stream["path"] = str(video.resolve())
    stream["file_bytes"] = video.stat().st_size
    stream["format_duration"] = result.get("format", {}).get("duration")
    rate = Fraction(stream["avg_frame_rate"])
    if rate <= 0:
        raise ValueError("Video has no usable average frame rate")
    stream["fps"] = float(rate)
    stream["frame_time_basis"] = "zero-based decoded frame index / avg_frame_rate; CFR recording assumed"
    if stream.get("r_frame_rate") != stream.get("avg_frame_rate"):
        raise ValueError("r_frame_rate differs from avg_frame_rate; this checker requires CFR input")
    return stream


def ffmpeg_base(args: argparse.Namespace, video: Path) -> list[str]:
    return [str(args.FFmpeg), "-hide_banner", "-loglevel", "error", "-nostdin",
            "-hwaccel", "none", "-threads", str(args.decode_threads), "-i", str(video),
            "-map", "0:v:0", "-an", "-sn", "-dn"]


def roi_filter(roi: tuple[int, ...], width: int, height: int) -> str:
    x, y, w, h = roi
    return f"crop={w}:{h}:{x}:{y}:exact=1,scale={width}:{height}:flags=area,format=gray"


def extract_template(args: argparse.Namespace, width: int, height: int) -> bytes:
    # Decode from frame zero so ReferenceFrame means an exact decoded index.
    filters = f"select=eq(n\\,{args.ReferenceFrame})," + roi_filter(tuple(args.ROI), width, height)
    completed = subprocess.run(ffmpeg_base(args, args.ReferenceVideo) +
                               ["-vf", filters, "-frames:v", "1", "-fps_mode", "passthrough",
                                "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"],
                               capture_output=True, check=True, **run_options())
    if len(completed.stdout) != width * height:
        raise ValueError("ReferenceFrame is missing or extracted template has an unexpected size")
    return completed.stdout


def metrics(pixels: bytes, template: bytes, centered: tuple[float, ...], template_norm: float) -> tuple[float, float, float]:
    count = len(pixels)
    total = sum(pixels)
    variance_sum = max(0.0, sum(value * value for value in pixels) - total * total / count)
    contrast = math.sqrt(variance_sum / count)
    correlation = (sum(value * weight for value, weight in zip(pixels, centered)) /
                   (math.sqrt(variance_sum) * template_norm)) if variance_sum > 1e-9 else 0.0
    mae = sum(abs(value - ref) for value, ref in zip(pixels, template)) / count
    return max(-1.0, min(1.0, correlation)), mae, contrast


def score_record(index: int, fps: float, score: tuple[float, float, float], matched: bool) -> dict:
    correlation, mae, contrast = score
    return {"frame": index, "time_seconds": round(index / fps, 6),
            "correlation": round(correlation, 6), "mean_absolute_error": round(mae, 6),
            "contrast_stddev": round(contrast, 6), "matched": matched}


def read_exact(pipe, length: int) -> bytes:
    chunks = []
    remaining = length
    while remaining:
        chunk = pipe.read(remaining)
        if not chunk:
            break
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def scan(args: argparse.Namespace, input_metadata: dict, template: bytes, width: int, height: int) -> dict:
    count = len(template)
    template_mean = sum(template) / count
    centered = tuple(value - template_mean for value in template)
    template_norm = math.sqrt(sum(value * value for value in centered))
    template_std = template_norm / math.sqrt(count)
    if template_std < args.contrast_threshold:
        raise ValueError(f"Reference ROI has insufficient contrast ({template_std:.3f}); verify its frame and ROI")
    command = ffmpeg_base(args, args.InputVideo)
    if args.firstNseconds is not None:
        command += ["-t", str(args.firstNseconds)]
    command += ["-vf", roi_filter(tuple(args.ROI), width, height), "-fps_mode", "passthrough",
                "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"]
    fps = input_metadata["fps"]
    matches, nearby, top_nonmatches = [], [], []
    index = 0
    maximum_nonmatch = -1.0
    same_reference = args.InputVideo.resolve() == args.ReferenceVideo.resolve()
    started = time.perf_counter()
    next_progress = started + 15
    with tempfile.TemporaryFile() as errors:
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=errors, **run_options())
        try:
            while True:
                pixels = read_exact(process.stdout, count)
                if not pixels:
                    break
                if len(pixels) != count:
                    raise ValueError(f"Truncated grayscale frame at index {index}")
                score = metrics(pixels, template, centered, template_norm)
                correlation, mae, contrast = score
                matched = (correlation >= args.correlation_threshold and mae <= args.mae_threshold
                           and contrast >= args.contrast_threshold)
                record = score_record(index, fps, score, matched)
                if matched:
                    matches.append(record)
                else:
                    maximum_nonmatch = max(maximum_nonmatch, correlation)
                    item = (correlation, index, record)
                    if len(top_nonmatches) < 20:
                        heapq.heappush(top_nonmatches, item)
                    elif item[:2] > top_nonmatches[0][:2]:
                        heapq.heapreplace(top_nonmatches, item)
                if same_reference and abs(index - args.ReferenceFrame) <= 5:
                    nearby.append(record)
                index += 1
                now = time.perf_counter()
                if now >= next_progress:
                    print(f"Scanned {index} frames ({index / fps:.1f}s), MENU matches: {len(matches)}", file=sys.stderr, flush=True)
                    next_progress = now + 15
            return_code = process.wait()
            if return_code:
                errors.seek(0)
                raise RuntimeError(f"FFmpeg exited {return_code}: {errors.read().decode(errors='replace')[-8000:]}")
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            process.stdout.close()
    if index == 0:
        raise ValueError("No video frames were decoded")
    expected = input_metadata.get("nb_frames")
    if args.firstNseconds is None and expected and expected != "N/A" and index != int(expected):
        raise ValueError(f"Full scan decoded {index} frames, but stream metadata reports {expected}")
    runs = []
    for record in matches:
        if not runs or record["frame"] != runs[-1]["last_frame"] + 1:
            runs.append({"first_frame": record["frame"], "last_frame": record["frame"],
                         "first_time_seconds": record["time_seconds"], "frame_count": 1})
        else:
            runs[-1]["last_frame"] = record["frame"]
            runs[-1]["frame_count"] += 1
    return {"scanned_frames": index, "scanned_duration_seconds": round(index / fps, 6),
            "wall_seconds": round(time.perf_counter() - started, 3),
            "matched_frame_count": len(matches), "matched_frames": matches, "matched_runs": runs,
            "highest_nonmatch_correlation": round(maximum_nonmatch, 6) if top_nonmatches else None,
            "top_nonmatches": [item[2] for item in sorted(top_nonmatches, reverse=True)],
            "reference_neighborhood": nearby, "template_mean_gray": round(template_mean, 6),
            "template_contrast_stddev": round(template_std, 6),
            "scope": "first_n_seconds" if args.firstNseconds is not None else "all_decoded_frames"}


def main() -> int:
    args = parse_args()
    try:
        for path in (args.ReferenceVideo, args.InputVideo, args.FFmpeg):
            if not path.is_file():
                raise FileNotFoundError(str(path))
        if args.OutputReport.resolve() in (args.ReferenceVideo.resolve(), args.InputVideo.resolve()):
            raise ValueError("OutputReport must not overwrite either input video")
        ffprobe = args.FFmpeg.with_name("ffprobe.exe" if args.FFmpeg.suffix.lower() == ".exe" else "ffprobe")
        reference_metadata = probe(ffprobe, args.ReferenceVideo)
        input_metadata = probe(ffprobe, args.InputVideo)
        reference_dimensions = (reference_metadata["width"], reference_metadata["height"])
        if reference_dimensions != (input_metadata["width"], input_metadata["height"]):
            raise ValueError("Reference and input dimensions differ; automatic ROI relocation is intentionally unsupported")
        x, y, w, h = args.ROI
        if x + w > reference_dimensions[0] or y + h > reference_dimensions[1]:
            raise ValueError("ROI lies outside the video frame")
        if tuple(args.ROI) == DEFAULT_ROI and reference_dimensions != (3120, 2080):
            raise ValueError("Default ROI is calibrated for 3120x2080; specify --ROI for another resolution")
        width, height = max(2, w // 2), max(2, h // 2)
        template = extract_template(args, width, height)
        result = scan(args, input_metadata, template, width, height)
        report = {"schema_version": 1, "created_utc": datetime.now(timezone.utc).isoformat(),
                  "reference_video": reference_metadata, "input_video": input_metadata,
                  "template": {"reference_frame": args.ReferenceFrame,
                               "reference_time_seconds": args.ReferenceFrame / reference_metadata["fps"],
                               "roi_xywh": list(args.ROI), "analysis_dimensions": [width, height],
                               "sha256": hashlib.sha256(template).hexdigest(),
                               "source": "runtime grayscale extraction from the supplied local reference video"},
                  "thresholds": {"minimum_normalized_correlation": args.correlation_threshold,
                                 "maximum_mean_absolute_error": args.mae_threshold,
                                 "minimum_contrast_stddev": args.contrast_threshold},
                  "first_n_seconds_requested": args.firstNseconds,
                  "method": "fixed-ROI grayscale template; all three thresholds must pass; CPU FFmpeg; no frame skipping",
                  "limitations": ["Detects the reference MENU appearance at the configured fixed ROI.",
                                  "A moved, redesigned, faint, or substantially rescaled control can be missed.",
                                  "Pixel results do not establish component state or prove an unscanned interval is clean."],
                  **result}
        args.OutputReport.parent.mkdir(parents=True, exist_ok=True)
        args.OutputReport.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"report": str(args.OutputReport.resolve()), "scanned_frames": result["scanned_frames"],
                          "matched_frame_count": result["matched_frame_count"],
                          "matched_frame_indices": [record["frame"] for record in result["matched_frames"]],
                          "wall_seconds": result["wall_seconds"]}, ensure_ascii=False))
        return 0
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"MENU pixel check failed: {error}", file=sys.stderr)
        if isinstance(error, subprocess.CalledProcessError) and error.stderr:
            print(error.stderr.decode(errors="replace")[-8000:], file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
