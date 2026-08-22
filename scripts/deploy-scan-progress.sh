#!/bin/zsh
set -euo pipefail

resource_group='audiochoice-staging'
image_tag='staging-020'
registry_name=$(az acr list --resource-group "$resource_group" --query '[0].name' --output tsv)
google_client_id=$(az containerapp show \
  --name audiochoice-stg-api --resource-group "$resource_group" \
  --query "properties.template.containers[0].env[?name=='AudioChoice__Authentication__GoogleClientID'].value | [0]" --output tsv)

az acr build --registry "$registry_name" --image "audiochoice-api:$image_tag" --file backend/Dockerfile .
az deployment group create \
  --name audiochoice-staging-scan-progress-api --resource-group "$resource_group" \
  --template-file deploy/azure/api.bicep \
  --parameters imageTag="$image_tag" googleClientID="$google_client_id" --output none
az deployment group create \
  --name audiochoice-staging-scan-progress-worker --resource-group "$resource_group" \
  --template-file deploy/azure/worker.bicep \
  --parameters imageTag="$image_tag" maximumAudioDurationSeconds=108000 maximumChunksPerJob=500 maximumRetries=3 \
  --output none

api_host=$(az containerapp show --name audiochoice-stg-api --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv)
curl --fail --silent --show-error "https://$api_host/health"
echo
echo 'Live scan progress deployment completed.'
