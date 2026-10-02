"""Regression for the Windows sharing violation observed during real RTX VSR."""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "bridge"))
import enhance


@unittest.skipUnless(os.name == "nt", "requires real Windows file-sharing semantics")
class WindowsPublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "progress.json"
        self.path.write_text('{"fraction":0}', encoding="utf-8")
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
            ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        self.kernel.CreateFileW.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]

    def read_lock(self):
        handle = self.kernel.CreateFileW(str(self.path), 0x80000000, 3, None, 3, 0x80, None)
        if handle == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        return handle

    def test_progress_reader_cannot_abort_inference_or_corrupt_json(self):
        handle = self.read_lock()
        try:
            started = time.monotonic()
            self.assertFalse(enhance.publish(self.path, {"fraction": .5}, required=False))
            self.assertLess(time.monotonic() - started, 1)
            self.assertEqual(json.loads(self.path.read_text()), {"fraction": 0})
        finally:
            self.kernel.CloseHandle(handle)
        self.assertTrue(enhance.publish(self.path, {"fraction": 1}, required=False))
        self.assertEqual(json.loads(self.path.read_text()), {"fraction": 1})
        self.assertFalse(list(self.path.parent.glob("*.tmp")))

    def test_required_result_retries_a_transient_reader(self):
        handle = self.read_lock()
        release = threading.Timer(.15, lambda: self.kernel.CloseHandle(handle))
        release.start()
        try:
            self.assertTrue(enhance.publish(self.path, {"ok": True}))
            self.assertEqual(json.loads(self.path.read_text()), {"ok": True})
        finally:
            release.join()
        self.assertFalse(list(self.path.parent.glob("*.tmp")))

    def test_required_result_is_not_silently_dropped(self):
        handle = self.read_lock()
        try:
            with self.assertRaises(PermissionError):
                enhance.publish(self.path, {"ok": True})
            self.assertEqual(json.loads(self.path.read_text()), {"fraction": 0})
        finally:
            self.kernel.CloseHandle(handle)
        self.assertFalse(list(self.path.parent.glob("*.tmp")))


if __name__ == "__main__":
    unittest.main()
