#!/usr/bin/env bash
set -Eeuo pipefail

readonly MAX_DURATION_SECONDS=180
readonly LOCK_FILE=/run/lock/indiefi-failure-drill.lock
readonly DROP_IN_NAME=90-indiefi-failure-drill.conf

scenario="${1:-}"
duration_seconds="${2:-60}"
run_id="${3:-}"
confirmation="${4:-}"
plan_only=false
restored=false
fault_applied=false

if [[ "${1:-}" == "--plan" ]]; then
  plan_only=true
  scenario="${2:-}"
  duration_seconds="${3:-60}"
  run_id="${4:-local-plan}"
fi

fail() {
  echo "failure-drill: $*" >&2
  exit 1
}

usage() {
  cat <<'EOF'
Usage:
  run-controlled-failure.sh --plan <scenario> [duration-seconds] [run-id]
  sudo run-controlled-failure.sh <scenario> <duration-seconds> <run-id> RUN-<run-id>-<scenario>

Scenarios:
  web-restart worker-restart postgresql-outage redis-outage
  rabbitmq-outage r2-outage ses-outage
EOF
}

is_known_scenario() {
  case "$1" in
    web-restart|worker-restart|postgresql-outage|redis-outage|rabbitmq-outage|r2-outage|ses-outage)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

validate_inputs() {
  is_known_scenario "$scenario" || { usage; fail "unknown scenario '$scenario'."; }
  [[ "$duration_seconds" =~ ^[0-9]+$ ]] || fail "duration must be an integer."
  (( duration_seconds >= 10 && duration_seconds <= MAX_DURATION_SECONDS )) \
    || fail "duration must be between 10 and ${MAX_DURATION_SECONDS} seconds."
  [[ "$run_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]] \
    || fail "run id must contain only letters, digits, dot, underscore, or hyphen."

  if ! $plan_only; then
    (( EUID == 0 )) || fail "actual execution requires root."
    [[ "$confirmation" == "RUN-${run_id}-${scenario}" ]] \
      || fail "confirmation must be RUN-${run_id}-${scenario}."
  fi
}

describe_plan() {
  printf 'scenario=%s\nrun_id=%s\nduration_seconds=%s\n' \
    "$scenario" "$run_id" "$duration_seconds"
  case "$scenario" in
    web-restart) echo 'action=restart systemd unit indiefi-web.service once' ;;
    worker-restart) echo 'action=SIGKILL Worker main process once and await systemd on-failure restart with a new PID' ;;
    postgresql-outage) echo 'action=stop exact container indiefi-db, wait, then start and await healthy' ;;
    redis-outage) echo 'action=stop exact container indiefi-redis, wait, then start and await healthy' ;;
    rabbitmq-outage) echo 'action=stop exact container indiefi-rabbitmq, wait, then start and await healthy' ;;
    r2-outage) echo 'action=inject /run systemd drop-ins for Web and Worker, wait, then remove and restart' ;;
    ses-outage) echo 'action=inject /run systemd drop-in for Worker, wait, then remove and restart' ;;
  esac
  echo 'automatic_recovery=enabled'
  echo 'persistent_configuration_changes=none'
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command '$1' was not found."
}

wait_for_unit() {
  local unit="$1"
  local deadline=$((SECONDS + 60))
  until systemctl is-active --quiet "$unit"; do
    (( SECONDS < deadline )) || fail "$unit did not become active within 60 seconds."
    sleep 2
  done
}

wait_for_restarted_unit() {
  local unit="$1"
  local previous_pid="$2"
  local deadline=$((SECONDS + 60))
  local current_pid
  until systemctl is-active --quiet "$unit" \
      && current_pid="$(systemctl show --property=MainPID --value "$unit")" \
      && [[ "$current_pid" =~ ^[1-9][0-9]*$ ]] \
      && [[ "$current_pid" != "$previous_pid" ]]; do
    (( SECONDS < deadline )) || fail "$unit did not restart with a new process within 60 seconds."
    sleep 1
  done
}

wait_for_container_health() {
  local container="$1"
  local deadline=$((SECONDS + 90))
  local status
  until status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container" 2>/dev/null)" \
      && [[ "$status" == "healthy" || "$status" == "running" ]]; do
    (( SECONDS < deadline )) || fail "$container did not become healthy within 90 seconds."
    sleep 2
  done
}

require_baseline() {
  local container
  systemctl is-active --quiet indiefi-web.service \
    || fail "indiefi-web.service must be active before the drill."
  systemctl is-active --quiet indiefi-worker.service \
    || fail "indiefi-worker.service must be active before the drill."
  for container in indiefi-db indiefi-redis indiefi-rabbitmq; do
    [[ "$(docker inspect --format '{{.State.Status}}' "$container" 2>/dev/null)" == "running" ]] \
      || fail "$container must be running before the drill."
    [[ "$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$container")" == "healthy" ]] \
      || fail "$container must be healthy before the drill."
  done
}

write_drop_in() {
  local unit="$1"
  shift
  local directory="/run/systemd/system/${unit}.d"
  install -d -m 0755 "$directory"
  {
    echo '[Service]'
    local setting
    for setting in "$@"; do
      printf 'Environment="%s"\n' "$setting"
    done
  } >"${directory}/${DROP_IN_NAME}"
  chmod 0644 "${directory}/${DROP_IN_NAME}"
}

remove_drop_in() {
  local unit="$1"
  rm -f "/run/systemd/system/${unit}.d/${DROP_IN_NAME}"
  rmdir "/run/systemd/system/${unit}.d" 2>/dev/null || true
}

restore() {
  local exit_code=$?
  $restored && return "$exit_code"
  restored=true
  trap - EXIT HUP INT TERM

  if $fault_applied; then
    echo "failure-drill: restoring scenario=$scenario"
    case "$scenario" in
      postgresql-outage)
        docker start indiefi-db >/dev/null || true
        wait_for_container_health indiefi-db || true
        ;;
      redis-outage)
        docker start indiefi-redis >/dev/null || true
        wait_for_container_health indiefi-redis || true
        ;;
      rabbitmq-outage)
        docker start indiefi-rabbitmq >/dev/null || true
        wait_for_container_health indiefi-rabbitmq || true
        ;;
      r2-outage)
        remove_drop_in indiefi-web.service
        remove_drop_in indiefi-worker.service
        systemctl daemon-reload || true
        systemctl restart indiefi-web.service indiefi-worker.service || true
        wait_for_unit indiefi-web.service || true
        wait_for_unit indiefi-worker.service || true
        ;;
      ses-outage)
        remove_drop_in indiefi-worker.service
        systemctl daemon-reload || true
        systemctl restart indiefi-worker.service || true
        wait_for_unit indiefi-worker.service || true
        ;;
    esac
  fi

  echo "failure-drill: recovery attempted scenario=$scenario exit_code=$exit_code"
  return "$exit_code"
}

apply_fault() {
  echo "failure-drill: applying scenario=$scenario run_id=$run_id at $(date -Is)"
  case "$scenario" in
    web-restart)
      systemctl restart indiefi-web.service
      wait_for_unit indiefi-web.service
      ;;
    worker-restart)
      previous_pid="$(systemctl show --property=MainPID --value indiefi-worker.service)"
      [[ "$previous_pid" =~ ^[1-9][0-9]*$ ]] \
        || fail "indiefi-worker.service has no running main process."
      systemctl kill --kill-who=main --signal=SIGKILL indiefi-worker.service
      wait_for_restarted_unit indiefi-worker.service "$previous_pid"
      ;;
    postgresql-outage)
      fault_applied=true
      docker stop --time 20 indiefi-db >/dev/null
      sleep "$duration_seconds"
      ;;
    redis-outage)
      fault_applied=true
      docker stop --time 20 indiefi-redis >/dev/null
      sleep "$duration_seconds"
      ;;
    rabbitmq-outage)
      fault_applied=true
      docker stop --time 20 indiefi-rabbitmq >/dev/null
      sleep "$duration_seconds"
      ;;
    r2-outage)
      write_drop_in indiefi-web.service \
        'Media__ObjectStorage__ServiceUrl=https://127.0.0.1:1' \
        'Media__ObjectStorage__ForcePathStyle=true' \
        'Media__ObjectStorage__OperationTimeout=00:00:03'
      write_drop_in indiefi-worker.service \
        'Media__ObjectStorage__ServiceUrl=https://127.0.0.1:1' \
        'Media__ObjectStorage__ForcePathStyle=true' \
        'Media__ObjectStorage__OperationTimeout=00:00:03'
      fault_applied=true
      systemctl daemon-reload
      systemctl restart indiefi-web.service indiefi-worker.service
      wait_for_unit indiefi-web.service
      wait_for_unit indiefi-worker.service
      sleep "$duration_seconds"
      ;;
    ses-outage)
      write_drop_in indiefi-worker.service \
        'AuthenticationEmail__Smtp__Host=127.0.0.1' \
        'AuthenticationEmail__Smtp__Port=1' \
        'AuthenticationEmail__Smtp__EnableSsl=false' \
        'AuthenticationEmail__Smtp__Timeout=00:00:03' \
        'NotificationEmail__Smtp__Host=127.0.0.1' \
        'NotificationEmail__Smtp__Port=1' \
        'NotificationEmail__Smtp__EnableSsl=false' \
        'NotificationEmail__Smtp__Timeout=00:00:03'
      fault_applied=true
      systemctl daemon-reload
      systemctl restart indiefi-worker.service
      wait_for_unit indiefi-worker.service
      sleep "$duration_seconds"
      ;;
  esac
}

validate_inputs
describe_plan
$plan_only && exit 0

for command_name in docker flock install systemctl; do
  require_command "$command_name"
done

exec 9>"$LOCK_FILE"
flock -n 9 || fail "another failure drill is already running."
require_baseline
trap restore EXIT HUP INT TERM
apply_fault
echo "failure-drill: observation window complete at $(date -Is)"
