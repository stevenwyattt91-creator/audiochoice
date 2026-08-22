#!/bin/zsh
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo 'Usage: ./scripts/download-conversion-consent.sh RECORD_ID' >&2
  exit 1
fi

resource_group='audiochoice-staging'
record_id=$1
vault_name=$(az keyvault list --resource-group "$resource_group" --query '[0].name' --output tsv)
api_token=$(az keyvault secret show --vault-name "$vault_name" --name staging-api-token --query value --output tsv)
api_host=$(az containerapp show --name audiochoice-stg-api --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv)
output="audiochoice-consent-$record_id.json"

curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" \
  --output "$output" \
  "https://$api_host/v1/admin/conversion-consents/$record_id/document"
echo "Saved audit document: $output"
