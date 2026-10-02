param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
$game=Split-Path $mod -Parent
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path (Split-Path $game -Parent) '安装交付/20260929'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
$build=Join-Path $output ('构建记录/enhancer-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
if(Test-Path -LiteralPath $build){throw 'Fresh enhancer build directory required.'}
$null=New-Item -ItemType Directory -Force -Path $build
$contract=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'enhancer-runtime-modules.json') -Raw|ConvertFrom-Json
$python=Join-Path $mod '.venv/Scripts/python.exe'
$arguments=@('-m','PyInstaller','--noconfirm','--clean','--onedir','--console','--noupx','--name','EnhanceHost',
    '--distpath',(Join-Path $build 'frozen'),'--workpath',(Join-Path $build 'pybuild'),'--specpath',$build,
    '--paths',(Join-Path $mod 'bridge'),'--paths',(Join-Path $mod 'bridge/vendor'),
    '--add-data',((Join-Path $mod 'bridge/gpu_profiles.json')+';.'))
foreach($module in $contract.required){$arguments+=@('--hidden-import',$module)}
foreach($module in $contract.excluded){$arguments+=@('--exclude-module',$module)}
$arguments+=(Join-Path $PSScriptRoot 'enhance_entry.py')
$oldConfig=$env:PYINSTALLER_CONFIG_DIR;$oldPythonPath=$env:PYTHONPATH
try{
    $env:PYINSTALLER_CONFIG_DIR=Join-Path $build 'pycache'
    $env:PYTHONPATH=(Join-Path $mod 'bridge/vendor')+[IO.Path]::PathSeparator+(Join-Path $mod 'bridge')
    if($oldPythonPath){$env:PYTHONPATH+=[IO.Path]::PathSeparator+$oldPythonPath}
    & $python @arguments *> (Join-Path $build 'freeze.log')
    if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $build 'freeze.log') -Tail 35;throw 'Enhancer build failed.'}
}finally{$env:PYINSTALLER_CONFIG_DIR=$oldConfig;$env:PYTHONPATH=$oldPythonPath}
$runtime=Join-Path $build 'frozen/EnhanceHost'
$verification=Join-Path $build 'frozen-runtime-source-check.json'
& $python (Join-Path $PSScriptRoot 'verify-frozen-runtime.py') --runtime $runtime --output $verification
if($LASTEXITCODE -ne 0){throw 'New frozen runtime did not match its current source contract.'}
$proof=[ordered]@{RuntimeDirectory=$runtime;BuildDirectory=$build;Verification=$verification;NoGpuInferenceExecuted=$true;BuiltUtc=[DateTime]::UtcNow.ToString('o');Files=@(Get-ChildItem -LiteralPath $runtime -File -Recurse|ForEach-Object{[ordered]@{Path=[IO.Path]::GetRelativePath($runtime,$_.FullName).Replace('\','/');Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})}
[IO.File]::WriteAllText((Join-Path $build 'runtime-build.json'),($proof|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
Write-Host "EnhanceHost complete; no GPU inference executed. Pass -FrozenRuntimeDirectory '$runtime' to both R6 installer builds."
[pscustomobject]@{RuntimeDirectory=$runtime;BuildDirectory=$build;Verification=$verification}
