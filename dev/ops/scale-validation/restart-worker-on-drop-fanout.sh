#!/usr/bin/env bash
set -Eeuo pipefail

run_id="${1:-}"
drop_id="${2:-}"
output_file="${3:-}"
timeout_seconds="${4:-420}"
manager=/usr/local/lib/indiefi/scale-validation/manage-scale-processes.sh

if [[ ! "$run_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ \
    || ! "$drop_id" =~ ^[1-9][0-9]*$ \
    || "$output_file" != /* \
    || ! "$timeout_seconds" =~ ^[1-9][0-9]*$ \
    || "$timeout_seconds" -gt 900 ]]; then
  echo 'Usage: restart-worker-on-drop-fanout.sh <run-id> <drop-id> <absolute-output-file> [timeout<=900]' >&2
  exit 64
fi

[[ -x "$manager" ]] || { echo 'The scale process manager is unavailable.' >&2; exit 69; }
[[ "$(docker inspect --format '{{.State.Status}}' indiefi-db 2>/dev/null)" == running ]] \
  || { echo 'indiefi-db must be running.' >&2; exit 69; }

install -d -m 0700 "$(dirname -- "$output_file")"
umask 077
temporary_file="${output_file}.tmp.$$"
trap 'rm -f -- "$temporary_file"' EXIT

deadline=$((SECONDS + timeout_seconds))
while (( SECONDS < deadline )); do
  staged="$(docker exec indiefi-db sh -ec '
    export PGPASSWORD="$POSTGRES_PASSWORD"
    exec psql --no-psqlrc -X -qAt -v ON_ERROR_STOP=1 \
      -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
      -c "SELECT open_notification_staged_at IS NOT NULL FROM key_drops WHERE id = ${1};"
  ' sh "$drop_id")"

  if [[ "$staged" == t ]]; then
    before_utc="$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)"
    before_pid="$(systemctl show indiefi-scale-worker@1.service --property=MainPID --value)"
    sudo "$manager" restart-worker-1 "$run_id" "RESTART-WORKER-1-$run_id"
    after_utc="$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)"
    after_pid="$(systemctl show indiefi-scale-worker@1.service --property=MainPID --value)"
    {
      printf 'run_id=%s\n' "$run_id"
      printf 'drop_id=%s\n' "$drop_id"
      printf 'fanout_detected=true\n'
      printf 'restart_requested_at_utc=%s\n' "$before_utc"
      printf 'restart_ready_at_utc=%s\n' "$after_utc"
      printf 'worker_1_pid_before=%s\n' "$before_pid"
      printf 'worker_1_pid_after=%s\n' "$after_pid"
    } >"$temporary_file"
    mv -- "$temporary_file" "$output_file"
    chmod 0600 "$output_file"
    echo "Worker 1 restart evidence saved to $output_file."
    exit 0
  fi
  sleep 0.5
done

echo "The drop fan-out was not staged within ${timeout_seconds}s." >&2
exit 2
