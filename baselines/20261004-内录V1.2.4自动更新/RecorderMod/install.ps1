$ErrorActionPreference = 'Stop'
$game = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1') -Install
$activeFile = Join-Path $game 'ActiveProfile.txt'
$name = if(Test-Path -LiteralPath $activeFile) { [IO.File]::ReadAllText($activeFile).Trim() } else { 'Recorder' }
if($name -notmatch '^[\p{L}\p{N}_ -]+$' -or $name.Contains('..')) { throw '当前 MOD 配置名称不安全，请通过 AA MOD 管理器启用。' }
$profileDir = Join-Path $game "profiles/$name"
$null = New-Item -ItemType Directory -Force -Path (Join-Path $profileDir 'configs')
$profileFile = Join-Path $profileDir 'modconfig.json'
$profile = if(Test-Path -LiteralPath $profileFile) { Get-Content -LiteralPath $profileFile -Raw | ConvertFrom-Json -AsHashtable } else { @{EnabledMods=@()} }
if($profile.EnabledMods -isnot [System.Collections.IEnumerable]) { throw 'MOD 配置格式不符，未修改。' }
$version=(Get-Content (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json).version_number
$existing=@($profile.EnabledMods | Where-Object name -eq 'AzureArchiveRecorder')
if($existing.Count -ne 1 -or $existing[0].version -ne $version) {
    if(Test-Path -LiteralPath $profileFile) { Copy-Item -LiteralPath $profileFile -Destination ($profileFile+'.recorder-backup-'+[Guid]::NewGuid().ToString('N')) }
    $profile.EnabledMods = @($profile.EnabledMods | Where-Object name -ne 'AzureArchiveRecorder') + @{name='AzureArchiveRecorder';version=$version}
    [IO.File]::WriteAllText($profileFile,($profile | ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
}
if(!(Test-Path -LiteralPath $activeFile)) { [IO.File]::WriteAllText($activeFile,$name,[Text.UTF8Encoding]::new($false)) }
Write-Host "已在配置 $name 启用 AzureArchiveRecorder。下次启动 AA 生效。"
