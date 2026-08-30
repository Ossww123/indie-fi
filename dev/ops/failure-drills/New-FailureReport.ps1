[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string] $RunId,

    [Parameter(Mandatory = $true)]
    [ValidateSet(
        'web-restart',
        'worker-restart',
        'postgresql-outage',
        'redis-outage',
        'rabbitmq-outage',
        'r2-outage',
        'ses-outage')]
    [string] $Scenario,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Passed', 'ValidFailure', 'Invalid', 'SafetyStop')]
    [string] $Verdict,

    [Parameter(Mandatory = $true)]
    [string] $InjectionStartedAtUtc,

    [Parameter(Mandatory = $true)]
    [string] $FirstDetectedAtUtc,

    [Parameter(Mandatory = $true)]
    [string] $TechnicalRecoveredAtUtc,

    [Parameter(Mandatory = $true)]
    [string] $UserRecoveredAtUtc,

    [Parameter(Mandatory = $true)]
    [string] $ConvergedAtUtc,

    [Parameter(Mandatory = $true)]
    [string] $RunDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateLength(1, 1000)]
    [string] $UserImpact,

    [string[]] $AutomaticRecovery = @(),
    [string[]] $ManualRecovery = @(),
    [string[]] $UnmetCriteria = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertFrom-UtcTimestamp {
    param([Parameter(Mandatory = $true)][string] $Value)

    $parsed = [DateTimeOffset]::MinValue
    $styles = [Globalization.DateTimeStyles]::AssumeUniversal -bor
        [Globalization.DateTimeStyles]::AdjustToUniversal
    if (-not [DateTimeOffset]::TryParse(
            $Value,
            [Globalization.CultureInfo]::InvariantCulture,
            $styles,
            [ref] $parsed)) {
        throw "Invalid UTC timestamp: $Value"
    }

    if ($parsed.Offset -ne [TimeSpan]::Zero) {
        throw "Timestamp must resolve to UTC: $Value"
    }

    return $parsed
}

function Get-SecondsBetween {
    param(
        [Parameter(Mandatory = $true)][DateTimeOffset] $Start,
        [Parameter(Mandatory = $true)][DateTimeOffset] $End
    )

    if ($End -lt $Start) {
        throw "Timestamp order is invalid: $End is before $Start."
    }

    return [Math]::Round(($End - $Start).TotalSeconds, 3)
}

$injection = ConvertFrom-UtcTimestamp $InjectionStartedAtUtc
$detected = ConvertFrom-UtcTimestamp $FirstDetectedAtUtc
$technical = ConvertFrom-UtcTimestamp $TechnicalRecoveredAtUtc
$user = ConvertFrom-UtcTimestamp $UserRecoveredAtUtc
$converged = ConvertFrom-UtcTimestamp $ConvergedAtUtc

if ($detected -lt $injection -or
    $technical -lt $detected -or
    $user -lt $technical -or
    $converged -lt $user) {
    throw 'Timestamps must be ordered: injection, detection, technical recovery, user recovery, convergence.'
}

$resolvedRunDirectory = [IO.Path]::GetFullPath($RunDirectory)
if (-not (Test-Path -LiteralPath $resolvedRunDirectory -PathType Container)) {
    throw "Run directory does not exist: $resolvedRunDirectory"
}

$reportPath = Join-Path $resolvedRunDirectory 'failure-report.json'
$evidence = @(
    Get-ChildItem -LiteralPath $resolvedRunDirectory -Recurse -File |
        Where-Object { $_.FullName -ne $reportPath } |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($resolvedRunDirectory.Length + 1).
                    Replace([IO.Path]::DirectorySeparatorChar, '/')
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)

$report = [ordered]@{
    schemaVersion = 1
    runId = $RunId
    scenario = $Scenario
    verdict = $Verdict
    timestamps = [ordered]@{
        injectionStartedAtUtc = $injection.ToString('o')
        firstDetectedAtUtc = $detected.ToString('o')
        technicalRecoveredAtUtc = $technical.ToString('o')
        userRecoveredAtUtc = $user.ToString('o')
        convergedAtUtc = $converged.ToString('o')
    }
    durationsSeconds = [ordered]@{
        detection = Get-SecondsBetween $injection $detected
        technicalRecovery = Get-SecondsBetween $injection $technical
        userRecovery = Get-SecondsBetween $injection $user
        finalConvergence = Get-SecondsBetween $injection $converged
    }
    userImpact = $UserImpact
    recovery = [ordered]@{
        automatic = @($AutomaticRecovery)
        manual = @($ManualRecovery)
    }
    unmetCriteria = @($UnmetCriteria)
    evidence = $evidence
}

$report | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "Failure report saved to $reportPath."
