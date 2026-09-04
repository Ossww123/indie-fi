[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("Up", "Status", "Down", "Purge")]
    [string]$Action,

    [switch]$ConfirmPurge
)

$ErrorActionPreference = "Stop"
$observabilityRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$composePath = Join-Path $observabilityRoot "portfolio-observability-local-compose.yml"
$environmentPath = Join-Path $observabilityRoot "local-observability.env"

function Invoke-Compose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & docker compose `
        --env-file $environmentPath `
        --file $composePath `
        @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed with exit code $LASTEXITCODE."
    }
}

function Wait-HttpReady {
    param(
        [string]$Name,
        [string]$Uri,
        [int]$Attempts = 30
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 2
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 400) {
                Write-Host "$Name ready: $Uri"
                return
            }
        }
        catch {
            if ($attempt -eq $Attempts) {
                throw "$Name did not become ready at $Uri. Check 'docker compose logs'."
            }
        }

        Start-Sleep -Seconds 2
    }
}

if (-not (Test-Path -LiteralPath $environmentPath)) {
    throw "Create '$environmentPath' from local-observability.env.example first."
}

switch ($Action) {
    "Up" {
        Invoke-Compose up --detach
        Wait-HttpReady "OpenTelemetry gateway" "http://127.0.0.1:13133/"
        Wait-HttpReady "Prometheus" "http://127.0.0.1:9090/-/ready"
        Wait-HttpReady "Loki" "http://127.0.0.1:3100/ready"
        Wait-HttpReady "Tempo" "http://127.0.0.1:3200/ready"
        Wait-HttpReady "Grafana" "http://127.0.0.1:3000/api/health"
        Write-Host "Grafana: http://127.0.0.1:3000 (user: indiefi-admin)"
    }
    "Status" {
        Invoke-Compose ps
    }
    "Down" {
        Invoke-Compose down --remove-orphans
    }
    "Purge" {
        if (-not $ConfirmPurge) {
            throw "Purge deletes all local telemetry volumes. Run again with -ConfirmPurge."
        }

        Invoke-Compose down --volumes --remove-orphans
    }
}
