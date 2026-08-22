#!/bin/zsh
set -euo pipefail

resource_group='audiochoice-staging'
query=${1:-}
vault_name=$(az keyvault list --resource-group "$resource_group" --query '[0].name' --output tsv)
api_token=$(az keyvault secret show --vault-name "$vault_name" --name staging-api-token --query value --output tsv)
api_host=$(az containerapp show --name audiochoice-stg-api --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv)

encoded_query=$(QUERY="$query" python3 -c 'import os, urllib.parse; print(urllib.parse.quote(os.environ["QUERY"]))')
curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" \
  "https://$api_host/v1/admin/conversion-consents?query=$encoded_query&limit=50" \
  | python3 -m json.tool
