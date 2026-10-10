#!/usr/bin/env bash
#
# Compares the Azure and AWS sides after migrate-data.sh, and fails if they disagree.
#
# Written to be run BEFORE any client is pointed at the new API, while both sides still exist and a
# disagreement is still fixable. A successful pg_restore is not evidence that the data arrived: it
# reports success for an empty dump just as happily as a complete one.
#
# Checks, in order of how much damage a failure would do:
#
#   1. Row counts, table by table, on both databases.
#   2. That user_library_books.id values match. This is the one that silently breaks every iPhone
#      if it is wrong, because that uuid is stored on the device as accountLibraryID and is the
#      only link between a local library and the account's server rows.
#   3. Object counts per prefix, against the source containers.
#   4. That every private_transcripts row has a reachable object under the new lowercase-hex S3
#      key, and every audit_review_sources / audit_review_clips row likewise.
#   5. That the data volume seed files are in place.
#
set -euo pipefail

: "${AZURE_PG_HOST:?}"
: "${AZURE_PG_USER:=audiochoice_admin}"
: "${AZURE_PG_PASSWORD:?}"
: "${AZURE_PG_DATABASE:=audiochoice}"
: "${AZURE_STORAGE_ACCOUNT:?}"
: "${AZURE_STORAGE_KEY:?}"
: "${RDS_HOST:?}"
: "${RDS_USER:=audiochoice_admin}"
: "${RDS_PASSWORD:?}"
: "${RDS_DATABASE:=audiochoice}"
: "${S3_BUCKET:?}"
: "${AWS_REGION:=us-east-1}"

export RCLONE_CONFIG_AZ_TYPE=azureblob
export RCLONE_CONFIG_AZ_ACCOUNT="$AZURE_STORAGE_ACCOUNT"
export RCLONE_CONFIG_AZ_KEY="$AZURE_STORAGE_KEY"

FAILURES=0

fail()  { printf '  FAIL  %s\n' "$*"; FAILURES=$((FAILURES + 1)); }
pass()  { printf '  ok    %s\n' "$*"; }
note()  { printf '\n=== %s\n' "$*"; }

azure_sql() {
  PGPASSWORD="$AZURE_PG_PASSWORD" psql --host="$AZURE_PG_HOST" --username="$AZURE_PG_USER" \
    --dbname="$AZURE_PG_DATABASE" --tuples-only --no-align --command="$1"
}

aws_sql() {
  PGPASSWORD="$RDS_PASSWORD" psql --host="$RDS_HOST" --username="$RDS_USER" \
    --dbname="$RDS_DATABASE" --tuples-only --no-align --command="$1"
}

# ------------------------------------------------------------------------------------------------
note "1. Row counts"
# ------------------------------------------------------------------------------------------------
#
# Every table holding something a user would notice the loss of. Ordered roughly by consequence.

TABLES=(
  users
  user_identities
  user_sessions
  account_action_tokens
  account_entitlements
  audiobook_editions
  user_library_books
  bookmarks
  book_notes
  library_collections
  library_collection_books
  book_filter_settings
  filter_profiles
  scan_results
  scan_events
  scan_uploads
  scan_jobs
  private_transcripts
  approved_scan_events
  audit_assignments
  audit_decisions
  audit_review_media
  audit_review_sources
  audit_review_clips
  affiliates
  affiliate_referrals
  device_push_tokens
  filter_reports
  conversion_consents
  companion_transfers
  narration_text_scans
)

for table in "${TABLES[@]}"; do
  # A table absent on one side reports as empty rather than aborting the run, because the migration
  # set is wider than any single schema version and a missing table is itself worth reporting.
  source_count=$(azure_sql "select count(*) from ${table};" 2>/dev/null | tr -d '[:space:]' || echo MISSING)
  target_count=$(aws_sql   "select count(*) from ${table};" 2>/dev/null | tr -d '[:space:]' || echo MISSING)

  if [ "$source_count" = "MISSING" ] && [ "$target_count" = "MISSING" ]; then
    printf '  --    %s (absent on both, skipped)\n' "$table"
  elif [ "$source_count" = "$target_count" ]; then
    pass "$table: $target_count"
  else
    fail "$table: Azure has $source_count, AWS has $target_count"
  fi
done

# ------------------------------------------------------------------------------------------------
note "2. Library row identity (the one that breaks every iPhone)"
# ------------------------------------------------------------------------------------------------
#
# Compares a checksum over the primary keys rather than the rows, because playback positions can
# legitimately have moved on if the old API is still serving. The ids must be identical regardless:
# every iOS install holds them as accountLibraryID.

source_ids=$(azure_sql "select md5(string_agg(id::text, ',' order by id)) from user_library_books;" | tr -d '[:space:]')
target_ids=$(aws_sql   "select md5(string_agg(id::text, ',' order by id)) from user_library_books;" | tr -d '[:space:]')

if [ -z "$source_ids" ] && [ -z "$target_ids" ]; then
  pass "no library rows on either side"
elif [ "$source_ids" = "$target_ids" ]; then
  pass "user_library_books ids identical ($source_ids)"
else
  fail "user_library_books ids DIFFER. Azure $source_ids, AWS $target_ids"
  printf '        Every iOS install stores these as accountLibraryID. Do not cut over.\n'
fi

# The same argument applies to accounts and to the sessions that keep people signed in.
for table in users user_sessions account_entitlements; do
  source_ids=$(azure_sql "select md5(string_agg(id::text, ',' order by id)) from ${table};" 2>/dev/null | tr -d '[:space:]' || echo SKIP)
  target_ids=$(aws_sql   "select md5(string_agg(id::text, ',' order by id)) from ${table};" 2>/dev/null | tr -d '[:space:]' || echo SKIP)
  if [ "$source_ids" = "SKIP" ] || [ "$target_ids" = "SKIP" ]; then
    printf '  --    %s id check skipped\n' "$table"
  elif [ "$source_ids" = "$target_ids" ]; then
    pass "$table ids identical"
  else
    fail "$table ids differ"
  fi
done

# ------------------------------------------------------------------------------------------------
note "3. Object counts per prefix"
# ------------------------------------------------------------------------------------------------

for container in private-transcripts audit-review-media temporary-audio companion-transfers; do
  source_count=$(rclone size "az:$container" --json 2>/dev/null | grep -o '"count":[0-9]*' | cut -d: -f2 || echo 0)
  target_count=$(aws s3 ls "s3://$S3_BUCKET/$container/" --recursive --region "$AWS_REGION" 2>/dev/null | grep -c . || echo 0)

  case "$container" in
    private-transcripts|audit-review-media)
      # Durable. Must match exactly.
      if [ "${source_count:-0}" -eq "${target_count:-0}" ]; then
        pass "$container: $target_count objects"
      else
        fail "$container: Azure $source_count, S3 $target_count (this prefix is durable)"
      fi
      ;;
    *)
      # Relay storage the application expires on its own, so a smaller count on the new side is
      # expected rather than wrong -- objects can have aged out between copy and check.
      if [ "${target_count:-0}" -le "${source_count:-0}" ]; then
        pass "$container: $target_count of $source_count (relay storage, shrinkage expected)"
      else
        pass "$container: $target_count (source $source_count)"
      fi
      ;;
  esac
done

# ------------------------------------------------------------------------------------------------
note "4. Transcript and audit objects resolve under their new keys"
# ------------------------------------------------------------------------------------------------
#
# A matching object count still would not prove the names line up. Transcripts are addressed by the
# SHA-256 of the fingerprint key, lower-cased by the blob store and by the S3 store, UPPER-cased by
# the file-backed store -- so a copy that went via the file layout would have the right number of
# objects under names nothing will ever look up. This checks actual reachability.

check_object() {
  aws s3api head-object --bucket "$S3_BUCKET" --key "$1" --region "$AWS_REGION" >/dev/null 2>&1
}

missing=0
checked=0
while IFS= read -r object_name; do
  [ -z "$object_name" ] && continue
  checked=$((checked + 1))
  if ! check_object "private-transcripts/${object_name}"; then
    # Also try the value as stored, in case it was written with a prefix already.
    if ! check_object "$object_name"; then
      missing=$((missing + 1))
      [ "$missing" -le 5 ] && printf '        unreachable: %s\n' "$object_name"
    fi
  fi
done < <(aws_sql "select object_name from private_transcripts;" 2>/dev/null || true)

if [ "$checked" -eq 0 ]; then
  printf '  --    no private_transcripts rows to check\n'
elif [ "$missing" -eq 0 ]; then
  pass "all $checked transcript objects reachable"
else
  fail "$missing of $checked transcript objects unreachable"
  printf '        Read-along will silently report no timing data for those books.\n'
fi

missing=0
checked=0
for table in audit_review_sources audit_review_clips; do
  while IFS= read -r object_name; do
    [ -z "$object_name" ] && continue
    checked=$((checked + 1))
    if ! check_object "audit-review-media/${object_name}" && ! check_object "$object_name"; then
      missing=$((missing + 1))
      [ "$missing" -le 5 ] && printf '        unreachable: %s\n' "$object_name"
    fi
  done < <(aws_sql "select object_name from ${table};" 2>/dev/null || true)
done

if [ "$checked" -eq 0 ]; then
  printf '  --    no audit media rows to check\n'
elif [ "$missing" -eq 0 ]; then
  pass "all $checked audit media objects reachable"
else
  fail "$missing of $checked audit media objects unreachable"
fi

# ------------------------------------------------------------------------------------------------
note "5. Data volume seed"
# ------------------------------------------------------------------------------------------------

if check_object "data-volume-seed/edition-signatures.json"; then
  pass "edition-signatures.json staged for the volume"
else
  fail "edition-signatures.json is NOT staged at data-volume-seed/"
  printf '        No table holds this and nothing regenerates it. Edition matching will degrade.\n'
fi

if check_object "data-volume-seed/edition-aliases.json"; then
  pass "edition-aliases.json staged for the volume"
else
  printf '  --    edition-aliases.json not staged (derivable cache, rebuilt on demand)\n'
fi

# ------------------------------------------------------------------------------------------------
printf '\n'
if [ "$FAILURES" -eq 0 ]; then
  cat <<'EOF'
Every check passed. Safe to point clients at the new API.

Note what this does NOT cover, because it cannot: iOS bookmarks, favourites and collections live
only in UserDefaults on each device and were never on the server, so they are unaffected either
way. Android bookmarks DO sync, and are covered by the bookmarks row count above.
EOF
  exit 0
fi

printf 'VERIFICATION FAILED: %d problem(s). Do not cut over.\n' "$FAILURES"
exit 1
