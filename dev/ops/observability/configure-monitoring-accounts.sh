#!/usr/bin/env bash
set -Eeuo pipefail
set +x

fail() {
  echo "Monitoring account configuration failed: $1" >&2
  exit 1
}

[[ "${EUID:-$(id -u)}" == "0" ]] || fail "run this script as root."
[[ "$#" -le 2 ]] || fail "usage: $0 [observability-env] [infra-env]"

observability_file="${1:-/etc/indiefi/observability.env}"
infra_file="${2:-/etc/indiefi/infra.env}"
postgres_container="${POSTGRES_CONTAINER:-indiefi-db}"
redis_container="${REDIS_CONTAINER:-indiefi-redis}"

for file in "$observability_file" "$infra_file"; do
  [[ -f "$file" && ! -L "$file" ]] \
    || fail "$file must be a regular file, not a symlink."
  [[ "$(stat -c '%u:%g' "$file")" == "0:0" ]] \
    || fail "$file must be owned by root:root."
  [[ "$(stat -c '%a' "$file")" == "600" ]] \
    || fail "$file must have mode 0600."
done

read_value() {
  local file="$1"
  local key="$2"
  awk -v prefix="$key=" '
    { sub(/\r$/, "") }
    index($0, prefix) == 1 {
      print substr($0, length(prefix) + 1)
      found++
    }
    END { if (found != 1) exit 1 }
  ' "$file"
}

postgres_monitor_password="$(
  read_value "$observability_file" POSTGRES_EXPORTER_PASSWORD
)" || fail "POSTGRES_EXPORTER_PASSWORD must occur exactly once."
redis_monitor_password="$(
  read_value "$observability_file" REDIS_EXPORTER_PASSWORD
)" || fail "REDIS_EXPORTER_PASSWORD must occur exactly once."
redis_owner_password="$(
  read_value "$infra_file" REDIS_PASSWORD
)" || fail "REDIS_PASSWORD must occur exactly once in infra.env."

for password_spec in \
  "$postgres_monitor_password:POSTGRES_EXPORTER_PASSWORD" \
  "$redis_monitor_password:REDIS_EXPORTER_PASSWORD" \
  "$redis_owner_password:REDIS_PASSWORD"; do
  password="${password_spec%%:*}"
  label="${password_spec#*:}"
  [[ "$password" =~ ^[A-Za-z0-9+/=]{32,128}$ ]] \
    || fail "$label must be a 32-128 character base64 value."
done
[[ "$postgres_monitor_password" != "$redis_monitor_password" ]] \
  || fail "PostgreSQL and Redis monitoring passwords must differ."

command -v docker >/dev/null 2>&1 || fail "docker is required."
for container in "$postgres_container" "$redis_container"; do
  [[ "$(docker inspect --format '{{.State.Running}}' "$container" 2>/dev/null || true)" == "true" ]] \
    || fail "required container is not running: $container"
done

escaped_postgres_password="${postgres_monitor_password//\'/\'\'}"
cat <<SQL | docker exec -i -u postgres "$postgres_container" \
  psql --username=indiefi --dbname=indiefi --no-psqlrc --set=ON_ERROR_STOP=1 \
  >/dev/null
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'indiefi_monitor') THEN
    CREATE ROLE indiefi_monitor LOGIN;
  END IF;
END
\$\$;
ALTER ROLE indiefi_monitor WITH
  LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
  CONNECTION LIMIT 4 PASSWORD '${escaped_postgres_password}';
GRANT CONNECT ON DATABASE indiefi TO indiefi_monitor;
GRANT pg_monitor TO indiefi_monitor;
SQL

docker exec \
  -e REDISCLI_AUTH="$redis_owner_password" \
  "$redis_container" \
  redis-cli --user default ACL SETUSER indiefi_monitor \
    reset on '~*' '&*' \
    -@all +@connection +memory -readonly +strlen '+config|get' \
    +xinfo +pfcount -quit +zcard +type +xlen -readwrite -command \
    +client -wait +scard +llen +hlen +arcount +get +eval +slowlog \
    '+cluster|info' '+cluster|slots' '+cluster|nodes' \
    -hello -echo +info +latency +scan -reset -auth -asking \
    ">$redis_monitor_password" \
  >/dev/null

postgres_role="$(
  docker exec -u postgres "$postgres_container" \
    psql --username=indiefi --dbname=indiefi --no-psqlrc \
      --tuples-only --no-align \
      --command="
        SELECT concat_ws('|', rolcanlogin, rolsuper, rolcreatedb,
          rolcreaterole, rolreplication, rolbypassrls,
          pg_has_role('indiefi_monitor', 'pg_monitor', 'member'))
        FROM pg_roles WHERE rolname = 'indiefi_monitor';"
)"
[[ "$postgres_role" == "t|f|f|f|f|f|t" ]] \
  || fail "indiefi_monitor has unexpected PostgreSQL privileges: $postgres_role"

redis_role="$(
  docker exec \
    -e REDISCLI_AUTH="$redis_monitor_password" \
    "$redis_container" \
    redis-cli --user indiefi_monitor --raw PING
)"
[[ "$redis_role" == "PONG" ]] \
  || fail "indiefi_monitor could not authenticate to Redis."

echo "PostgreSQL and Redis monitoring accounts are ready with bounded privileges."
