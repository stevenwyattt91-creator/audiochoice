#!/bin/zsh
set -euo pipefail

# Deploys the Admin Portal API and scan worker. The worker creates focused
# review tasks after each newly completed catalog scan.
resource_group='audiochoice-staging'
image_tag='staging-060'
registry_name=$(az acr list --resource-group "$resource_group" --query '[0].name' --output tsv)
google_client_id=$(az containerapp show \
  --name audiochoice-stg-api \
  --resource-group "$resource_group" \
  --query "properties.template.containers[0].env[?name=='AudioChoice__Authentication__GoogleClientID'].value | [0]" \
  --output tsv)

if [[ -z "$registry_name" || -z "$google_client_id" ]]; then
  echo 'The existing AudioChoice staging API configuration could not be found.' >&2
  exit 1
fi

az acr build --registry "$registry_name" --image "audiochoice-api:$image_tag" \
  --file backend/Dockerfile .

az deployment group create --name audiochoice-staging-admin-portal-api \
  --resource-group "$resource_group" --template-file deploy/azure/api.bicep \
  --parameters imageTag="$image_tag" googleClientID="$google_client_id" --output none

az deployment group create --name audiochoice-staging-focused-audit-worker \
  --resource-group "$resource_group" --template-file deploy/azure/worker.bicep \
  --parameters imageTag="$image_tag" maximumAudioDurationSeconds=108000 \
    maximumChunksPerJob=500 maximumRetries=3 maximumJobAttempts=3 --output none

api_host=$(az containerapp show --name audiochoice-stg-api --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv)
curl --fail --silent --show-error "https://$api_host/health"
echo
vault_name=$(az keyvault list --resource-group "$resource_group" --query '[0].name' --output tsv)
api_token=$(az keyvault secret show --vault-name "$vault_name" --name staging-api-token --query value --output tsv)
echo 'Fourth Wing focused-audit estimate:'
curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" \
  "https://$api_host/v1/admin/explore/3d37a3c485debd42249bc939/audit-estimate" | jq .
echo 'Focused audit workflow deployment completed.'
