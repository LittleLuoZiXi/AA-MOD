$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$env:PYINSTALLER_CONFIG_DIR=Join-Path $PSScriptRoot 'pycache'
& (Join-Path $mod '.venv/Scripts/python.exe') -m PyInstaller --noconfirm --clean --onedir --console --name EnhanceHost --distpath (Join-Path $PSScriptRoot 'frozen') --workpath (Join-Path $PSScriptRoot 'pybuild') --specpath $PSScriptRoot --paths (Join-Path $mod 'bridge') --paths (Join-Path $mod 'bridge/vendor') --add-data ((Join-Path $mod 'bridge/gpu_profiles.json')+';.') --collect-submodules dlss5tool --exclude-module dlss5tool.app --exclude-module tkinter --exclude-module matplotlib --exclude-module scipy (Join-Path $PSScriptRoot 'enhance_entry.py') *> (Join-Path $PSScriptRoot 'freeze.log')
if($LASTEXITCODE -ne 0){Get-Content (Join-Path $PSScriptRoot 'freeze.log') -Tail 25;throw 'Enhancer build failed'}
Write-Host 'EnhanceHost build complete; no GPU inference executed.'
