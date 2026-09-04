#!/usr/bin/env bash
set -Eeuo pipefail
set +x

fail() {
  echo "Portfolio database migration failed: $1" >&2
  exit 1
}

if [[ "${EUID:-$(id -u)}" != "0" ]]; then
  fail "run this script as root."
fi

[[ "$#" -ge "1" && "$#" -le "2" ]] \
  || fail "usage: $0 <migration-bundle> [infra-env]"

migration_bundle="$1"
infra_file="${2:-/etc/indiefi/infra.env}"

[[ -f "$migration_bundle" && ! -L "$migration_bundle" && -x "$migration_bundle" ]] \
  || fail "the migration bundle must be an executable regular file."
[[ -f "$infra_file" && ! -L "$infra_file" ]] \
  || fail "infra.env must be a regular file, not a symlink."
[[ "$(stat -c '%u:%g' "$infra_file")" == "0:0" ]] \
  || fail "infra.env must be owned by root:root."
[[ "$(stat -c '%a' "$infra_file")" == "600" ]] \
  || fail "infra.env must have mode 0600."

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

[[ "$postgres_password" =~ ^[A-Za-z0-9+/=]{32,128}$ ]] \
  || fail "POSTGRES_PASSWORD must be a 32-128 character base64 value."

# The owner connection exists only in this root process. Web and Worker keep using
# the separate indiefi_app connection from their protected environment files.
export ConnectionStrings__Database="Host=127.0.0.1;Port=5432;Database=indiefi;Username=indiefi;Password=$postgres_password"
exec "$migration_bundle"
