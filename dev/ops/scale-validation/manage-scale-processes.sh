#!/usr/bin/env bash
set -Eeuo pipefail

readonly runtime_directory=/run/indiefi-scale
readonly marker_file="${runtime_directory}/armed"
readonly lock_file=/run/lock/indiefi-scale-validation.lock
readonly web_units=(indiefi-scale-web@1.service indiefi-scale-web@2.service)
readonly worker_units=(indiefi-scale-worker@1.service indiefi-scale-worker@2.service)

command_name="${1:-}"
run_id="${2:-}"
confirmation="${3:-}"

usage() {
  cat <<'EOF'
Usage:
  manage-scale-processes.sh --plan
  sudo manage-scale-processes.sh start <run-id> START-SCALE-PROCESSES-<run-id>
  sudo manage-scale-processes.sh stop <run-id> STOP-SCALE-PROCESSES-<run-id>
  sudo manage-scale-processes.sh restart-web-1 <run-id> RESTART-WEB-1-<run-id>
  sudo manage-scale-processes.sh restart-web-2 <run-id> RESTART-WEB-2-<run-id>
  sudo manage-scale-processes.sh restart-worker-1 <run-id> RESTART-WORKER-1-<run-id>
  sudo manage-scale-processes.sh restart-worker-2 <run-id> RESTART-WORKER-2-<run-id>
  sudo manage-scale-processes.sh stop-web-1 <run-id> STOP-WEB-1-<run-id>
  sudo manage-scale-processes.sh start-web-1 <run-id> START-WEB-1-<run-id>
  sudo manage-scale-processes.sh stop-web-2 <run-id> STOP-WEB-2-<run-id>
  sudo manage-scale-processes.sh start-web-2 <run-id> START-WEB-2-<run-id>
  manage-scale-processes.sh status
EOF
}

fail() {
  echo "scale-processes: $*" >&2
  exit 1
}

validate_run_id() {
  [[ "$run_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]] \
    || fail "invalid run id."
}

require_root() {
  (( EUID == 0 )) || fail "this command requires root."
}

require_armed_run() {
  [[ -r "$marker_file" ]] || fail "the scale topology is not armed."
  [[ "$(<"$marker_file")" == "$run_id" ]] \
    || fail "the armed run id does not match."
}

wait_for_unit() {
  local unit="$1"
  local expected="$2"
  local deadline=$((SECONDS + 60))
  while true; do
    if [[ "$expected" == active ]] && systemctl is-active --quiet "$unit"; then
      return 0
    fi
    if [[ "$expected" == inactive ]] && ! systemctl is-active --quiet "$unit"; then
      return 0
    fi
    (( SECONDS < deadline )) || fail "$unit did not become $expected within 60 seconds."
    sleep 1
  done
}

wait_for_listener() {
  local unit="$1"
  local port="$2"
  local deadline=$((SECONDS + 60))
  while true; do
    if ss -lnt | awk '{print $4}' | grep -Eq "(^|:)${port}$"; then
      return 0
    fi
    systemctl is-active --quiet "$unit" \
      || fail "$unit stopped before opening port $port."
    (( SECONDS < deadline )) \
      || fail "$unit did not open port $port within 60 seconds."
    sleep 1
  done
}

write_instance_environments() {
  install -d -o root -g root -m 0700 "$runtime_directory"
  printf 'ASPNETCORE_URLS=http://127.0.0.1:5101\nObservability__InstanceId=scale-web-1\n' \
    >"${runtime_directory}/web-1.env"
  printf 'ASPNETCORE_URLS=http://127.0.0.1:5102\nObservability__InstanceId=scale-web-2\n' \
    >"${runtime_directory}/web-2.env"
  printf 'Observability__InstanceId=scale-worker-1\n' \
    >"${runtime_directory}/worker-1.env"
  printf 'Observability__InstanceId=scale-worker-2\n' \
    >"${runtime_directory}/worker-2.env"
  chmod 0600 "$runtime_directory"/*.env
}

start_processes() {
  require_root
  validate_run_id
  [[ "$confirmation" == "START-SCALE-PROCESSES-${run_id}" ]] \
    || fail "invalid start confirmation."
  require_armed_run

  local unit
  for unit in "${web_units[@]}" "${worker_units[@]}"; do
    [[ "$(systemctl show "$unit" --property=UnitFileState --value)" == static ]] \
      || fail "$unit must remain a static, non-boot-enabled template."
    ! systemctl is-active --quiet "$unit" \
      || fail "$unit is already active."
  done
  ! ss -lnt | awk '{print $4}' | grep -Eq '(^|:)(5101|5102)$' \
    || fail "port 5101 or 5102 is already listening."

  write_instance_environments
  systemctl reset-failed "${web_units[@]}" "${worker_units[@]}" || true
  systemctl start "${web_units[@]}" "${worker_units[@]}"
  for unit in "${web_units[@]}" "${worker_units[@]}"; do
    wait_for_unit "$unit" active
  done
  wait_for_listener indiefi-scale-web@1.service 5101
  wait_for_listener indiefi-scale-web@2.service 5102
  echo "Scale validation processes are active for run $run_id."
}

stop_processes() {
  require_root
  validate_run_id
  [[ "$confirmation" == "STOP-SCALE-PROCESSES-${run_id}" \
      || "$confirmation" == "AUTO-ROLLBACK-${run_id}" ]] \
    || fail "invalid stop confirmation."
  require_armed_run

  systemctl stop "${worker_units[@]}" "${web_units[@]}" || true
  local unit
  for unit in "${web_units[@]}" "${worker_units[@]}"; do
    wait_for_unit "$unit" inactive
    systemctl reset-failed "$unit" || true
  done
  rm -f -- "$runtime_directory"/web-1.env "$runtime_directory"/web-2.env \
    "$runtime_directory"/worker-1.env "$runtime_directory"/worker-2.env
  echo "Scale validation processes are stopped for run $run_id."
}

restart_instance() {
  local unit="$1"
  local expected_confirmation="$2"
  require_root
  validate_run_id
  [[ "$confirmation" == "${expected_confirmation}-${run_id}" ]] \
    || fail "invalid restart confirmation."
  require_armed_run
  systemctl restart "$unit"
  wait_for_unit "$unit" active
  case "$unit" in
    indiefi-scale-web@1.service) wait_for_listener "$unit" 5101 ;;
    indiefi-scale-web@2.service) wait_for_listener "$unit" 5102 ;;
  esac
  echo "$unit restarted."
}

set_web_instance_state() {
  local instance="$1"
  local desired_state="$2"
  local expected_confirmation="$3"
  local unit="indiefi-scale-web@${instance}.service"
  require_root
  validate_run_id
  [[ "$confirmation" == "${expected_confirmation}-${run_id}" ]] \
    || fail "invalid Web state confirmation."
  require_armed_run

  if [[ "$desired_state" == active ]]; then
    [[ -r "${runtime_directory}/web-${instance}.env" ]] \
      || fail "the Web instance environment is missing."
    systemctl start "$unit"
  else
    systemctl stop "$unit"
  fi
  wait_for_unit "$unit" "$desired_state"
  if [[ "$desired_state" == active ]]; then
    wait_for_listener "$unit" "$((5100 + instance))"
  fi
  echo "$unit is $desired_state."
}

show_status() {
  systemctl show "${web_units[@]}" "${worker_units[@]}" \
    --property=Id,LoadState,ActiveState,SubState,MainPID,NRestarts,EnvironmentFiles
  ss -lnt | grep -E '(:5000|:5101|:5102)' || true
}

case "$command_name" in
  --plan)
    cat <<'EOF'
web_instances=indiefi-scale-web@1:127.0.0.1:5101,indiefi-scale-web@2:127.0.0.1:5102
worker_instances=indiefi-scale-worker@1,indiefi-scale-worker@2
instance_environment=/run/indiefi-scale/*.env
requires_armed_marker=true
unit_file_state=static
enabled_at_boot=false
EOF
    ;;
  start|stop|restart-web-1|restart-web-2|restart-worker-1|restart-worker-2|stop-web-1|start-web-1|stop-web-2|start-web-2)
    exec 9>"$lock_file"
    flock -n 9 || fail "another scale validation command is running."
    case "$command_name" in
      start) start_processes ;;
      stop) stop_processes ;;
      restart-web-1) restart_instance indiefi-scale-web@1.service RESTART-WEB-1 ;;
      restart-web-2) restart_instance indiefi-scale-web@2.service RESTART-WEB-2 ;;
      restart-worker-1) restart_instance indiefi-scale-worker@1.service RESTART-WORKER-1 ;;
      restart-worker-2) restart_instance indiefi-scale-worker@2.service RESTART-WORKER-2 ;;
      stop-web-1) set_web_instance_state 1 inactive STOP-WEB-1 ;;
      start-web-1) set_web_instance_state 1 active START-WEB-1 ;;
      stop-web-2) set_web_instance_state 2 inactive STOP-WEB-2 ;;
      start-web-2) set_web_instance_state 2 active START-WEB-2 ;;
    esac
    ;;
  status) show_status ;;
  *) usage; exit 64 ;;
esac
