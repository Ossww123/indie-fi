#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
sql_file="${script_dir}/audit-authoritative-state.sql"
output_file="${1:-}"

if [[ -z "$output_file" || "$output_file" != /* ]]; then
  echo 'Usage: capture-authoritative-state.sh <absolute-output-file>' >&2
  exit 1
fi

[[ -r "$sql_file" ]] || { echo "Cannot read $sql_file." >&2; exit 1; }
[[ "$(docker inspect --format '{{.State.Status}}' indiefi-db 2>/dev/null)" == 'running' ]] \
  || { echo 'indiefi-db must be running.' >&2; exit 1; }

install -d -m 0700 "$(dirname -- "$output_file")"
umask 077
temporary_file="${output_file}.tmp.$$"
trap 'rm -f -- "$temporary_file"' EXIT

{
  printf 'captured_at_utc\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  docker exec -i indiefi-db sh -ec '
    export PGPASSWORD="$POSTGRES_PASSWORD"
    exec psql --no-psqlrc -X -q -v ON_ERROR_STOP=1 -At -F "	" \
      -U "$POSTGRES_USER" -d "$POSTGRES_DB"
  ' <"$sql_file"
} >"$temporary_file"

mv -- "$temporary_file" "$output_file"
chmod 0600 "$output_file"
echo "Authoritative state audit saved to $output_file."
