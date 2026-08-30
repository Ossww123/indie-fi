[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RunDirectory,
    [Parameter(Mandatory = $true)][string]$AgeIdentityPath,
    [Parameter(Mandatory = $true)][string]$EnvironmentFile,
    [Parameter(Mandatory = $true)][string]$WorkspaceRoot,
    [string]$AgePath,
    [string]$TarPath,
    [switch]$StartApplication
)

. "$PSScriptRoot/recovery-common.ps1"

$ErrorActionPreference = "Stop"
$composeFile = Join-Path $PSScriptRoot 'portfolio-recovery-restore-compose.yml'
$docker = Resolve-NativeTool 'docker' $null
$age = Resolve-NativeTool 'age' $AgePath
$tar = Resolve-NativeTool 'tar' $TarPath
$run = (Resolve-Path -LiteralPath $RunDirectory).Path
$identity = (Resolve-Path -LiteralPath $AgeIdentityPath).Path
$environment = (Resolve-Path -LiteralPath $EnvironmentFile).Path
$workspaceRootFull = [System.IO.Path]::GetFullPath($WorkspaceRoot)
$restoreStarted = [DateTimeOffset]::UtcNow
$workspace = $null

function Get-ComposeArguments {
    param([string[]]$Tail, [string]$RuntimeEnvironment)
    return @(
        'compose',
        '--env-file', $environment,
        '--env-file', $RuntimeEnvironment,
        '-f', $composeFile
    ) + $Tail
}

function Wait-ContainerHealthy {
    param([string]$Container, [int]$Attempts = 40)
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $state = (Invoke-NativeText $docker @(
            'inspect', '--format',
            '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}',
            $Container)).Trim()
        if ($state -eq 'healthy' -or $state -eq 'running') {
            return
        }
        if ($state -eq 'unhealthy' -or $state -eq 'exited' -or $state -eq 'dead') {
            throw "$Container entered terminal state: $state"
        }
        Start-Sleep -Seconds 2
    }
    throw "$Container did not become healthy."
}

function Invoke-RestoreSql {
    param([string]$Sql)
    return Invoke-NativeText $docker @(
        'exec', 'indiefi-recovery-db',
        'psql',
        '--username', $restoreEnvironment['RESTORE_POSTGRES_USER'],
        '--dbname', $restoreEnvironment['RESTORE_POSTGRES_DB'],
        '--no-psqlrc', '--tuples-only', '--no-align',
        '--command', $Sql)
}

try {
    & "$PSScriptRoot/verify-recovery-bundle.ps1" `
        -RunDirectory $run `
        -AgeIdentityPath $identity `
        -AgePath $age `
        -TarPath $tar | Out-Host

    $restoreEnvironment = Read-KeyValueFile $environment
    foreach ($name in @(
        'RESTORE_POSTGRES_USER', 'RESTORE_POSTGRES_PASSWORD',
        'RESTORE_POSTGRES_DB', 'RESTORE_MINIO_ROOT_USER',
        'RESTORE_MINIO_ROOT_PASSWORD', 'RESTORE_BUCKET')) {
        if (-not $restoreEnvironment.ContainsKey($name)) {
            throw "Missing $name in restore environment file."
        }
    }
    if ($restoreEnvironment['RESTORE_POSTGRES_PASSWORD'] -eq
        $restoreEnvironment['RESTORE_MINIO_ROOT_PASSWORD']) {
        throw "PostgreSQL and MinIO restore passwords must differ."
    }

    if (-not (Test-Path -LiteralPath $workspaceRootFull)) {
        [void](New-Item -ItemType Directory -Path $workspaceRootFull)
    }
    $evidence = Get-Content -Raw -Encoding utf8 (Join-Path $run 'evidence.json') | ConvertFrom-Json
    $workspace = Join-Path $workspaceRootFull ([string]$evidence.runId)
    if (Test-Path -LiteralPath $workspace) {
        throw "Restore workspace already exists: $workspace"
    }
    [void](New-Item -ItemType Directory -Path $workspace)
    Write-Utf8Json ([ordered]@{
        formatVersion = 1
        workspace = [System.IO.Path]::GetFullPath($workspace)
        createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
        sourceRun = $run
    }) (Join-Path $workspace '.indiefi-recovery-workspace')

    $plainTar = Join-Path $workspace 'bundle.tar'
    [void](Invoke-NativeText $age @(
        '-d', '-i', $identity, '-o', $plainTar, (Join-Path $run 'bundle.tar.age')))
    [void](Invoke-NativeText $tar @('-xf', $plainTar, '-C', $workspace))
    Remove-Item -LiteralPath $plainTar -Force

    $bundleRoot = Join-Path $workspace 'bundle'
    $manifest = Get-Content -Raw -Encoding utf8 (Join-Path $bundleRoot 'manifest.json') | ConvertFrom-Json
    $keysPath = (Resolve-Path -LiteralPath (Join-Path $bundleRoot 'keys')).Path.Replace('\', '/')
    $runtimeEnvironment = Join-Path $workspace 'runtime.env'
    [System.IO.File]::WriteAllText(
        $runtimeEnvironment,
        "RECOVERY_KEYS_PATH=$keysPath`n",
        (New-Object System.Text.UTF8Encoding($false)))

    $existing = Invoke-NativeText $docker (Get-ComposeArguments @('ps', '-aq') $runtimeEnvironment)
    if ($existing.Trim()) {
        throw "The isolated restore Compose project is not empty. Purge it before restoring."
    }
    [void](Invoke-NativeText $docker (Get-ComposeArguments @('up', '-d', 'db', 'minio') $runtimeEnvironment))
    Wait-ContainerHealthy 'indiefi-recovery-db'
    Wait-ContainerHealthy 'indiefi-recovery-minio'

    $dumpPath = Join-Path $bundleRoot 'postgresql/indiefi.dump'
    [void](Invoke-NativeFromFile $docker @(
        'exec', '-i', 'indiefi-recovery-db',
        'pg_restore',
        '--username', $restoreEnvironment['RESTORE_POSTGRES_USER'],
        '--dbname', $restoreEnvironment['RESTORE_POSTGRES_DB'],
        '--exit-on-error', '--single-transaction', '--no-owner', '--no-acl') $dumpPath)

    $mcImage = 'quay.io/minio/mc:RELEASE.2025-08-13T08-35-41Z'
    $objectRoot = (Resolve-Path -LiteralPath (Join-Path $bundleRoot 'r2/objects')).Path
    $mcBase = @(
        'run', '--rm',
        '--network', 'indiefi-recovery-restore_default',
        '--env-file', $environment,
        '-v', "${objectRoot}:/objects:ro",
        '--entrypoint', '/bin/sh',
        $mcImage, '-ec')
    $mcSetup = @'
mc alias set restore http://minio:9000 "$RESTORE_MINIO_ROOT_USER" "$RESTORE_MINIO_ROOT_PASSWORD" >/dev/null
mc mb --ignore-existing "restore/$RESTORE_BUCKET" >/dev/null
mc mirror --overwrite /objects "restore/$RESTORE_BUCKET" >/dev/null
'@
    [void](Invoke-NativeText $docker ($mcBase + @($mcSetup)))

    $actualRowCountsJson = Invoke-RestoreSql @'
SELECT jsonb_build_object(
    'users', (SELECT count(*) FROM users),
    'games', (SELECT count(*) FROM games),
    'game_keys', (SELECT count(*) FROM game_keys),
    'key_grants', (SELECT count(*) FROM key_grants),
    'media_assets', (SELECT count(*) FROM media_assets),
    'data_protection_keys', (SELECT count(*) FROM data_protection_keys),
    'message_outbox_messages', (SELECT count(*) FROM message_outbox_messages));
'@
    $actualRowCounts = $actualRowCountsJson.Trim() | ConvertFrom-Json
    foreach ($property in $manifest.postgresql.rowCounts.PSObject.Properties) {
        if ([long]$actualRowCounts.($property.Name) -ne [long]$property.Value) {
            throw "Restored row count mismatch: $($property.Name)"
        }
    }

    $mcList = @'
mc alias set restore http://minio:9000 "$RESTORE_MINIO_ROOT_USER" "$RESTORE_MINIO_ROOT_PASSWORD" >/dev/null
mc ls --recursive --json "restore/$RESTORE_BUCKET"
'@
    $objectListText = Invoke-NativeText $docker ($mcBase + @($mcList))
    $restoredObjects = @(
        $objectListText -split "`r?`n" |
            Where-Object { $_ } |
            ForEach-Object { $_ | ConvertFrom-Json }
    )
    if ($restoredObjects.Count -ne [int]$manifest.r2.objectCount) {
        throw "Restored object count mismatch."
    }
    $restoredBytes = [long](($restoredObjects | Measure-Object -Property size -Sum).Sum)
    if ($restoredBytes -ne [long]$manifest.r2.totalBytes) {
        throw "Restored object byte count mismatch."
    }

    $certificatePublicKey = Invoke-NativeText $docker @(
        'run', '--rm', '-v', "${keysPath}:/keys:ro", 'postgres:18',
        'openssl', 'x509', '-in', '/keys/data-protection.crt', '-pubkey', '-noout')
    $privatePublicKey = Invoke-NativeText $docker @(
        'run', '--rm', '-v', "${keysPath}:/keys:ro", 'postgres:18',
        'openssl', 'pkey', '-in', '/keys/data-protection.key', '-pubout')
    if ($certificatePublicKey.Trim() -ne $privatePublicKey.Trim()) {
        throw "Restored Data Protection certificate and private key do not match."
    }

    $readiness = 'not-requested'
    if ($StartApplication) {
        [void](Invoke-NativeText $docker (
            Get-ComposeArguments @('--profile', 'application', 'up', '-d', '--build', 'web') $runtimeEnvironment))
        $readinessProbe = @'
exec 3<>/dev/tcp/127.0.0.1/8080
printf "GET /health/ready HTTP/1.0\r\nHost: localhost\r\nX-Forwarded-For: 127.0.0.1\r\nX-Forwarded-Proto: https\r\n\r\n" >&3
timeout 3 cat <&3
'@
        for ($attempt = 1; $attempt -le 60; $attempt++) {
            try {
                $response = Invoke-NativeText $docker @(
                    'exec', 'indiefi-recovery-web', '/bin/bash', '-ec', $readinessProbe)
                if ($response -match '^HTTP/1\.[01] 200 ') {
                    $readiness = 'pass'
                    break
                }
            }
            catch {
                Start-Sleep -Seconds 2
            }
        }
        if ($readiness -ne 'pass') {
            throw "Restored Web did not become ready at http://127.0.0.1:5500/health/ready."
        }
    }

    $restoreCompleted = [DateTimeOffset]::UtcNow
    $result = [ordered]@{
        formatVersion = 1
        runId = $manifest.runId
        startedUtc = $restoreStarted.ToString('o')
        completedUtc = $restoreCompleted.ToString('o')
        elapsedSeconds = [Math]::Round(($restoreCompleted - $restoreStarted).TotalSeconds, 3)
        rowCountsMatch = $true
        objectCount = $restoredObjects.Count
        objectBytes = $restoredBytes
        protectionKeyPairMatches = $true
        applicationReadiness = $readiness
        result = 'pass'
    }
    Write-Utf8Json $result (Join-Path $workspace 'restore-result.json')
    $result | Format-List
    Write-Host "Isolated recovery restore: PASS"
    Write-Host "Workspace: $workspace"
    Write-Host "Purge the environment and plaintext workspace after user-flow verification."
}
catch {
    if ($workspace -and (Test-Path -LiteralPath $workspace)) {
        Write-Host "Restore failed. Inspect before purge: $workspace" -ForegroundColor Red
    }
    throw
}
