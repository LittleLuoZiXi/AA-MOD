#requires -Version 7.0
param(
    [string]$ToolsDirectory = (Join-Path $PSScriptRoot 'tools'),
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Build dependency only; never include this directory or the NuGet package in an installer.
# Version/hash are pinned to the recorder's verified dependency and official NuGet package.
$version = '2.2.50'
$expectedHash = '8790E1E36D613613311EC512129A6701C63576428692F20A95988A351FDC3187'
$packageUrl = 'https://api.nuget.org/v3-flatcontainer/obfuscar/2.2.50/obfuscar.2.2.50.nupkg'
$toolsRoot = [IO.Path]::GetFullPath($ToolsDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$packagePath = Join-Path $toolsRoot 'obfuscar.2.2.50.nupkg'
$destination = Join-Path $toolsRoot 'obfuscar'
$stage = $null
$backup = $null
$committed = $false

function Assert-NoLinks([string]$Path) {
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        try { $attributes = [IO.File]::GetAttributes($cursor) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Tool paths must not contain symbolic links or directory junctions.' }
    }
}
function Assert-WithinTools([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (!$absolute.StartsWith($toolsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Tool restore path escapes its dedicated directory.'
    }
    Assert-NoLinks $absolute
}
function Assert-Package([string]$Path) {
    Assert-NoLinks $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $expectedHash) {
        throw 'Obfuscar package SHA-256 differs from the pinned 2.2.50 package; existing tools were not replaced.'
    }
}
function Get-PackageFiles([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $files = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $expanded = 0L
        if ($zip.Entries.Count -gt 1000) { throw 'Unexpected package entry count.' }
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if (!$names.Add($name) -or $name.Contains('\') -or $name.Contains(':') -or $name.StartsWith('/') -or
                @(($name.TrimEnd('/') -split '/') | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -eq '' }).Count -gt 0 -or
                (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unsafe package entry.' }
            $expanded += $entry.Length
            if ($expanded -gt 64MB) { throw 'Package expanded size exceeds the build-tool limit.' }
            if ($name.EndsWith('/')) { continue }
            $input = $entry.Open(); $hash = [Security.Cryptography.SHA256]::Create()
            try { $files.Add($name, [Convert]::ToHexString($hash.ComputeHash($input))) }
            finally { $hash.Dispose(); $input.Dispose() }
        }
        if (!$files.ContainsKey('tools/Obfuscar.Console.exe') -or !$files.ContainsKey('tools/Mono.Cecil.dll')) { throw 'Package is missing required tools.' }
        return ,$files
    } finally { $zip.Dispose() }
}
function Test-Installed([string]$Root, $Files) {
    if (![IO.Directory]::Exists($Root)) { return $false }
    Assert-WithinTools $Root
    # Compare every extracted package file, not only the executable version.
    $seen = 0
    foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Existing tools contain a link; they were not changed.' }
        if ($item.PSIsContainer) { continue }
        $relative = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/')
        if (!$Files.ContainsKey($relative) -or (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -cne $Files[$relative]) { return $false }
        $seen++
    }
    if ($seen -ne $Files.Count) { return $false }
    return [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Root 'tools/Obfuscar.Console.exe')).Version.ToString() -eq '2.2.50.0'
}
function Receive-Package([string]$Path) {
    $handler = [Net.Http.SocketsHttpHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.AutomaticDecompression = [Net.DecompressionMethods]::None
    $client = [Net.Http.HttpClient]::new($handler, $true)
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(60))
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $packageUrl)
    $request.Headers.UserAgent.ParseAdd('AzureArchiveRevisionCompare-BuildTools')
    $response = $null; $input = $null; $output = $null
    try {
        $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK -or $response.Content.Headers.ContentEncoding.Count -ne 0 -or
            $response.Content.Headers.ContentLength -gt 8MB) { throw 'Official NuGet returned an unsupported response.' }
        $input = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
        $output = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $block = [byte[]]::new(65536); $received = 0L
        while (($count = $input.ReadAsync($block, 0, $block.Length, $timeout.Token).GetAwaiter().GetResult()) -gt 0) {
            $received += $count
            if ($received -gt 8MB) { throw 'Official NuGet package exceeds the build-tool download limit.' }
            $output.Write($block, 0, $count)
        }
        $output.Flush($true)
    } finally {
        if ($output) { $output.Dispose() }; if ($input) { $input.Dispose() }; if ($response) { $response.Dispose() }
        $request.Dispose(); $timeout.Dispose(); $client.Dispose()
    }
}

try {
    Assert-NoLinks $toolsRoot
    if ([IO.File]::Exists($destination)) { throw 'The tool destination is a file; it was not changed.' }
    $cached = [IO.File]::Exists($packagePath)
    if ($cached) {
        Assert-Package $packagePath
        $files = Get-PackageFiles $packagePath
        if (Test-Installed $destination $files) {
            [pscustomobject]@{ Tool='Obfuscar'; Version=$version; ToolPath=(Join-Path $destination 'tools/Obfuscar.Console.exe'); Changed=$false; Downloaded=$false; PackageSha256=$expectedHash; VerifiedFiles=$files.Count }
            return
        }
    } elseif ($Offline) { throw 'The pinned NuGet package is not cached. Offline restore left existing tools unchanged.' }

    [IO.Directory]::CreateDirectory($toolsRoot) | Out-Null
    Assert-NoLinks $toolsRoot
    $stage = Join-Path $toolsRoot ('.obfuscar-restore-' + [Guid]::NewGuid().ToString('N'))
    Assert-WithinTools $stage
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    $inputPackage = $packagePath
    if (!$cached) {
        $inputPackage = Join-Path $stage 'obfuscar.2.2.50.nupkg'
        Receive-Package $inputPackage
        Assert-Package $inputPackage
        $files = Get-PackageFiles $inputPackage
        # A correctly installed tool can be recognized after restoring only its missing cache.
        if (Test-Installed $destination $files) {
            Assert-WithinTools $packagePath
            [IO.File]::Move($inputPackage, $packagePath, $false)
            [pscustomobject]@{ Tool='Obfuscar'; Version=$version; ToolPath=(Join-Path $destination 'tools/Obfuscar.Console.exe'); Changed=$false; Downloaded=$true; PackageSha256=$expectedHash; VerifiedFiles=$files.Count }
            return
        }
    }
    $extracted = Join-Path $stage 'extracted'
    [IO.Compression.ZipFile]::ExtractToDirectory($inputPackage, $extracted)
    if (!(Test-Installed $extracted $files)) { throw 'Extracted tool verification failed; existing tools were not replaced.' }
    if (!$cached) {
        Assert-WithinTools $packagePath
        [IO.File]::Move($inputPackage, $packagePath, $false)
    }

    # Publish only a completely validated tree. Keep any prior tree as a recoverable backup.
    Assert-WithinTools $destination
    if ([IO.Directory]::Exists($destination)) {
        $backup = Join-Path $toolsRoot ('obfuscar.previous-' + [Guid]::NewGuid().ToString('N'))
        Assert-WithinTools $backup
        [IO.Directory]::Move($destination, $backup)
    }
    try { [IO.Directory]::Move($extracted, $destination); $committed = $true }
    catch {
        if ($backup -and ![IO.Directory]::Exists($destination)) { [IO.Directory]::Move($backup, $destination); $backup = $null }
        throw
    }
    [pscustomobject]@{ Tool='Obfuscar'; Version=$version; ToolPath=(Join-Path $destination 'tools/Obfuscar.Console.exe'); Changed=$true; Downloaded=(!$cached); PackageSha256=$expectedHash; VerifiedFiles=$files.Count; PreviousTools=$backup }
} finally {
    if ($stage -and [IO.Directory]::Exists($stage)) {
        try {
            # Delete only this invocation's validated GUID staging directory, never the installed tree.
            Assert-WithinTools $stage
            if ([IO.Path]::GetFileName($stage) -notmatch '^\.obfuscar-restore-[a-f0-9]{32}$') { throw 'Unexpected staging cleanup target.' }
            foreach ($item in Get-ChildItem -LiteralPath $stage -Recurse -Force) {
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Staging contains a link; cleanup stopped.' }
            }
            Remove-Item -LiteralPath $stage -Recurse -Force
        } catch {
            # A housekeeping failure must not turn a completed atomic publication into a
            # reported restore failure, or hide the original pre-publication error.
            Write-Warning ('Restore staging was retained for inspection. ToolsCommitted={0}; Staging={1}' -f $committed, $stage)
        }
    }
}
