[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $EnvironmentFile,

    [string] $WebProject = "dev/src/IndieFi.Web",

    [string] $WorkerProject = "dev/src/IndieFi.Worker"
)

$ErrorActionPreference = "Stop"

function Stop-ConfigurationCheck {
    param([string] $Message)

    [Console]::Error.WriteLine("[local configuration is incomplete]")
    [Console]::Error.WriteLine("  $Message")
    exit 1
}

function Get-EnvironmentValues {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Stop-ConfigurationCheck "Environment file '$Path' was not found."
    }

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith("#")) {
            continue
        }

        $parts = $line -split "=", 2
        if ($parts.Count -eq 2) {
            $values[$parts[0].Trim()] = $parts[1]
        }
    }

    return $values
}

function Get-UserSecretValues {
    param(
        [string] $Project,
        [string] $Label
    )

    $output = & dotnet user-secrets list --json --project $Project 2>$null
    if ($LASTEXITCODE -ne 0) {
        Stop-ConfigurationCheck "Could not read the $Label user-secrets store."
    }

    $json = ($output | Where-Object { $_ -notmatch '^//(BEGIN|END)$' }) -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($json)) {
        Stop-ConfigurationCheck "The $Label user-secrets store is empty."
    }

    try {
        $document = ConvertFrom-Json $json
    }
    catch {
        Stop-ConfigurationCheck "The $Label user-secrets store could not be parsed."
    }

    $values = @{}
    foreach ($property in $document.PSObject.Properties) {
        $values[$property.Name] = [string] $property.Value
    }

    return $values
}

function Require-Value {
    param(
        [hashtable] $Values,
        [string] $Name,
        [string] $Label
    )

    if (-not $Values.ContainsKey($Name) -or [string]::IsNullOrWhiteSpace($Values[$Name])) {
        Stop-ConfigurationCheck "$Label is missing '$Name'."
    }
}

function Get-IndexedValues {
    param(
        [hashtable] $Values,
        [string] $Prefix
    )

    $pattern = "^$([Regex]::Escape($Prefix)):(\d+)$"
    return @(
        $Values.GetEnumerator() |
            Where-Object { $_.Key -match $pattern -and -not [string]::IsNullOrWhiteSpace($_.Value) } |
            Sort-Object { [int] ([Regex]::Match($_.Key, $pattern).Groups[1].Value) } |
            ForEach-Object { [string] $_.Value }
    )
}

function Test-ProtectionConfiguration {
    param(
        [hashtable] $Values,
        [string] $Label
    )

    $purposes = @(
        "account-email-encryption",
        "account-email-lookup",
        "account-email-message-encryption",
        "account-request-ip-lookup",
        "game-key-encryption",
        "game-key-fingerprint"
    )
    $seenKeyMaterial = @{}

    foreach ($purpose in $purposes) {
        $prefix = "AccountProtection:Purposes:$purpose"
        $currentKeyName = "${prefix}:CurrentKeyId"
        Require-Value $Values $currentKeyName $Label

        $currentKeyId = $Values[$currentKeyName]
        $readKeyIds = Get-IndexedValues $Values "${prefix}:ReadKeyIds"
        $writeKeyIds = Get-IndexedValues $Values "${prefix}:WriteKeyIds"

        if ($readKeyIds.Count -eq 0) {
            Stop-ConfigurationCheck "$Label needs at least one readable key for '$purpose'."
        }
        if ($writeKeyIds.Count -eq 0) {
            Stop-ConfigurationCheck "$Label needs at least one writable key for '$purpose'."
        }
        if ($currentKeyId -notin $readKeyIds -or $currentKeyId -notin $writeKeyIds) {
            Stop-ConfigurationCheck "$Label current key for '$purpose' must be readable and writable."
        }

        $allKeyIds = @()
        $allKeyIds += $readKeyIds
        $allKeyIds += $writeKeyIds
        $allKeyIds += $currentKeyId
        $requiredKeyIds = @($allKeyIds | Sort-Object -Unique)
        foreach ($keyId in $requiredKeyIds) {
            $keyName = "${prefix}:Keys:$keyId"
            Require-Value $Values $keyName $Label

            try {
                $keyBytes = [Convert]::FromBase64String($Values[$keyName])
            }
            catch {
                Stop-ConfigurationCheck "$Label key '$keyId' for '$purpose' is not valid Base64."
            }

            try {
                if ($keyBytes.Length -ne 32) {
                    Stop-ConfigurationCheck "$Label key '$keyId' for '$purpose' must contain 32 bytes."
                }

                $canonicalKey = [Convert]::ToBase64String($keyBytes)
                if ($seenKeyMaterial.ContainsKey($canonicalKey) -and
                    $seenKeyMaterial[$canonicalKey] -ne $purpose) {
                    Stop-ConfigurationCheck "$Label protection purposes must not share key material."
                }
                $seenKeyMaterial[$canonicalKey] = $purpose
            }
            finally {
                [Array]::Clear($keyBytes, 0, $keyBytes.Length)
            }
        }
    }
}

function Require-MatchingValue {
    param(
        [hashtable] $First,
        [hashtable] $Second,
        [string] $Name,
        [string] $Description
    )

    Require-Value $First $Name "Web user secrets"
    Require-Value $Second $Name "Worker user secrets"
    if ($First[$Name] -cne $Second[$Name]) {
        Stop-ConfigurationCheck "$Description must match between Web and Worker user secrets."
    }
}

try {
    $environment = Get-EnvironmentValues $EnvironmentFile
    $webSecrets = Get-UserSecretValues $WebProject "Web"
    $workerSecrets = Get-UserSecretValues $WorkerProject "Worker"

    foreach ($name in @(
        "POSTGRES_PASSWORD",
        "RABBITMQ_PASSWORD",
        "MINIO_ROOT_PASSWORD",
        "MINIO_APP_PASSWORD"
    )) {
        Require-Value $environment $name "dev/.env"
    }

    foreach ($name in @(
        "ConnectionStrings:Database",
        "Messaging:RabbitMq:Password",
        "Media:ObjectStorage:SecretAccessKey"
    )) {
        Require-MatchingValue $webSecrets $workerSecrets $name $name
    }

    Test-ProtectionConfiguration $webSecrets "Web user secrets"
    Test-ProtectionConfiguration $workerSecrets "Worker user secrets"

    $webProtection = @($webSecrets.Keys | Where-Object { $_ -like "AccountProtection:Purposes:*" })
    $workerProtection = @($workerSecrets.Keys | Where-Object { $_ -like "AccountProtection:Purposes:*" })
    if ($webProtection.Count -ne $workerProtection.Count) {
        Stop-ConfigurationCheck "AccountProtection settings must match between Web and Worker."
    }
    foreach ($name in $webProtection) {
        if (-not $workerSecrets.ContainsKey($name) -or $webSecrets[$name] -cne $workerSecrets[$name]) {
            Stop-ConfigurationCheck "AccountProtection settings must match between Web and Worker."
        }
    }

    if ($webSecrets["Messaging:RabbitMq:Password"] -cne $environment["RABBITMQ_PASSWORD"]) {
        Stop-ConfigurationCheck "RABBITMQ_PASSWORD must match the Web and Worker user secret."
    }
    if ($webSecrets["Media:ObjectStorage:SecretAccessKey"] -cne $environment["MINIO_APP_PASSWORD"]) {
        Stop-ConfigurationCheck "MINIO_APP_PASSWORD must match the Web and Worker user secret."
    }

    Write-Host "[local user secrets are ready]"
    exit 0
}
catch {
    Stop-ConfigurationCheck "The local configuration check failed without exposing secret values."
}
