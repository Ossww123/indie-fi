#!/usr/bin/env bash
set -Eeuo pipefail

readonly runtime_directory=/run/indiefi-scale
readonly state_directory=/var/lib/indiefi-scale-validation
readonly active_marker="${state_directory}/active"
readonly original_config="${state_directory}/nginx.original"
readonly candidate_config="${state_directory}/nginx.scale"
readonly nginx_config=/etc/nginx/sites-available/indiefi
readonly nginx_drop_in_directory=/etc/systemd/system/nginx.service.d
readonly nginx_drop_in="${nginx_drop_in_directory}/90-indiefi-scale-emergency-restore.conf"
readonly process_tool=/usr/local/lib/indiefi/scale-validation/manage-scale-processes.sh
readonly emergency_tool=/usr/local/lib/indiefi/scale-validation/emergency-restore-nginx.sh
readonly render_tool=/usr/local/lib/indiefi/scale-validation/render-scale-nginx.sh
readonly rollback_timer=indiefi-scale-auto-rollback.timer
readonly rollback_service=indiefi-scale-auto-rollback.service
readonly lock_file=/run/lock/indiefi-scale-validation-topology.lock

command_name="${1:-}"
run_id="${2:-}"
confirmation="${3:-}"
rollback_started=false

usage() {
  cat <<'EOF'
Usage:
  manage-scale-topology.sh --plan
  sudo manage-scale-topology.sh start <run-id> START-MULTI-<run-id>
  sudo manage-scale-topology.sh stop <run-id> STOP-MULTI-<run-id>
  manage-scale-topology.sh status
EOF
}

fail() {
  echo "scale-topology: $*" >&2
  exit 1
}

validate_run_id() {
  [[ "$run_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]] \
    || fail "invalid run id."
}

require_root() {
  (( EUID == 0 )) || fail "this command requires root."
}

wait_for_unit_bounded() {
  local unit="$1"
  local deadline=$((SECONDS + 60))
  until systemctl is-active --quiet "$unit"; do
    (( SECONDS < deadline )) || return 1
    sleep 1
  done
}

wait_for_unit() {
  local unit="$1"
  wait_for_unit_bounded "$unit" \
    || fail "$unit did not become active within 60 seconds."
}

require_baseline() {
  local container
  systemctl is-active --quiet indiefi-web.service \
    || fail "the original Web service must be active."
  systemctl is-active --quiet indiefi-worker.service \
    || fail "the original Worker service must be active."
  for container in indiefi-db indiefi-redis indiefi-rabbitmq; do
    [[ "$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$container" 2>/dev/null)" == healthy ]] \
      || fail "$container must be healthy."
  done
  (( $(awk '/MemAvailable:/ {print int($2 / 1024)}' /proc/meminfo) >= 768 )) \
    || fail "at least 768 MiB of available memory is required."
  (( $(df -P / | awk 'NR == 2 {gsub("%", "", $5); print $5}') < 85 )) \
    || fail "root disk usage must stay below 85 percent."
  [[ -r "$nginx_config" ]] || fail "the active Nginx site is missing."
  [[ ! -e "$active_marker" ]] || fail "another scale topology is already active."
}

render_candidate() {
  local source="$1"
  local destination="$2"
  "$render_tool" "$source" >"$destination"
  chmod 0600 "$destination"
}

install_emergency_guard() {
  install -d -o root -g root -m 0755 "$nginx_drop_in_directory"
  {
    echo '[Service]'
    printf 'ExecStartPre=%s\n' "$emergency_tool"
  } >"$nginx_drop_in"
  chmod 0644 "$nginx_drop_in"
  systemctl daemon-reload
}

schedule_rollback() {
  systemctl stop "$rollback_timer" 2>/dev/null || true
  systemctl reset-failed "$rollback_timer" "$rollback_service" 2>/dev/null || true
  systemd-run \
    --unit=indiefi-scale-auto-rollback \
    --on-active=45m \
    --timer-property=AccuracySec=1s \
    /usr/local/lib/indiefi/scale-validation/manage-scale-topology.sh \
      auto-stop "$run_id" "AUTO-ROLLBACK-${run_id}" >/dev/null
}

verify_distribution() {
  local server_name
  server_name="$(awk '$1 == "server_name" && $2 != "_" {gsub(";", "", $2); print $2; exit}' "$nginx_config")"
  [[ -n "$server_name" ]] || fail "could not read the Nginx server_name."
  local deadline=$((SECONDS + 60))
  local header
  local seen_5101=false
  local seen_5102=false
  while (( SECONDS < deadline )); do
    header="$(curl --insecure --silent --show-error --dump-header - --output /dev/null \
      --connect-timeout 3 --max-time 10 --header "Host: ${server_name}" \
      https://127.0.0.1/health/ready \
      | awk -F': ' 'tolower($1) == "x-indiefi-upstream" {gsub("\r", "", $2); print $2; exit}')" || true
    [[ "$header" == 127.0.0.1:5101 ]] && seen_5101=true
    [[ "$header" == 127.0.0.1:5102 ]] && seen_5102=true
    $seen_5101 && $seen_5102 && return 0
    sleep 1
  done
  fail "both Web upstreams were not observed within 60 seconds."
}

remove_runtime_state() {
  rm -f -- "$runtime_directory/armed" \
    "$state_directory/active" "$state_directory/nginx.original" "$state_directory/nginx.scale" \
    "$nginx_drop_in"
  rmdir "$runtime_directory" 2>/dev/null || true
  rmdir "$state_directory" 2>/dev/null || true
  rmdir "$nginx_drop_in_directory" 2>/dev/null || true
  systemctl daemon-reload
}

rollback() {
  local reason="$1"
  $rollback_started && return 0
  rollback_started=true
  trap - ERR INT TERM
  echo "scale-topology: rollback started reason=$reason"

  systemctl start indiefi-web.service indiefi-worker.service || true
  wait_for_unit_bounded indiefi-web.service || true
  wait_for_unit_bounded indiefi-worker.service || true

  if [[ -r "$original_config" ]]; then
    install -o root -g root -m 0644 "$original_config" "$nginx_config"
    nginx -t && systemctl reload nginx || true
  fi

  if [[ -r "$runtime_directory/armed" ]]; then
    "$process_tool" stop "$run_id" "AUTO-ROLLBACK-${run_id}" || true
  else
    systemctl stop \
      indiefi-scale-worker@1.service indiefi-scale-worker@2.service \
      indiefi-scale-web@1.service indiefi-scale-web@2.service || true
  fi

  systemctl stop "$rollback_timer" 2>/dev/null || true
  systemctl reset-failed "$rollback_timer" 2>/dev/null || true
  remove_runtime_state
  echo "scale-topology: rollback finished reason=$reason"
}

start_topology() {
  require_root
  validate_run_id
  [[ "$confirmation" == "START-MULTI-${run_id}" ]] \
    || fail "invalid start confirmation."
  require_baseline
  command -v systemd-run >/dev/null 2>&1 || fail "systemd-run is required."

  install -d -o root -g root -m 0700 "$state_directory"
  install -o root -g root -m 0600 "$nginx_config" "$original_config"
  printf '%s\n' "$run_id" >"$active_marker"
  trap 'rollback start-failure' ERR INT TERM
  render_candidate "$original_config" "$candidate_config"
  install_emergency_guard

  install -d -o root -g root -m 0700 "$runtime_directory"
  printf '%s\n' "$run_id" >"$runtime_directory/armed"
  chmod 0600 "$runtime_directory/armed"
  schedule_rollback

  "$process_tool" start "$run_id" "START-SCALE-PROCESSES-${run_id}"
  install -o root -g root -m 0644 "$candidate_config" "$nginx_config"
  nginx -t
  systemctl reload nginx
  verify_distribution

  systemctl stop indiefi-worker.service indiefi-web.service
  echo "Scale topology is active for run $run_id; automatic rollback is scheduled in 45 minutes."
  trap - ERR INT TERM
}

stop_topology() {
  require_root
  validate_run_id
  [[ "$confirmation" == "STOP-MULTI-${run_id}" \
      || "$confirmation" == "AUTO-ROLLBACK-${run_id}" ]] \
    || fail "invalid stop confirmation."
  [[ -r "$active_marker" && "$(<"$active_marker")" == "$run_id" ]] \
    || fail "the active topology run id does not match."
  rollback "$command_name"
}

show_status() {
  printf 'active_run=%s\n' "$(cat "$active_marker" 2>/dev/null || echo none)"
  "$process_tool" status
  systemctl show indiefi-web.service indiefi-worker.service \
    --property=Id,ActiveState,SubState,MainPID,NRestarts
  systemctl list-timers "$rollback_timer" --no-pager 2>/dev/null || true
  grep -nE 'server 127[.]0[.]0[.]1:(5000|5101|5102)|X-IndieFi-Upstream' \
    "$nginx_config" || true
}

case "$command_name" in
  --plan)
    cat <<'EOF'
transition=single 5000 -> round-robin 5101,5102 -> single 5000
validation_header=X-IndieFi-Upstream
sticky_session=false
automatic_rollback_minutes=45
reboot_guard=enabled_during_run
EOF
    ;;
  start|stop|auto-stop)
    exec 9>"$lock_file"
    flock -n 9 || fail "another topology command is running."
    if [[ "$command_name" == start ]]; then
      start_topology
    else
      stop_topology
    fi
    ;;
  status) show_status ;;
  *) usage; exit 64 ;;
esac
