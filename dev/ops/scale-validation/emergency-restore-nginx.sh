#!/usr/bin/env bash
set -Eeuo pipefail

readonly state_directory=/var/lib/indiefi-scale-validation
readonly active_marker="${state_directory}/active"
readonly original_config="${state_directory}/nginx.original"
readonly nginx_config=/etc/nginx/sites-available/indiefi

if [[ -r "$active_marker" && -r "$original_config" ]]; then
  systemctl start indiefi-web.service indiefi-worker.service
  install -o root -g root -m 0644 "$original_config" "$nginx_config"
  nginx -t
  systemctl stop \
    indiefi-scale-worker@1.service indiefi-scale-worker@2.service \
    indiefi-scale-web@1.service indiefi-scale-web@2.service || true
  echo 'Emergency guard restored the single-Web Nginx configuration.'
fi
