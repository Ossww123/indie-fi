#!/usr/bin/env bash
# Counts key-claim outcomes for the current drop from the app log.
# The app must have been run with its output tee'd to this log (see README).
# Usage: bash dev/loadtest/tally.sh [path-to-app.log]
set -euo pipefail

LOG="${1:-dev/loadtest/app.log}"
DROP=$(grep -oP '"dropId":\s*\K\d+' dev/loadtest/drop.json)

echo "drop $DROP claim outcomes:"
# Filter by this drop so a reused (appended) log still tallies only this run.
grep "drop=$DROP " "$LOG" | grep -oP 'outcome=\K\w+' | sort | uniq -c
