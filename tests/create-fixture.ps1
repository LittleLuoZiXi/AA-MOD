#requires -Version 7.0
param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$TestHost)
$ErrorActionPreference = 'Stop'

# Only generate inside a host explicitly prepared for isolated MOD tests.
# This script never reads a user's project, resource directory, or settings.
$hostItem = Get-Item -LiteralPath $TestHost
if (!$hostItem.PSIsContainer) { throw 'TestHost must be an existing directory.' }
$hostRoot = [IO.Path]::GetFullPath($hostItem.FullName).TrimEnd('\', '/')
$markerPath = Join-Path $hostRoot 'RevisionTestHost.json'
if (!(Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw 'Isolated test marker missing.' }
$marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
if ($marker.kind -ne 'RevisionCompareIsolatedHost' -or [string]::IsNullOrWhiteSpace($marker.root)) {
    throw 'Invalid isolated test marker.'
}
$markedRoot = [IO.Path]::GetFullPath([string]$marker.root).TrimEnd('\', '/')
if (![string]::Equals($hostRoot, $markedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Isolated test marker root does not match TestHost.'
}

$evidence = Join-Path $hostRoot 'evidence'
if (Test-Path -LiteralPath $evidence) {
    $evidenceItem = Get-Item -LiteralPath $evidence
    if (!$evidenceItem.PSIsContainer -or ($evidenceItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Evidence must be an ordinary directory inside the isolated test host.'
    }
}
$fixturePath = Join-Path $evidence 'fixture.aap2'
if (Test-Path -LiteralPath $fixturePath) {
    throw 'Synthetic fixture already exists; this script never overwrites it.'
}

function New-SyntheticLine([string]$LineId, [string]$Text, [bool]$Dialogue = $true) {
    $characters = @(for ($slot = 0; $slot -lt 6; $slot++) {
        $isShiroko = $Dialogue -and $slot -eq 3
        [ordered]@{
            name = $(if ($isShiroko) { '시로코' } else { '' })
            faceId = '00'
            startingPos = $(if ($isShiroko) { 3 } else { 0 })
            endingPos = $(if ($isShiroko) { 3 } else { 0 })
            displayOrder = 1
            emoticon = $(if ($isShiroko) { 7 } else { -1 })
            action = 0
            effect = 0
            appear = 0
            shapeOverride = 0
        }
    })
    [ordered]@{
        LineId = $LineId
        text = $Text
        popup = ''
        bgEffect = 0
        bgName = $(if ($Dialogue) { 1046815759 } else { 0 })
        bgFriendlyName = $(if ($Dialogue) { 'BG_MainOffice' } else { '' })
        sound = ''
        voice = ''
        transition = 0
        bgmId = $(if ($Dialogue) { 37 } else { 0 })
        selectionGroup = 0
        additionalPrompt = ''
        characters = $characters
        speakerSlotNum = $(if ($Dialogue) { 3 } else { 0 })
        highlightedSlotNums = @()
        isDialogScript = $Dialogue
        placeText = ''
    }
}

$first = New-SyntheticLine '8d217912-569b-4bed-9afb-48a12a6cd013' '这是上次播放的版本。'
$second = New-SyntheticLine 'b9d5c8cd-0572-4263-bc5c-b220b763f746' '这一条应当保持不变。'
$exitPlaceholder = New-SyntheticLine 'd74b0afc-7feb-4ceb-a20d-5e5ee3fc875d' '' $false
# Preserve the native synthetic template's unused exit placeholder identity.
# IsEnding is true, so this empty non-dialogue record is not a third spoken line.
$exitPlaceholder.voice = 'fb5d01d5-87a6-4567-8d9d-e7547083c628'
$project = [ordered]@{
    LegacySourceVersion = 'absent'
    FormatVersion = 2
    ProjectId = '2da56373-c016-4229-8d03-5df62285a884'
    ProjectName = '改稿对比隔离测试'
    PreviewBgName = 1046815759
    PreviewHeader = '独立测试工程'
    PreviewTitle = '改稿对比'
    nodes = @(
        [ordered]@{
            Title = 'test'; Header = '111'
            Guid = '00000000-0000-0000-0000-000000000000'
            ConnectionsTo = @('8233e27a-3b7b-494f-a2b5-4d2105e0a9d4')
            X = -41.6249962; Y = 60.1250076; Kind = 'entry'
        }
        [ordered]@{
            Scripts = @($first, $second); NodeName = $null
            Guid = '8233e27a-3b7b-494f-a2b5-4d2105e0a9d4'
            ConnectionsTo = @('991ceb64-28f8-4bc7-892f-d8e68128bb1c')
            X = -95.8053; Y = -199.992233; Kind = 'dialogue'
        }
        [ordered]@{
            IsEnding = $true; EndText = 'end'; NeHeader = ''; NeTitle = ''
            NeScriptDirty = $exitPlaceholder
            Guid = '991ceb64-28f8-4bc7-892f-d8e68128bb1c'
            ConnectionsTo = @()
            X = -124.719818; Y = -589.3897; Kind = 'exit'
        }
    )
}

$json = $project | ConvertTo-Json -Depth 20
$bytes = [Text.UTF8Encoding]::new($false).GetBytes("AAP2`n" + $json)
[IO.Directory]::CreateDirectory($evidence) | Out-Null
# CreateNew also prevents overwriting a file created after the earlier check.
$stream = [IO.FileStream]::new($fixturePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
"Synthetic two-line fixture created: $fixturePath"
