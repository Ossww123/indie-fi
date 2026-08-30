#!/usr/bin/env bash
set -Eeuo pipefail

fail() {
  echo "$1" >&2
  exit "${2:-64}"
}

[[ "${EUID:-$(id -u)}" == "0" ]] \
  || fail "Run through sudo so the wrapper can create a protected run directory." 77
[[ "$#" -ge 3 ]] || fail \
  "usage: $0 prepare <run-id> <base-url> <commit> <owners> <users> <late> <retries> <games> <keys> <opens-in> | audit <run-id> <before|after>"

command_name="$1"
run_id="$2"
[[ "$run_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]] \
  || fail "run id has an invalid format."

release_path="$(readlink -f /opt/indiefi/current 2>/dev/null || true)"
[[ "$release_path" =~ ^/opt/indiefi/releases/([0-9a-f]{40})$ ]] \
  || fail "the active release path is invalid." 69
release_commit="${BASH_REMATCH[1]}"
seed_dll="$release_path/seed/IndieFi.Seed.dll"
web_environment="/etc/indiefi/web.env"
credentials="/etc/indiefi/loadtest-credentials.json"
run_root="/var/lib/indiefi/loadtest"
run_directory="$run_root/$run_id"

[[ -f "$seed_dll" && ! -L "$seed_dll" ]] \
  || fail "the active release does not contain the seed tool." 69
[[ -f "$web_environment" && ! -L "$web_environment" ]] \
  || fail "the Web environment file is missing or unsafe." 69
[[ "$run_directory" == "$run_root/"* ]] \
  || fail "the resolved run directory escaped the loadtest root." 77

run_seed() {
  local unit_suffix
  unit_suffix="$(printf '%s' "$run_id-$command_name-$$" | sha256sum | cut -c1-16)"
  systemd-run \
    --quiet \
    --wait \
    --pipe \
    --collect \
    --unit="indiefi-load-$unit_suffix.service" \
    --uid=indiefi-web \
    --gid=indiefi-web \
    --property=SupplementaryGroups=indiefi-secrets \
    --property=EnvironmentFile="$web_environment" \
    --property=WorkingDirectory="$release_path" \
    --property=NoNewPrivileges=yes \
    --property=PrivateTmp=yes \
    --property=ProtectHome=yes \
    --property=ProtectSystem=strict \
    --property="ReadWritePaths=$run_directory" \
    -- \
    /usr/bin/dotnet "$seed_dll" "$@"
}

case "$command_name" in
  prepare)
    [[ "$#" == "11" ]] || fail \
      "prepare requires run-id, base-url, commit and seven numeric scale arguments."
    base_url="$3"
    commit="$4"
    [[ "$commit" == "$release_commit" ]] \
      || fail "the requested commit is not the active release."
    [[ -f "$credentials" && ! -L "$credentials" ]] \
      || fail "install /etc/indiefi/loadtest-credentials.json first." 69
    [[ "$(stat -c '%U:%G %a' "$credentials")" == "root:indiefi-web 640" ]] \
      || fail "load-test credentials must be root:indiefi-web mode 640." 77
    [[ ! -e "$run_directory" ]] \
      || fail "the run directory already exists; use a new run id."
    install -d -o indiefi-web -g indiefi-web -m 0750 "$run_directory"
    run_seed prepare \
      --run-id "$run_id" \
      --output "$run_directory/seed-manifest.json" \
      --credentials "$credentials" \
      --base-url "$base_url" \
      --commit "$commit" \
      --owners "$5" \
      --users "$6" \
      --late-users "$7" \
      --retry-users "$8" \
      --games "$9" \
      --keys "${10}" \
      --opens-in "${11}"
    ;;
  audit)
    [[ "$#" == "3" ]] || fail "audit requires run-id and before|after."
    phase="$3"
    [[ "$phase" == "before" || "$phase" == "after" ]] \
      || fail "audit phase must be before or after."
    [[ -d "$run_directory" && ! -L "$run_directory" ]] \
      || fail "the run directory is missing or unsafe." 69
    [[ -f "$run_directory/seed-manifest.json" ]] \
      || fail "the seed manifest is missing." 69
    output="$run_directory/$phase-audit.json"
    [[ ! -e "$output" ]] || fail "$phase audit already exists."
    run_seed audit \
      --manifest "$run_directory/seed-manifest.json" \
      --output "$output" \
      --phase "$phase"
    ;;
  *)
    fail "command must be prepare or audit."
    ;;
esac
