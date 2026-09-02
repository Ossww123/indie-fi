#!/usr/bin/env bash
set -Eeuo pipefail

readonly nginx_config=/etc/nginx/sites-available/indiefi
readonly nginx_link=/etc/nginx/sites-enabled/indiefi
readonly runtime_directory=/run/indiefi-scale
readonly state_directory=/var/lib/indiefi-scale-validation
readonly nginx_drop_in=/etc/systemd/system/nginx.service.d/90-indiefi-scale-emergency-restore.conf
readonly scale_units=(
  indiefi-scale-web@1.service indiefi-scale-web@2.service
  indiefi-scale-worker@1.service indiefi-scale-worker@2.service
)

fail() {
  echo "single-topology: $*" >&2
  exit 1
}

if [[ "${1:-}" == --plan ]]; then
  cat <<'EOF'
original_units=indiefi-web.service,indiefi-worker.service
expected_web_port=127.0.0.1:5000
forbidden_scale_ports=5101,5102
nginx_upstream=127.0.0.1:5000
scale_runtime_artifacts=absent
data_containers=healthy
readiness=https-200
EOF
  exit 0
fi
[[ $# -eq 0 ]] || fail 'Usage: verify-single-topology.sh [--plan]'

systemctl is-active --quiet nginx || fail 'nginx is not active.'
systemctl is-active --quiet indiefi-web.service || fail 'the original Web is not active.'
systemctl is-active --quiet indiefi-worker.service || fail 'the original Worker is not active.'

for unit in "${scale_units[@]}"; do
  ! systemctl is-active --quiet "$unit" || fail "$unit is still active."
  [[ "$(systemctl show "$unit" --property=UnitFileState --value)" == static ]] \
    || fail "$unit is not a static, non-boot-enabled template."
done

ss -lnt | awk '{print $4}' | grep -Eq '(^|:)5000$' \
  || fail 'port 5000 is not listening.'
! ss -lnt | awk '{print $4}' | grep -Eq '(^|:)(5101|5102)$' \
  || fail 'a scale Web port is still listening.'

[[ -r "$nginx_config" ]] || fail 'the IndieFi Nginx config is missing.'
[[ -L "$nginx_link" && "$(readlink -f "$nginx_link")" == "$(readlink -f "$nginx_config")" ]] \
  || fail 'the enabled IndieFi Nginx link does not target the expected config.'
[[ "$(grep -Ec '^[[:space:]]*server[[:space:]]+127[.]0[.]0[.]1:5000;' "$nginx_config")" == 1 ]] \
  || fail 'Nginx does not contain exactly one original Web upstream.'
grep -Eq 'proxy_pass[[:space:]]+http://indiefi_web;' "$nginx_config" \
  || fail 'Nginx does not proxy through the IndieFi upstream.'
! grep -Eq '127[.]0[.]0[.]1:(5101|5102)|X-IndieFi-Upstream' "$nginx_config" \
  || fail 'the temporary scale upstream or response header remains in Nginx.'

for artifact in \
  "$runtime_directory/armed" \
  "$runtime_directory/web-1.env" "$runtime_directory/web-2.env" \
  "$runtime_directory/worker-1.env" "$runtime_directory/worker-2.env" \
  "$state_directory/active" "$state_directory/nginx.original" "$state_directory/nginx.scale" \
  "$nginx_drop_in"; do
  [[ ! -e "$artifact" ]] || fail "temporary artifact remains: $artifact"
done

! systemctl is-active --quiet indiefi-scale-auto-rollback.timer \
  || fail 'the scale auto-rollback timer is still active.'
! systemctl is-enabled --quiet indiefi-scale-auto-rollback.timer \
  || fail 'the scale auto-rollback timer is enabled.'

for container in indiefi-db indiefi-redis indiefi-rabbitmq; do
  [[ "$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$container" 2>/dev/null)" == healthy ]] \
    || fail "$container is not healthy."
done

server_name="$(awk '$1 == "server_name" && $2 != "_" {gsub(";", "", $2); print $2; exit}' "$nginx_config")"
[[ -n "$server_name" ]] || fail 'could not read the HTTPS server_name.'
http_code="$(curl --insecure --silent --show-error --output /dev/null --write-out '%{http_code}' \
  --connect-timeout 3 --max-time 10 --header "Host: ${server_name}" \
  https://127.0.0.1/health/ready)"
[[ "$http_code" == 200 ]] || fail "readiness returned HTTP $http_code."

echo 'Single Web/Worker topology verification: OK'
