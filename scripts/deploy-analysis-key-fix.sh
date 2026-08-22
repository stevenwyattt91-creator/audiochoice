#!/bin/zsh
set -euo pipefail

resource_group='audiochoice-staging'
image_tag='staging-023'
registry_name=$(az acr list \
  --resource-group "$resource_group" \
  --query '[0].name' \
  --output tsv)

az acr build \
  --registry "$registry_name" \
  --image "audiochoice-api:$image_tag" \
  --file backend/Dockerfile .

az deployment group create \
  --name audiochoice-staging-analysis-key-fix \
  --resource-group "$resource_group" \
  --template-file deploy/azure/worker.bicep \
  --parameters \
    imageTag="$image_tag" \
    maximumAudioDurationSeconds=108000 \
    maximumChunksPerJob=500 \
    maximumRetries=3 \
  --output none

echo 'Analysis result storage fix deployed.'
