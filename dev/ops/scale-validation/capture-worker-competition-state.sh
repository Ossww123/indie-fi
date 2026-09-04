#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
sql_file="${script_dir}/audit-worker-competition.sql"
output_file="${1:-}"
run_id="${2:-}"
drop_id="${3:-}"

if [[ "$output_file" != /* \
    || ! "$run_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ \
    || ! "$drop_id" =~ ^[1-9][0-9]*$ ]]; then
  echo 'Usage: capture-worker-competition-state.sh <absolute-output-file> <run-id> <drop-id>' >&2
  exit 64
fi

[[ -r "$sql_file" ]] || { echo 'The Worker competition audit SQL is unavailable.' >&2; exit 69; }
[[ "$(docker inspect --format '{{.State.Status}}' indiefi-db 2>/dev/null)" == running ]] \
  || { echo 'indiefi-db must be running.' >&2; exit 69; }

install -d -m 0700 "$(dirname -- "$output_file")"
umask 077
temporary_file="${output_file}.tmp.$$"
trap 'rm -f -- "$temporary_file"' EXIT

{
  printf 'captured_at_utc\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf 'run_id\t%s\n' "$run_id"
  printf 'drop_id\t%s\n' "$drop_id"
  docker exec -i indiefi-db sh -ec '
    export PGPASSWORD="$POSTGRES_PASSWORD"
    exec psql --no-psqlrc -X -q -v ON_ERROR_STOP=1 \
      -v "run_id=$1" -v "drop_id=$2" -At -F "	" \
      -U "$POSTGRES_USER" -d "$POSTGRES_DB"
  ' sh "$run_id" "$drop_id" <"$sql_file"
} >"$temporary_file"

mv -- "$temporary_file" "$output_file"
chmod 0600 "$output_file"
echo "Worker competition aggregate audit saved to $output_file."
