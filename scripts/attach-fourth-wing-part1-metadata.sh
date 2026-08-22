#!/bin/zsh
set -euo pipefail

resource_group='audiochoice-staging'
catalog_prefix='3d37a3c485debd42249bc939'
vault_name=$(az keyvault list --resource-group "$resource_group" --query '[0].name' --output tsv)
api_token=$(az keyvault secret show --vault-name "$vault_name" --name staging-api-token --query value --output tsv)
api_host=$(az containerapp show --name audiochoice-stg-api --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv)

transcripts=$(curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" \
  "https://$api_host/v1/admin/transcripts")
matches=$(print -r -- "$transcripts" | jq --arg prefix "$catalog_prefix" \
  '[.[] | select(.isComplete == true and (.fingerprint.sha256 | ascii_downcase | startswith($prefix)))]')
if [[ $(print -r -- "$matches" | jq 'length') -ne 1 ]]; then
  echo 'Could not uniquely identify the Fourth Wing Part 1 transcript.' >&2
  print -r -- "$matches" | jq . >&2
  exit 1
fi

fingerprint=$(print -r -- "$matches" | jq '.[0].fingerprint')
payload=$(jq -n --argjson fingerprint "$fingerprint" '{
  fingerprint: $fingerprint,
  workTitle: "Fourth Wing, Part 1 of 2 (Dramatized Adaptation)",
  author: "Rebecca Yarros",
  seriesTitle: "The Empyrean",
  seriesNumber: 1,
  editionType: "Dramatized Adaptation",
  partNumber: 1,
  totalParts: 2,
  duration: 28800
}')

curl --fail --silent --show-error --request PUT \
  --header "Authorization: Bearer $api_token" \
  --header 'Content-Type: application/json' \
  --data "$payload" \
  "https://$api_host/v1/admin/editions/metadata"

curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" \
  "https://$api_host/v1/admin/explore" | jq --arg prefix "$catalog_prefix" \
  '.[] | select(.catalogID == $prefix) | {title, author, seriesTitle, seriesNumber, editionType, duration, coverImageURL, eventCount}'
