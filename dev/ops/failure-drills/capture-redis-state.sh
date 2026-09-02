#!/usr/bin/env bash
set -Eeuo pipefail

output_file="${1:-}"
drop_id="${2:-}"

if [[ -z "$output_file" || "$output_file" != /* || ! "$drop_id" =~ ^[1-9][0-9]*$ ]]; then
  echo 'Usage: capture-redis-state.sh <absolute-output-file> <positive-drop-id>' >&2
  exit 1
fi

[[ "$(docker inspect --format '{{.State.Status}}' indiefi-redis 2>/dev/null)" == 'running' ]] \
  || { echo 'indiefi-redis must be running.' >&2; exit 1; }

install -d -m 0700 "$(dirname -- "$output_file")"
umask 077
temporary_file="${output_file}.tmp.$$"
trap 'rm -f -- "$temporary_file"' EXIT

redis_cli() {
  docker exec -e FAILURE_DRILL_DROP_ID="$drop_id" indiefi-redis sh -ec '
    export REDISCLI_AUTH="$REDIS_PASSWORD"
    exec redis-cli --raw "$@"
  ' sh "$@"
}

base="indiefi:waiting-room:v1:{drop:${drop_id}}"
{
  printf 'captured_at_utc\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf 'drop_id\t%s\n' "$drop_id"
  redis_cli PING | awk '{ print "redis.ping\t" $0 }'
  redis_cli INFO persistence \
    | tr -d '\r' \
    | awk -F: '/^(loading|aof_enabled|aof_rewrite_in_progress|aof_last_bgrewrite_status|aof_last_write_status|aof_current_size):/ { print "redis." $1 "\t" $2 }'
  printf 'waiting_room.metadata_exists\t%s\n' "$(redis_cli EXISTS "${base}:meta")"
  printf 'waiting_room.entries\t%s\n' "$(redis_cli HLEN "${base}:entries")"
  printf 'waiting_room.pre\t%s\n' "$(redis_cli ZCARD "${base}:pre")"
  printf 'waiting_room.queue\t%s\n' "$(redis_cli ZCARD "${base}:queue")"
  printf 'waiting_room.admitted\t%s\n' "$(redis_cli ZCARD "${base}:admitted")"
} >"$temporary_file"

mv -- "$temporary_file" "$output_file"
chmod 0600 "$output_file"
echo "Redis persistence and waiting-room counts saved to $output_file."
