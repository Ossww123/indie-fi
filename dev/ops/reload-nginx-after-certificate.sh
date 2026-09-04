#!/usr/bin/env bash
set -Eeuo pipefail

/usr/sbin/nginx -t 2>&1
/bin/systemctl reload nginx
