$ErrorActionPreference = 'Stop'
# Compile the production COM definitions. The only test seam closes our own
# dialog with Cancel after Show enters its modal pump; no other window is used.
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/FolderPicker.cs') -Raw
$source = $source.Replace('internal static', 'public static')
$probe = @'
dialog.GetOptions(out var options);
if ((options & 0x60) != 0x60) throw new Exception("Folder/filesystem flags missing.");
dialog.GetFolder(out var selectedFolder);
try {
    selectedFolder.GetDisplayName(0x80058000, out var chosenPath);
    try {
        var actual = Marshal.PtrToStringUni(chosenPath);
        if (!string.Equals(actual, initial, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Initial folder did not round-trip: " + actual);
    } finally { Marshal.FreeCoTaskMem(chosenPath); }
} finally { Marshal.ReleaseComObject(selectedFolder); }
var closeTask = Task.Run(() => { Thread.Sleep(350); dialog.Close(unchecked((int)0x800704C7)); });
int hr=dialog.Show(owner);
closeTask.GetAwaiter().GetResult();
'@
if (!$source.Contains('int hr=dialog.Show(owner);')) { throw 'Folder picker test seam changed.' }
$source = $source.Replace('int hr=dialog.Show(owner);', $probe)
Add-Type -TypeDefinition ("#nullable enable`n" + $source)
$expected = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyVideos)
if ([AzureArchive.Recorder.FolderPicker]::Videos -ne $expected) { throw 'Windows Videos path mismatch.' }
$folder = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../test-output/本次内录 videos'))
$null = New-Item -ItemType Directory -Path $folder -Force
$result = [AzureArchive.Recorder.FolderPicker]::ChooseAsync($folder).GetAwaiter().GetResult()
if ($null -ne $result) { throw 'Cancel must not change the destination.' }
Write-Host "PASS: Windows Videos = $expected; Unicode folder COM round-trip; real Show/Cancel returned null."
