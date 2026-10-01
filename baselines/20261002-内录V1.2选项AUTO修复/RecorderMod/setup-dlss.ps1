param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$environment = Join-Path $PSScriptRoot '.venv'
$executable = Join-Path $environment 'Scripts/python.exe'
if (!(Test-Path -LiteralPath $executable)) {
    & $Python -m venv $environment
    if ($LASTEXITCODE -ne 0) { throw '需要 Python 3.11 或更新版本创建独立环境。' }
}
& $executable -m pip --version 2>$null
if ($LASTEXITCODE -ne 0) {
    & $executable -m ensurepip --upgrade --default-pip
    if ($LASTEXITCODE -ne 0) { throw '无法初始化 pip，请检查临时目录写入权限。' }
}
& $executable -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot 'bridge/requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'DLSS 桥接依赖安装失败。' }
Write-Host '桥接环境就绪。此脚本没有运行任何 NVIDIA/GPU 测试。'
