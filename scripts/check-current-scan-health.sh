#!/bin/zsh
set -u

resource_group='audiochoice-staging'
api_name='audiochoice-stg-api'
worker_name='audiochoice-stg-worker'

api_host=$(az containerapp show \
  --name "$api_name" --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv)

echo "API address: https://$api_host"
echo 'API health:'
curl --connect-timeout 15 --max-time 30 --silent --show-error \
  --write-out '\nHTTP %{http_code} in %{time_total}s\n' \
  "https://$api_host/health" || true

echo 'API revision:'
az containerapp revision list \
  --name "$api_name" --resource-group "$resource_group" \
  --query "[?properties.active].{Name:name,Healthy:properties.healthState,State:properties.provisioningState,Replicas:properties.replicas}" \
  --output table

echo 'Worker revision:'
az containerapp revision list \
  --name "$worker_name" --resource-group "$resource_group" \
  --query "[?properties.active].{Name:name,Healthy:properties.healthState,State:properties.provisioningState,Replicas:properties.replicas}" \
  --output table

echo 'Recent API logs:'
az containerapp logs show \
  --name "$api_name" --resource-group "$resource_group" --tail 30 || true

echo 'Recent worker logs:'
az containerapp logs show \
  --name "$worker_name" --resource-group "$resource_group" --tail 50 || true
