#!/usr/bin/env bash
set -Eeuo pipefail

infra_file="${1:-/etc/indiefi/infra.env}"
web_file="${2:-/etc/indiefi/web.env}"
worker_file="${3:-/etc/indiefi/worker.env}"

fail() {
  echo "Portfolio environment validation failed: $1" >&2
  exit 1
}

validate_environment_file() {
  local path="$1"
  local label="$2"

  [[ -f "$path" && ! -L "$path" ]] \
    || fail "$label must be a regular file, not a symlink: $path"

  if [[ "${SKIP_FILE_PERMISSION_CHECK:-0}" != "1" ]]; then
    local mode
    mode="$(stat -c '%a' "$path")"
    [[ "$mode" == "600" || "$mode" == "640" ]] \
      || fail "$label must have mode 0600 or 0640."
  fi

  awk '
    { sub(/\r$/, "") }
    /^[[:space:]]*$/ || /^#/ { next }
    !/^[A-Za-z][A-Za-z0-9_]*=[^[:space:]]*$/ { exit 1 }
  ' "$path" || fail "$label contains a malformed line."

  local duplicates
  duplicates="$(
    awk -F= '{ sub(/\r$/, "") } /^[A-Za-z][A-Za-z0-9_]*=/ { print $1 }' "$path" \
      | sort \
      | uniq -d
  )"
  [[ -z "$duplicates" ]] \
    || fail "$label contains duplicate keys: $(tr '\n' ',' <<< "$duplicates" | sed 's/,$//')"
}

get_value() {
  local path="$1"
  local key="$2"

  awk -v prefix="$key=" '
    { sub(/\r$/, "") }
    index($0, prefix) == 1 {
      print substr($0, length(prefix) + 1)
      exit
    }
  ' "$path"
}

has_key() {
  local path="$1"
  local key="$2"
  awk -v prefix="$key=" 'index($0, prefix) == 1 { found=1 } END { exit !found }' "$path"
}

require_value() {
  local path="$1"
  local key="$2"
  local label="$3"
  local value
  value="$(get_value "$path" "$key")"
  [[ -n "$value" ]] || fail "$label is missing $key."
  printf '%s' "$value"
}

require_exact() {
  local path="$1"
  local key="$2"
  local expected="$3"
  local label="$4"
  local actual
  actual="$(require_value "$path" "$key" "$label")"
  [[ "$actual" == "$expected" ]] \
    || fail "$label must set $key=$expected."
}

require_same() {
  local first_file="$1"
  local second_file="$2"
  local key="$3"
  local first_label="$4"
  local second_label="$5"
  local first
  local second
  first="$(require_value "$first_file" "$key" "$first_label")"
  second="$(require_value "$second_file" "$key" "$second_label")"
  [[ "$first" == "$second" ]] \
    || fail "$key must match between $first_label and $second_label."
}

reject_key() {
  local path="$1"
  local key="$2"
  local label="$3"
  if has_key "$path" "$key"; then
    fail "$label must not contain $key."
  fi
}

reject_prefix() {
  local path="$1"
  local prefix="$2"
  local label="$3"
  if awk -v prefix="$prefix" 'index($0, prefix) == 1 { found=1 } END { exit !found }' "$path"; then
    fail "$label must not contain settings with prefix $prefix."
  fi
}

validate_https_origin() {
  local value="$1"
  local label="$2"
  [[ "$value" =~ ^https://[^/?#]+$ ]] \
    || fail "$label must be an HTTPS origin without a path, query, or fragment."
}

connection_value() {
  local connection="$1"
  local expected_key="$2"
  local segment
  local -a segments
  local key
  local value

  IFS=';' read -ra segments <<< "$connection"
  for segment in "${segments[@]}"; do
    key="${segment%%=*}"
    value="${segment#*=}"
    if [[ "$key" == "$expected_key" && "$value" != "$segment" ]]; then
      printf '%s' "$value"
      return 0
    fi
  done
  return 1
}

validate_reference_file() {
  local path="$1"
  local label="$2"
  [[ "$path" == /* ]] || fail "$label must use an absolute path."

  if [[ "${SKIP_REFERENCED_FILE_CHECK:-0}" != "1" ]]; then
    [[ -f "$path" && ! -L "$path" && -r "$path" ]] \
      || fail "$label is missing, unreadable, or a symlink."
  fi
}

validate_environment_file "$infra_file" "infra.env"
validate_environment_file "$web_file" "web.env"
validate_environment_file "$worker_file" "worker.env"

infra_postgres_password="$(require_value "$infra_file" POSTGRES_PASSWORD infra.env)"
infra_redis_password="$(require_value "$infra_file" REDIS_PASSWORD infra.env)"
infra_rabbitmq_password="$(require_value "$infra_file" RABBITMQ_PASSWORD infra.env)"

infra_key_count="$(awk -F= '/^[A-Za-z][A-Za-z0-9_]*=/ { count++ } END { print count+0 }' "$infra_file")"
[[ "$infra_key_count" == "3" ]] \
  || fail "infra.env may contain only the three data-service passwords."

require_exact "$web_file" ASPNETCORE_ENVIRONMENT Production web.env
require_exact "$web_file" ASPNETCORE_URLS http://127.0.0.1:5000 web.env
require_exact "$web_file" Authentication__Google__Enabled false web.env
require_exact "$web_file" WaitingRoom__Redis__Enabled true web.env
require_exact "$web_file" Messaging__RabbitMq__Enabled true web.env
require_exact "$web_file" Media__ObjectStorage__Enabled true web.env
require_exact "$web_file" Media__ObjectStorage__Region auto web.env
require_exact "$web_file" Media__ObjectStorage__ForcePathStyle false web.env
require_exact "$web_file" Media__Worker__Enabled false web.env
require_exact "$web_file" ReverseProxy__KnownProxies__0 127.0.0.1 web.env

require_exact "$worker_file" DOTNET_ENVIRONMENT Production worker.env
require_exact "$worker_file" WaitingRoom__Redis__Enabled true worker.env
require_exact "$worker_file" Messaging__RabbitMq__Enabled true worker.env
require_exact "$worker_file" Media__ObjectStorage__Enabled true worker.env
require_exact "$worker_file" Media__ObjectStorage__Region auto worker.env
require_exact "$worker_file" Media__ObjectStorage__ForcePathStyle false worker.env
require_exact "$worker_file" Media__Worker__Enabled true worker.env
require_exact "$worker_file" AuthenticationEmail__Enabled true worker.env
require_exact "$worker_file" NotificationEmail__Enabled true worker.env
require_exact "$worker_file" NotificationMaintenance__Enabled true worker.env
require_exact "$worker_file" NotificationOperations__Enabled true worker.env

reject_prefix "$web_file" AuthenticationEmail__ web.env
reject_prefix "$web_file" NotificationEmail__ web.env
reject_prefix "$worker_file" DataProtectionKeyManagement__ worker.env
reject_key "$worker_file" ASPNETCORE_URLS worker.env
for key in POSTGRES_PASSWORD REDIS_PASSWORD RABBITMQ_PASSWORD; do
  reject_key "$web_file" "$key" web.env
  reject_key "$worker_file" "$key" worker.env
done

shared_keys=(
  ConnectionStrings__Database
  WaitingRoom__Redis__ConnectionString
  Messaging__RabbitMq__Host
  Messaging__RabbitMq__Port
  Messaging__RabbitMq__VirtualHost
  Messaging__RabbitMq__Username
  Messaging__RabbitMq__Password
  Messaging__RabbitMq__EndpointPrefix
  Media__ObjectStorage__ServiceUrl
  Media__ObjectStorage__Region
  Media__ObjectStorage__BucketName
  Media__ObjectStorage__AccessKeyId
  Media__ObjectStorage__SecretAccessKey
  Media__ObjectStorage__ForcePathStyle
  AccountProtectionKeyFile__Path
)
for key in "${shared_keys[@]}"; do
  require_same "$web_file" "$worker_file" "$key" web.env worker.env
done

database_connection="$(require_value "$web_file" ConnectionStrings__Database web.env)"
database_password="$(connection_value "$database_connection" Password || true)"
[[ -n "$database_password" ]] || fail "The application database password is missing."
expected_database_connection="Host=127.0.0.1;Port=5432;Database=indiefi;Username=indiefi_app;Password=$database_password"
[[ "$database_connection" == "$expected_database_connection" ]] \
  || fail "ConnectionStrings__Database must use loopback and the indiefi_app role."
[[ "$database_password" != "$infra_postgres_password" ]] \
  || fail "The application database role must not reuse the PostgreSQL owner password."

redis_connection="$(require_value "$web_file" WaitingRoom__Redis__ConnectionString web.env)"
expected_redis_connection="127.0.0.1:6379,password=$infra_redis_password,abortConnect=false"
[[ "$redis_connection" == "$expected_redis_connection" ]] \
  || fail "The Redis connection must use loopback, its infrastructure password, and abortConnect=false."

web_rabbitmq_password="$(require_value "$web_file" Messaging__RabbitMq__Password web.env)"
[[ "$web_rabbitmq_password" == "$infra_rabbitmq_password" ]] \
  || fail "The RabbitMQ application password must match infra.env."
require_exact "$web_file" Messaging__RabbitMq__Host 127.0.0.1 web.env
require_exact "$web_file" Messaging__RabbitMq__Port 5672 web.env
require_exact "$web_file" Messaging__RabbitMq__VirtualHost / web.env
require_exact "$web_file" Messaging__RabbitMq__Username indiefi web.env

r2_origin="$(require_value "$web_file" Media__ObjectStorage__ServiceUrl web.env)"
validate_https_origin "$r2_origin" "The R2 service URL"
r2_access_key="$(require_value "$web_file" Media__ObjectStorage__AccessKeyId web.env)"
r2_secret_key="$(require_value "$web_file" Media__ObjectStorage__SecretAccessKey web.env)"
[[ "$r2_access_key" != "$r2_secret_key" ]] \
  || fail "The R2 access key ID and secret access key must be different."

public_origin="$(require_value "$worker_file" AuthenticationEmail__PublicBaseUrl worker.env)"
notification_origin="$(require_value "$worker_file" NotificationEmail__PublicBaseUrl worker.env)"
validate_https_origin "$public_origin" "AuthenticationEmail__PublicBaseUrl"
[[ "$public_origin" == "$notification_origin" ]] \
  || fail "Authentication and notification email must use the same public origin."
allowed_host="$(require_value "$web_file" AllowedHosts web.env)"
[[ "$allowed_host" != "*" && "$public_origin" == "https://$allowed_host" ]] \
  || fail "AllowedHosts must be the host from the public HTTPS origin."

for prefix in AuthenticationEmail NotificationEmail; do
  require_exact "$worker_file" "${prefix}__Smtp__Host" email-smtp.ap-northeast-2.amazonaws.com worker.env
  require_exact "$worker_file" "${prefix}__Smtp__Port" 587 worker.env
  require_exact "$worker_file" "${prefix}__Smtp__EnableSsl" true worker.env
  require_value "$worker_file" "${prefix}__Smtp__Username" worker.env >/dev/null
  require_value "$worker_file" "${prefix}__Smtp__Password" worker.env >/dev/null
  require_value "$worker_file" "${prefix}__Smtp__FromAddress" worker.env >/dev/null
done

account_key_file="$(require_value "$web_file" AccountProtectionKeyFile__Path web.env)"
validate_reference_file "$account_key_file" "The shared account protection key file"
data_protection_certificate="$(require_value "$web_file" DataProtectionKeyManagement__CertificatePath web.env)"
data_protection_private_key="$(require_value "$web_file" DataProtectionKeyManagement__PrivateKeyPath web.env)"
validate_reference_file "$data_protection_certificate" "The Data Protection certificate"
validate_reference_file "$data_protection_private_key" "The Data Protection private key"

if [[ "${SKIP_REFERENCED_FILE_CHECK:-0}" != "1" ]]; then
  private_key_mode="$(stat -c '%a' "$data_protection_private_key")"
  [[ "$private_key_mode" == "600" ]] \
    || fail "The Data Protection private key must have mode 0600."
fi

echo "Portfolio environment files are valid."
