param(
    [Parameter(Mandatory=$true)][string]$InputPath,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [Parameter(Mandatory=$true)][ValidateSet('installer','uninstaller')][string]$Role
)
$ErrorActionPreference='Stop'
$InputPath=[IO.Path]::GetFullPath($InputPath)
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
$build=Join-Path $PSScriptRoot "protected-build/$Role"
$obfuscated=Join-Path $build 'obfuscated'
$null=New-Item -ItemType Directory -Force -Path $obfuscated
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$configPath=Join-Path $build 'obfuscar.generated.xml'
$xml=[xml]@'
<Obfuscator>
  <Var name="InPath" value="" />
  <Var name="OutPath" value="" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="HideStrings" value="true" />
  <Var name="RenameProperties" value="false" />
  <Var name="RegenerateDebugInfo" value="false" />
  <AssemblySearchPath path="" />
  <Module file="">
    <SkipType name="OwnedFile" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="Receipt" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="DependencyIssue" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipMethod type="Program" name="MoveFileEx" />
  </Module>
</Obfuscator>
'@
($xml.Obfuscator.Var|Where-Object name -eq 'InPath').value=Split-Path $InputPath -Parent
($xml.Obfuscator.Var|Where-Object name -eq 'OutPath').value=$obfuscated
$xml.Obfuscator.AssemblySearchPath.path=$framework
$xml.Obfuscator.Module.file=$InputPath
$xml.Save($configPath)
& (Join-Path $PSScriptRoot 'tools/obfuscar/tools/Obfuscar.Console.exe') $configPath
if($LASTEXITCODE -ne 0){throw "$Role core obfuscation failed"}
$core=Join-Path $obfuscated ([IO.Path]::GetFileName($InputPath))
if(!(Test-Path -LiteralPath $core)){throw "$Role obfuscation did not produce its core"}
$coreHash=(Get-FileHash -LiteralPath $core).Hash
$encrypted=Join-Path $build 'encrypted-core.bin'
$aes=[Security.Cryptography.Aes]::Create();$aes.GenerateKey();$aes.GenerateIV()
$key=[Convert]::ToBase64String($aes.Key);$iv=[Convert]::ToBase64String($aes.IV)
$plainStream=$null;$cipherStream=$null;$crypto=$null;$gzip=$null
try {
    $plainStream=[IO.File]::OpenRead($core)
    $cipherStream=[IO.File]::Create($encrypted)
    $crypto=[Security.Cryptography.CryptoStream]::new($cipherStream,$aes.CreateEncryptor(),[Security.Cryptography.CryptoStreamMode]::Write)
    $gzip=[IO.Compression.GZipStream]::new($crypto,[IO.Compression.CompressionLevel]::Optimal,$true)
    $plainStream.CopyTo($gzip);$gzip.Dispose();$gzip=$null
    $crypto.FlushFinalBlock()
} finally {
    if($gzip){$gzip.Dispose()};if($crypto){$crypto.Dispose()};if($cipherStream){$cipherStream.Dispose()};if($plainStream){$plainStream.Dispose()};$aes.Dispose()
}
$template=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ExecutableShell.cs.in') -Raw).Replace('@@KEY@@',$key).Replace('@@IV@@',$iv).Replace('@@COREHASH@@',$coreHash)
$source=Join-Path $build 'ExecutableShell.generated.cs'
[IO.File]::WriteAllText($source,$template,[Text.UTF8Encoding]::new($true))
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /debug- /r:System.Windows.Forms.dll "/resource:$encrypted,aa.encrypted-core" "/out:$OutputPath" $source
if($LASTEXITCODE -ne 0){throw "$Role encrypted shell compilation failed"}

# Inspect metadata without invoking the entry point; no plaintext program or ZIP resource may ship in the shell.
$cecilPath=Join-Path $PSScriptRoot 'tools/obfuscar/tools/Mono.Cecil.dll'
$null=[Reflection.Assembly]::LoadFrom($cecilPath)
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($OutputPath)
try {
    $resources=@($assembly.MainModule.Resources)
    if($resources.Count -ne 1 -or $resources[0].Name -ne 'aa.encrypted-core'){throw "$Role has an unprotected or unexpected resource"}
    if($assembly.MainModule.Types | Where-Object Name -in @('InstallCore','Program','Receipt','OwnedFile')){throw "$Role exposes its plaintext installation core"}
    $resourceStream=$resources[0].GetResourceStream()
    try {
        $prefix=[byte[]]::new(4);$null=$resourceStream.Read($prefix,0,4)
        if($resourceStream.Length -lt 16 -or $resourceStream.Length%16 -ne 0 -or ($prefix[0] -eq 0x50 -and $prefix[1] -eq 0x4b) -or ($prefix[0] -eq 0x4d -and $prefix[1] -eq 0x5a)) {throw "$Role encrypted resource validation failed"}
    } finally {$resourceStream.Dispose()}
} finally {$assembly.Dispose()}
$proof=[ordered]@{
    Role=$Role
    Protection='Obfuscar private names/strings + AES-256-CBC-PKCS7 + GZip + memory Assembly.Load'
    OutputPath=$OutputPath
    OutputSha256=(Get-FileHash -LiteralPath $OutputPath).Hash
    CoreSha256=$coreHash
    Resources=@('aa.encrypted-core')
    PlaintextInstallerPayloadInShell=$false
    DecryptedCoreWrittenToDisk=$false
    BuiltUtc=[DateTime]::UtcNow.ToString('o')
}
[IO.File]::WriteAllText((Join-Path $build 'protection-proof.json'),($proof|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
Write-Host "$Role protection verified: $($proof.OutputSha256)"
