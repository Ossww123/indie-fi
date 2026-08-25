#!/usr/bin/env bash
set -Eeuo pipefail
set +x

fail() {
  echo "Portfolio key material generation failed: $1" >&2
  exit 1
}

if [[ "${EUID:-$(id -u)}" != "0" ]]; then
  fail "run this script as root."
fi

[[ "$#" == "0" ]] || fail "this script does not accept arguments."

required_commands=(awk chmod date getent install mktemp openssl rm sha256sum stat tr wc)
for command_name in "${required_commands[@]}"; do
  command -v "$command_name" >/dev/null 2>&1 \
    || fail "required command is missing: $command_name"
done

getent group indiefi-secrets >/dev/null \
  || fail "the indiefi-secrets group is missing; install the service files first."
getent passwd indiefi-web >/dev/null \
  || fail "the indiefi-web service account is missing; install the service files first."

keys_directory="/etc/indiefi/keys"
account_key_file="$keys_directory/account-protection.json"
certificate_file="$keys_directory/data-protection.crt"
private_key_file="$keys_directory/data-protection.key"

for target in "$account_key_file" "$certificate_file" "$private_key_file"; do
  [[ ! -e "$target" && ! -L "$target" ]] \
    || fail "refusing to replace existing key material: $target"
done

install -d -o root -g indiefi-secrets -m 0750 "$keys_directory"

temporary_directory="$(mktemp -d "$keys_directory/.bootstrap.XXXXXXXX")"
case "$temporary_directory" in
  "$keys_directory/.bootstrap."*) ;;
  *) fail "mktemp returned an unexpected path." ;;
esac
chmod 0700 "$temporary_directory"

created_targets=()
cleanup() {
  local exit_code="$?"
  if [[ "$exit_code" != "0" && "${#created_targets[@]}" -gt "0" ]]; then
    rm -f -- "${created_targets[@]}"
  fi
  case "$temporary_directory" in
    "$keys_directory/.bootstrap."*) rm -rf -- "$temporary_directory" ;;
  esac
  return "$exit_code"
}
trap cleanup EXIT

declare -A generated_secrets=()
generate_secret() {
  local output_variable="$1"
  local value
  local decoded_length

  while true; do
    value="$(openssl rand -base64 32 | tr -d '\r\n')"
    [[ "$value" =~ ^[A-Za-z0-9+/=]{43,48}$ ]] || continue
    decoded_length="$(printf '%s' "$value" | openssl base64 -d -A | wc -c)"
    [[ "$decoded_length" == "32" ]] || continue
    [[ -z "${generated_secrets[$value]+present}" ]] || continue

    generated_secrets["$value"]=1
    printf -v "$output_variable" '%s' "$value"
    return 0
  done
}

generate_secret email_encryption_key
generate_secret email_lookup_key
generate_secret email_message_key
generate_secret request_ip_key
generate_secret game_encryption_key
generate_secret game_fingerprint_key

key_version="$(date -u +%Y-%m)"
email_key_id="email-$key_version"
lookup_key_id="lookup-$key_version"
message_key_id="message-$key_version"
request_ip_key_id="request-ip-$key_version"
game_encryption_key_id="game-encryption-$key_version"
game_fingerprint_key_id="game-fingerprint-$key_version"

temporary_account_key_file="$temporary_directory/account-protection.json"
{
  printf '{\n  "Purposes": {\n'
  printf '    "account-email-encryption": {\n'
  printf '      "CurrentKeyId": "%s",\n' "$email_key_id"
  printf '      "ReadKeyIds": ["%s"],\n' "$email_key_id"
  printf '      "WriteKeyIds": ["%s"],\n' "$email_key_id"
  printf '      "Keys": {"%s": "%s"}\n' "$email_key_id" "$email_encryption_key"
  printf '    },\n'
  printf '    "account-email-lookup": {\n'
  printf '      "CurrentKeyId": "%s",\n' "$lookup_key_id"
  printf '      "ReadKeyIds": ["%s"],\n' "$lookup_key_id"
  printf '      "WriteKeyIds": ["%s"],\n' "$lookup_key_id"
  printf '      "Keys": {"%s": "%s"}\n' "$lookup_key_id" "$email_lookup_key"
  printf '    },\n'
  printf '    "account-email-message-encryption": {\n'
  printf '      "CurrentKeyId": "%s",\n' "$message_key_id"
  printf '      "ReadKeyIds": ["%s"],\n' "$message_key_id"
  printf '      "WriteKeyIds": ["%s"],\n' "$message_key_id"
  printf '      "Keys": {"%s": "%s"}\n' "$message_key_id" "$email_message_key"
  printf '    },\n'
  printf '    "account-request-ip-lookup": {\n'
  printf '      "CurrentKeyId": "%s",\n' "$request_ip_key_id"
  printf '      "ReadKeyIds": ["%s"],\n' "$request_ip_key_id"
  printf '      "WriteKeyIds": ["%s"],\n' "$request_ip_key_id"
  printf '      "Keys": {"%s": "%s"}\n' "$request_ip_key_id" "$request_ip_key"
  printf '    },\n'
  printf '    "game-key-encryption": {\n'
  printf '      "CurrentKeyId": "%s",\n' "$game_encryption_key_id"
  printf '      "ReadKeyIds": ["%s"],\n' "$game_encryption_key_id"
  printf '      "WriteKeyIds": ["%s"],\n' "$game_encryption_key_id"
  printf '      "Keys": {"%s": "%s"}\n' "$game_encryption_key_id" "$game_encryption_key"
  printf '    },\n'
  printf '    "game-key-fingerprint": {\n'
  printf '      "CurrentKeyId": "%s",\n' "$game_fingerprint_key_id"
  printf '      "ReadKeyIds": ["%s"],\n' "$game_fingerprint_key_id"
  printf '      "WriteKeyIds": ["%s"],\n' "$game_fingerprint_key_id"
  printf '      "Keys": {"%s": "%s"}\n' "$game_fingerprint_key_id" "$game_fingerprint_key"
  printf '    }\n  }\n}\n'
} > "$temporary_account_key_file"
chmod 0600 "$temporary_account_key_file"

temporary_certificate_file="$temporary_directory/data-protection.crt"
temporary_private_key_file="$temporary_directory/data-protection.key"
openssl req \
  -x509 \
  -newkey rsa:3072 \
  -sha256 \
  -nodes \
  -keyout "$temporary_private_key_file" \
  -out "$temporary_certificate_file" \
  -days 365 \
  -subj '/CN=IndieFi Data Protection' \
  >/dev/null 2>&1
chmod 0600 "$temporary_private_key_file"

openssl x509 -in "$temporary_certificate_file" -noout -checkend 2592000 \
  >/dev/null || fail "the generated certificate is not valid for at least 30 days."
openssl pkey -in "$temporary_private_key_file" -check -noout \
  >/dev/null || fail "the generated RSA private key is invalid."

certificate_public_hash="$(
  openssl x509 -in "$temporary_certificate_file" -pubkey -noout \
    | openssl pkey -pubin -outform DER 2>/dev/null \
    | sha256sum \
    | awk '{ print $1 }'
)"
private_public_hash="$(
  openssl pkey -in "$temporary_private_key_file" -pubout -outform DER 2>/dev/null \
    | sha256sum \
    | awk '{ print $1 }'
)"
[[ "$certificate_public_hash" == "$private_public_hash" ]] \
  || fail "the generated certificate and private key do not match."

install -o root -g indiefi-secrets -m 0640 \
  "$temporary_account_key_file" "$account_key_file"
created_targets+=("$account_key_file")
install -o indiefi-web -g indiefi-web -m 0640 \
  "$temporary_certificate_file" "$certificate_file"
created_targets+=("$certificate_file")
install -o indiefi-web -g indiefi-web -m 0600 \
  "$temporary_private_key_file" "$private_key_file"
created_targets+=("$private_key_file")

[[ "$(stat -c '%U:%G %a' "$account_key_file")" == "root:indiefi-secrets 640" ]] \
  || fail "the account protection key file permissions are incorrect."
[[ "$(stat -c '%U:%G %a' "$certificate_file")" == "indiefi-web:indiefi-web 640" ]] \
  || fail "the Data Protection certificate permissions are incorrect."
[[ "$(stat -c '%U:%G %a' "$private_key_file")" == "indiefi-web:indiefi-web 600" ]] \
  || fail "the Data Protection private key permissions are incorrect."

trap - EXIT
rm -rf -- "$temporary_directory"

echo "Portfolio account protection keys and the Web Data Protection certificate are installed."
echo "No secret values were printed. Back up the three files outside the EC2 instance before production use."
