#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "${EUID:-$(id -u)}" != "0" ]]; then
  echo "Run this installer as root." >&2
  exit 77
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

required_commands=(install systemd-sysusers systemd-tmpfiles systemctl)
for command_name in "${required_commands[@]}"; do
  command -v "$command_name" >/dev/null 2>&1 || {
    echo "Required command is missing: $command_name" >&2
    exit 69
  }
done

install -D -m 0644 \
  "$script_dir/systemd/indiefi.sysusers.conf" \
  /etc/sysusers.d/indiefi.conf
systemd-sysusers /etc/sysusers.d/indiefi.conf

install -D -m 0644 \
  "$script_dir/systemd/indiefi.tmpfiles.conf" \
  /etc/tmpfiles.d/indiefi.conf
systemd-tmpfiles --create /etc/tmpfiles.d/indiefi.conf

install -D -m 0755 \
  "$script_dir/verify-portfolio-environment.sh" \
  /usr/local/lib/indiefi/verify-portfolio-environment.sh
install -D -m 0755 \
  "$script_dir/reload-nginx-after-certificate.sh" \
  /usr/local/lib/indiefi/reload-nginx-after-certificate.sh

install -D -m 0644 \
  "$script_dir/systemd/indiefi-web.service" \
  /etc/systemd/system/indiefi-web.service
install -D -m 0644 \
  "$script_dir/systemd/indiefi-worker.service" \
  /etc/systemd/system/indiefi-worker.service

systemctl daemon-reload

echo "Portfolio service users, directories, validation tools, and systemd units are installed."
echo "No service was enabled or started."
