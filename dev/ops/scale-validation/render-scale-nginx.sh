#!/usr/bin/env bash
set -Eeuo pipefail

source_config="${1:-}"
if [[ -z "$source_config" || ! -r "$source_config" ]]; then
  echo 'Usage: render-scale-nginx.sh <readable-single-Web-site-config>' >&2
  exit 64
fi

[[ "$(grep -Ec '^[[:space:]]*server[[:space:]]+127[.]0[.]0[.]1:5000;' "$source_config")" == 1 ]] \
  || { echo 'Source must contain exactly one 127.0.0.1:5000 upstream.' >&2; exit 65; }
! grep -q 'X-IndieFi-Upstream' "$source_config" \
  || { echo 'Source already contains the validation header.' >&2; exit 65; }

awk '
  /^[[:space:]]*server[[:space:]]+127[.]0[.]0[.]1:5000;/ {
    match($0, /^[[:space:]]*/)
    indent = substr($0, 1, RLENGTH)
    print indent "server 127.0.0.1:5101 max_fails=1 fail_timeout=5s;"
    print indent "server 127.0.0.1:5102 max_fails=1 fail_timeout=5s;"
    next
  }
  { print }
  /^[[:space:]]*proxy_pass[[:space:]]+http:\/\/indiefi_web;/ {
    match($0, /^[[:space:]]*/)
    indent = substr($0, 1, RLENGTH)
    print indent "add_header X-IndieFi-Upstream $upstream_addr always;"
  }
' "$source_config"
