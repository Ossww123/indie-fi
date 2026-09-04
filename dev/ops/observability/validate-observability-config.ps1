[CmdletBinding()]
param(
    [string]$EnvironmentFile,
    [switch]$Runtime
)

$ErrorActionPreference = "Stop"
$observabilityRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$composePath = Join-Path $observabilityRoot "portfolio-observability-local-compose.yml"
$localRoot = Join-Path $observabilityRoot "local"

if ([string]::IsNullOrWhiteSpace($EnvironmentFile)) {
    $EnvironmentFile = Join-Path $observabilityRoot "local-observability.env.example"
}

function Assert-LastExitCode {
    param([string]$Operation)

    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

& docker compose `
    --env-file $EnvironmentFile `
    --file $composePath `
    config --quiet
Assert-LastExitCode "docker compose config"

$renderedJson = & docker compose `
    --env-file $EnvironmentFile `
    --file $composePath `
    config --format json
Assert-LastExitCode "docker compose config JSON"
$rendered = $renderedJson | ConvertFrom-Json

foreach ($serviceProperty in $rendered.services.PSObject.Properties) {
    foreach ($port in @($serviceProperty.Value.ports)) {
        if ($null -ne $port.published -and $port.host_ip -ne "127.0.0.1") {
            throw "Service '$($serviceProperty.Name)' publishes port $($port.published) on '$($port.host_ip)' instead of loopback."
        }
    }
}
Write-Host "Compose and loopback port policy: OK"

$uids = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($dashboardPath in Get-ChildItem `
    (Join-Path $localRoot "grafana/dashboards/*.json")) {
    $dashboard = Get-Content -Raw -Encoding UTF8 $dashboardPath | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($dashboard.uid) -or
        [string]::IsNullOrWhiteSpace($dashboard.title) -or
        $dashboard.panels.Count -eq 0) {
        throw "Dashboard '$($dashboardPath.Name)' is missing uid, title, or panels."
    }
    if (-not $uids.Add($dashboard.uid)) {
        throw "Duplicate Grafana dashboard uid '$($dashboard.uid)'."
    }
}
Write-Host "Grafana dashboards: OK ($($uids.Count) unique uids)"

$parseTargets = @(
    (Join-Path $observabilityRoot "manage-local-observability.ps1"),
    (Join-Path $observabilityRoot "smoke-local-observability.ps1")
)
foreach ($parseTarget in $parseTargets) {
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $parseTarget,
        [ref]$null,
        [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) {
        throw "PowerShell parse failed for '$parseTarget': $($parseErrors[0].Message)"
    }
}
Write-Host "PowerShell syntax: OK"

if (-not $Runtime) {
    Write-Host "Static observability validation: PASS"
    exit 0
}

& docker info --format "{{.ServerVersion}}" | Out-Null
Assert-LastExitCode "docker info"

$dockerLocalRoot = $localRoot -replace "\\", "/"
& docker run --rm `
    --volume "${dockerLocalRoot}:/work:ro" `
    --entrypoint /bin/promtool `
    prom/prometheus:v3.14.0 `
    check config /work/prometheus.yaml
Assert-LastExitCode "promtool config validation"

& docker run --rm `
    --volume "${dockerLocalRoot}:/work:ro" `
    --entrypoint /bin/promtool `
    prom/prometheus:v3.14.0 `
    check rules /work/prometheus-rules/indiefi-recording-and-alerts.yaml
Assert-LastExitCode "promtool rule validation"

& docker run --rm `
    --volume "${dockerLocalRoot}:/work:ro" `
    otel/opentelemetry-collector-contrib:0.159.0 `
    validate --config=/work/otel-gateway.yaml
Assert-LastExitCode "Collector config validation"

$lokiArguments = @(
    "-config.file=/work/loki.yaml",
    "-verify-config=true"
)
& docker run --rm `
    --volume "${dockerLocalRoot}:/work:ro" `
    grafana/loki:3.7.7 `
    @lokiArguments
Assert-LastExitCode "Loki config validation"

Write-Host "Runtime image configuration validation: PASS"
