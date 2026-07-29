# Poll a diagnostic query (summary.sql or detail.sql) while load runs, appending
# samples to a file. Read-only observer: takes no locks.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File sample.ps1                 # summary
#   powershell -NoProfile -ExecutionPolicy Bypass -File sample.ps1 -Query detail   # detail
#
# HOW IT SAMPLES: one persistent psql session inside the container re-runs the query
# with \watch. Server-side timing keeps the cadence steady even when the host is
# saturated during the burst. (An earlier version spawned a fresh `docker exec psql`
# per sample; under peak load that starved and missed the contention entirely.)

param(
    [ValidateSet('summary', 'detail')]
    [string]$Query = 'summary',
    [int]$Count = 2400,           # number of samples (2400 * 0.1s = 4 min)
    [double]$IntervalSec = 0.1,   # seconds between samples (psql \watch, server-side)
    [string]$Container = 'indiefi-db'
)

$ErrorActionPreference = 'Continue'
$sqlPath = Join-Path $PSScriptRoot "$Query.sql"
$outPath = Join-Path $PSScriptRoot "dump-$Query.txt"
$sql = Get-Content $sqlPath -Raw

# The query, then a \watch that re-runs it $Count times every $IntervalSec seconds.
$script = "$sql`n\watch i=$IntervalSec c=$Count`n"

Write-Host "polling '$Query' -> $outPath  ($Count samples @ ${IntervalSec}s, Ctrl+C to stop)"

# StreamWriter with AutoFlush so every sample hits disk immediately (survives Ctrl+C),
# and a fixed UTF-8 encoding so the dump stays clean and greppable.
$writer = [System.IO.StreamWriter]::new($outPath, $false, [System.Text.Encoding]::UTF8)
$writer.AutoFlush = $true
try {
    $writer.WriteLine("# poller=$Query start=$(Get-Date -Format o) interval=$IntervalSec")
    $script | docker exec -i $Container psql -U indiefi -d indiefi -qAt -F'|' 2>$null |
        ForEach-Object {
            Write-Host $_
            $writer.WriteLine($_)
        }
}
finally {
    $writer.Flush()
    $writer.Close()
}
