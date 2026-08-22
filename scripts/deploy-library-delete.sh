#!/bin/zsh
set -euo pipefail

resource_group='audiochoice-staging'
image_tag='staging-031'
registry_name=$(az acr list --resource-group "$resource_group" --query '[0].name' --output tsv)
google_client_id=$(az containerapp show \
  --name audiochoice-stg-api \
  --resource-group "$resource_group" \
  --query "properties.template.containers[0].env[?name=='AudioChoice__Authentication__GoogleClientID'].value | [0]" \
  --output tsv)

if [[ -z "$registry_name" || -z "$google_client_id" ]]; then
  echo 'The registry or existing Google client configuration could not be found.' >&2
  exit 1
fi

az acr build \
  --registry "$registry_name" \
  --image "audiochoice-api:$image_tag" \
  --file backend/Dockerfile \
  .

az deployment group create \
  --name audiochoice-staging-library-delete \
  --resource-group "$resource_group" \
  --template-file deploy/azure/api.bicep \
  --parameters imageTag="$image_tag" googleClientID="$google_client_id" \
  --output none

api_host=$(az containerapp show \
  --name audiochoice-stg-api \
  --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn \
  --output tsv)

curl --fail --silent --show-error "https://$api_host/health"
echo
echo 'Library deletion backend deployment completed.'
