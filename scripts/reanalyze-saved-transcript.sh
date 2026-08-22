#!/bin/zsh
set -euo pipefail

resource_group='audiochoice-staging'
query=${1:?Usage: ./scripts/reanalyze-saved-transcript.sh "Audiobook title"}
vault_name=$(az keyvault list --resource-group "$resource_group" --query '[0].name' --output tsv)
api_token=$(az keyvault secret show --vault-name "$vault_name" --name staging-api-token --query value --output tsv)
api_host=$(az containerapp show --name audiochoice-stg-api --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv)
encoded_query=$(QUERY="$query" python3 -c 'import os, urllib.parse; print(urllib.parse.quote(os.environ["QUERY"]))')

records=$(curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" \
  "https://$api_host/v1/admin/conversion-consents?query=$encoded_query&limit=10")
count=$(print -r -- "$records" | jq 'length')
if [[ "$count" -ne 1 ]]; then
  echo "Expected exactly one matching consent record, found $count. Use a more specific title." >&2
  print -r -- "$records" | jq '[.[] | {id, userEmail, sourceFileName, acceptedAt}]' >&2
  exit 1
fi

transcripts=$(curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" \
  "https://$api_host/v1/admin/transcripts")
full_transcripts=$(print -r -- "$transcripts" | jq '[.[] | select(.isComplete == true and .segmentCount >= 300)]')
full_count=$(print -r -- "$full_transcripts" | jq 'length')
if [[ "$full_count" -ne 1 ]]; then
  echo "Expected exactly one complete full-length transcript, found $full_count." >&2
  print -r -- "$transcripts" | jq '[.[] | {fingerprint, segmentCount, isComplete, transcriptionModel, createdAt}]' >&2
  exit 1
fi
owner_user_id=$(print -r -- "$records" | jq -r '.[0].userID')
transcript_fingerprint=$(print -r -- "$full_transcripts" | jq '.[0].fingerprint')
payload=$(jq -n \
  --arg ownerUserID "$owner_user_id" \
  --argjson fingerprint "$transcript_fingerprint" \
  '{ownerUserID: $ownerUserID, fingerprint: $fingerprint}')
response_file=$(mktemp)
http_status=$(curl --silent --show-error \
    --request POST \
    --header "Authorization: Bearer $api_token" \
    --header 'Content-Type: application/json' \
    --data "$payload" \
    --output "$response_file" \
    --write-out '%{http_code}' \
    "https://$api_host/v1/admin/scans/reanalysis")
response=$(<"$response_file")
rm -f "$response_file"
if [[ "$http_status" -lt 200 || "$http_status" -ge 300 ]]; then
  echo "Reanalysis request failed with HTTP $http_status:" >&2
  print -r -- "$response" | jq . >&2
  exit 1
fi
scan_id=$(print -r -- "$response" | jq -r '.scanID')
echo "Saved-transcript reanalysis submitted: $scan_id"

while true; do
  status_response=$(curl --fail --silent --show-error \
    --header "Authorization: Bearer $api_token" \
    "https://$api_host/v1/admin/scans/jobs/$scan_id")
  job_status=$(print -r -- "$status_response" | jq -r '.status')
  percent=$(print -r -- "$status_response" | jq -r '.progressPercent // 0')
  stage=$(print -r -- "$status_response" | jq -r '.progressStage // "waiting"')
  echo "Status: $job_status — $percent% ($stage)"
  case "$job_status" in
    completed)
      event_count=$(print -r -- "$status_response" | jq '.result.events | length')
      echo "Reanalysis completed with $event_count filter events."
      echo 'Complete sexual-scene ranges:'
      print -r -- "$status_response" | jq '[
        .result.events[]
        | select((.eventID | ascii_downcase) == "11100000-0000-0000-0000-000000000006")
        | {startTime, endTime, durationSeconds: (.endTime - .startTime), safeDescription}
      ]'
      break
      ;;
    failed)
      echo 'Reanalysis stopped after a non-recoverable error. Completed analysis batches remain checkpointed.' >&2
      exit 1
      ;;
  esac
  sleep 10
done
