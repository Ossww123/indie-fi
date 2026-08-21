#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "Usage: $0 <http|https> <public-ipv4>" >&2
  exit 64
}

[[ "$#" == "2" ]] || usage

mode="$1"
public_ip="$2"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

case "$mode" in
  http)
    template="$script_dir/nginx/indiefi-http.conf.template"
    ;;
  https)
    template="$script_dir/nginx/indiefi-https.conf.template"
    ;;
  *)
    usage
    ;;
esac

IFS=. read -r -a octets <<< "$public_ip"
[[ "${#octets[@]}" == "4" ]] || {
  echo "A dotted-quad public IPv4 address is required." >&2
  exit 65
}

for octet in "${octets[@]}"; do
  if [[ ! "$octet" =~ ^(0|[1-9][0-9]{0,2})$ ]] \
    || (( 10#$octet > 255 )); then
    echo "A dotted-quad public IPv4 address is required." >&2
    exit 65
  fi
done

[[ -f "$template" ]] || {
  echo "Nginx template is missing: $template" >&2
  exit 66
}

sed "s/__PUBLIC_IP__/$public_ip/g" "$template"
