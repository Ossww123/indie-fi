#!/usr/bin/env bash
set -Eeuo pipefail

/usr/sbin/nginx -t
/bin/systemctl reload nginx
