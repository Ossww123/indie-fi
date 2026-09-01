#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output_directory="${1:-}"
phase="${2:-}"
since_utc="${3:-}"
drop_id="${4:-}"

if [[ -z "$output_directory" || "$output_directory" != /* \
    || ! "$phase" =~ ^(before|fault|recovered|cooldown)$ \
    || ! "$since_utc" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ \
    || (-n "$drop_id" && ! "$drop_id" =~ ^[1-9][0-9]*$) ]]; then
  echo 'Usage: capture-failure-snapshot.sh <absolute-output-directory> <before|fault|recovered|cooldown> <since-utc> [positive-drop-id]' >&2
  exit 1
fi

phase_directory="${output_directory}/${phase}"
install -d -m 0700 "$phase_directory"
umask 077

capture_or_mark_unavailable() {
  local name="$1"
  shift
  if ! "$@" >"${phase_directory}/${name}.command.txt" 2>&1; then
    printf 'capture_status\tunavailable\ncaptured_at_utc\t%s\n' \
      "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
      >"${phase_directory}/${name}.unavailable.tsv"
  fi
}

{
  printf 'captured_at_utc\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf 'phase\t%s\n' "$phase"
  printf 'release\t%s\n' "$(readlink -f /opt/indiefi/current 2>/dev/null || echo unavailable)"
  printf '\n[systemd]\n'
  systemctl show indiefi-web.service indiefi-worker.service \
    --property=Id,ActiveState,SubState,MainPID,NRestarts,ExecMainStatus
  printf '\n[containers]\n'
  docker inspect \
    --format '{{.Name}} status={{.State.Status}} health={{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} restart={{.RestartCount}}' \
    indiefi-db indiefi-redis indiefi-rabbitmq
  printf '\n[resources]\n'
  uptime
  free -h
  df -h /
  printf '\n[ports]\n'
  ss -lnt | grep -E '(:80|:443|:5000|:5432|:6379|:5672)' || true
  printf '\n[temporary_drop_ins]\n'
  find /run/systemd/system -maxdepth 3 -type f \
    -name '90-indiefi-failure-drill.conf' -print 2>/dev/null || true
} >"${phase_directory}/system.txt" 2>&1

readiness_host="$({
  awk '$1 == "server_name" { gsub(/;/, "", $2); print $2; exit }' \
    /etc/nginx/sites-enabled/indiefi 2>/dev/null
} || true)"
if [[ -z "$readiness_host" ]]; then
  readiness_host=127.0.0.1
fi

{
  printf 'captured_at_utc\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  curl --insecure --silent --show-error --output /dev/null \
    --connect-timeout 3 --max-time 10 \
    --header "Host: $readiness_host" \
    --write-out 'https_status\t%{http_code}\nresponse_seconds\t%{time_total}\n' \
    https://127.0.0.1/health/ready
} >"${phase_directory}/readiness.tsv" 2>&1 || true

capture_or_mark_unavailable authoritative \
  "$script_dir/capture-authoritative-state.sh" \
  "${phase_directory}/authoritative.tsv"
capture_or_mark_unavailable external-retry \
  "$script_dir/capture-external-retry-state.sh" \
  "${phase_directory}/external-retry.tsv"
capture_or_mark_unavailable rabbitmq \
  "$script_dir/capture-rabbitmq-state.sh" \
  "${phase_directory}/rabbitmq.txt"

if [[ -n "$drop_id" ]]; then
  capture_or_mark_unavailable redis \
    "$script_dir/capture-redis-state.sh" \
    "${phase_directory}/redis.tsv" \
    "$drop_id"
fi

journalctl -u indiefi-web.service -u indiefi-worker.service \
  --since "$since_utc" -p warning..alert --no-pager -o short-iso \
  >"${phase_directory}/journal-warning.txt" 2>&1 || true

find "$phase_directory" -maxdepth 1 -type f ! -name SHA256SUMS -print0 \
  | sort -z \
  | xargs -0 sha256sum \
  >"${phase_directory}/SHA256SUMS"
chmod 0600 "$phase_directory"/*
echo "Failure snapshot saved to $phase_directory."
