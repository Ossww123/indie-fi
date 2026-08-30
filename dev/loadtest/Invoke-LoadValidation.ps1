[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ContractPath,

    [Parameter(Mandatory = $true)]
    [string]$SeedManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$BeforeAuditPath,

    [string]$CredentialsPath,
    [string]$PlanPath = 'dev/loadtest/config/load-plan.json',
    [string]$PrometheusUrl = 'http://127.0.0.1:9090',
    [string]$GrafanaUrl = 'http://127.0.0.1:3000',
    [string]$GrafanaEnvironmentPath = 'dev/ops/observability/local-observability.env',
    [switch]$ValidateOnly,
    [switch]$SkipMonitoring,
    [switch]$SkipCoolDown
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$loadtestRoot = [IO.Path]::GetFullPath($PSScriptRoot)

function Resolve-ExistingFile([string]$Path, [string]$Name) {
    $candidate = if ([IO.Path]::IsPathRooted($Path)) {
        $Path
    } else {
        Join-Path $workspaceRoot $Path
    }
    $resolved = [IO.Path]::GetFullPath($candidate)
    if (-not [IO.File]::Exists($resolved)) {
        throw "$Name does not exist: $resolved"
    }
    return $resolved
}

function Read-JsonFile([string]$Path, [string]$Name) {
    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        throw "$Name is not valid JSON."
    }
}

function Require-Equal($Actual, $Expected, [string]$Name) {
    if ($Actual -ne $Expected) {
        throw "$Name does not match the execution contract."
    }
}

function Require-Integer($Value, [int]$Minimum, [string]$Name) {
    if ($Value -isnot [int] -and $Value -isnot [long]) {
        throw "$Name must be an integer."
    }
    if ([long]$Value -lt $Minimum) {
        throw "$Name must be at least $Minimum."
    }
}

function Get-K6ModuleSpecifier([string]$Path, [string]$Name) {
    $prefix = $loadtestRoot.TrimEnd('\') + '\'
    if (-not $Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must stay under dev/loadtest so k6 can load it safely."
    }
    $relative = $Path.Substring($prefix.Length).Replace('\', '/')
    return "../$relative"
}

function Write-JsonExclusive([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 10
    $utf8 = New-Object Text.UTF8Encoding($false)
    $stream = New-Object IO.FileStream(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $writer = New-Object IO.StreamWriter($stream, $utf8)
        try {
            $writer.Write($json)
            $writer.Write("`n")
        } finally {
            $writer.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Test-Target([string]$BaseUrl) {
    try {
        $response = Invoke-WebRequest -Uri "$BaseUrl/" -Method Get -UseBasicParsing -TimeoutSec 5
        return $response.StatusCode -eq 200
    } catch {
        return $false
    }
}

function Get-FiringAlerts([string]$Severity) {
    $query = [Uri]::EscapeDataString("ALERTS{alertstate=`"firing`",severity=`"$Severity`"}")
    $response = Invoke-RestMethod -Uri "$PrometheusUrl/api/v1/query?query=$query" -TimeoutSec 5
    if ($response.status -ne 'success') {
        throw 'Prometheus alert query did not succeed.'
    }
    return @($response.data.result | ForEach-Object { $_.metric.alertname } |
        Where-Object { $_ } | Sort-Object -Unique)
}

function Get-GrafanaHeaders([string]$EnvironmentPath) {
    $passwordLine = Get-Content -LiteralPath $EnvironmentPath -Encoding UTF8 |
        Where-Object { $_ -match '^GRAFANA_ADMIN_PASSWORD=' } |
        Select-Object -First 1
    if (-not $passwordLine) {
        throw 'GRAFANA_ADMIN_PASSWORD is missing from the local observability environment file.'
    }
    $password = $passwordLine.Substring('GRAFANA_ADMIN_PASSWORD='.Length)
    if ([string]::IsNullOrWhiteSpace($password)) {
        throw 'GRAFANA_ADMIN_PASSWORD is empty.'
    }
    $pair = "indiefi-admin:$password"
    $basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pair))
    return @{ Authorization = "Basic $basic" }
}

function Add-GrafanaAnnotation($Headers, $Contract, [string]$Event) {
    $body = [ordered]@{
        time = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        tags = @(
            'indiefi-loadtest',
            "run:$($Contract.runId)",
            "scenario:$($Contract.scenario)",
            "stage:$($Contract.stage)",
            "topology:$($Contract.topology)",
            "commit:$($Contract.commit)",
            "event:$Event"
        )
        text = "IndieFi load validation $Event ($($Contract.runId))"
    } | ConvertTo-Json -Depth 4
    $response = Invoke-RestMethod -Uri "$GrafanaUrl/api/annotations" -Method Post `
        -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 5
    return $response.id
}

$contractFile = Resolve-ExistingFile $ContractPath 'contract'
$seedFile = Resolve-ExistingFile $SeedManifestPath 'seed manifest'
$beforeAuditFile = Resolve-ExistingFile $BeforeAuditPath 'before audit'
$planFile = Resolve-ExistingFile $PlanPath 'load plan'
$contract = Read-JsonFile $contractFile 'contract'
$seed = Read-JsonFile $seedFile 'seed manifest'
$beforeAudit = Read-JsonFile $beforeAuditFile 'before audit'
$plan = Read-JsonFile $planFile 'load plan'

Require-Equal $contract.schemaVersion 1 'contract.schemaVersion'
if ($contract.runId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
    throw 'contract.runId has an invalid format.'
}
if ($contract.scenario -notin @('browse', 'keydrop')) {
    throw 'contract.scenario must be browse or keydrop.'
}
if ($contract.baseUrl -notmatch '^https://[^/]+$' -and
    $contract.baseUrl -notmatch '^http://(localhost|127(?:\.[0-9]{1,3}){3})(?::[0-9]+)?$') {
    throw 'contract.baseUrl must use HTTPS unless the target is loopback.'
}
if ($contract.commit -notmatch '^[a-f0-9]{40}$') {
    throw 'contract.commit must be a full lowercase commit SHA.'
}
Require-Equal $seed.runId $contract.runId 'seed.runId'
Require-Equal $seed.commit $contract.commit 'seed.commit'
Require-Equal $seed.baseUrl.TrimEnd('/') $contract.baseUrl.TrimEnd('/') 'seed.baseUrl'
Require-Equal $beforeAudit.runId $contract.runId 'beforeAudit.runId'
Require-Equal $beforeAudit.phase 'before' 'beforeAudit.phase'
Require-Equal $beforeAudit.passed $true 'beforeAudit.passed'
Require-Equal $plan.schemaVersion 1 'plan.schemaVersion'

$stageNames = @($plan.stages | ForEach-Object { $_.name })
$stageKeys = @($plan.stages | ForEach-Object { "$($_.scenario)/$($_.contractStage)" })
if ($stageNames.Count -eq 0 -or
    @($stageNames | Sort-Object -Unique).Count -ne $stageNames.Count -or
    @($stageKeys | Sort-Object -Unique).Count -ne $stageKeys.Count) {
    throw 'The load plan must have stages with unique names and scenario/stage pairs.'
}

$matchingStages = @($plan.stages | Where-Object {
    $_.scenario -eq $contract.scenario -and $_.contractStage -eq $contract.stage
})
if ($matchingStages.Count -ne 1) {
    throw 'The load plan must contain exactly one matching scenario and contract stage.'
}
$selectedStage = $matchingStages[0]
foreach ($property in $selectedStage.parameters.PSObject.Properties) {
    if ($contract.parameters.PSObject.Properties.Name -notcontains $property.Name) {
        throw "contract.parameters.$($property.Name) is missing."
    }
    Require-Equal $contract.parameters.($property.Name) $property.Value `
        "contract.parameters.$($property.Name)"
}

Require-Integer $plan.safety.pollSeconds 1 'plan.safety.pollSeconds'
Require-Integer $plan.safety.targetFailureSeconds $plan.safety.pollSeconds `
    'plan.safety.targetFailureSeconds'
Require-Integer $plan.safety.coolDownSeconds 10 'plan.safety.coolDownSeconds'
Require-Integer $plan.safety.maxRunSeconds 30 'plan.safety.maxRunSeconds'

if ($contract.scenario -eq 'keydrop') {
    $opensAt = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        [string]$seed.drop.opensAtUtc,
        [ref]$opensAt)) {
        throw 'seed.drop.opensAtUtc is invalid.'
    }
    $requiredPreparationSeconds =
        [double]$contract.parameters.preparationSpreadSeconds + 10
    if (($opensAt - [DateTimeOffset]::UtcNow).TotalSeconds -lt
        $requiredPreparationSeconds) {
        throw 'The key drop does not leave enough future time for login preparation.'
    }
}

$isLoopback = $contract.baseUrl -match '^http://(localhost|127(?:\.[0-9]{1,3}){3})'
if (($SkipMonitoring -or $SkipCoolDown) -and -not $isLoopback) {
    throw 'Monitoring and cool-down may only be skipped for a loopback target.'
}
if (-not $isLoopback -and
    ($plan.safety.pollSeconds -gt 5 -or
     $plan.safety.targetFailureSeconds -gt 15 -or
     $plan.safety.coolDownSeconds -lt 60 -or
     $plan.safety.maxRunSeconds -gt 3600)) {
    throw 'The operational plan exceeds the fixed poll, abort, cool-down, or run-time safety boundary.'
}

$credentialsFile = $null
if ($contract.scenario -eq 'keydrop') {
    if ([string]::IsNullOrWhiteSpace($CredentialsPath)) {
        throw 'CredentialsPath is required for the keydrop scenario.'
    }
    $credentialsFile = Resolve-ExistingFile $CredentialsPath 'credentials'
}

$contractSpecifier = Get-K6ModuleSpecifier $contractFile 'contract'
$seedSpecifier = Get-K6ModuleSpecifier $seedFile 'seed manifest'
$credentialsSpecifier = if ($credentialsFile) {
    Get-K6ModuleSpecifier $credentialsFile 'credentials'
} else {
    $null
}

$outputRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $contract.outputDirectory))
$workspacePrefix = $workspaceRoot.TrimEnd('\') + '\'
if (-not $outputRoot.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'contract.outputDirectory must stay inside the workspace.'
}
$runDirectory = Join-Path $outputRoot $contract.runId
$runPrefix = [IO.Path]::GetFullPath($runDirectory).TrimEnd('\') + '\'
if (-not $seedFile.StartsWith($runPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $beforeAuditFile.StartsWith($runPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Seed manifest and before audit must be inside the contract run directory.'
}

Write-Host "Validated $($contract.scenario)/$($contract.stage) for run $($contract.runId)."
Write-Host "Target commit: $($contract.commit)"
if ($ValidateOnly) {
    exit 0
}

[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$startPath = Join-Path $runDirectory 'runner-start.json'
$resultPath = Join-Path $runDirectory 'runner-result.json'
if ([IO.File]::Exists($startPath) -or [IO.File]::Exists($resultPath)) {
    throw 'This run already has runner evidence; use a new run id.'
}

$grafanaHeaders = $null
$startAnnotationId = $null
if (-not $SkipMonitoring) {
    if (-not (Test-Target $contract.baseUrl)) {
        throw 'The public target probe failed before the load run.'
    }
    $critical = @(Get-FiringAlerts 'critical')
    if ($critical.Count -gt 0) {
        throw "Critical alerts are already firing: $($critical -join ', ')"
    }
    $grafanaEnvironmentFile = Resolve-ExistingFile $GrafanaEnvironmentPath `
        'Grafana environment file'
    $grafanaHeaders = Get-GrafanaHeaders $grafanaEnvironmentFile
    $startAnnotationId = Add-GrafanaAnnotation $grafanaHeaders $contract 'measurement-start'
}

$startedAt = [DateTimeOffset]::UtcNow
$startEvidence = [ordered]@{
    schemaVersion = 1
    runId = $contract.runId
    scenario = $contract.scenario
    stage = $contract.stage
    topology = $contract.topology
    commit = $contract.commit
    startedAtUtc = $startedAt.ToString('o')
    selectedPlanStage = $selectedStage.name
    monitoringEnabled = -not $SkipMonitoring
    grafanaStartAnnotationId = $startAnnotationId
}
Write-JsonExclusive $startPath $startEvidence

$k6 = Get-Command k6 -ErrorAction Stop
$scriptPath = "dev/loadtest/$($contract.scenario).js"
$stdoutPath = Join-Path $runDirectory 'k6.stdout.log'
$stderrPath = Join-Path $runDirectory 'k6.stderr.log'
$arguments = @(
    'run',
    '-e', "CONTRACT_FILE=$contractSpecifier",
    '-e', "SEED_MANIFEST_FILE=$seedSpecifier"
)
if ($credentialsSpecifier) {
    $arguments += @('-e', "CREDENTIALS_FILE=$credentialsSpecifier")
}
$arguments += $scriptPath

$process = Start-Process -FilePath $k6.Source -ArgumentList $arguments `
    -WorkingDirectory $workspaceRoot -NoNewWindow -PassThru `
    -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
# Windows PowerShell only retains ExitCode reliably when the native process handle is
# opened before waiting. Keeping this handle prevents a failed k6 run from looking green.
$processHandle = $process.Handle
$abortReason = $null
$criticalAlerts = @()
$warningAlerts = @()
$consecutiveTargetFailures = 0
$targetFailureLimit = [Math]::Ceiling(
    $plan.safety.targetFailureSeconds / $plan.safety.pollSeconds)

while (-not $process.HasExited) {
    Start-Sleep -Seconds $plan.safety.pollSeconds
    $process.Refresh()
    if ($process.HasExited) {
        break
    }
    if (([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds -ge
        $plan.safety.maxRunSeconds) {
        $abortReason = 'max-run-seconds'
        break
    }
    if (-not $SkipMonitoring) {
        if (Test-Target $contract.baseUrl) {
            $consecutiveTargetFailures = 0
        } else {
            $consecutiveTargetFailures++
        }
        if ($consecutiveTargetFailures -ge $targetFailureLimit) {
            $abortReason = 'target-probe-failed'
            break
        }
        try {
            $criticalAlerts = @(Get-FiringAlerts 'critical')
            $warningAlerts = @($warningAlerts + @(Get-FiringAlerts 'warning') |
                Sort-Object -Unique)
        } catch {
            $abortReason = 'monitoring-unavailable'
            break
        }
        if ($criticalAlerts.Count -gt 0) {
            $abortReason = 'critical-prometheus-alert'
            break
        }
    }
}

if ($abortReason -and -not $process.HasExited) {
    $process.Kill()
}
$process.WaitForExit()
$k6ExitCode = $process.ExitCode
$measurementEndedAt = [DateTimeOffset]::UtcNow

$endAnnotationFailed = $false
if (-not $SkipMonitoring) {
    try {
        [void](Add-GrafanaAnnotation $grafanaHeaders $contract `
            $(if ($abortReason) { 'measurement-aborted' } else { 'measurement-end' }))
    } catch {
        $endAnnotationFailed = $true
        if (-not $abortReason) {
            $abortReason = 'grafana-end-annotation-failed'
        }
    }
}

if (-not $SkipCoolDown) {
    Write-Host "Cooling down for $($plan.safety.coolDownSeconds) seconds."
    Start-Sleep -Seconds $plan.safety.coolDownSeconds
}

$postCoolDownTargetHealthy = $true
if (-not $SkipMonitoring) {
    $postCoolDownTargetHealthy = Test-Target $contract.baseUrl
    try {
        $postCritical = @(Get-FiringAlerts 'critical')
        $postWarning = @(Get-FiringAlerts 'warning')
        $criticalAlerts = @($criticalAlerts + $postCritical | Sort-Object -Unique)
        $warningAlerts = @($warningAlerts + $postWarning | Sort-Object -Unique)
        if (-not $postCoolDownTargetHealthy -or $postCritical.Count -gt 0) {
            if (-not $abortReason) {
                $abortReason = 'post-cooldown-not-converged'
            }
        }
    } catch {
        $postCoolDownTargetHealthy = $false
        if (-not $abortReason) {
            $abortReason = 'post-cooldown-monitoring-unavailable'
        }
    }
}

$exitCode = if ($abortReason) { 2 } else { $k6ExitCode }
$result = [ordered]@{
    schemaVersion = 1
    runId = $contract.runId
    scenario = $contract.scenario
    stage = $contract.stage
    topology = $contract.topology
    commit = $contract.commit
    startedAtUtc = $startedAt.ToString('o')
    measurementEndedAtUtc = $measurementEndedAt.ToString('o')
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    k6ExitCode = $k6ExitCode
    runnerExitCode = $exitCode
    aborted = [bool]$abortReason
    abortReason = $abortReason
    warningAlerts = @($warningAlerts)
    criticalAlerts = @($criticalAlerts)
    endAnnotationFailed = $endAnnotationFailed
    postCoolDownTargetHealthy = $postCoolDownTargetHealthy
    afterAuditRequired = $true
    eligibleForAfterAudit = ($exitCode -eq 0)
    candidateForNextStageAfterAudit =
        ($exitCode -eq 0 -and $warningAlerts.Count -eq 0)
    artifacts = [ordered]@{
        stdout = 'k6.stdout.log'
        stderr = 'k6.stderr.log'
        k6Summary = "$($contract.scenario)-summary.json"
    }
}
Write-JsonExclusive $resultPath $result

Write-Host "Runner result: $resultPath"
Write-Host 'Run the after audit and verify queue convergence before the next stage.'
exit $exitCode
