#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
target_dir=/usr/local/lib/indiefi/failure-drills

if (( EUID != 0 )); then
  echo 'Run this installer with sudo.' >&2
  exit 1
fi

install -d -o root -g root -m 0755 "$target_dir"
install -o root -g root -m 0755 \
  "$script_dir/run-controlled-failure.sh" \
  "$target_dir/run-controlled-failure.sh"
install -o root -g root -m 0755 \
  "$script_dir/capture-authoritative-state.sh" \
  "$target_dir/capture-authoritative-state.sh"
install -o root -g root -m 0755 \
  "$script_dir/capture-redis-state.sh" \
  "$target_dir/capture-redis-state.sh"
install -o root -g root -m 0755 \
  "$script_dir/capture-rabbitmq-state.sh" \
  "$target_dir/capture-rabbitmq-state.sh"
install -o root -g root -m 0644 \
  "$script_dir/audit-authoritative-state.sql" \
  "$target_dir/audit-authoritative-state.sql"

"$target_dir/run-controlled-failure.sh" --plan web-restart 60 install-check >/dev/null
test -r "$target_dir/audit-authoritative-state.sql"
echo "Failure drill tools installed in $target_dir."
