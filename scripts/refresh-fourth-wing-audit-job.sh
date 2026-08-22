#!/bin/zsh
set -euo pipefail

# Clears the internal audit queue and recreates the focused Fourth Wing Part 1
# job. Source audio is intentionally not stored by this script; attach it from
# the Admin Portal after the job appears.
resource_group='audiochoice-staging'
fourth_wing_catalog_id='3d37a3c485debd42249bc939'

api_host=$(az containerapp show --name audiochoice-stg-api --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv)
vault_name=$(az keyvault list --resource-group "$resource_group" --query '[0].name' --output tsv)
api_token=$(az keyvault secret show --vault-name "$vault_name" --name staging-api-token --query value --output tsv)

curl --fail --silent --show-error --request DELETE \
  --header "Authorization: Bearer $api_token" \
  "https://$api_host/v1/admin/audits" | jq .

echo 'Creating the fresh Fourth Wing Part 1 focused audit job:'
curl --fail --silent --show-error --request POST \
  --header "Authorization: Bearer $api_token" \
  "https://$api_host/v1/admin/explore/$fourth_wing_catalog_id/focused-audit" | jq .
