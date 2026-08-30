[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$otlpBase = "http://127.0.0.1:4318/v1"
$prometheusBase = "http://127.0.0.1:9090"
$lokiBase = "http://127.0.0.1:3100"
$tempoBase = "http://127.0.0.1:3200"
$smokeId = "smoke-$([DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ'))"

function New-RandomHex {
    param([int]$ByteCount)

    $bytes = [byte[]]::new($ByteCount)
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }

    return ([BitConverter]::ToString($bytes) -replace "-", "").ToLowerInvariant()
}

$traceId = New-RandomHex 16
$spanId = New-RandomHex 8
$startNano = [long]([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) * [long]1000000
$endNano = $startNano + [long]1000000

$resource = @{
    attributes = @(
        @{ key = "service.name"; value = @{ stringValue = "IndieFi.Smoke" } },
        @{ key = "service.instance.id"; value = @{ stringValue = "local-smoke" } },
        @{ key = "deployment.environment.name"; value = @{ stringValue = "local-validation" } }
    )
}
$scope = @{ name = "IndieFi.Observability.Smoke"; version = "1.0.0" }

function Send-OtlpJson {
    param(
        [string]$Signal,
        [hashtable]$Payload
    )

    $body = $Payload | ConvertTo-Json -Depth 30 -Compress
    Invoke-RestMethod `
        -Method Post `
        -Uri "$otlpBase/$Signal" `
        -ContentType "application/json" `
        -Body $body | Out-Null
}

function Wait-ForResult {
    param(
        [string]$Name,
        [scriptblock]$Probe,
        [int]$Attempts = 30
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            if (& $Probe) {
                Write-Host "${Name}: OK"
                return
            }
        }
        catch {
            if ($attempt -eq $Attempts) {
                throw
            }
        }

        Start-Sleep -Seconds 2
    }

    throw "$Name was not queryable after $Attempts attempts."
}

$tracePayload = @{
    resourceSpans = @(@{
        resource = $resource
        scopeSpans = @(@{
            scope = $scope
            spans = @(@{
                traceId = $traceId
                spanId = $spanId
                name = "observability_smoke"
                kind = 1
                startTimeUnixNano = "$startNano"
                endTimeUnixNano = "$endNano"
                attributes = @(
                    @{ key = "smoke.id"; value = @{ stringValue = $smokeId } }
                )
                status = @{ code = 1 }
            })
        })
    })
}

$metricPayload = @{
    resourceMetrics = @(@{
        resource = $resource
        scopeMetrics = @(@{
            scope = $scope
            metrics = @(@{
                name = "indiefi.smoke.metric"
                unit = "{smoke}"
                sum = @{
                    aggregationTemporality = 2
                    isMonotonic = $true
                    dataPoints = @(@{
                        startTimeUnixNano = "$startNano"
                        timeUnixNano = "$endNano"
                        asInt = "1"
                    })
                }
            })
        })
    })
}

$logPayload = @{
    resourceLogs = @(@{
        resource = $resource
        scopeLogs = @(@{
            scope = $scope
            logRecords = @(@{
                timeUnixNano = "$endNano"
                observedTimeUnixNano = "$endNano"
                severityNumber = 9
                severityText = "INFO"
                body = @{ stringValue = "indiefi-observability-smoke $smokeId" }
                traceId = $traceId
                spanId = $spanId
            })
        })
    })
}

Send-OtlpJson "traces" $tracePayload
Send-OtlpJson "metrics" $metricPayload
Send-OtlpJson "logs" $logPayload

$promQuery = [Uri]::EscapeDataString('{__name__=~"indiefi_smoke_metric.*",service_name="IndieFi.Smoke"}')
Wait-ForResult "Prometheus metric" {
    $result = Invoke-RestMethod "$prometheusBase/api/v1/query?query=$promQuery"
    return $result.status -eq "success" -and $result.data.result.Count -gt 0
}

$lokiQuery = [Uri]::EscapeDataString('{service_name="IndieFi.Smoke"} |= "indiefi-observability-smoke"')
$lokiStart = $startNano - 60000000000
$lokiEnd = [DateTimeOffset]::UtcNow.AddMinutes(1).ToUnixTimeMilliseconds() * 1000000
Wait-ForResult "Loki log" {
    $result = Invoke-RestMethod "$lokiBase/loki/api/v1/query_range?query=$lokiQuery&start=$lokiStart&end=$lokiEnd&limit=10"
    return $result.status -eq "success" -and $result.data.result.Count -gt 0
}

Wait-ForResult "Tempo trace" {
    $result = Invoke-RestMethod "$tempoBase/api/traces/$traceId"
    return $null -ne $result.batches -and $result.batches.Count -gt 0
}

Write-Host "Local OTLP smoke: PASS"
Write-Host "smoke_id=$smokeId trace_id=$traceId"
