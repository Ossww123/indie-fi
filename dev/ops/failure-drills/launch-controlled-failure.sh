#!/usr/bin/env bash
set -Eeuo pipefail

readonly TOOL=/usr/local/lib/indiefi/failure-drills/run-controlled-failure.sh
readonly RUNTIME_GRACE_SECONDS=180

fail() {
  echo "failure-drill-launcher: $*" >&2
  exit 1
}

usage() {
  cat <<'EOF'
Usage:
  sudo launch-controlled-failure.sh <scenario> <duration-seconds> <run-id> RUN-<run-id>-<scenario> [PREPARED-WORKER-STOPPED]

The drill runs as an independent transient systemd service. Closing SSH does not
stop the service or bypass the drill's bounded recovery path.
EOF
}

(( EUID == 0 )) || fail "run this launcher as root."
[[ "$#" == "4" || "$#" == "5" ]] \
  || { usage; fail "expected four or five arguments."; }
[[ -x "$TOOL" ]] \
  || fail "the installed controlled failure tool is missing."
command -v systemctl >/dev/null 2>&1 || fail "systemctl is required."
command -v systemd-run >/dev/null 2>&1 || fail "systemd-run is required."

scenario="$1"
duration_seconds="$2"
run_id="$3"
confirmation="$4"

[[ "$duration_seconds" =~ ^[0-9]+$ ]] \
  || fail "duration must be an integer."
(( duration_seconds >= 10 && duration_seconds <= 180 )) \
  || fail "duration must be between 10 and 180 seconds."
[[ "$run_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]] \
  || fail "run id must contain only letters, digits, dot, underscore, or hyphen."
[[ "$confirmation" == "RUN-${run_id}-${scenario}" ]] \
  || fail "confirmation must be RUN-${run_id}-${scenario}."

unit_base="indiefi-failure-drill-$(date -u +%Y%m%dT%H%M%S)-$$"
unit_name="${unit_base}.service"
runtime_max_seconds=$((duration_seconds + RUNTIME_GRACE_SECONDS))

systemd-run \
  --quiet \
  --unit="$unit_base" \
  --collect \
  --property=Type=exec \
  --property="RuntimeMaxSec=${runtime_max_seconds}s" \
  --property=TimeoutStopSec=120s \
  -- "$TOOL" "$@" >/dev/null

deadline=$((SECONDS + 10))
while (( SECONDS < deadline )); do
  load_state="$(
    systemctl show "$unit_name" --property=LoadState --value 2>/dev/null \
      || true
  )"
  active_state="$(
    systemctl show "$unit_name" --property=ActiveState --value 2>/dev/null \
      || true
  )"
  case "$active_state" in
    active|activating)
      printf 'unit=%s\n' "$unit_name"
      printf 'runtime_max_seconds=%s\n' "$runtime_max_seconds"
      printf 'journal_command=sudo journalctl -u %s --no-pager\n' "$unit_name"
      exit 0
      ;;
    failed|inactive)
      fail "$unit_name stopped before the launcher confirmed it was running; inspect its journal."
      ;;
  esac
  [[ "$load_state" != "not-found" ]] || sleep 1
done

fail "$unit_name did not enter an active state within 10 seconds."
