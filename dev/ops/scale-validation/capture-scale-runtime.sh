#!/usr/bin/env bash
set -Eeuo pipefail

output_file="${1:-}"
if [[ -z "$output_file" || "$output_file" != /* ]]; then
  echo 'Usage: capture-scale-runtime.sh <absolute-output-file>' >&2
  exit 1
fi

install -d -m 0700 "$(dirname -- "$output_file")"
umask 077
temporary_file="${output_file}.tmp.$$"
trap 'rm -f -- "$temporary_file"' EXIT

{
  printf 'captured_at_utc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf 'release=%s\n' "$(readlink -f /opt/indiefi/current 2>/dev/null || echo unavailable)"
  systemctl show \
    indiefi-web.service indiefi-worker.service \
    indiefi-scale-web@1.service indiefi-scale-web@2.service \
    indiefi-scale-worker@1.service indiefi-scale-worker@2.service \
    --property=Id,LoadState,ActiveState,SubState,MainPID,NRestarts
  ss -lnt | awk 'NR == 1 || $4 ~ /:(5000|5101|5102)$/'
  if [[ "$(docker inspect --format '{{.State.Status}}' indiefi-rabbitmq 2>/dev/null)" == running ]]; then
    docker exec indiefi-rabbitmq rabbitmqctl list_queues \
      name durable consumers messages_ready messages_unacknowledged \
      | awk 'NR <= 2 || $1 ~ /^indiefi/'
  else
    echo 'rabbitmq=not-running'
  fi
} >"$temporary_file"

mv -- "$temporary_file" "$output_file"
chmod 0600 "$output_file"
echo "Scale runtime snapshot saved to $output_file."
