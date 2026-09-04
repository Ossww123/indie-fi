[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RunDirectory,
    [Parameter(Mandatory = $true)][string]$AgeIdentityPath,
    [string]$AgePath,
    [string]$TarPath,
    [switch]$WriteVerification
)

. "$PSScriptRoot/recovery-common.ps1"

$ErrorActionPreference = "Stop"
$temporaryDirectory = $null
try {
    $run = (Resolve-Path -LiteralPath $RunDirectory).Path
    $identity = (Resolve-Path -LiteralPath $AgeIdentityPath).Path
    $age = Resolve-NativeTool 'age' $AgePath
    $tar = Resolve-NativeTool 'tar' $TarPath
    $bundlePath = Join-Path $run 'bundle.tar.age'
    $hashPath = Join-Path $run 'bundle.tar.age.sha256'
    $evidencePath = Join-Path $run 'evidence.json'
    foreach ($required in @($bundlePath, $hashPath, $evidencePath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Recovery run is missing: $required"
        }
    }

    $expectedHashLine = (Get-Content -LiteralPath $hashPath -Encoding ascii | Select-Object -First 1)
    if ($expectedHashLine -notmatch '^([0-9a-fA-F]{64})  bundle\.tar\.age$') {
        throw "Invalid recovery SHA-256 file."
    }
    $expectedHash = $Matches[1].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Encrypted recovery bundle SHA-256 mismatch."
    }

    $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "indiefi-verify-$([Guid]::NewGuid().ToString('N'))"
    [void](New-Item -ItemType Directory -Path $temporaryDirectory)
    $plainTar = Join-Path $temporaryDirectory 'bundle.tar'
    [void](Invoke-NativeText $age @('-d', '-i', $identity, '-o', $plainTar, $bundlePath))
    $entryText = Invoke-NativeText $tar @('-tf', $plainTar)
    $entries = @($entryText -split "`r?`n")
    foreach ($entry in $entries) {
        $segments = @($entry.Replace('\', '/') -split '/')
        if (-not $entry.StartsWith('bundle/', [StringComparison]::Ordinal) -or
            $segments -contains '..' -or
            [System.IO.Path]::IsPathRooted($entry)) {
            throw "Unsafe entry in encrypted recovery archive: $entry"
        }
    }
    foreach ($requiredEntry in @(
        'bundle/manifest.json',
        'bundle/postgresql/indiefi.dump',
        'bundle/postgresql/dump-list.txt',
        'bundle/postgresql/row-counts.json',
        'bundle/r2/inventory.json',
        'bundle/keys/account-protection.json',
        'bundle/keys/data-protection.crt',
        'bundle/keys/data-protection.key',
        'bundle/configuration/fingerprints.txt')) {
        if ($entries -notcontains $requiredEntry) {
            throw "Encrypted recovery bundle is missing: $requiredEntry"
        }
    }

    $extractRoot = Join-Path $temporaryDirectory 'extracted'
    [void](New-Item -ItemType Directory -Path $extractRoot)
    [void](Invoke-NativeText $tar @('-xf', $plainTar, '-C', $extractRoot))
    $bundleRoot = Join-Path $extractRoot 'bundle'
    $manifestPath = Join-Path $bundleRoot 'manifest.json'
    $manifest = Get-Content -Raw -Encoding utf8 $manifestPath | ConvertFrom-Json
    if ($manifest.formatVersion -ne 1) {
        throw "Unsupported recovery manifest version: $($manifest.formatVersion)"
    }
    foreach ($file in @($manifest.files)) {
        $relative = [string]$file.path
        if ($relative.Contains('..') -or [System.IO.Path]::IsPathRooted($relative)) {
            throw "Unsafe path in recovery manifest: $relative"
        }
        $fullPath = Join-Path $bundleRoot ($relative.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Manifest file is missing from bundle: $relative"
        }
        $item = Get-Item -LiteralPath $fullPath
        $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($item.Length -ne [long]$file.size -or $hash -ne [string]$file.sha256) {
            throw "Manifest size or SHA-256 mismatch: $relative"
        }
    }

    $verification = [ordered]@{
        formatVersion = 1
        runId = $manifest.runId
        verifiedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        encryptedSha256 = $actualHash
        ageVersion = (Invoke-NativeText $age @('--version')).Trim()
        tarVersion = ((Invoke-NativeText $tar @('--version')) -split "`r?`n")[0]
        archiveEntryCount = $entries.Count
        manifestFileCount = @($manifest.files).Count
        result = 'pass'
    }
    if ($WriteVerification) {
        Write-Utf8Json $verification (Join-Path $run 'verification.json')
        $evidence = Get-Content -Raw -Encoding utf8 $evidencePath | ConvertFrom-Json
        $evidence.verified = $true
        if ($evidence.PSObject.Properties.Name -contains 'verifiedUtc') {
            $evidence.verifiedUtc = $verification.verifiedUtc
        }
        else {
            $evidence | Add-Member -NotePropertyName verifiedUtc -NotePropertyValue $verification.verifiedUtc
        }
        Write-Utf8Json $evidence $evidencePath
    }
    $verification | Format-List
    Write-Host "Encrypted recovery bundle verification: PASS"
}
finally {
    if ($temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
