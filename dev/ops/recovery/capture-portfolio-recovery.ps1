[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServerAddress,
    [Parameter(Mandatory = $true)][string]$SshIdentityPath,
    [Parameter(Mandatory = $true)][string]$BackupRoot,
    [Parameter(Mandatory = $true)][string]$AgeRecipient,
    [Parameter(Mandatory = $true)][string]$R2Endpoint,
    [Parameter(Mandatory = $true)][string]$R2Bucket,
    [Parameter(Mandatory = $true)][string]$R2CredentialsPath,
    [string]$SshUser = "ubuntu",
    [string]$PostgresContainer = "indiefi-db",
    [string]$PostgresDatabase = "indiefi",
    [string]$PostgresUser = "indiefi",
    [string]$AgePath,
    [string]$AwsPath,
    [string]$SshPath,
    [string]$TarPath,
    [switch]$ConfirmWriteIngressControlled,
    [switch]$ConfirmQueuesConverged,
    [switch]$PlanOnly
)

. "$PSScriptRoot/recovery-common.ps1"

$ErrorActionPreference = "Stop"
$temporaryDirectory = $null
$previousAwsAccessKey = $env:AWS_ACCESS_KEY_ID
$previousAwsSecretKey = $env:AWS_SECRET_ACCESS_KEY
$previousAwsRegion = $env:AWS_DEFAULT_REGION
$previousMetadataDisabled = $env:AWS_EC2_METADATA_DISABLED

function Get-ToolOrPlaceholder {
    param([string]$Name, [string]$ExplicitPath)
    try {
        return Resolve-NativeTool $Name $ExplicitPath
    }
    catch {
        if ($PlanOnly) {
            return "<missing:$Name>"
        }
        throw
    }
}

function Invoke-SshText {
    param([string]$Command)
    return Invoke-NativeText $script:ssh @(
        '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=yes',
        '-i', $SshIdentityPath,
        "$SshUser@$ServerAddress",
        $Command)
}

function Invoke-SshToFile {
    param([string]$Command, [string]$OutputPath)
    Invoke-NativeToFile $script:ssh @(
        '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=yes',
        '-i', $SshIdentityPath,
        "$SshUser@$ServerAddress",
        $Command) $OutputPath
}

function Invoke-RemoteSqlText {
    param([Parameter(Mandatory = $true)][string]$Sql)

    $encodedSql = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Sql))
    return Invoke-SshText (
        "printf '%s' '$encodedSql' | base64 -d | " +
        "sudo -n docker exec -i $PostgresContainer psql " +
        "--username=$PostgresUser --dbname=$PostgresDatabase " +
        "--no-psqlrc --tuples-only --no-align --file=-")
}

try {
    $script:ssh = Get-ToolOrPlaceholder 'ssh' $SshPath
    $aws = Get-ToolOrPlaceholder 'aws' $AwsPath
    $age = Get-ToolOrPlaceholder 'age' $AgePath
    $tar = Get-ToolOrPlaceholder 'tar' $TarPath

    if ($AgeRecipient -notmatch '^age1[0-9a-z]+$') {
        throw "AgeRecipient must be a native public recipient beginning with age1."
    }
    if (-not [Uri]::IsWellFormedUriString($R2Endpoint, [UriKind]::Absolute) -or
        ([Uri]$R2Endpoint).Scheme -ne 'https') {
        throw "R2Endpoint must be an absolute HTTPS S3 API endpoint."
    }
    if ($R2Bucket -notmatch '^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$') {
        throw "R2Bucket has an invalid name."
    }

    $backupRootFull = [System.IO.Path]::GetFullPath($BackupRoot)
    $identityFull = [System.IO.Path]::GetFullPath($SshIdentityPath)
    $credentialsFull = [System.IO.Path]::GetFullPath($R2CredentialsPath)
    if ($backupRootFull.StartsWith($PSScriptRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "BackupRoot must be outside the repository recovery tool directory."
    }

    if ($PlanOnly) {
        [pscustomobject]@{
            server = "$SshUser@$ServerAddress"
            backupRoot = $backupRootFull
            r2EndpointHost = ([Uri]$R2Endpoint).Host
            r2Bucket = $R2Bucket
            ssh = $script:ssh
            aws = $aws
            age = $age
            tar = $tar
            remotePlaintextDump = $false
            requiresWriteIngressControl = $true
            requiresQueueConvergence = $true
        } | Format-List
        return
    }

    if (-not $ConfirmWriteIngressControlled -or -not $ConfirmQueuesConverged) {
        throw "Actual capture requires both write-ingress and queue-convergence confirmations."
    }
    $ageVersionText = (Invoke-NativeText $age @('--version')).Trim()
    if ($ageVersionText -notmatch '^v?(\d+\.\d+\.\d+)$' -or
        [version]$Matches[1] -lt [version]'1.3.2') {
        throw "age 1.3.2 or newer is required; found: $ageVersionText"
    }
    foreach ($requiredPath in @($identityFull, $credentialsFull)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required local file does not exist: $requiredPath"
        }
    }
    if (-not (Test-Path -LiteralPath $backupRootFull)) {
        [void](New-Item -ItemType Directory -Path $backupRootFull)
    }

    $captureStarted = [DateTimeOffset]::UtcNow
    $remoteRelease = (Invoke-SshText 'readlink -f /opt/indiefi/current').Trim()
    if ($remoteRelease -notmatch '^/opt/indiefi/releases/([0-9a-f]{40})$') {
        throw "Remote release path is not a full Git commit release: $remoteRelease"
    }
    $commit = $Matches[1]
    $runId = "recovery-$($captureStarted.ToString('yyyyMMddTHHmmssZ'))-$($commit.Substring(0, 8))"
    $runDirectory = Join-Path $backupRootFull $runId
    if (Test-Path -LiteralPath $runDirectory) {
        throw "Recovery run already exists: $runDirectory"
    }
    [void](New-Item -ItemType Directory -Path $runDirectory)

    $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "$runId-$([Guid]::NewGuid().ToString('N'))"
    $bundleRoot = Join-Path $temporaryDirectory 'bundle'
    $postgresRoot = Join-Path $bundleRoot 'postgresql'
    $r2Root = Join-Path $bundleRoot 'r2'
    $r2Objects = Join-Path $r2Root 'objects'
    $keysRoot = Join-Path $bundleRoot 'keys'
    $configurationRoot = Join-Path $bundleRoot 'configuration'
    foreach ($path in @($postgresRoot, $r2Objects, $keysRoot, $configurationRoot)) {
        [void](New-Item -ItemType Directory -Path $path -Force)
    }

    $dumpPath = Join-Path $postgresRoot 'indiefi.dump'
    Invoke-SshToFile (
        "sudo -n docker exec $PostgresContainer pg_dump " +
        "--username=$PostgresUser --dbname=$PostgresDatabase " +
        "--format=custom --no-owner") $dumpPath

    $dumpListPath = Join-Path $postgresRoot 'dump-list.txt'
    $docker = Resolve-NativeTool 'docker' $null
    Invoke-NativeToFile $docker @(
        'run', '--rm', '-v', "${postgresRoot}:/backup:ro",
        'postgres:18', 'pg_restore', '--list', '/backup/indiefi.dump') $dumpListPath

    $rowCountsJson = Invoke-RemoteSqlText @'
SELECT jsonb_build_object(
    'users', (SELECT count(*) FROM users),
    'games', (SELECT count(*) FROM games),
    'game_keys', (SELECT count(*) FROM game_keys),
    'key_grants', (SELECT count(*) FROM key_grants),
    'media_assets', (SELECT count(*) FROM media_assets),
    'data_protection_keys', (SELECT count(*) FROM data_protection_keys),
    'message_outbox_messages', (SELECT count(*) FROM message_outbox_messages));
'@
    $rowCounts = $rowCountsJson.Trim() | ConvertFrom-Json
    Write-Utf8Json $rowCounts (Join-Path $postgresRoot 'row-counts.json')

    $postgresVersion = (Invoke-RemoteSqlText 'SHOW server_version;').Trim()
    $pgDumpVersion = (Invoke-SshText (
        "sudo -n docker exec $PostgresContainer pg_dump --version")).Trim()

    $keysTar = Join-Path $temporaryDirectory 'keys.tar'
    Invoke-SshToFile (
        "sudo -n tar -C /etc/indiefi/keys -cf - " +
        "account-protection.json data-protection.crt data-protection.key") $keysTar
    [void](Invoke-NativeText $tar @('-xf', $keysTar, '-C', $keysRoot))
    Remove-Item -LiteralPath $keysTar -Force

    $configurationFingerprints = Invoke-SshText (
        "sudo -n sha256sum /etc/indiefi/infra.env /etc/indiefi/web.env " +
        "/etc/indiefi/worker.env")
    [System.IO.File]::WriteAllText(
        (Join-Path $configurationRoot 'fingerprints.txt'),
        $configurationFingerprints.Trim() + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false)))

    $credentials = Read-KeyValueFile $credentialsFull
    foreach ($name in @('AWS_ACCESS_KEY_ID', 'AWS_SECRET_ACCESS_KEY')) {
        if (-not $credentials.ContainsKey($name)) {
            throw "Missing $name in R2 credentials file."
        }
    }
    $env:AWS_ACCESS_KEY_ID = $credentials['AWS_ACCESS_KEY_ID']
    $env:AWS_SECRET_ACCESS_KEY = $credentials['AWS_SECRET_ACCESS_KEY']
    $env:AWS_DEFAULT_REGION = 'auto'
    $env:AWS_EC2_METADATA_DISABLED = 'true'

    $inventoryText = Invoke-NativeText $aws @(
        '--no-cli-pager', '--endpoint-url', $R2Endpoint,
        's3api', 'list-objects-v2', '--bucket', $R2Bucket, '--output', 'json')
    $inventory = $inventoryText | ConvertFrom-Json
    $objects = @($inventory.Contents)
    foreach ($object in $objects) {
        $key = [string]$object.Key
        $valid = $key -match '^staging/[0-9a-f]{32}/[0-9a-f]{32}$' -or
            $key -match '^sources/[0-9a-f]{32}/[1-9][0-9]*$' -or
            $key -match '^derived/[0-9a-f]{32}/[1-9][0-9]*/(game-display|game-list|profile-display|profile-thumbnail)\.webp$'
        if (-not $valid) {
            throw "R2 contains an object outside the canonical media key space: $key"
        }
    }
    Write-Utf8Json $inventory (Join-Path $r2Root 'inventory.json') 20

    [void](Invoke-NativeText $aws @(
        '--no-cli-pager', '--endpoint-url', $R2Endpoint,
        's3', 'sync', "s3://$R2Bucket", $r2Objects,
        '--only-show-errors', '--no-progress'))
    $localObjectFiles = @(Get-ChildItem -LiteralPath $r2Objects -File -Recurse)
    if ($localObjectFiles.Count -ne $objects.Count) {
        throw "R2 object count mismatch: inventory=$($objects.Count), local=$($localObjectFiles.Count)"
    }
    foreach ($object in $objects) {
        $localObjectPath = Join-Path $r2Objects ([string]$object.Key).Replace('/', '\')
        if (-not (Test-Path -LiteralPath $localObjectPath -PathType Leaf) -or
            (Get-Item -LiteralPath $localObjectPath).Length -ne [long]$object.Size) {
            throw "R2 object size mismatch after download: $($object.Key)"
        }
    }

    $accountKey = Get-Content -Raw -Encoding utf8 (Join-Path $keysRoot 'account-protection.json') | ConvertFrom-Json
    $purposeIds = [ordered]@{}
    foreach ($purpose in $accountKey.Purposes.PSObject.Properties) {
        $purposeIds[$purpose.Name] = @($purpose.Value.ReadKeyIds | Sort-Object -Unique)
    }
    $certificatePath = Join-Path $keysRoot 'data-protection.crt'
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certificatePath)
    $certificatePublicKey = Invoke-NativeText $docker @(
        'run', '--rm', '-v', "${keysRoot}:/keys:ro", 'postgres:18',
        'openssl', 'x509', '-in', '/keys/data-protection.crt', '-pubkey', '-noout')
    $privatePublicKey = Invoke-NativeText $docker @(
        'run', '--rm', '-v', "${keysRoot}:/keys:ro", 'postgres:18',
        'openssl', 'pkey', '-in', '/keys/data-protection.key', '-pubout')
    $certificateMatchesPrivateKey = $certificatePublicKey.Trim() -eq $privatePublicKey.Trim()
    if (-not $certificateMatchesPrivateKey) {
        throw "Data Protection certificate and private key do not match."
    }

    $expectedObjectSql = @'
WITH expected AS (
    SELECT
        'sources/' || replace(id::text, '-', '') || '/' || revision::text AS object_key
    FROM media_assets
    WHERE status IN ('ProcessingPending', 'Processing', 'Ready')

    UNION ALL

    SELECT
        'derived/' || replace(media.id::text, '-', '') || '/' ||
        media.revision::text || '/' || variants.file_name AS object_key
    FROM media_assets AS media
    CROSS JOIN LATERAL (
        SELECT configured.file_name
        FROM (VALUES
            ('GameCover', 'game-display.webp'),
            ('GameCover', 'game-list.webp'),
            ('GameScreenshot', 'game-display.webp'),
            ('GameScreenshot', 'game-list.webp'),
            ('ProfileImage', 'profile-display.webp'),
            ('ProfileImage', 'profile-thumbnail.webp')
        ) AS configured(attachment_kind, file_name)
        WHERE configured.attachment_kind = media.attachment_kind
    ) AS variants
    WHERE media.status = 'Ready'
)
SELECT object_key FROM expected ORDER BY object_key;
'@
    $expectedObjectText = (Invoke-RemoteSqlText $expectedObjectSql).Trim()
    $expectedObjectKeys = if ($expectedObjectText) {
        @($expectedObjectText -split "`r?`n")
    }
    else {
        @()
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $r2Root 'expected-active-objects.txt'),
        (($expectedObjectKeys -join "`n") + $(if ($expectedObjectKeys.Count) { "`n" } else { "" })),
        (New-Object System.Text.UTF8Encoding($false)))
    $inventoryKeySet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($object in $objects) {
        [void]$inventoryKeySet.Add([string]$object.Key)
    }
    $missingObjectKeys = @($expectedObjectKeys | Where-Object { -not $inventoryKeySet.Contains($_) })
    if ($missingObjectKeys.Count -gt 0) {
        throw "R2 is missing $($missingObjectKeys.Count) object(s) referenced by active media rows."
    }

    $captureCompleted = [DateTimeOffset]::UtcNow
    $manifest = [ordered]@{
        formatVersion = 1
        runId = $runId
        capture = [ordered]@{
            startedUtc = $captureStarted.ToString('o')
            completedUtc = $captureCompleted.ToString('o')
            writeIngressControlled = $true
            queuesConverged = $true
        }
        release = [ordered]@{ commit = $commit; path = $remoteRelease }
        postgresql = [ordered]@{
            serverVersion = $postgresVersion
            pgDumpVersion = $pgDumpVersion
            database = $PostgresDatabase
            format = 'custom'
            rowCounts = $rowCounts
        }
        r2 = [ordered]@{
            endpointHost = ([Uri]$R2Endpoint).Host
            bucket = $R2Bucket
            objectCount = $objects.Count
            totalBytes = [long](($objects | Measure-Object -Property Size -Sum).Sum)
            activeReferenceCount = $expectedObjectKeys.Count
            activeReferencesPresent = $true
        }
        protection = [ordered]@{
            accountPurposeKeyIds = $purposeIds
            dataProtectionCertificateSha256 = (Get-FileHash $certificatePath -Algorithm SHA256).Hash.ToLowerInvariant()
            dataProtectionCertificateNotAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('o')
            certificateMatchesPrivateKey = $certificateMatchesPrivateKey
        }
        files = @()
    }
    $manifest.files = Get-RelativeFileInventory $bundleRoot @('manifest.json')
    Write-Utf8Json $manifest (Join-Path $bundleRoot 'manifest.json') 24

    $plainTar = Join-Path $temporaryDirectory 'bundle.tar'
    [void](Invoke-NativeText $tar @('-cf', $plainTar, '-C', $temporaryDirectory, 'bundle'))
    $encryptedBundle = Join-Path $runDirectory 'bundle.tar.age'
    [void](Invoke-NativeText $age @('-r', $AgeRecipient, '-o', $encryptedBundle, $plainTar))

    $archiveHash = (Get-FileHash -LiteralPath $encryptedBundle -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        (Join-Path $runDirectory 'bundle.tar.age.sha256'),
        "$archiveHash  bundle.tar.age`n",
        (New-Object System.Text.UTF8Encoding($false)))
    $evidence = [ordered]@{
        formatVersion = 1
        runId = $runId
        captureCompletedUtc = $captureCompleted.ToString('o')
        releaseCommit = $commit
        postgresqlVersion = $postgresVersion
        r2EndpointHost = ([Uri]$R2Endpoint).Host
        r2Bucket = $R2Bucket
        r2ObjectCount = $objects.Count
        encryptedBytes = (Get-Item -LiteralPath $encryptedBundle).Length
        encryptedSha256 = $archiveHash
        verified = $false
    }
    Write-Utf8Json $evidence (Join-Path $runDirectory 'evidence.json')

    Write-Host "Encrypted recovery capture created: $runDirectory"
    Write-Host "Run verify-recovery-bundle.ps1 before stopping EC2."
}
finally {
    $env:AWS_ACCESS_KEY_ID = $previousAwsAccessKey
    $env:AWS_SECRET_ACCESS_KEY = $previousAwsSecretKey
    $env:AWS_DEFAULT_REGION = $previousAwsRegion
    $env:AWS_EC2_METADATA_DISABLED = $previousMetadataDisabled

    if ($temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
