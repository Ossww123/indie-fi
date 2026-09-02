[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RunDirectory,

    [Parameter(Mandatory = $true)]
    [string] $StartAtUtc,

    [Parameter(Mandatory = $true)]
    [string] $EndAtUtc,

    [ValidateRange(5, 300)]
    [int] $StepSeconds = 15,

    [string] $PrometheusBaseUrl = 'http://127.0.0.1:9090',
    [string] $TempoBaseUrl = 'http://127.0.0.1:3200'
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

function Invoke-PrometheusRangeQuery {
    param(
        [Parameter(Mandatory = $true)][string] $Query,
        [Parameter(Mandatory = $true)][long] $StartSeconds,
        [Parameter(Mandatory = $true)][long] $EndSeconds
    )

    $encodedQuery = [Uri]::EscapeDataString($Query)
    $uri = "$PrometheusBaseUrl/api/v1/query_range?query=$encodedQuery" +
        "&start=$StartSeconds&end=$EndSeconds&step=${StepSeconds}s"
    $response = Invoke-RestMethod -Method Get -Uri $uri
    if ($response.status -ne 'success') {
        throw "Prometheus query failed: $Query"
    }

    return @($response.data.result)
}

$start = ConvertFrom-UtcTimestamp $StartAtUtc
$end = ConvertFrom-UtcTimestamp $EndAtUtc
if ($end -le $start) {
    throw 'EndAtUtc must be after StartAtUtc.'
}

$resolvedRunDirectory = [IO.Path]::GetFullPath($RunDirectory)
if (-not (Test-Path -LiteralPath $resolvedRunDirectory -PathType Container)) {
    throw "Run directory does not exist: $resolvedRunDirectory"
}

$observabilityDirectory = Join-Path $resolvedRunDirectory 'observability'
[void](New-Item -ItemType Directory -Path $observabilityDirectory -Force)

$startSeconds = $start.ToUnixTimeSeconds()
$endSeconds = $end.ToUnixTimeSeconds()
$definitions = @(
    [ordered]@{ name = 'alerts'; query = 'ALERTS{alertstate=~"pending|firing"}' },
    [ordered]@{ name = 'scrape-health'; query = 'up' },
    [ordered]@{ name = 'http-request-rate'; query = 'indiefi:http_request_rate:5m' },
    [ordered]@{ name = 'http-error-ratio'; query = 'indiefi:http_error_ratio:5m' },
    [ordered]@{ name = 'http-p95'; query = 'indiefi:http_p95_seconds:5m' },
    [ordered]@{ name = 'host-cpu-ratio'; query = '1 - (sum(rate(system_cpu_time_seconds_total{state="idle"}[5m])) / clamp_min(sum(rate(system_cpu_time_seconds_total[5m])), 1))' },
    [ordered]@{ name = 'host-memory-available'; query = 'sum(system_memory_usage_bytes{state=~"free|cached"})' },
    [ordered]@{ name = 'postgres-up'; query = 'pg_up' },
    [ordered]@{ name = 'redis-up'; query = 'redis_up' },
    [ordered]@{ name = 'rabbitmq-consumers'; query = 'sum(rabbitmq_queue_consumers{queue=~"indiefi.*"})' },
    [ordered]@{ name = 'rabbitmq-ready'; query = 'indiefi:rabbitmq_ready_total' },
    [ordered]@{ name = 'notification-outbox'; query = 'indiefi_notifications_outbox_backlog' },
    [ordered]@{ name = 'notification-email-pending'; query = 'indiefi_notifications_email_pending' },
    [ordered]@{ name = 'notification-email-failed'; query = 'indiefi_notifications_email_failed' },
    [ordered]@{ name = 'media-processing-pending'; query = 'indiefi_media_processing_pending' },
    [ordered]@{ name = 'media-processing-leases'; query = 'indiefi_media_processing_active_leases' },
    [ordered]@{ name = 'media-failed'; query = 'indiefi_media_failed' }
)

$prometheusResults = [ordered]@{}
$nonEmptyQueries = 0
foreach ($definition in $definitions) {
    $series = @(Invoke-PrometheusRangeQuery `
        -Query $definition.query `
        -StartSeconds $startSeconds `
        -EndSeconds $endSeconds)
    if ($series.Count -gt 0) {
        $nonEmptyQueries++
    }

    $prometheusResults[$definition.name] = [ordered]@{
        query = $definition.query
        seriesCount = $series.Count
        series = $series
    }
}

$prometheusDocument = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    startAtUtc = $start.ToString('o')
    endAtUtc = $end.ToString('o')
    stepSeconds = $StepSeconds
    results = $prometheusResults
}
$prometheusPath = Join-Path $observabilityDirectory 'prometheus-range.json'
$prometheusDocument | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $prometheusPath -Encoding UTF8

$tempoUri = "$TempoBaseUrl/api/search?start=$startSeconds&end=$endSeconds&limit=100"
$tempoResponse = Invoke-RestMethod -Method Get -Uri $tempoUri
$safeTraces = @(
    @($tempoResponse.traces) | ForEach-Object {
        $durationMs = $null
        $durationProperty = $_.PSObject.Properties['durationMs']
        if ($null -ne $durationProperty) {
            $durationMs = $durationProperty.Value
        }

        [ordered]@{
            traceId = $_.traceID
            rootServiceName = $_.rootServiceName
            rootTraceName = $_.rootTraceName
            startTimeUnixNano = $_.startTimeUnixNano
            durationMs = $durationMs
        }
    }
)
$tempoDocument = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    startAtUtc = $start.ToString('o')
    endAtUtc = $end.ToString('o')
    traces = $safeTraces
}
$tempoPath = Join-Path $observabilityDirectory 'tempo-trace-index.json'
$tempoDocument | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $tempoPath -Encoding UTF8

$alertSeriesCount = $prometheusResults['alerts'].seriesCount
$summary = [ordered]@{
    schemaVersion = 1
    startAtUtc = $start.ToString('o')
    endAtUtc = $end.ToString('o')
    prometheusQueries = $definitions.Count
    nonEmptyPrometheusQueries = $nonEmptyQueries
    alertSeries = $alertSeriesCount
    traceCount = $safeTraces.Count
}
$summaryPath = Join-Path $observabilityDirectory 'export-summary.json'
$summary | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host "Failure observability exported to $observabilityDirectory."
