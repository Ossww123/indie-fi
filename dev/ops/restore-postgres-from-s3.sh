#!/usr/bin/env bash
set -Eeuo pipefail

POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-indiefi-db}"
POSTGRES_USER="${POSTGRES_USER:-indiefi}"
: "${RESTORE_S3_URI:?set RESTORE_S3_URI to an exact s3://bucket/object.dump}"
: "${RESTORE_DATABASE:?set RESTORE_DATABASE to the target database name}"

if [[ "${CONFIRM_RESTORE_DATABASE:-}" != "$RESTORE_DATABASE" ]]; then
  echo "Set CONFIRM_RESTORE_DATABASE to the exact target database name." >&2
  exit 2
fi

if [[ ! "$RESTORE_S3_URI" =~ ^s3://([^/]+)/(.+)$ ]]; then
  echo "RESTORE_S3_URI must name one S3 object." >&2
  exit 2
fi
bucket="${BASH_REMATCH[1]}"
object_key="${BASH_REMATCH[2]}"

for command_name in docker aws; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command is missing: $command_name" >&2
    exit 2
  fi
done

aws_region_args=()
if [[ -n "${BACKUP_AWS_REGION:-}" ]]; then
  aws_region_args=(--region "$BACKUP_AWS_REGION")
fi

encryption="$(aws s3api head-object \
  --bucket "$bucket" \
  --key "$object_key" \
  --query ServerSideEncryption \
  --output text \
  "${aws_region_args[@]}")"
if [[ "$encryption" != "aws:kms" ]]; then
  echo "Restore refused: the S3 object is not protected by AWS KMS." >&2
  exit 1
fi

# Validate that the decrypted stream is a readable PostgreSQL archive before
# running the destructive restore. The exact database-name confirmation above
# prevents an accidental paste from targeting a different database.
aws s3 cp "$RESTORE_S3_URI" - \
    --only-show-errors \
    "${aws_region_args[@]}" \
  | docker exec -i "$POSTGRES_CONTAINER" pg_restore --list >/dev/null

aws s3 cp "$RESTORE_S3_URI" - \
    --only-show-errors \
    "${aws_region_args[@]}" \
  | docker exec -i "$POSTGRES_CONTAINER" \
      pg_restore \
        --username="$POSTGRES_USER" \
        --dbname="$RESTORE_DATABASE" \
        --clean \
        --if-exists \
        --exit-on-error \
        --single-transaction \
        --no-owner

echo "PostgreSQL restore completed: $RESTORE_DATABASE"
