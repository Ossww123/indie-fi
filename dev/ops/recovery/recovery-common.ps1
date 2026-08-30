$ErrorActionPreference = "Stop"

function Resolve-NativeTool {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$ExplicitPath
    )

    if ($ExplicitPath) {
        $resolved = Resolve-Path -LiteralPath $ExplicitPath -ErrorAction Stop
        return $resolved.Path
    }

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "Required command is missing: $Name"
    }

    return $command.Source
}

function ConvertTo-NativeArgumentString {
    param([string[]]$Arguments)

    $quoted = foreach ($argument in $Arguments) {
        if ($null -eq $argument -or $argument.Length -eq 0) {
            '""'
            continue
        }

        if ($argument -notmatch '[\s"]') {
            $argument
            continue
        }

        $builder = New-Object System.Text.StringBuilder
        [void]$builder.Append('"')
        $backslashes = 0
        foreach ($character in $argument.ToCharArray()) {
            if ($character -eq '\') {
                $backslashes++
                continue
            }

            if ($character -eq '"') {
                [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
                [void]$builder.Append('"')
                $backslashes = 0
                continue
            }

            if ($backslashes -gt 0) {
                [void]$builder.Append(('\' * $backslashes))
                $backslashes = 0
            }
            [void]$builder.Append($character)
        }

        if ($backslashes -gt 0) {
            [void]$builder.Append(('\' * ($backslashes * 2)))
        }
        [void]$builder.Append('"')
        $builder.ToString()
    }

    return ($quoted -join ' ')
}

function Invoke-NativeText {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = ConvertTo-NativeArgumentString $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start command: $FilePath"
        }
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $outputTask.Result
        $errorText = $errorTask.Result
        if ($process.ExitCode -ne 0) {
            throw "Command failed ($($process.ExitCode)): $FilePath`n$errorText"
        }
        if ($errorText) {
            Write-Verbose $errorText.Trim()
        }
        return $output.TrimEnd("`r", "`n")
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-NativeToFile {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = ConvertTo-NativeArgumentString $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $output = $null
    try {
        if (-not $process.Start()) {
            throw "Could not start command: $FilePath"
        }

        $errorTask = $process.StandardError.ReadToEndAsync()
        $output = [System.IO.File]::Open(
            $OutputPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $process.StandardOutput.BaseStream.CopyTo($output)
        $output.Dispose()
        $output = $null
        $process.WaitForExit()
        $errorText = $errorTask.Result

        if ($process.ExitCode -ne 0) {
            throw "Command failed ($($process.ExitCode)): $FilePath`n$errorText"
        }
    }
    finally {
        if ($output) {
            $output.Dispose()
        }
        $process.Dispose()
    }
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Depth = 12
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    [System.IO.File]::WriteAllText(
        $Path,
        $json + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false)))
}

function Get-RelativeFileInventory {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string[]]$ExcludeRelativePaths = @()
    )

    $rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\', '/')
    return @(
        Get-ChildItem -LiteralPath $rootPath -File -Recurse |
            ForEach-Object {
                $relative = $_.FullName.Substring($rootPath.Length + 1).Replace('\', '/')
                if ($ExcludeRelativePaths -contains $relative) {
                    return
                }

                [ordered]@{
                    path = $relative
                    size = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            } |
            Sort-Object { $_.path }
    )
}

function Read-KeyValueFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding utf8) {
        if (-not $line -or $line.TrimStart().StartsWith('#')) {
            continue
        }

        $separator = $line.IndexOf('=')
        if ($separator -le 0) {
            throw "Invalid key=value line in $Path"
        }
        $name = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1)
        if (-not $value) {
            throw "Missing value for $name in $Path"
        }
        $values[$name] = $value
    }
    return $values
}
