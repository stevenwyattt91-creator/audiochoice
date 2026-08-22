#!/bin/zsh
set -euo pipefail

resource_group='audiochoice-staging'
image_tag='staging-024'
registry_name=$(az acr list \
  --resource-group "$resource_group" \
  --query '[0].name' \
  --output tsv)

az acr build \
  --registry "$registry_name" \
  --image "audiochoice-api:$image_tag" \
  --file backend/Dockerfile .

az deployment group create \
  --name audiochoice-staging-backend-reliability \
  --resource-group "$resource_group" \
  --template-file deploy/azure/worker.bicep \
  --parameters \
    imageTag="$image_tag" \
    maximumAudioDurationSeconds=108000 \
    maximumChunksPerJob=500 \
    maximumRetries=3 \
  --output none

worker_revision=$(az containerapp revision list \
  --name audiochoice-stg-worker \
  --resource-group "$resource_group" \
  --query '[?properties.active].[name,properties.healthState,properties.provisioningState]' \
  --output tsv)

echo "Worker revision status: $worker_revision"
echo 'Backend reliability deployment completed.'
