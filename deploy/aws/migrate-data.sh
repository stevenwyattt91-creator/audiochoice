#!/usr/bin/env bash
#
# Moves AudioChoice's durable data from Azure to AWS.
#
# Three stores move, and they move in this order because the later ones are addressed by values in
# the first:
#
#   1. PostgreSQL       Azure Database for PostgreSQL  ->  RDS
#   2. Object storage   four Azure Blob containers     ->  one S3 bucket, four prefixes
#   3. Data volume      the Azure Files /data share    ->  an S3 seed prefix the API restores from
#
# Run it from a machine that can reach the Azure resources and has AWS credentials. It is
# idempotent for the object copies (rclone skips matching objects) but NOT for the database
# restore, which expects an empty target -- see the guard in phase 1.
#
# --- What must not change -----------------------------------------------------------------------
#
# Primary keys. `user_library_books.id` is stored on every iPhone as `accountLibraryID` and is the
# only link between a device's local library and the account's server-side rows: playback position
# sync, per-book filter settings and notes all key off it. `users.id` anchors everything else, and
# `user_sessions.token_hash` is what keeps people signed in. A dump-and-restore preserves all of
# them. Re-creating rows through the API would not, and the damage would be silent -- every install
# would look fine and quietly stop syncing.
#
# Object names. Transcripts are addressed by the SHA-256 of the fingerprint key, and the Azure blob
# store lower-cases that hex while the file-backed store upper-cases it. The S3 store follows the
# blob store's lowercase form, so a byte-for-byte name-preserving copy is correct and a
# re-derivation is not. Audit media object names are stored in the database as container-relative
# paths and the S3 layer re-applies the prefix itself, so those must keep their relative shape too.
#
# --- Requirements -------------------------------------------------------------------------------
#
#   pg_dump / pg_restore   16.x, to match the server version
#   rclone                 for Azure Blob -> S3 without staging locally
#   aws                     CLI v2
#   azcopy                 only for phase 3, the SMB file share
#
set -euo pipefail

# --- Configuration ------------------------------------------------------------------------------

: "${AZURE_PG_HOST:?Azure PostgreSQL hostname, e.g. audiochoice-stg-db-xxxx.postgres.database.azure.com}"
: "${AZURE_PG_USER:=audiochoice_admin}"
: "${AZURE_PG_PASSWORD:?Azure PostgreSQL password}"
: "${AZURE_PG_DATABASE:=audiochoice}"

: "${AZURE_STORAGE_ACCOUNT:?Azure storage account name, e.g. audiochoicestgxxxxxxxx}"
: "${AZURE_STORAGE_KEY:?Azure storage account key, for rclone and azcopy}"
: "${AZURE_FILE_SHARE:=audiochoice-staging-data}"

: "${RDS_HOST:?RDS endpoint address, the DatabaseAddress output of the foundation stack}"
: "${RDS_USER:=audiochoice_admin}"
: "${RDS_PASSWORD:?RDS master password}"
: "${RDS_DATABASE:=audiochoice}"

: "${S3_BUCKET:?Target bucket, the BucketName output of the foundation stack}"
: "${AWS_REGION:=us-east-1}"

WORK_DIR="${WORK_DIR:-./migration-work}"
DUMP_FILE="$WORK_DIR/audiochoice-$(date -u +%Y%m%dT%H%M%SZ).dump"

mkdir -p "$WORK_DIR"

note() { printf '\n=== %s\n' "$*"; }

# rclone is configured entirely through the environment so nothing is written to a config file and
# no credentials linger on disk.
export RCLONE_CONFIG_AZ_TYPE=azureblob
export RCLONE_CONFIG_AZ_ACCOUNT="$AZURE_STORAGE_ACCOUNT"
export RCLONE_CONFIG_AZ_KEY="$AZURE_STORAGE_KEY"
export RCLONE_CONFIG_S3_TYPE=s3
export RCLONE_CONFIG_S3_PROVIDER=AWS
export RCLONE_CONFIG_S3_ENV_AUTH=true
export RCLONE_CONFIG_S3_REGION="$AWS_REGION"

# ------------------------------------------------------------------------------------------------
# Phase 1: PostgreSQL
# ------------------------------------------------------------------------------------------------

note "Phase 1a: dumping Azure PostgreSQL"

# Custom format so the restore can be ordered and parallelised, and so a partial transfer is
# detectable rather than a truncated SQL script that would replay halfway.
#
# --no-owner and --no-privileges because the role names differ between Azure Flexible Server and
# RDS; object ownership lands on the restoring role instead, which is what we want. Everything
# that matters -- every primary key, every foreign key, every uuid -- is unaffected by this.
PGPASSWORD="$AZURE_PG_PASSWORD" pg_dump \
  --host="$AZURE_PG_HOST" \
  --username="$AZURE_PG_USER" \
  --dbname="$AZURE_PG_DATABASE" \
  --format=custom \
  --no-owner \
  --no-privileges \
  --verbose \
  --file="$DUMP_FILE"

printf 'Dump written: %s (%s)\n' "$DUMP_FILE" "$(du -h "$DUMP_FILE" | cut -f1)"

note "Phase 1b: checking the RDS target is empty"

# The API applies its own migrations at startup, so the target may well already have the full
# schema with no rows. Restoring into that is fine. Restoring into a database that has *rows* is
# not: pg_restore would hit unique-constraint failures partway and leave a half-merged mess.
EXISTING_USERS=$(PGPASSWORD="$RDS_PASSWORD" psql \
  --host="$RDS_HOST" --username="$RDS_USER" --dbname="$RDS_DATABASE" \
  --tuples-only --no-align \
  --command="select coalesce((select count(*) from users), 0);" 2>/dev/null || echo 0)

if [ "${EXISTING_USERS:-0}" -gt 0 ]; then
  cat >&2 <<EOF

REFUSING TO RESTORE. The target database already holds ${EXISTING_USERS} users.

Restoring on top of existing rows would fail partway through on unique constraints and leave the
database half merged. If this is a retry after a failed run, drop and recreate the database first:

  psql -h "$RDS_HOST" -U "$RDS_USER" -d postgres \\
    -c 'drop database $RDS_DATABASE;' -c 'create database $RDS_DATABASE;'

EOF
  exit 1
fi

note "Phase 1c: restoring into RDS"

# --clean --if-exists so the schema the API's own startup migrations may have already created is
# replaced by the dump's, rather than collided with. --single-transaction so a failure rolls the
# whole thing back instead of leaving a partial database that looks alive.
PGPASSWORD="$RDS_PASSWORD" pg_restore \
  --host="$RDS_HOST" \
  --username="$RDS_USER" \
  --dbname="$RDS_DATABASE" \
  --no-owner \
  --no-privileges \
  --clean \
  --if-exists \
  --single-transaction \
  --verbose \
  "$DUMP_FILE"

# ------------------------------------------------------------------------------------------------
# Phase 2: Blob containers -> S3 prefixes
# ------------------------------------------------------------------------------------------------
#
# Container name becomes prefix name, unchanged, which is what the S3 storage layer expects.
#
# temporary-audio and companion-transfers are relay storage that the application expires on its
# own -- hours for audio, minutes for a transfer. They are copied anyway so that anything genuinely
# in flight during the cutover survives, but an empty result for either is the normal case and not
# a problem.
#
# private-transcripts and audit-review-media are the durable ones. Those two must arrive complete.

copy_container() {
  local container="$1"
  note "Phase 2: $container -> s3://$S3_BUCKET/$container/"
  rclone copy "az:$container" "s3:$S3_BUCKET/$container" \
    --checksum \
    --transfers 16 \
    --progress \
    --stats-one-line
}

copy_container private-transcripts
copy_container audit-review-media
copy_container temporary-audio
copy_container companion-transfers

# ------------------------------------------------------------------------------------------------
# Phase 3: the /data file share
# ------------------------------------------------------------------------------------------------
#
# Only two files from this share need to reach the new deployment, and one of them is the reason
# this phase exists at all:
#
#   edition-signatures.json   Client-reported product identifiers, narrators and chapter offsets,
#                             keyed by fingerprint. There is NO database equivalent and nothing
#                             server-side regenerates it. It is what lets a converted or re-tagged
#                             copy of an audiobook match the filter results already paid for.
#                             Losing it degrades matching to comparing fingerprint metadata until
#                             every client happens to report again.
#
#   edition-aliases.json      Links between fingerprints known to be the same recording. A cache
#                             of a derivable fact -- EditionResolver rebuilds it on demand -- so
#                             losing this costs lookup time, not data. Carried because it is free.
#
# Everything else on the share is either a no-database fallback that a Postgres deployment never
# reads (accounts.json, user-library.json, user-data.json, entitlements.json, affiliates.json,
# companion-transfers.json, conversion-consents.json, scan-catalog.json) or in-flight scratch
# (analysis-checkpoints/, uploads/). Seeding the fallbacks would plant stale duplicates of tables
# that are authoritative in Postgres, which is worse than not seeding them.
#
# They go to a seed prefix in S3 rather than straight onto EFS because EFS is reachable only from
# inside the VPC and the runtime image carries no AWS CLI or NFS client. DataVolumeSeeder restores
# them from this prefix on the first start, before anything reads the volume.

note "Phase 3a: downloading the /data share"

SHARE_DIR="$WORK_DIR/data-share"
mkdir -p "$SHARE_DIR"

AZURE_FILE_URL="https://${AZURE_STORAGE_ACCOUNT}.file.core.windows.net/${AZURE_FILE_SHARE}"
AZCOPY_SAS="${AZCOPY_SAS:-}"

if [ -z "$AZCOPY_SAS" ]; then
  cat >&2 <<'EOF'

AZCOPY_SAS is not set, so the file share cannot be read.

azcopy cannot use a storage account key for Azure Files; it needs a SAS token. Generate a
short-lived read/list one:

  az storage share generate-sas \
    --account-name "$AZURE_STORAGE_ACCOUNT" \
    --account-key "$AZURE_STORAGE_KEY" \
    --name "$AZURE_FILE_SHARE" \
    --permissions rl \
    --expiry "$(date -u -d '+2 hours' +%Y-%m-%dT%H:%MZ)" \
    --output tsv

Then re-run with AZCOPY_SAS set to that value.

EOF
  exit 1
fi

for file in edition-signatures.json edition-aliases.json; do
  # Not fatal if absent: a deployment that has never had a client report a signature will not have
  # the file, and that is a legitimate state rather than a failed migration.
  azcopy copy "${AZURE_FILE_URL}/${file}?${AZCOPY_SAS}" "$SHARE_DIR/${file}" \
    --overwrite=true || printf 'Not present on the share, skipping: %s\n' "$file"
done

note "Phase 3b: uploading the seed files"

for file in edition-signatures.json edition-aliases.json; do
  if [ -f "$SHARE_DIR/$file" ]; then
    aws s3 cp "$SHARE_DIR/$file" "s3://$S3_BUCKET/data-volume-seed/$file" \
      --region "$AWS_REGION"
  fi
done

cat <<EOF

Migration complete.

  Database dump   $DUMP_FILE
  Seed files      $SHARE_DIR

Keep the dump until the new deployment has been verified against real traffic. It is the only
rollback that does not depend on the Azure subscription still being alive.

Next: run verify-migration.sh against both sides before pointing any client at the new API.
EOF
