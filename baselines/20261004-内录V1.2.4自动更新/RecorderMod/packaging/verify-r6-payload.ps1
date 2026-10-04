param([Parameter(Mandatory=$true)][string]$BuildProof)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'build-r6-support.ps1')
$proof=Get-Content -LiteralPath $BuildProof -Raw|ConvertFrom-Json
if($proof.Product -ne 'AzureArchiveRecorder' -or $proof.Version -notin @('0.2.1','1.0.0','1.1.0','1.2.0','1.2.1','1.2.2','1.2.3','1.2.4')){throw 'Unexpected product/version in build proof.'}
$artifacts=@(@('Installer','InstallerSha256'),@('Uninstaller','UninstallerSha256'),@('Plugin','PluginSha256'))
$requiresUpdater=[Version]$proof.Version -ge [Version]'1.2.4'
if($requiresUpdater){
    if(!$proof.PSObject.Properties['Updater'] -or !$proof.PSObject.Properties['UpdaterSha256'] -or [String]::IsNullOrWhiteSpace($proof.Updater) -or $proof.UpdaterSha256 -notmatch '^[0-9a-fA-F]{64}$'){throw 'Updater identity/hash missing from build proof.'}
    $artifacts+=,@('Updater','UpdaterSha256')
}
foreach($item in $artifacts){
    if((Get-FileHash -LiteralPath $proof.($item[0])).Hash -ne $proof.($item[1])){throw "Artifact changed after its build verification: $($item[0])"}
}
$archive=[IO.Compression.ZipFile]::OpenRead($proof.Payload)
try{
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $archive.Entries){
        if(!$seen.Add($entry.FullName)){throw "Duplicate ZIP entry: $($entry.FullName)"}
        if($entry.FullName.Contains(':') -or [IO.Path]::IsPathRooted($entry.FullName) -or @($entry.FullName.Split('/')|Where-Object{$_ -in @('.','..')}).Count){throw "Unsafe ZIP entry: $($entry.FullName)"}
    }
    $manifestEntry=$archive.GetEntry('payload-manifest.json')
    if(!$manifestEntry){throw 'Payload manifest is missing.'}
    $reader=[IO.StreamReader]::new($manifestEntry.Open())
    try{$receipt=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
    if($receipt.Product -ne $proof.Product -or $receipt.Version -ne $proof.Version){throw 'Receipt product/version mismatch.'}
    if($archive.Entries.Count -ne $receipt.Files.Count+1){throw 'ZIP includes files absent from its exact receipt.'}
    $receiptSeen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $checked=@(foreach($file in $receipt.Files){
        if(!$receiptSeen.Add($file.Path) -or !$file.Path.StartsWith('mods/AzureArchiveRecorder/')){throw "Unexpected receipt path: $($file.Path)"}
        $entry=$archive.GetEntry($file.Path)
        if(!$entry){throw "Receipt file absent from ZIP: $($file.Path)"}
        $stream=$entry.Open()
        try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if($hash -ne $file.Sha256){throw "ZIP SHA-256 mismatch: $($file.Path)"}
        if((Get-FileHash -LiteralPath (Join-Path $proof.Stage $file.Path)).Hash -ne $hash){throw "Stage changed since ZIP creation: $($file.Path)"}
        [ordered]@{Path=$file.Path;Bytes=$entry.Length;Sha256=$hash}
    })
    if(!$receiptSeen.Contains('mods/AzureArchiveRecorder/卸载内录MOD.exe')){throw 'Dedicated uninstaller missing from receipt.'}
    if($requiresUpdater){
        $updaterRelative='mods/AzureArchiveRecorder/'+$proof.Version+'/更新内录MOD.exe'
        $updaterFiles=@($checked|Where-Object Path -eq $updaterRelative)
        if($updaterFiles.Count -ne 1 -or $updaterFiles[0].Bytes -le 0 -or $updaterFiles[0].Sha256 -ne $proof.UpdaterSha256){throw 'Dedicated updater missing from receipt or differs from the verified artifact.'}
        $expectedUpdater=[IO.Path]::GetFullPath((Join-Path $proof.Stage $updaterRelative))
        if(![String]::Equals([IO.Path]::GetFullPath($proof.Updater),$expectedUpdater,[StringComparison]::OrdinalIgnoreCase)){throw 'Updater artifact is outside its exact staged version path.'}
    }
    $pdbPresent=$null
    if($proof.Variant -like 'development-*'){
        $tutorial=@(Assert-TutorialResources $proof.Plugin (Split-Path $PSScriptRoot -Parent))
        $assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($proof.Plugin)
        try{$pdbPresent=@($assembly.MainModule.GetDebugHeader().Entries|Where-Object{$_.Directory.Type.ToString() -eq 'EmbeddedPortablePdb'}).Count -eq 1}finally{$assembly.Dispose()}
        if(!$pdbPresent){throw 'Development plugin lacks its promised embedded portable PDB.'}
    }else{$tutorial=@($proof.TutorialResources)}
    if($tutorial.Count -ne 6){throw 'Tutorial image count is not six.'}
    $noDlssNative=@($checked | Where-Object {$_.Path -match '(?i)(?:^|/)(?:nvngx[^/]*\.(?:dll|exe)|vsr_host\.dll|dlssg_video_worker\.exe|dlssnr_host[^/]*\.dll)$'}).Count -eq 0
    if($proof.Version -in @('1.0.0','1.1.0','1.2.0','1.2.1','1.2.2','1.2.3','1.2.4') -and !$noDlssNative){throw 'Online installers must not embed DLSS native components.'}
    $result=[ordered]@{Passed=$true;Variant=$proof.Variant;Version=$proof.Version;PayloadSha256=(Get-FileHash -LiteralPath $proof.Payload).Hash;ZipEntries=$archive.Entries.Count;VerifiedReceiptFiles=$checked.Count;DedicatedUninstaller=$true;DedicatedUpdater=$requiresUpdater;UpdaterSha256=$(if($requiresUpdater){$proof.UpdaterSha256}else{$null});NoBundledDlssNative=$noDlssNative;EmbeddedPortablePdb=$pdbPresent;TutorialResources=$tutorial;Files=$checked;NoInstallerEntryPointInvoked=$true}
}finally{$archive.Dispose()}
$output=Join-Path (Split-Path $BuildProof -Parent) 'payload-verification.json'
[IO.File]::WriteAllText($output,($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{Passed=$true;Variant=$proof.Variant;Files=$checked.Count;Report=$output}
