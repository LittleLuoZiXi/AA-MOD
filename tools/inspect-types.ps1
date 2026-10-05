param([string[]]$Names,[Parameter(Mandatory=$true)][string]$GameRoot,[switch]$PropertiesOnly)
$ErrorActionPreference='Stop'
[Reflection.Assembly]::LoadFrom((Join-Path $GameRoot 'BepInEx/core/Mono.Cecil.dll'))|Out-Null
$a=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
try {
    foreach($name in $Names) {
        $t=$a.MainModule.Types | Where-Object FullName -EQ $name
        if(!$t){"Type not found: $name";continue}
        $t.FullName
        $t.Properties | ForEach-Object ToString
        if(!$PropertiesOnly){$t.Methods | Where-Object { $_.Name -notmatch '^(get_|set_|\.cctor$)' } | ForEach-Object ToString}
    }
} finally {$a.Dispose()}
