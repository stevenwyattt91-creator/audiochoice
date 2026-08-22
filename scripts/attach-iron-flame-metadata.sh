#!/bin/zsh
set -euo pipefail

resource_group='audiochoice-staging'
vault_name=$(az keyvault list --resource-group "$resource_group" --query '[0].name' --output tsv)
api_token=$(az keyvault secret show --vault-name "$vault_name" --name staging-api-token --query value --output tsv)
api_host=$(az containerapp show --name audiochoice-stg-api --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv)

transcripts=$(curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" \
  "https://$api_host/v1/admin/transcripts")
matches=$(print -r -- "$transcripts" | jq '[.[] | select(.isComplete == true and .segmentCount >= 10000)]')
if [[ $(print -r -- "$matches" | jq 'length') -ne 1 ]]; then
  echo 'Could not uniquely identify the full Iron Flame transcript.' >&2
  print -r -- "$matches" | jq . >&2
  exit 1
fi

fingerprint=$(print -r -- "$matches" | jq '.[0].fingerprint')
payload=$(jq -n --argjson fingerprint "$fingerprint" '{
  fingerprint: $fingerprint,
  workTitle: "Iron Flame, Part 2 of 2 (Dramatized Adaptation)",
  author: "Rebecca Yarros",
  seriesTitle: "The Empyrean",
  seriesNumber: 2,
  editionType: "Dramatized Adaptation",
  partNumber: 2,
  totalParts: 2,
  duration: 34086.233
}')

curl --fail --silent --show-error --request PUT \
  --header "Authorization: Bearer $api_token" \
  --header 'Content-Type: application/json' \
  --data "$payload" \
  "https://$api_host/v1/admin/editions/metadata"

curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" \
  "https://$api_host/v1/admin/explore" | jq '[.[] | select(.title | contains("Iron Flame"))][0] | {title, author, seriesTitle, seriesNumber, editionType, duration, eventCount, scannerVersion, libroURL}'
