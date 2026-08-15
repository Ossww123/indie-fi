#!/usr/bin/env bash
set -Eeuo pipefail

POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-indiefi-db}"

if ! command -v docker >/dev/null 2>&1; then
  echo "Required command is missing: docker" >&2
  exit 2
fi

bindings="$(docker port "$POSTGRES_CONTAINER" 5432/tcp 2>/dev/null || true)"
if [[ -z "$bindings" ]]; then
  echo "PostgreSQL has no published host port."
  exit 0
fi

while IFS= read -r binding; do
  if [[ ! "$binding" =~ ^127\.0\.0\.1: && ! "$binding" =~ ^\[::1\]: ]]; then
    echo "Unsafe PostgreSQL host binding: $binding" >&2
    exit 1
  fi
done <<< "$bindings"

echo "PostgreSQL port is bound only to loopback: $bindings"
