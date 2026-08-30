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

"$target_dir/run-controlled-failure.sh" --plan web-restart 60 install-check >/dev/null
echo "Failure drill tools installed in $target_dir."
