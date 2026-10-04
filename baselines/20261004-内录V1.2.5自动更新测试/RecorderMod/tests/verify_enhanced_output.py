"""Validate real enhanced output against its input; retain machine-readable evidence."""
import argparse
from fractions import Fraction
import hashlib
import json
from pathlib import Path
import subprocess

import cv2
import numpy as np


def probe(executable, path):
    return json.loads(subprocess.check_output([str(executable), "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)]))


def audio_packets(executable, path):
    raw = subprocess.check_output([str(executable), "-v", "error", "-select_streams", "a:0", "-show_packets",
                                   "-show_data_hash", "sha256", "-show_entries", "packet=data_hash", "-of", "json", str(path)])
    return [p["data_hash"] for p in json.loads(raw).get("packets", [])]


def audio_samples(ffmpeg, path):
    raw = subprocess.check_output([str(ffmpeg), "-v", "error", "-i", str(path), "-vn", "-ac", "1", "-ar", "16000", "-f", "f32le", "-"])
    return np.frombuffer(raw, dtype="<f4")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--ffmpeg", required=True, type=Path)
    args = parser.parse_args()
    directory = args.directory.resolve()
    job = json.loads((directory / "enhancement-job.json").read_text(encoding="utf-8-sig"))
    result = json.loads((directory / "enhancement-result.json").read_text(encoding="utf-8-sig"))
    metrics = {"ok": False, "mode": "video/audio validation; visual quality needs frame review", "checks": []}
    def check(condition, description):
        metrics["checks"].append({"check": description, "passed": bool(condition)})
        if not condition:
            raise AssertionError(description)
    try:
        check(result.get("ok") is True and result.get("upstream", {}).get("status") == "complete", "bridge and upstream report completed inference")
        ffprobe = args.ffmpeg.with_name("ffprobe.exe")
        source, output = Path(job["input"]), Path(job["output"])
        before, after = probe(ffprobe, source), probe(ffprobe, output)
        for name, value in (("input-ffprobe.json", before), ("output-ffprobe.json", after)):
            (directory / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
        iv = next(s for s in before["streams"] if s["codec_type"] == "video")
        ov = next(s for s in after["streams"] if s["codec_type"] == "video")
        ia = next(s for s in before["streams"] if s["codec_type"] == "audio")
        oa = next(s for s in after["streams"] if s["codec_type"] == "audio")
        metrics.update(width=ov["width"], height=ov["height"], fps=ov["avg_frame_rate"], frames=int(ov["nb_frames"]),
                       video_seconds=float(ov["duration"]), audio_seconds=float(oa["duration"]), video_codec=ov["codec_name"], audio_codec=oa["codec_name"])
        check((ov["width"], ov["height"]) == (iv["width"] * job["scale"], iv["height"] * job["scale"]), "dimensions match requested scale")
        check(Fraction(ov["avg_frame_rate"]) == Fraction(iv["avg_frame_rate"]) * job["multiplier"], "frame rate matches requested multiplier")
        check(int(ov["nb_frames"]) == int(iv["nb_frames"]) * job["multiplier"], "frame count includes every requested frame")
        check(abs(float(ov["duration"]) - float(iv["duration"])) < .002, "video duration preserved")
        check(abs(float(oa["duration"]) - float(ov["duration"])) < .05, "audio/video duration difference below 50 ms")
        with (directory / "full-decode.log").open("wb") as log:
            decoded = subprocess.run([str(args.ffmpeg), "-v", "error", "-xerror", "-i", str(output), "-f", "null", "-"], stdout=log, stderr=subprocess.STDOUT)
        check(decoded.returncode == 0, "full video/audio decode")
        original_audio, enhanced_audio = audio_samples(args.ffmpeg, source), audio_samples(args.ffmpeg, output)
        metrics["audio_peak_dbfs"] = float(20 * np.log10(max(1e-12, float(np.abs(enhanced_audio).max()))))
        check(metrics["audio_peak_dbfs"] > -80, "audio is not silent")
        packets_equal = audio_packets(ffprobe, source) == audio_packets(ffprobe, output)
        metrics["audio_packets_identical"] = packets_equal
        count = min(len(original_audio), len(enhanced_audio))
        x, y = original_audio[:count].astype(np.float64), enhanced_audio[:count].astype(np.float64)
        metrics["audio_zero_lag_correlation"] = float(np.dot(x, y) / max(1e-30, np.linalg.norm(x) * np.linalg.norm(y)))
        check(packets_equal or metrics["audio_zero_lag_correlation"] > .98, "audio content and timing preserved")
        cap = cv2.VideoCapture(str(output))
        samples = []
        try:
            for index in np.linspace(1, max(1, int(iv["nb_frames"]) - 3), 9, dtype=int):
                base = int(index) * job["multiplier"]
                cap.set(cv2.CAP_PROP_POS_FRAMES, base)
                valid, first = cap.read()
                if not valid:
                    continue
                if len(samples) in (1, 4, 7):
                    # Keep original output pixels for visual review, including Chinese subtitles.
                    cv2.imencode(".png", first)[1].tofile(str(directory / f"sample-{base:05d}.png"))
                item = {"output_frame": base}
                if job["multiplier"] > 1:
                    valid, middle = cap.read()
                    cap.set(cv2.CAP_PROP_POS_FRAMES, base + job["multiplier"])
                    valid_next, next_frame = cap.read()
                    if valid and valid_next:
                        item["min_intermediate_neighbor_mae"] = min(float(np.abs(middle.astype(np.float32) - frame.astype(np.float32)).mean()) for frame in (first, next_frame))
                samples.append(item)
        finally:
            cap.release()
        metrics["frame_samples"] = samples
        if job["multiplier"] > 1:
            check(result["upstream"].get("generated_frames", 0) > 0, "native pipeline reports generated frames")
            check(any(s.get("min_intermediate_neighbor_mae", 0) > .5 for s in samples), "sampled intermediate frames contain motion differences (not a visual-quality certification)")
        with output.open("rb") as stream:
            metrics["output_sha256"] = hashlib.file_digest(stream, "sha256").hexdigest()
        metrics["ok"] = True
    except Exception as error:
        metrics["error"] = str(error)
        raise
    finally:
        (directory / "video-verification.json").write_text(json.dumps(metrics, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps(metrics, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
