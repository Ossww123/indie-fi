#!/usr/bin/env bash
set -Eeuo pipefail
set +x

fail() {
  echo "Portfolio application database role configuration failed: $1" >&2
  exit 1
}

if [[ "${EUID:-$(id -u)}" != "0" ]]; then
  fail "run this script as root."
fi

[[ "$#" -le "2" ]] \
  || fail "usage: $0 [database-bootstrap-env] [infra-env]"

bootstrap_file="${1:-/etc/indiefi/database-bootstrap.env}"
infra_file="${2:-/etc/indiefi/infra.env}"
postgres_container="${POSTGRES_CONTAINER:-indiefi-db}"

for file_spec in "$bootstrap_file:database-bootstrap.env" "$infra_file:infra.env"; do
  path="${file_spec%%:*}"
  label="${file_spec#*:}"
  [[ -f "$path" && ! -L "$path" ]] \
    || fail "$label must be a regular file, not a symlink."
  [[ "$(stat -c '%u:%g' "$path")" == "0:0" ]] \
    || fail "$label must be owned by root:root."
  [[ "$(stat -c '%a' "$path")" == "600" ]] \
    || fail "$label must have mode 0600."
done

application_password="$(
  awk -v prefix='APPLICATION_DATABASE_PASSWORD=' '
    { sub(/\r$/, "") }
    index($0, prefix) == 1 {
      print substr($0, length(prefix) + 1)
      found++
    }
    END { if (found != 1) exit 1 }
  ' "$bootstrap_file"
)" || fail "database-bootstrap.env must contain APPLICATION_DATABASE_PASSWORD exactly once."

bootstrap_key_count="$(
  awk -F= '/^[A-Za-z][A-Za-z0-9_]*=/ { count++ } END { print count+0 }' \
    "$bootstrap_file"
)"
[[ "$bootstrap_key_count" == "1" ]] \
  || fail "database-bootstrap.env may contain only APPLICATION_DATABASE_PASSWORD."

postgres_password="$(
  awk -v prefix='POSTGRES_PASSWORD=' '
    { sub(/\r$/, "") }
    index($0, prefix) == 1 {
      print substr($0, length(prefix) + 1)
      found++
    }
    END { if (found != 1) exit 1 }
  ' "$infra_file"
)" || fail "infra.env must contain POSTGRES_PASSWORD exactly once."

[[ "$application_password" =~ ^[A-Za-z0-9+/=]{32,128}$ ]] \
  || fail "APPLICATION_DATABASE_PASSWORD must be a 32-128 character base64 value."
[[ "$postgres_password" =~ ^[A-Za-z0-9+/=]{32,128}$ ]] \
  || fail "POSTGRES_PASSWORD must be a 32-128 character base64 value."
[[ "$application_password" != "$postgres_password" ]] \
  || fail "the application role must not reuse the PostgreSQL owner password."

command -v docker >/dev/null 2>&1 || fail "docker is required."
[[ "$(docker inspect --format '{{.State.Running}}' "$postgres_container" 2>/dev/null || true)" == "true" ]] \
  || fail "the PostgreSQL container is not running: $postgres_container"

role_exists="$({
  docker exec -u postgres "$postgres_container" \
    psql \
      --username=indiefi \
      --dbname=indiefi \
      --no-psqlrc \
      --tuples-only \
      --no-align \
      --command="SELECT 1 FROM pg_roles WHERE rolname = 'indiefi_app';"
} 2>/dev/null)"
[[ "$role_exists" == "1" ]] \
  || fail "indiefi_app does not exist; apply the migrations first."

role_attributes="$({
  docker exec -u postgres "$postgres_container" \
    psql \
      --username=indiefi \
      --dbname=indiefi \
      --no-psqlrc \
      --tuples-only \
      --no-align \
      --command="
        SELECT concat_ws(
          '|',
          rolname,
          rolcanlogin,
          rolsuper,
          rolcreatedb,
          rolcreaterole,
          rolreplication,
          rolbypassrls)
        FROM pg_roles
        WHERE rolname = 'indiefi_app';"
} 2>/dev/null)"
[[ "$role_attributes" == "indiefi_app|t|f|f|f|f|f" ]] \
  || fail "indiefi_app has unexpected cluster-level privileges: $role_attributes"

escaped_application_password="${application_password//\'/\'\'}"
printf "ALTER ROLE indiefi_app PASSWORD '%s';\n" "$escaped_application_password" \
  | docker exec -i -u postgres "$postgres_container" \
      psql \
        --username=indiefi \
        --dbname=indiefi \
        --no-psqlrc \
        --set=ON_ERROR_STOP=1 \
        >/dev/null

run_as_application_role() {
  local sql="$1"
  printf '%s\n' "$application_password" \
    | docker exec -i "$postgres_container" \
        sh -ec '
          IFS= read -r PGPASSWORD
          export PGPASSWORD
          exec psql \
            --host=127.0.0.1 \
            --username=indiefi_app \
            --dbname=indiefi \
            --no-password \
            --no-psqlrc \
            --set=ON_ERROR_STOP=1 \
            --tuples-only \
            --no-align \
            --command="$1"
        ' sh "$sql"
}

[[ "$(run_as_application_role 'SELECT current_user;' 2>/dev/null)" == "indiefi_app" ]] \
  || fail "indiefi_app could not log in with the configured password."

migration_count="$(
  run_as_application_role 'SELECT count(*) FROM "__EFMigrationsHistory";' 2>/dev/null
)" || fail "indiefi_app cannot read the migration history."
[[ "$migration_count" =~ ^[1-9][0-9]*$ ]] \
  || fail "the migration history is empty."

set +e
ddl_output="$(
  run_as_application_role \
    'BEGIN; CREATE TABLE public.__indiefi_permission_probe (id integer); ROLLBACK;' \
    2>&1
)"
ddl_exit_code="$?"
set -e

if [[ "$ddl_exit_code" == "0" ]]; then
  fail "indiefi_app unexpectedly created a table."
fi
[[ "$ddl_output" == *"permission denied for schema public"* ]] \
  || fail "the DDL denial probe failed for an unexpected reason: $ddl_output"

echo "Application database role login and minimum privilege boundary are valid."
