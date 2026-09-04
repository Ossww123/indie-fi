#!/usr/bin/env bash
set -Eeuo pipefail
set +x

fail() {
  echo "Portfolio runtime environment configuration failed: $1" >&2
  exit 1
}

if [[ "${EUID:-$(id -u)}" != "0" ]]; then
  fail "run this script as root."
fi

[[ "$#" == "0" ]] || fail "this script does not accept arguments."
[[ -t 0 && -t 1 ]] || fail "run this script from an interactive terminal."

required_commands=(awk chmod getent install mktemp mv rm stat)
for command_name in "${required_commands[@]}"; do
  command -v "$command_name" >/dev/null 2>&1 \
    || fail "required command is missing: $command_name"
done

infra_file="/etc/indiefi/infra.env"
database_bootstrap_file="/etc/indiefi/database-bootstrap.env"
web_file="/etc/indiefi/web.env"
worker_file="/etc/indiefi/worker.env"
verification_script="/usr/local/lib/indiefi/verify-portfolio-environment.sh"

for source_file in "$infra_file" "$database_bootstrap_file"; do
  [[ -f "$source_file" && ! -L "$source_file" ]] \
    || fail "required protected file is missing or is a symlink: $source_file"
  [[ "$(stat -c '%U:%G %a' "$source_file")" == "root:root 600" ]] \
    || fail "protected source file must be root:root with mode 0600: $source_file"
done

[[ -x "$verification_script" ]] \
  || fail "the installed environment verification script is missing."
getent passwd indiefi-web >/dev/null \
  || fail "the indiefi-web service account is missing."
getent passwd indiefi-worker >/dev/null \
  || fail "the indiefi-worker service account is missing."

for target_file in "$web_file" "$worker_file"; do
  [[ -f "$target_file" && ! -L "$target_file" ]] \
    || fail "runtime target must already be a regular file: $target_file"
done

get_value() {
  local path="$1"
  local key="$2"

  awk -v prefix="$key=" '
    { sub(/\r$/, "") }
    index($0, prefix) == 1 {
      print substr($0, length(prefix) + 1)
      found++
    }
    END { exit found == 1 ? 0 : 1 }
  ' "$path"
}

require_file_value() {
  local path="$1"
  local key="$2"
  local value

  value="$(get_value "$path" "$key")" \
    || fail "$path must contain $key exactly once."
  validate_environment_value "$key" "$value"
  printf '%s' "$value"
}

validate_environment_value() {
  local label="$1"
  local value="$2"
  local forbidden

  [[ -n "$value" ]] || fail "$label must not be empty."
  [[ ! "$value" =~ [[:space:]] ]] \
    || fail "$label must not contain whitespace."

  for forbidden in "'" '"' '\' '#' ';'; do
    [[ "$value" != *"$forbidden"* ]] \
      || fail "$label contains a character that is unsafe in this environment file."
  done
}

prompt_visible() {
  local output_variable="$1"
  local prompt="$2"
  local default_value="${3:-}"
  local value

  printf '%s' "$prompt"
  IFS= read -r value || fail "input ended before configuration was complete."
  if [[ -z "$value" && -n "$default_value" ]]; then
    value="$default_value"
  fi
  validate_environment_value "$output_variable" "$value"
  printf -v "$output_variable" '%s' "$value"
}

prompt_hidden() {
  local output_variable="$1"
  local prompt="$2"
  local value

  printf '%s' "$prompt"
  IFS= read -r -s value || fail "input ended before configuration was complete."
  printf '\n'
  validate_environment_value "$output_variable" "$value"
  printf -v "$output_variable" '%s' "$value"
}

postgres_owner_password="$(
  require_file_value "$infra_file" POSTGRES_PASSWORD
)"
redis_password="$(require_file_value "$infra_file" REDIS_PASSWORD)"
rabbitmq_password="$(require_file_value "$infra_file" RABBITMQ_PASSWORD)"
application_database_password="$(
  require_file_value "$database_bootstrap_file" APPLICATION_DATABASE_PASSWORD
)"

[[ "$application_database_password" != "$postgres_owner_password" ]] \
  || fail "the application database password must differ from the owner password."

echo "Enter the deployment values below. Hidden inputs are not echoed or stored in shell history."
prompt_visible public_origin \
  "Public HTTPS origin (for example https://203.0.113.10): "
prompt_visible r2_service_url \
  "Cloudflare R2 S3 API service URL: "
prompt_visible r2_bucket_name \
  "Cloudflare R2 bucket name [indiefi-media-portfolio]: " \
  "indiefi-media-portfolio"
prompt_hidden r2_access_key_id "Cloudflare R2 access key ID (hidden): "
prompt_hidden r2_secret_access_key "Cloudflare R2 secret access key (hidden): "
prompt_hidden smtp_username "Amazon SES SMTP username (hidden): "
prompt_hidden smtp_password "Amazon SES SMTP password (hidden): "
prompt_visible smtp_from_address "Verified Amazon SES sender email address: "

[[ "$public_origin" =~ ^https://[A-Za-z0-9.-]+$ ]] \
  || fail "the public origin must be an HTTPS host without a path or port."
public_host="${public_origin#https://}"
[[ "$r2_service_url" =~ ^https://[A-Za-z0-9.-]+\.r2\.cloudflarestorage\.com$ ]] \
  || fail "the R2 service URL must be the account S3 API endpoint without a path."
[[ "$r2_bucket_name" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] \
  || fail "the R2 bucket name has an invalid format."
[[ "$r2_access_key_id" != "$r2_secret_access_key" ]] \
  || fail "the R2 access key ID and secret access key must differ."
[[ "$smtp_from_address" =~ ^[^@]+@[^@]+\.[^@]+$ ]] \
  || fail "the SES sender must look like an email address."

echo
echo "Non-secret configuration summary:"
echo "  Public origin: $public_origin"
echo "  R2 bucket: $r2_bucket_name"
echo "  SES sender: $smtp_from_address"
printf 'Install these Web and Worker environment files? [y/N]: '
IFS= read -r confirmation || fail "confirmation input ended unexpectedly."
[[ "$confirmation" == "y" || "$confirmation" == "Y" ]] \
  || fail "configuration was cancelled before any runtime file was changed."

database_connection="Host=127.0.0.1;Port=5432;Database=indiefi;Username=indiefi_app;Password=$application_database_password"
redis_connection="127.0.0.1:6379,password=$redis_password,abortConnect=false"

temporary_directory="$(mktemp -d /etc/indiefi/.runtime.XXXXXXXX)"
case "$temporary_directory" in
  /etc/indiefi/.runtime.*) ;;
  *) fail "mktemp returned an unexpected path." ;;
esac
install -d -o root -g root -m 0700 "$temporary_directory"

web_next="/etc/indiefi/.web.env.next"
worker_next="/etc/indiefi/.worker.env.next"
cleanup() {
  local exit_code="$?"
  rm -f -- "$web_next" "$worker_next"
  case "$temporary_directory" in
    /etc/indiefi/.runtime.*) rm -rf -- "$temporary_directory" ;;
  esac
  return "$exit_code"
}
trap cleanup EXIT

temporary_web_file="$temporary_directory/web.env"
{
  printf 'ASPNETCORE_ENVIRONMENT=Production\n'
  printf 'ASPNETCORE_URLS=http://127.0.0.1:5000\n'
  printf 'AllowedHosts=%s\n' "$public_host"
  printf 'ConnectionStrings__Database=%s\n' "$database_connection"
  printf 'ReverseProxy__KnownProxies__0=127.0.0.1\n'
  printf 'Authentication__Google__Enabled=false\n'
  printf 'WaitingRoom__Redis__Enabled=true\n'
  printf 'WaitingRoom__Redis__ConnectionString=%s\n' "$redis_connection"
  printf 'Messaging__RabbitMq__Enabled=true\n'
  printf 'Messaging__RabbitMq__Host=127.0.0.1\n'
  printf 'Messaging__RabbitMq__Port=5672\n'
  printf 'Messaging__RabbitMq__VirtualHost=/\n'
  printf 'Messaging__RabbitMq__Username=indiefi\n'
  printf 'Messaging__RabbitMq__Password=%s\n' "$rabbitmq_password"
  printf 'Messaging__RabbitMq__EndpointPrefix=indiefi\n'
  printf 'Media__ObjectStorage__Enabled=true\n'
  printf 'Media__ObjectStorage__ServiceUrl=%s\n' "$r2_service_url"
  printf 'Media__ObjectStorage__Region=auto\n'
  printf 'Media__ObjectStorage__BucketName=%s\n' "$r2_bucket_name"
  printf 'Media__ObjectStorage__AccessKeyId=%s\n' "$r2_access_key_id"
  printf 'Media__ObjectStorage__SecretAccessKey=%s\n' "$r2_secret_access_key"
  printf 'Media__ObjectStorage__ForcePathStyle=false\n'
  printf 'Media__Worker__Enabled=false\n'
  printf 'AccountProtectionKeyFile__Path=/etc/indiefi/keys/account-protection.json\n'
  printf 'DataProtectionKeyManagement__CertificatePath=/etc/indiefi/keys/data-protection.crt\n'
  printf 'DataProtectionKeyManagement__PrivateKeyPath=/etc/indiefi/keys/data-protection.key\n'
} > "$temporary_web_file"
chmod 0600 "$temporary_web_file"

temporary_worker_file="$temporary_directory/worker.env"
{
  printf 'DOTNET_ENVIRONMENT=Production\n'
  printf 'ConnectionStrings__Database=%s\n' "$database_connection"
  printf 'WaitingRoom__Redis__Enabled=true\n'
  printf 'WaitingRoom__Redis__ConnectionString=%s\n' "$redis_connection"
  printf 'Messaging__RabbitMq__Enabled=true\n'
  printf 'Messaging__RabbitMq__Host=127.0.0.1\n'
  printf 'Messaging__RabbitMq__Port=5672\n'
  printf 'Messaging__RabbitMq__VirtualHost=/\n'
  printf 'Messaging__RabbitMq__Username=indiefi\n'
  printf 'Messaging__RabbitMq__Password=%s\n' "$rabbitmq_password"
  printf 'Messaging__RabbitMq__EndpointPrefix=indiefi\n'
  printf 'Media__ObjectStorage__Enabled=true\n'
  printf 'Media__ObjectStorage__ServiceUrl=%s\n' "$r2_service_url"
  printf 'Media__ObjectStorage__Region=auto\n'
  printf 'Media__ObjectStorage__BucketName=%s\n' "$r2_bucket_name"
  printf 'Media__ObjectStorage__AccessKeyId=%s\n' "$r2_access_key_id"
  printf 'Media__ObjectStorage__SecretAccessKey=%s\n' "$r2_secret_access_key"
  printf 'Media__ObjectStorage__ForcePathStyle=false\n'
  printf 'Media__Worker__Enabled=true\n'
  printf 'AccountProtectionKeyFile__Path=/etc/indiefi/keys/account-protection.json\n'
  printf 'AuthenticationEmail__Enabled=true\n'
  printf 'AuthenticationEmail__PublicBaseUrl=%s\n' "$public_origin"
  printf 'AuthenticationEmail__Smtp__Host=email-smtp.ap-northeast-2.amazonaws.com\n'
  printf 'AuthenticationEmail__Smtp__Port=587\n'
  printf 'AuthenticationEmail__Smtp__EnableSsl=true\n'
  printf 'AuthenticationEmail__Smtp__Username=%s\n' "$smtp_username"
  printf 'AuthenticationEmail__Smtp__Password=%s\n' "$smtp_password"
  printf 'AuthenticationEmail__Smtp__FromAddress=%s\n' "$smtp_from_address"
  printf 'AuthenticationEmail__Smtp__FromName=IndieFi\n'
  printf 'NotificationEmail__Enabled=true\n'
  printf 'NotificationEmail__PublicBaseUrl=%s\n' "$public_origin"
  printf 'NotificationEmail__Smtp__Host=email-smtp.ap-northeast-2.amazonaws.com\n'
  printf 'NotificationEmail__Smtp__Port=587\n'
  printf 'NotificationEmail__Smtp__EnableSsl=true\n'
  printf 'NotificationEmail__Smtp__Username=%s\n' "$smtp_username"
  printf 'NotificationEmail__Smtp__Password=%s\n' "$smtp_password"
  printf 'NotificationEmail__Smtp__FromAddress=%s\n' "$smtp_from_address"
  printf 'NotificationEmail__Smtp__FromName=IndieFi\n'
  printf 'NotificationMaintenance__Enabled=true\n'
  printf 'NotificationOperations__Enabled=true\n'
} > "$temporary_worker_file"
chmod 0600 "$temporary_worker_file"

"$verification_script" \
  "$infra_file" \
  "$temporary_web_file" \
  "$temporary_worker_file"

install -o root -g indiefi-web -m 0640 "$temporary_web_file" "$web_next"
install -o root -g indiefi-worker -m 0640 "$temporary_worker_file" "$worker_next"
mv -f -- "$web_next" "$web_file"
mv -f -- "$worker_next" "$worker_file"

[[ "$(stat -c '%U:%G %a' "$web_file")" == "root:indiefi-web 640" ]] \
  || fail "the installed Web environment file permissions are incorrect."
[[ "$(stat -c '%U:%G %a' "$worker_file")" == "root:indiefi-worker 640" ]] \
  || fail "the installed Worker environment file permissions are incorrect."

trap - EXIT
rm -rf -- "$temporary_directory"

echo "Portfolio Web and Worker runtime environment files are installed and valid."
echo "No secret values were printed. Do not delete database-bootstrap.env until the first deployment succeeds."
