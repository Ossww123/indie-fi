#!/usr/bin/env bash
set -Eeuo pipefail

fail() {
  echo "Portfolio deployment failed: $1" >&2
  exit 1
}

[[ "$#" == "1" ]] || fail "usage: $0 <40-character-release-commit>"

release_id="$1"
[[ "$release_id" =~ ^[0-9a-f]{40}$ ]] \
  || fail "the release identifier must be a full lowercase Git commit SHA."

upload_root="/home/ubuntu/indiefi-upload"
release_root="/opt/indiefi/releases"
current_link="/opt/indiefi/current"
archive="$upload_root/$release_id.tar.gz"
archive_checksum="$archive.sha256"
target_release="$release_root/$release_id"
incoming_release=""
deployment_started=0
previous_release=""

cleanup() {
  local exit_code="$?"

  if [[ -n "$incoming_release" ]]; then
    case "$incoming_release" in
      "$release_root/.incoming-$release_id."*)
        sudo -n rm -rf -- "$incoming_release"
        ;;
      *)
        echo "Refusing to clean an unexpected incoming path: $incoming_release" >&2
        ;;
    esac
  fi

  if [[ "$exit_code" != "0" && "$deployment_started" == "1" ]]; then
    echo "Deployment stopped after the services entered maintenance." >&2
    if [[ -n "$previous_release" ]]; then
      echo "Previous release: $previous_release" >&2
    fi
    sudo -n systemctl --no-pager --full status \
      indiefi-web.service indiefi-worker.service >&2 || true
  fi
}
trap cleanup EXIT

required_commands=(
  awk
  chmod
  chown
  curl
  date
  dotnet
  grep
  install
  journalctl
  mktemp
  mv
  readlink
  rm
  seq
  sha256sum
  sleep
  sudo
  systemctl
  tar
  wc
)
for command_name in "${required_commands[@]}"; do
  command -v "$command_name" >/dev/null 2>&1 \
    || fail "required command is missing: $command_name"
done

sudo -n true || fail "the deployment user needs non-interactive sudo access."

[[ -f "$archive" && ! -L "$archive" ]] \
  || fail "the release archive is missing or is a symlink."
[[ -f "$archive_checksum" && ! -L "$archive_checksum" ]] \
  || fail "the release archive checksum is missing or is a symlink."

[[ "$(wc -l < "$archive_checksum")" == "1" ]] \
  || fail "the archive checksum file must contain one record."
expected_archive_hash="$(awk 'NF == 2 { print $1 }' "$archive_checksum")"
[[ "$expected_archive_hash" =~ ^[0-9a-f]{64}$ ]] \
  || fail "the archive checksum record is malformed."
actual_archive_hash="$(sha256sum "$archive" | awk '{ print $1 }')"
[[ "$actual_archive_hash" == "$expected_archive_hash" ]] \
  || fail "the uploaded release archive checksum does not match."

while IFS= read -r archive_entry; do
  normalized_entry="${archive_entry#./}"
  [[ -z "$normalized_entry" ]] && continue
  [[ "$normalized_entry" != /* ]] \
    || fail "the release archive contains an invalid path."
  case "/$normalized_entry/" in
    */../*) fail "the release archive contains a parent path traversal." ;;
  esac
done < <(tar -tzf "$archive")

sudo -n install -d -m 0755 "$release_root"

if [[ -L "$target_release" ]]; then
  fail "an immutable release path may not be a symlink."
fi
if [[ -e "$target_release" && ! -d "$target_release" ]]; then
  fail "an immutable release path exists but is not a directory."
fi

if [[ ! -d "$target_release" ]]; then
  incoming_release="$(
    sudo -n mktemp -d \
      --tmpdir="$release_root" \
      ".incoming-$release_id.XXXXXXXX"
  )"

  case "$incoming_release" in
    "$release_root/.incoming-$release_id."*) ;;
    *) fail "mktemp returned an unexpected incoming release path." ;;
  esac

  sudo -n tar \
    --extract \
    --gzip \
    --file "$archive" \
    --directory "$incoming_release" \
    --no-same-owner
  sudo -n chown -R root:root "$incoming_release"
  sudo -n chmod -R u=rwX,go=rX "$incoming_release"
  sudo -n mv "$incoming_release" "$target_release"
  incoming_release=""
fi

[[ -f "$target_release/release.sha" ]] \
  || fail "the release commit marker is missing."
[[ "$(< "$target_release/release.sha")" == "$release_id" ]] \
  || fail "the release commit marker does not match the requested commit."
[[ -f "$target_release/SHA256SUMS" ]] \
  || fail "the release file manifest is missing."
(
  cd "$target_release"
  sha256sum --check --strict --quiet SHA256SUMS
) || fail "one or more release files do not match the manifest."

[[ -x "$target_release/efbundle" ]] \
  || fail "the migration bundle is missing or is not executable."
[[ -f "$target_release/web/IndieFi.Web.dll" ]] \
  || fail "the Web artifact is missing."
[[ -f "$target_release/worker/IndieFi.Worker.dll" ]] \
  || fail "the Worker artifact is missing."
[[ -x "$target_release/ops/install-portfolio-service-files.sh" ]] \
  || fail "the host service installer is missing or is not executable."

installed_runtimes="$(dotnet --list-runtimes)"
grep -q '^Microsoft.NETCore.App 10\.' <<< "$installed_runtimes" \
  || fail ".NET 10 runtime is not installed."
grep -q '^Microsoft.AspNetCore.App 10\.' <<< "$installed_runtimes" \
  || fail "ASP.NET Core 10 runtime is not installed."

sudo -n "$target_release/ops/install-portfolio-service-files.sh"
sudo -n /usr/local/lib/indiefi/verify-portfolio-environment.sh \
  /etc/indiefi/infra.env \
  /etc/indiefi/web.env \
  /etc/indiefi/worker.env

sudo -n systemctl cat indiefi-web.service >/dev/null
sudo -n systemctl cat indiefi-worker.service >/dev/null

if sudo -n systemctl is-active --quiet indiefi.service \
  || sudo -n systemctl is-enabled --quiet indiefi.service; then
  fail "the legacy indiefi.service is still active or enabled; disable it before the first split-service deployment."
fi

if [[ -e "$current_link" && ! -L "$current_link" ]]; then
  fail "$current_link exists but is not a symlink."
fi

previous_release="$(readlink -f "$current_link" 2>/dev/null || true)"
deployment_started=1
deployment_start="$(date +%s)"

# Stop background writes first, then stop public traffic before changing the schema.
sudo -n systemctl stop indiefi-worker.service
sudo -n systemctl stop indiefi-web.service

# Read the connection string inside the privileged shell. It is exported directly
# to the bundle and never appears as a command argument or workflow log line.
sudo -n /bin/bash -s -- "$target_release/efbundle" <<'MIGRATION'
set -Eeuo pipefail
database_connection="$(
  awk -v prefix='ConnectionStrings__Database=' '
    { sub(/\r$/, "") }
    index($0, prefix) == 1 {
      print substr($0, length(prefix) + 1)
      exit
    }
  ' /etc/indiefi/web.env
)"
[[ -n "$database_connection" ]] || {
  echo "The Web database connection is missing." >&2
  exit 1
}
export ConnectionStrings__Database="$database_connection"
exec "$1"
MIGRATION

next_link="/opt/indiefi/.current-next-$release_id"
sudo -n ln -sfnT "$target_release" "$next_link"
sudo -n mv -Tf "$next_link" "$current_link"
[[ "$(readlink -f "$current_link")" == "$target_release" ]] \
  || fail "the current release symlink did not move to the new release."

allowed_host="$(
  sudo -n awk -v prefix='AllowedHosts=' '
    { sub(/\r$/, "") }
    index($0, prefix) == 1 {
      print substr($0, length(prefix) + 1)
      exit
    }
  ' /etc/indiefi/web.env
)"
[[ -n "$allowed_host" ]] || fail "AllowedHosts is missing from web.env."

sudo -n systemctl reset-failed indiefi-web.service || true
sudo -n systemctl start indiefi-web.service

web_ready=0
for _ in $(seq 1 30); do
  if ! sudo -n systemctl is-active --quiet indiefi-web.service; then
    break
  fi

  if curl \
    --fail \
    --silent \
    --show-error \
    --max-time 5 \
    --header "Host: $allowed_host" \
    --header "X-Forwarded-Proto: https" \
    http://127.0.0.1:5000/health/ready \
    >/dev/null; then
    web_ready=1
    break
  fi

  sleep 2
done

if [[ "$web_ready" != "1" ]]; then
  sudo -n journalctl -u indiefi-web.service -n 100 --no-pager >&2 || true
  fail "the Web service did not become ready."
fi

sudo -n systemctl reset-failed indiefi-worker.service || true
sudo -n systemctl start indiefi-worker.service

worker_stable_checks=0
for _ in $(seq 1 10); do
  if ! sudo -n systemctl is-active --quiet indiefi-worker.service; then
    break
  fi
  worker_stable_checks=$((worker_stable_checks + 1))
  sleep 1
done

if [[ "$worker_stable_checks" != "10" ]]; then
  sudo -n journalctl -u indiefi-worker.service -n 100 --no-pager >&2 || true
  fail "the Worker service did not remain active during the startup window."
fi

sudo -n systemctl is-active --quiet indiefi-web.service \
  || fail "the Web service stopped after the Worker started."

rm -f -- "$archive" "$archive_checksum"

echo "Release $release_id is active."
echo "Deployment maintenance window: $(( $(date +%s) - deployment_start )) seconds."
