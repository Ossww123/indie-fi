#!/usr/bin/env bash
set -Eeuo pipefail

output_file="${1:-}"
if [[ -z "$output_file" || "$output_file" != /* ]]; then
  echo 'Usage: capture-rabbitmq-state.sh <absolute-output-file>' >&2
  exit 1
fi

[[ "$(docker inspect --format '{{.State.Status}}' indiefi-rabbitmq 2>/dev/null)" == 'running' ]] \
  || { echo 'indiefi-rabbitmq must be running.' >&2; exit 1; }

install -d -m 0700 "$(dirname -- "$output_file")"
umask 077
temporary_file="${output_file}.tmp.$$"
trap 'rm -f -- "$temporary_file"' EXIT

{
  printf 'captured_at_utc\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  docker exec indiefi-rabbitmq rabbitmq-diagnostics -q check_running
  docker exec indiefi-rabbitmq rabbitmq-diagnostics -q check_local_alarms
  docker exec indiefi-rabbitmq rabbitmqctl list_queues \
    name durable consumers messages_ready messages_unacknowledged \
    | awk 'NR <= 2 || $1 ~ /^indiefi/'
} >"$temporary_file"

mv -- "$temporary_file" "$output_file"
chmod 0600 "$output_file"
echo "RabbitMQ health and queue counts saved to $output_file."
