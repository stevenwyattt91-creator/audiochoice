#!/bin/zsh
set -euo pipefail

ROOT_DIR="${0:A:h:h}"
AUDIO_FILE="${1:-$ROOT_DIR/work/live-scan/TwoTwistedCrowns_4m50s.stripped.m4a}"
RESULT_FILE="$ROOT_DIR/work/live-scan/live-scan-result.json"

if [[ ! -f "$AUDIO_FILE" ]]; then
  print -u2 "Test audio was not found: $AUDIO_FILE"
  exit 1
fi

for command_name in az curl jq shasum ffprobe openssl; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    print -u2 "Required tool is missing: $command_name"
    exit 1
  fi
done

API_HOST=$(az containerapp show \
  --name audiochoice-stg-api \
  --resource-group audiochoice-staging \
  --query properties.configuration.ingress.fqdn \
  --output tsv)
API_BASE="https://$API_HOST"

FILE_SIZE=$(stat -f %z "$AUDIO_FILE")
FILE_HASH=$(shasum -a 256 "$AUDIO_FILE" | awk '{print toupper($1)}')
DURATION=$(ffprobe -v error \
  -show_entries format=duration \
  -of default=noprint_wrappers=1:nokey=1 \
  "$AUDIO_FILE")

if (( ${DURATION%.*} > 300 )); then
  print -u2 "Controlled test audio must be no longer than 300 seconds."
  exit 1
fi

TEST_EMAIL="scan-test-$(date +%s)@audiochoiceapp.com"
TEST_PASSWORD=$(openssl rand -base64 24 | tr -d '/+=' | head -c 24)
AUTH_BODY=$(jq -n \
  --arg email "$TEST_EMAIL" \
  --arg password "$TEST_PASSWORD" \
  '{email:$email,password:$password,displayName:"AudioChoice Scan Test"}')
AUTH_RESPONSE=$(curl --fail --silent --show-error \
  --retry 5 --retry-delay 5 \
  -H 'Content-Type: application/json' \
  -d "$AUTH_BODY" \
  "$API_BASE/v1/auth/register")
ACCESS_TOKEN=$(jq -er '.accessToken' <<< "$AUTH_RESPONSE")

FINGERPRINT=$(jq -n \
  --arg sha256 "$FILE_HASH" \
  --argjson fileSize "$FILE_SIZE" \
  --argjson duration "$DURATION" \
  '{version:1,sha256:$sha256,fileSize:$fileSize,duration:$duration,fileType:"m4a",workTitle:"Two Twisted Crowns - controlled scan",author:null,seriesTitle:null,seriesNumber:null,editionType:"test excerpt",partNumber:null,totalParts:null}')

AUTHORIZATION_BODY=$(jq -n \
  --argjson fingerprint "$FINGERPRINT" \
  --arg fileName "${AUDIO_FILE:t}" \
  --argjson fileSize "$FILE_SIZE" \
  '{fingerprint:$fingerprint,fileName:$fileName,contentType:"audio/mp4",fileSize:$fileSize}')
UPLOAD_AUTHORIZATION=$(curl --fail --silent --show-error \
  -H "Authorization: Bearer $ACCESS_TOKEN" \
  -H 'Content-Type: application/json' \
  -d "$AUTHORIZATION_BODY" \
  "$API_BASE/v1/uploads/authorizations")

UPLOAD_ID=$(jq -er '.uploadID' <<< "$UPLOAD_AUTHORIZATION")
UPLOAD_URL=$(jq -er '.uploadURL' <<< "$UPLOAD_AUTHORIZATION")
BLOB_TYPE=$(jq -er '.headers["x-ms-blob-type"]' <<< "$UPLOAD_AUTHORIZATION")
CONTENT_TYPE=$(jq -er '.headers["Content-Type"]' <<< "$UPLOAD_AUTHORIZATION")

print "Uploading the private 4-minute-50-second test excerpt..."
curl --fail --silent --show-error \
  -X PUT \
  -H "x-ms-blob-type: $BLOB_TYPE" \
  -H "Content-Type: $CONTENT_TYPE" \
  --upload-file "$AUDIO_FILE" \
  "$UPLOAD_URL" \
  >/dev/null

curl --fail --silent --show-error \
  -X POST \
  -H "Authorization: Bearer $ACCESS_TOKEN" \
  "$API_BASE/v1/uploads/$UPLOAD_ID/complete" \
  >/dev/null

JOB_BODY=$(jq -n \
  --arg uploadID "$UPLOAD_ID" \
  --argjson fingerprint "$FINGERPRINT" \
  '{uploadID:$uploadID,fingerprint:$fingerprint}')
JOB_RESPONSE=$(curl --fail --silent --show-error \
  -H "Authorization: Bearer $ACCESS_TOKEN" \
  -H 'Content-Type: application/json' \
  -d "$JOB_BODY" \
  "$API_BASE/v1/scans/jobs")
SCAN_ID=$(jq -er '.scanID' <<< "$JOB_RESPONSE")

print "Scan submitted: $SCAN_ID"
print "Waiting for timestamped transcription and filter analysis..."

for attempt in {1..180}; do
  STATUS_RESPONSE=$(curl --fail --silent --show-error \
    -H "Authorization: Bearer $ACCESS_TOKEN" \
    "$API_BASE/v1/scans/jobs/$SCAN_ID")
  STATUS=$(jq -er '.status' <<< "$STATUS_RESPONSE")
  print "Status: $STATUS"

  if [[ "$STATUS" == "completed" ]]; then
    print -r -- "$STATUS_RESPONSE" | jq . > "$RESULT_FILE"
    EVENT_COUNT=$(jq '.result.events | length' "$RESULT_FILE")
    print "Controlled live scan completed with $EVENT_COUNT filter events."
    print "Result saved to: $RESULT_FILE"
    unset ACCESS_TOKEN TEST_PASSWORD UPLOAD_URL
    exit 0
  fi

  if [[ "$STATUS" == "failed" ]]; then
    print -u2 "The controlled live scan failed. Check the worker logs."
    az containerapp logs show \
      --name audiochoice-stg-worker \
      --resource-group audiochoice-staging \
      --tail 40 || true
    unset ACCESS_TOKEN TEST_PASSWORD UPLOAD_URL
    exit 2
  fi

  sleep 5
done

print -u2 "The scan did not finish within 15 minutes."
unset ACCESS_TOKEN TEST_PASSWORD UPLOAD_URL
exit 3
