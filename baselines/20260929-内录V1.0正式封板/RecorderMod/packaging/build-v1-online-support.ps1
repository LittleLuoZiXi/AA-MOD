# Compiles only the online component manager/uninstaller. Native DLSS libraries
# are deliberately excluded and are obtained by the installer from fixed URLs.
function New-V1OnlineUninstaller([object]$Context,[string]$GeneratedInstaller,[string]$SharedSource,[switch]$Protected) {
    $csc=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $online=Join-Path $PSScriptRoot 'OnlineDlssInstall.cs'
    $refs=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Net.Http.dll')
    $core=Join-Path $Context.Build 'AADlss.OnlineUninstall.Core.exe'
    & $csc /nologo /target:winexe /platform:x64 /optimize+ /debug- /define:UNINSTALL /main:OnlineDlssUninstallerProgram $refs "/out:$core" $GeneratedInstaller $SharedSource $online (Join-Path $PSScriptRoot 'V1AssemblyInfo.cs') | Out-Host
    if($LASTEXITCODE -ne 0){throw 'Online DLSS uninstaller compilation failed.'}
    $output=Join-Path $Context.Build '卸载DLSS补充MOD.exe'
    if($Protected){
        & (Join-Path $PSScriptRoot 'protect-executable.ps1') -InputPath $core -OutputPath $output -Role uninstaller -BuildDirectory (Join-Path $Context.Build 'protection/dlss-uninstaller') | Out-Host
    }else{Copy-Item -LiteralPath $core -Destination $output}
    return $output
}

function Assert-V1NoBundledDlss([string]$Stage) {
    $forbidden=@(Get-ChildItem -LiteralPath $Stage -File -Recurse | Where-Object {$_.Name -match '^(?i:nvngx.*\.(dll|exe)|vsr_host\.dll|dlssg_video_worker\.exe|dlssnr_host.*\.dll)$'})
    if($forbidden.Count -gt 0){throw ('V1.0 may not contain native DLSS components: '+($forbidden.FullName -join ', '))}
}
