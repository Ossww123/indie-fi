#!/usr/bin/env bash
set -Eeuo pipefail

if (( EUID != 0 )); then
  echo 'Run this installer with sudo.' >&2
  exit 1
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
target_dir=/usr/local/lib/indiefi/scale-validation

install -d -o root -g root -m 0755 "$target_dir"
install -o root -g root -m 0755 \
  "$script_dir/manage-scale-processes.sh" \
  "$target_dir/manage-scale-processes.sh"
install -o root -g root -m 0755 \
  "$script_dir/manage-scale-topology.sh" \
  "$target_dir/manage-scale-topology.sh"
install -o root -g root -m 0755 \
  "$script_dir/emergency-restore-nginx.sh" \
  "$target_dir/emergency-restore-nginx.sh"
install -o root -g root -m 0755 \
  "$script_dir/render-scale-nginx.sh" \
  "$target_dir/render-scale-nginx.sh"
install -o root -g root -m 0755 \
  "$script_dir/capture-key-drop-state.sh" \
  "$target_dir/capture-key-drop-state.sh"
install -o root -g root -m 0755 \
  "$script_dir/capture-scale-runtime.sh" \
  "$target_dir/capture-scale-runtime.sh"
install -o root -g root -m 0644 \
  "$script_dir/audit-key-drop.sql" \
  "$target_dir/audit-key-drop.sql"
install -o root -g root -m 0644 \
  "$script_dir/systemd/indiefi-scale-web@.service" \
  /etc/systemd/system/indiefi-scale-web@.service
install -o root -g root -m 0644 \
  "$script_dir/systemd/indiefi-scale-worker@.service" \
  /etc/systemd/system/indiefi-scale-worker@.service

systemctl daemon-reload
for unit in \
  indiefi-scale-web@1.service indiefi-scale-web@2.service \
  indiefi-scale-worker@1.service indiefi-scale-worker@2.service; do
  systemctl is-enabled --quiet "$unit" \
    && { echo "$unit unexpectedly became enabled." >&2; exit 1; }
done
"$target_dir/manage-scale-processes.sh" --plan >/dev/null
"$target_dir/manage-scale-topology.sh" --plan >/dev/null
test -r "$target_dir/audit-key-drop.sql"
if "$target_dir/capture-scale-runtime.sh" >/dev/null 2>&1; then
  echo 'capture-scale-runtime.sh unexpectedly accepted a missing output path.' >&2
  exit 1
fi
echo 'Scale-validation templates and tools are installed; no instance was started or enabled.'
