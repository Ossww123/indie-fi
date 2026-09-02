#!/usr/bin/env bash
set -Eeuo pipefail
set +x

fail() {
  echo "Portfolio R2 credential rotation failed: $1" >&2
  exit 1
}

if [[ "${EUID:-$(id -u)}" != "0" ]]; then
  fail "run this script as root."
fi

[[ "$#" == "0" ]] || fail "this script does not accept arguments."
[[ -t 0 && -t 1 ]] || fail "run this script from an interactive terminal."

required_commands=(chmod install mktemp mv rm stat)
for command_name in "${required_commands[@]}"; do
  command -v "$command_name" >/dev/null 2>&1 \
    || fail "required command is missing: $command_name"
done

infra_file="/etc/indiefi/infra.env"
web_file="/etc/indiefi/web.env"
worker_file="/etc/indiefi/worker.env"
verification_script="/usr/local/lib/indiefi/verify-portfolio-environment.sh"

[[ -f "$infra_file" && ! -L "$infra_file" ]] \
  || fail "the protected infrastructure environment file is missing or is a symlink."
[[ "$(stat -c '%U:%G %a' "$infra_file")" == "root:root 600" ]] \
  || fail "the infrastructure environment file must be root:root with mode 0600."
[[ -x "$verification_script" ]] \
  || fail "the installed environment verification script is missing."

for target_file in "$web_file" "$worker_file"; do
  [[ -f "$target_file" && ! -L "$target_file" ]] \
    || fail "runtime target must be a regular file: $target_file"
done

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

prompt_hidden() {
  local output_variable="$1"
  local prompt="$2"
  local value

  printf '%s' "$prompt"
  IFS= read -r -s value || fail "input ended before rotation was complete."
  printf '\n'
  validate_environment_value "$output_variable" "$value"
  printf -v "$output_variable" '%s' "$value"
}

replace_exact_key() {
  local source_file="$1"
  local destination_file="$2"
  local key="$3"
  local value="$4"
  local line
  local matches=0

  : > "$destination_file"
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    if [[ "$line" == "$key="* ]]; then
      printf '%s=%s\n' "$key" "$value" >> "$destination_file"
      matches=$((matches + 1))
    else
      printf '%s\n' "$line" >> "$destination_file"
    fi
  done < "$source_file"

  [[ "$matches" == "1" ]] \
    || fail "$source_file must contain $key exactly once."
}

echo "Enter the replacement Cloudflare R2 credentials. Hidden inputs are not echoed or stored in shell history."
prompt_hidden r2_access_key_id "New Cloudflare R2 access key ID (hidden): "
prompt_hidden r2_secret_access_key "New Cloudflare R2 secret access key (hidden): "
[[ "$r2_access_key_id" != "$r2_secret_access_key" ]] \
  || fail "the R2 access key ID and secret access key must differ."

printf 'Replace the R2 credentials in both Web and Worker environments? [y/N]: '
IFS= read -r confirmation || fail "confirmation input ended unexpectedly."
[[ "$confirmation" == "y" || "$confirmation" == "Y" ]] \
  || fail "rotation was cancelled before any runtime file was changed."

temporary_directory="$(mktemp -d /etc/indiefi/.r2-rotation.XXXXXXXX)"
case "$temporary_directory" in
  /etc/indiefi/.r2-rotation.*) ;;
  *) fail "mktemp returned an unexpected path." ;;
esac
chmod 0700 "$temporary_directory"

cleanup() {
  local exit_code="$?"
  case "$temporary_directory" in
    /etc/indiefi/.r2-rotation.*) rm -rf -- "$temporary_directory" ;;
  esac
  return "$exit_code"
}
trap cleanup EXIT

web_access="$temporary_directory/web-access.env"
web_next="$temporary_directory/web.env"
worker_access="$temporary_directory/worker-access.env"
worker_next="$temporary_directory/worker.env"

replace_exact_key \
  "$web_file" "$web_access" \
  Media__ObjectStorage__AccessKeyId "$r2_access_key_id"
replace_exact_key \
  "$web_access" "$web_next" \
  Media__ObjectStorage__SecretAccessKey "$r2_secret_access_key"
replace_exact_key \
  "$worker_file" "$worker_access" \
  Media__ObjectStorage__AccessKeyId "$r2_access_key_id"
replace_exact_key \
  "$worker_access" "$worker_next" \
  Media__ObjectStorage__SecretAccessKey "$r2_secret_access_key"
chmod 0600 "$web_access" "$web_next" "$worker_access" "$worker_next"

"$verification_script" "$infra_file" "$web_next" "$worker_next"

install -o root -g indiefi-web -m 0640 "$web_next" /etc/indiefi/.web.env.next
install -o root -g indiefi-worker -m 0640 "$worker_next" /etc/indiefi/.worker.env.next
mv -f -- /etc/indiefi/.web.env.next "$web_file"
mv -f -- /etc/indiefi/.worker.env.next "$worker_file"

[[ "$(stat -c '%U:%G %a' "$web_file")" == "root:indiefi-web 640" ]] \
  || fail "the installed Web environment file permissions are incorrect."
[[ "$(stat -c '%U:%G %a' "$worker_file")" == "root:indiefi-worker 640" ]] \
  || fail "the installed Worker environment file permissions are incorrect."

trap - EXIT
rm -rf -- "$temporary_directory"

echo "Portfolio R2 credentials were rotated in both runtime environment files."
echo "No secret values were printed. Restart Web and Worker to load the new credentials."
