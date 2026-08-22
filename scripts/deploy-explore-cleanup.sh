#!/bin/zsh
set -euo pipefail

resource_group='audiochoice-staging'
image_tag='staging-036'
registry_name=$(az acr list --resource-group "$resource_group" --query '[0].name' --output tsv)
google_client_id=$(az containerapp show \
  --name audiochoice-stg-api \
  --resource-group "$resource_group" \
  --query "properties.template.containers[0].env[?name=='AudioChoice__Authentication__GoogleClientID'].value | [0]" \
  --output tsv)

az acr build --registry "$registry_name" --image "audiochoice-api:$image_tag" \
  --file backend/Dockerfile .
az deployment group create --name audiochoice-staging-explore-cleanup \
  --resource-group "$resource_group" --template-file deploy/azure/api.bicep \
  --parameters imageTag="$image_tag" googleClientID="$google_client_id" --output none

vault_name=$(az keyvault list --resource-group "$resource_group" --query '[0].name' --output tsv)
api_token=$(az keyvault secret show --vault-name "$vault_name" --name staging-api-token --query value --output tsv)
api_host=$(az containerapp show --name audiochoice-stg-api --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv)
curl --fail --silent --show-error "https://$api_host/health"
echo

catalog=$(curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" "https://$api_host/v1/admin/explore")
candidates=$(print -r -- "$catalog" | jq '[.[] | select(
  ((.title | ascii_downcase) | test("test|two[[:space:]]*twisted|twotwisted")) and
  ((.title | ascii_downcase) | contains("iron flame") | not)
)]')

if [[ $(print -r -- "$candidates" | jq 'length') -ne 2 ]]; then
  echo 'Safety stop: expected exactly two test Explore entries. Nothing was hidden.' >&2
  print -r -- "$candidates" | jq '[.[] | {catalogID, title, author}]' >&2
  exit 1
fi

print -r -- "$candidates" | jq -r '.[].catalogID' | while IFS= read -r catalog_id; do
  curl --fail --silent --show-error --request DELETE \
    --header "Authorization: Bearer $api_token" \
    "https://$api_host/v1/admin/explore/$catalog_id"
done

echo 'Hidden Explore test entries:'
print -r -- "$candidates" | jq -r '.[] | "- \(.title)"'

updated_catalog=$(curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" "https://$api_host/v1/admin/explore")
iron_flame=$(print -r -- "$updated_catalog" | jq '[.[] | select(
  (.title | ascii_downcase) | contains("iron flame"))][0]')
if [[ $(print -r -- "$iron_flame" | jq -r '.scannerVersion // ""') != '2.5' ]]; then
  echo 'Safety check failed: Iron Flame is not publishing scanner version 2.5.' >&2
  print -r -- "$iron_flame" | jq . >&2
  exit 1
fi
echo 'Verified Iron Flame Explore scan:'
print -r -- "$iron_flame" | jq '{title, editionType, eventCount, scannerVersion}'
echo 'Explore cleanup deployment completed.'
