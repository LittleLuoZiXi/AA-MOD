import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

BRIDGE = Path(__file__).resolve().parents[1] / "bridge"
sys.path.insert(0, str(BRIDGE))
import enhance


class BridgeValidation(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        (root / "_internal").mkdir()
        self.job = dict(input=str(root / "input.mp4"), output=str(root / "result.mp4"),
                        tool_directory=str(root), runtime=str(root / "runtime.dll"), series=40,
                        gpu="NVIDIA GeForce RTX 4070 SUPER", scale=2, multiplier=2, neural=False)
        Path(self.job["input"]).touch()
        Path(self.job["runtime"]).write_bytes(b"fixture")
        for file in ("ffmpeg.EXE", "vsr_host.dll", "nvngx_vsr.dll", "dlssg_video_worker.exe", "nvngx_dlssg.dll"):
            (root / "_internal" / file).touch()

    def validate(self):
        return enhance.validate_job(self.job, check_dependencies=False)

    def test_missing_series_rejected_before_any_gpu_import(self):
        for series in (0, -1, 20, 60):
            self.job["series"] = series
            with self.assertRaisesRegex(ValueError, "暂不支持"):
                self.validate()
        self.assertNotIn("dlss5tool.dlss_engine", sys.modules)

    def test_boolean_is_not_multiplier(self):
        self.job["multiplier"] = True
        with self.assertRaises(ValueError): self.validate()

    def test_existing_output_never_overwritten(self):
        Path(self.job["output"]).write_bytes(b"keep")
        with self.assertRaisesRegex(ValueError, "禁止覆盖"): self.validate()
        self.assertEqual(Path(self.job["output"]).read_bytes(), b"keep")

    def test_wrong_generation_runtime_rejected(self):
        with self.assertRaisesRegex(ValueError, "SHA-256"): self.validate()

    def test_happy_contract_without_gpu(self):
        with patch.dict(enhance.PROFILES["40"], sha256=enhance.hashlib.sha256(b"fixture").hexdigest()):
            self.assertEqual(self.validate()["scale"], 2)

    def test_missing_component_rejected(self):
        with patch.dict(enhance.PROFILES["40"], sha256=enhance.hashlib.sha256(b"fixture").hexdigest()):
            (Path(self.job["tool_directory"]) / "_internal" / "vsr_host.dll").unlink()
            with self.assertRaises(FileNotFoundError): self.validate()

    def test_source_and_output_must_differ(self):
        self.job["output"] = self.job["input"]
        with self.assertRaisesRegex(ValueError, "禁止覆盖"): self.validate()

    def test_checked_profiles(self):
        self.assertFalse(enhance.PROFILES["20"]["supported"])
        for series in ("30", "40", "50"):
            self.assertTrue(enhance.PROFILES[series]["supported"])
            self.assertEqual(len(enhance.PROFILES[series]["sha256"]), 64)


if __name__ == "__main__": unittest.main()
