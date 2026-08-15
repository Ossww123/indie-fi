#!/usr/bin/env bash
set -Eeuo pipefail

POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-indiefi-db}"
POSTGRES_DATABASE="${POSTGRES_DATABASE:-indiefi}"
POSTGRES_USER="${POSTGRES_USER:-indiefi}"
: "${BACKUP_S3_PREFIX:?set BACKUP_S3_PREFIX to s3://bucket/prefix}"
: "${BACKUP_KMS_KEY_ID:?set BACKUP_KMS_KEY_ID to the dedicated backup KMS key}"

if [[ ! "$BACKUP_S3_PREFIX" =~ ^s3://([^/]+)/(.+)$ ]]; then
  echo "BACKUP_S3_PREFIX must include a bucket and prefix." >&2
  exit 2
fi
bucket="${BASH_REMATCH[1]}"
prefix="${BASH_REMATCH[2]%/}"

for command_name in docker aws; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command is missing: $command_name" >&2
    exit 2
  fi
done

if [[ "$(docker inspect --format '{{.State.Running}}' "$POSTGRES_CONTAINER")" != "true" ]]; then
  echo "PostgreSQL container is not running: $POSTGRES_CONTAINER" >&2
  exit 1
fi

timestamp="$(date -u +'%Y%m%dT%H%M%SZ')"
object_key="$prefix/postgresql-$timestamp.dump"
object_uri="s3://$bucket/$object_key"
aws_region_args=()
if [[ -n "${BACKUP_AWS_REGION:-}" ]]; then
  aws_region_args=(--region "$BACKUP_AWS_REGION")
fi

# Stream the custom-format dump directly to S3. No plaintext backup file is
# written to the EC2 filesystem, and S3 must encrypt the object with KMS.
docker exec "$POSTGRES_CONTAINER" \
  pg_dump \
    --username="$POSTGRES_USER" \
    --dbname="$POSTGRES_DATABASE" \
    --format=custom \
    --no-owner \
  | aws s3 cp - "$object_uri" \
      --only-show-errors \
      --sse aws:kms \
      --sse-kms-key-id "$BACKUP_KMS_KEY_ID" \
      "${aws_region_args[@]}"

encryption="$(aws s3api head-object \
  --bucket "$bucket" \
  --key "$object_key" \
  --query ServerSideEncryption \
  --output text \
  "${aws_region_args[@]}")"
size="$(aws s3api head-object \
  --bucket "$bucket" \
  --key "$object_key" \
  --query ContentLength \
  --output text \
  "${aws_region_args[@]}")"

if [[ "$encryption" != "aws:kms" || ! "$size" =~ ^[1-9][0-9]*$ ]]; then
  echo "Uploaded backup did not pass KMS encryption and size verification." >&2
  exit 1
fi

echo "Encrypted PostgreSQL backup uploaded: $object_uri ($size bytes)"
