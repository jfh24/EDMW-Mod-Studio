param(
    [Parameter(Mandatory = $true)]
    [string]$TracePath,

    [Parameter(Mandatory = $true)]
    [string]$FaunaJsonlPath,

    [switch]$Markdown
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedTrace = (Resolve-Path -LiteralPath $TracePath).Path
$resolvedJsonl = (Resolve-Path -LiteralPath $FaunaJsonlPath).Path
$traceLines = @(Get-Content -LiteralPath $resolvedTrace)
$events = @(Get-Content -LiteralPath $resolvedJsonl | ForEach-Object { $_ | ConvertFrom-Json })

$attemptStarts = @()
for ($index = 0; $index -lt $traceLines.Count; ++$index) {
    if ($traceLines[$index] -match ' Map initialized [0-9A-F]{8}$') {
        $attemptStarts += $index
    }
}
if ($attemptStarts.Count -eq 0) {
    throw 'The trace contains no Map initialized attempt boundary.'
}

# The old, incorrect decoder called +0x2BC world_unit_ring_vector and +0x2D4
# place_world_unit_vector. Corrected captures use the names below. Supporting both
# makes this parser useful for the completed failed run as well as future evidence.
$eventGroups = @()
$currentGroup = $null
foreach ($event in $events) {
    $isAttemptStart = $event.event -eq 'candidate_vector_header' -and
        $event.phase -eq 'after set random map values' -and
        [int]$event.offset -eq 0x2BC
    if ($isAttemptStart) {
        if ($null -ne $currentGroup) { $eventGroups += ,@($currentGroup) }
        $currentGroup = @()
    }
    if ($null -ne $currentGroup) { $currentGroup += ,$event }
}
if ($null -ne $currentGroup) { $eventGroups += ,@($currentGroup) }
if ($eventGroups.Count -gt $attemptStarts.Count) {
    throw "Trace/JSONL attempt mismatch: $($attemptStarts.Count) trace attempts and $($eventGroups.Count) JSONL attempts."
}
if ($eventGroups.Count -lt $attemptStarts.Count) {
    # The trace log is cumulative across processes while each JSONL filename is
    # process-specific. Align a selected process file with the matching suffix.
    $attemptStarts = @($attemptStarts | Select-Object -Last $eventGroups.Count)
}

function Get-LastHexValue {
    param([string[]]$Lines, [string]$Label)
    $pattern = [regex]::Escape($Label) + ' ([0-9A-F]{8})$'
    $match = @($Lines | Select-String -Pattern $pattern | Select-Object -Last 1)
    if ($match.Count -eq 0) { return $null }
    return [Convert]::ToUInt32($match[0].Matches[0].Groups[1].Value, 16)
}

function Get-FirstClearanceSnapshotZeroCount {
    param([string[]]$Lines)
    $started = $false
    $lastRecord = -1
    $zeroCount = 0
    foreach ($line in $Lines) {
        if ($line -notmatch 'World height clearance record (\d+) ([0-9A-F]{8})$') {
            continue
        }
        $record = [int]$Matches[1]
        if ($started -and $record -le $lastRecord) { break }
        $started = $true
        $lastRecord = $record
        if ($Matches[2] -eq '00000000') { ++$zeroCount }
    }
    return $zeroCount
}

$rows = @()
for ($attempt = 0; $attempt -lt $attemptStarts.Count; ++$attempt) {
    $end = if ($attempt + 1 -lt $attemptStarts.Count) {
        $attemptStarts[$attempt + 1] - 1
    } else {
        $traceLines.Count - 1
    }
    $chunk = @($traceLines[$attemptStarts[$attempt]..$end])
    $group = @($eventGroups[$attempt])

    $unifiedVectors = @($group | Where-Object event -eq 'unified_unit_placement_vector')
    $unified = @($unifiedVectors |
        Select-Object -First 1)
    $walls = @($group | Where-Object event -eq 'place_wall_vector' |
        Select-Object -First 1)
    $decoder = 'corrected'
    if ($unified.Count -eq 0) {
        $unified = @($group | Where-Object event -eq 'world_unit_ring_vector' |
            Select-Object -First 1)
        $walls = @($group | Where-Object event -eq 'place_world_unit_vector' |
            Select-Object -First 1)
        $decoder = 'legacy-labels-corrected-by-parser'
    }
    $finalUnified = if ($unifiedVectors.Count) {
        @($unifiedVectors | Select-Object -Last 1)
    } else {
        $unified
    }
    $finalPhase = if ($finalUnified.Count) { $finalUnified[0].phase } else { $null }
    $finalEngineGenerated = if ($null -ne $finalPhase) {
        @($group | Where-Object {
            $_.event -eq 'unified_unit_placement_record' -and
            $_.phase -eq $finalPhase -and
            [int]$_.engineGenerated -ne 0
        }).Count
    } else { 0 }
    $placement = @($group | Where-Object event -eq 'placement_digest' |
        Select-Object -First 1)
    $manualPlacements = @($group | Where-Object event -eq 'manual_fauna_placement')
    $finalResources = @($group | Where-Object {
        $_.event -eq 'resource_vector' -and
        $_.phase -eq 'after manual fauna reconciliation'
    } | Select-Object -Last 1)
    $resourceCallbacks = @($chunk | Select-String -Pattern 'Resources result').Count
    $treeCallbacks = @($chunk | Select-String -Pattern 'Trees result').Count
    $lastStage = if ($treeCallbacks -gt 0) { 'Trees' } elseif ($resourceCallbacks -gt 0) {
        'Resources'
    } elseif (@($chunk | Select-String -Pattern 'River fill result').Count -gt 0) {
        'River'
    } else {
        'Earlier'
    }

    $rows += [pscustomobject]@{
        Attempt = $attempt + 1
        LastStage = $lastStage
        PlayerInvalidDomains = Get-LastHexValue $chunk 'Player generated invalid domains'
        NeutralInvalidDomains = Get-LastHexValue $chunk 'Neutral generated invalid domains'
        InternalHeightLand = Get-LastHexValue $chunk 'Stage height land'
        PreTransferWorldHeightLand = Get-LastHexValue $chunk 'World height land'
        WorldTypedLand = Get-LastHexValue $chunk 'World typed land'
        ZeroHeightClearanceRecords = Get-FirstClearanceSnapshotZeroCount $chunk
        UnifiedUnitCount = if ($unified.Count) { $unified[0].count } else { $null }
        FinalUnifiedUnitCount = if ($finalUnified.Count) { $finalUnified[0].count } else { $null }
        FinalEngineGeneratedCount = $finalEngineGenerated
        ManualWaterCommitted = @($manualPlacements | Where-Object {
            $_.category -eq 'water' -and $_.committed
        }).Count
        ManualLandCommitted = @($manualPlacements | Where-Object {
            $_.category -eq 'land' -and $_.committed
        }).Count
        FinalFishCount = if ($finalResources.Count) { $finalResources[0].fishCount } else { $null }
        UnifiedUnitDigest = if ($unified.Count) { $unified[0].digest } else { $null }
        PlaceWallCount = if ($walls.Count) { $walls[0].count } else { $null }
        PlaceWallDigest = if ($walls.Count) { $walls[0].digest } else { $null }
        PlacementDigest = if ($placement.Count) { $placement[0].digest } else { $null }
        ResourceCallbacks = $resourceCallbacks
        TreeCallbacks = $treeCallbacks
        Decoder = $decoder
    }
}

if ($Markdown) {
    '| Attempt | Last stage | Player invalid | Neutral invalid | Internal height land | Pre-transfer world height | World typed land | First clearance snapshot zeros | +0x2BC initial | +0x2BC final | Engine-generated final | Manual water | Manual land | Final Fish | Resources | Trees |'
    '|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|'
    foreach ($row in $rows) {
        "| $($row.Attempt) | $($row.LastStage) | $($row.PlayerInvalidDomains) | $($row.NeutralInvalidDomains) | $($row.InternalHeightLand) | $($row.PreTransferWorldHeightLand) | $($row.WorldTypedLand) | $($row.ZeroHeightClearanceRecords) | $($row.UnifiedUnitCount) | $($row.FinalUnifiedUnitCount) | $($row.FinalEngineGeneratedCount) | $($row.ManualWaterCommitted) | $($row.ManualLandCommitted) | $($row.FinalFishCount) | $($row.ResourceCallbacks) | $($row.TreeCallbacks) |"
    }
} else {
    $rows
}
