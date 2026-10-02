"""Frozen entry point; diagnostic --check validates files without GPU work."""
import multiprocessing
import os
from pathlib import Path
import sys

def require_host():
    import ctypes
    from ctypes import wintypes
    pid = int(os.environ.get("AA_RECORDER_HOST_PID", "0"))
    expected = Path(os.environ.get("AA_RECORDER_HOST_EXE", "missing")).resolve()
    if not pid or expected.name.lower() != "azurearchive.exe":
        raise RuntimeError("请在 AzureArchive 的内录面板中启动增强。")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE,wintypes.DWORD,wintypes.LPWSTR,ctypes.POINTER(wintypes.DWORD)]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle: raise RuntimeError("AzureArchive 主进程未运行。")
    try:
        value = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(len(value))
        if not kernel.QueryFullProcessImageNameW(handle,0,value,ctypes.byref(size)) or Path(value.value).resolve() != expected:
            raise RuntimeError("运行环境校验失败。")
        if expected.parent not in Path(sys.executable).resolve().parents:
            raise RuntimeError("增强组件必须安装于 AzureArchive 目录。")
    finally: kernel.CloseHandle(handle)

if __name__ == "__main__":
    # Import the path redirection before spawn unpickles an upstream worker.
    # This only reads configuration; it does not load/initialize a GPU backend.
    from enhance import main
    # Worker children must divert before processing the recorder command line.
    multiprocessing.freeze_support()
    try:
        if "--check" not in sys.argv[1:]: require_host()
        raise SystemExit(main())
    except Exception as error:
        print(str(error),file=sys.stderr)
        raise SystemExit(2)
