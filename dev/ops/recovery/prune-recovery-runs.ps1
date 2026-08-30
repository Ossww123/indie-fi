[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BackupRoot,
    [ValidateRange(1, 20)][int]$KeepLatest = 3,
    [ValidateRange(1, 365)][int]$MaximumAgeDays = 14,
    [switch]$ConfirmDeletion
)

. "$PSScriptRoot/recovery-common.ps1"

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $BackupRoot).Path.TrimEnd('\', '/')
$rootItem = Get-Item -LiteralPath $root -Force
if ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'BackupRoot must not be a reparse point.'
}

$verifiedRuns = @()
$manualRuns = @()
foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -Force) {
    if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        $manualRuns += [pscustomobject]@{ Run = $directory.Name; Reason = 'reparse-point' }
        continue
    }

    $markerPath = Join-Path $directory.FullName '.indiefi-recovery-run'
    $evidencePath = Join-Path $directory.FullName 'evidence.json'
    $verificationPath = Join-Path $directory.FullName 'verification.json'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $evidencePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $verificationPath -PathType Leaf)) {
        $manualRuns += [pscustomobject]@{ Run = $directory.Name; Reason = 'incomplete-or-unverified' }
        continue
    }

    try {
        $marker = Get-Content -Raw -Encoding utf8 $markerPath | ConvertFrom-Json
        $evidence = Get-Content -Raw -Encoding utf8 $evidencePath | ConvertFrom-Json
        $verification = Get-Content -Raw -Encoding utf8 $verificationPath | ConvertFrom-Json
        $resolvedDirectory = [System.IO.Path]::GetFullPath($directory.FullName)
        if ([System.IO.Path]::GetFullPath([string]$marker.runDirectory) -ne $resolvedDirectory -or
            [string]$marker.runId -ne [string]$evidence.runId -or
            [string]$verification.runId -ne [string]$evidence.runId -or
            [string]$verification.result -ne 'pass' -or
            -not [bool]$evidence.verified) {
            throw 'marker or verification mismatch'
        }
        $verifiedUtc = [DateTimeOffset]::Parse([string]$verification.verifiedUtc)
        $verifiedRuns += [pscustomobject]@{
            Run = $directory.Name
            Path = $resolvedDirectory
            VerifiedUtc = $verifiedUtc
        }
    }
    catch {
        $manualRuns += [pscustomobject]@{ Run = $directory.Name; Reason = 'invalid-evidence' }
    }
}

$orderedRuns = @($verifiedRuns | Sort-Object VerifiedUtc -Descending)
$cutoff = [DateTimeOffset]::UtcNow.AddDays(-$MaximumAgeDays)
$candidates = @()
for ($index = 0; $index -lt $orderedRuns.Count; $index++) {
    $run = $orderedRuns[$index]
    if ($index -ge $KeepLatest -or $run.VerifiedUtc -lt $cutoff) {
        $candidates += $run
    }
}

Write-Host "Verified runs: $($orderedRuns.Count)"
Write-Host "Delete candidates: $($candidates.Count)"
foreach ($candidate in $candidates) {
    Write-Host "  $($candidate.Run) verified=$($candidate.VerifiedUtc.ToString('o'))"
}
foreach ($manual in $manualRuns) {
    Write-Host "Manual review: $($manual.Run) reason=$($manual.Reason)"
}

if (-not $ConfirmDeletion) {
    Write-Host 'Preview only. Add -ConfirmDeletion to remove listed verified runs.'
    return
}

foreach ($candidate in $candidates) {
    $candidatePath = [System.IO.Path]::GetFullPath($candidate.Path)
    $expectedPrefix = $root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidatePath.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Deletion candidate escaped BackupRoot: $candidatePath"
    }
    $item = Get-Item -LiteralPath $candidatePath -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Deletion candidate became a reparse point: $candidatePath"
    }
    Remove-Item -LiteralPath $candidatePath -Recurse -Force
    Write-Host "Removed verified recovery run: $candidatePath"
}

