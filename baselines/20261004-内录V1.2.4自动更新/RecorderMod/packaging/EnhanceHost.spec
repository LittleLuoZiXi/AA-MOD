# -*- mode: python ; coding: utf-8 -*-
from PyInstaller.utils.hooks import collect_submodules

hiddenimports = []
hiddenimports += collect_submodules('dlss5tool')


a = Analysis(
    ['C:/Users/49024/Desktop/AA测试/RTX开发测试/AA/RecorderMod/packaging/enhance_entry.py'],
    pathex=['C:/Users/49024/Desktop/AA测试/RTX开发测试/AA/RecorderMod/bridge', 'C:/Users/49024/Desktop/AA测试/RTX开发测试/AA/RecorderMod/bridge/vendor'],
    binaries=[],
    datas=[('C:/Users/49024/Desktop/AA测试/RTX开发测试/AA/RecorderMod/bridge/gpu_profiles.json', '.')],
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=['dlss5tool.app', 'tkinter', 'matplotlib', 'scipy'],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='EnhanceHost',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='EnhanceHost',
)
