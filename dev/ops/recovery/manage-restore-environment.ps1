[CmdletBinding()]
param(
    [ValidateSet('Status', 'Down', 'Purge')][string]$Action = 'Status',
    [Parameter(Mandatory = $true)][string]$EnvironmentFile,
    [string]$Workspace,
    [switch]$ConfirmPurge
)

. "$PSScriptRoot/recovery-common.ps1"

$ErrorActionPreference = 'Stop'
$docker = Resolve-NativeTool 'docker' $null
$environment = (Resolve-Path -LiteralPath $EnvironmentFile).Path
$composeFile = Join-Path $PSScriptRoot 'portfolio-recovery-restore-compose.yml'
$runtimeEnvironment = if ($Workspace) { Join-Path $Workspace 'runtime.env' } else { $null }

$composeArguments = @(
    'compose', '--profile', 'application', '--env-file', $environment)
if ($runtimeEnvironment -and (Test-Path -LiteralPath $runtimeEnvironment)) {
    $composeArguments += @('--env-file', $runtimeEnvironment)
}
else {
    # Compose evaluates the optional Web bind even when only status/down is requested.
    $placeholder = [System.IO.Path]::GetFullPath($PSScriptRoot).Replace('\', '/')
    $env:RECOVERY_KEYS_PATH = $placeholder
}
$composeArguments += @('-f', $composeFile)

switch ($Action) {
    'Status' {
        Invoke-NativeText $docker ($composeArguments + @('ps', '-a')) | Write-Host
    }
    'Down' {
        [void](Invoke-NativeText $docker ($composeArguments + @('down', '--remove-orphans')))
        Write-Host 'Restore containers stopped; volumes and workspace preserved.'
    }
    'Purge' {
        if (-not $ConfirmPurge) {
            throw 'Purge requires -ConfirmPurge.'
        }
        [void](Invoke-NativeText $docker ($composeArguments + @('down', '--volumes', '--remove-orphans')))

        if ($Workspace) {
            $resolvedWorkspace = (Resolve-Path -LiteralPath $Workspace).Path
            $markerPath = Join-Path $resolvedWorkspace '.indiefi-recovery-workspace'
            if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
                throw "Workspace marker is missing; refusing recursive deletion: $resolvedWorkspace"
            }
            $marker = Get-Content -Raw -Encoding utf8 $markerPath | ConvertFrom-Json
            if ([System.IO.Path]::GetFullPath([string]$marker.workspace) -ne
                [System.IO.Path]::GetFullPath($resolvedWorkspace)) {
                throw "Workspace marker does not match deletion target."
            }
            $workspaceItem = Get-Item -LiteralPath $resolvedWorkspace -Force
            if ($workspaceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Workspace is a reparse point; refusing recursive deletion."
            }
            Remove-Item -LiteralPath $resolvedWorkspace -Recurse -Force
            Write-Host "Removed plaintext restore workspace: $resolvedWorkspace"
        }
        Write-Host 'Removed isolated restore containers and volumes.'
    }
}
