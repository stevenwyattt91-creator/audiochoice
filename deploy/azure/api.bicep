targetScope = 'resourceGroup'

@description('Azure region used by the existing staging foundation.')
param location string = resourceGroup().location

@description('Private image tag already built in the AudioChoice registry.')
param imageTag string = 'staging-020'

@description('Comma-separated Google OAuth client IDs accepted as token audiences (for example Android/web and iOS). Leave empty until configured.')
param googleClientID string = ''

var suffix = take(uniqueString(subscription().subscriptionId, resourceGroup().id), 8)
var registryName = 'audiochoicestg${suffix}'
var storageName = 'audiochoicestg${suffix}'
var environmentName = 'audiochoice-stg-environment'
var vaultName = 'audiochoice-stg-${suffix}'
var acrPullRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d'
)
var keyVaultSecretsUserRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '4633458b-17de-408a-b874-0445c86b69e6'
)
var storageBlobDataContributorRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: storage
  name: 'default'
}

// Companion M4B transfers are private, account-paired, and automatically
// deleted after claim or expiration. This is not user library storage.
resource companionTransfersContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'companion-transfers'
  properties: {
    publicAccess: 'None'
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

resource pullIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'audiochoice-stg-api-identity'
  location: location
}

resource registryPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, pullIdentity.id, acrPullRoleDefinitionId)
  scope: registry
  properties: {
    principalId: pullIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleDefinitionId
  }
}

resource vaultSecretsRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, pullIdentity.id, keyVaultSecretsUserRoleDefinitionId)
  scope: vault
  properties: {
    principalId: pullIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleDefinitionId
  }
}

resource temporaryBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, pullIdentity.id, storageBlobDataContributorRoleDefinitionId)
  scope: storage
  properties: {
    principalId: pullIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleDefinitionId
  }
}

resource environmentStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: containerEnvironment
  name: 'staging-data'
  properties: {
    azureFile: {
      accountKey: storage.listKeys().keys[0].value
      accountName: storage.name
      accessMode: 'ReadWrite'
      shareName: 'audiochoice-staging-data'
    }
  }
}

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'audiochoice-stg-api'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${pullIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        allowInsecure: false
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          identity: pullIdentity.id
          server: registry.properties.loginServer
        }
      ]
      secrets: [
        {
          name: 'api-token'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/staging-api-token'
          identity: pullIdentity.id
        }
        {
          name: 'postgres-connection-string'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/postgres-connection-string'
          identity: pullIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: '${registry.properties.loginServer}/audiochoice-api:${imageTag}'
          env: [
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: pullIdentity.properties.clientId
            }
            {
              name: 'AudioChoice__ApiToken'
              secretRef: 'api-token'
            }
            {
              name: 'AudioChoice__DataPath'
              value: '/data'
            }
            {
              name: 'AudioChoice__Database__Enabled'
              value: 'true'
            }
            {
              name: 'AudioChoice__Database__ApplyMigrations'
              value: 'true'
            }
            {
              name: 'AudioChoice__Database__ConnectionString'
              secretRef: 'postgres-connection-string'
            }
            {
              name: 'AudioChoice__MaximumUploadBytes'
              value: '21474836480'
            }
            {
              name: 'AudioChoice__TemporaryAudioStorage__BlobEnabled'
              value: 'true'
            }
            {
              name: 'AudioChoice__TemporaryAudioStorage__StorageAccountName'
              value: storage.name
            }
            {
              name: 'AudioChoice__TemporaryAudioStorage__ContainerName'
              value: 'temporary-audio'
            }
            {
              name: 'AudioChoice__TemporaryAudioStorage__CompanionTransferContainerName'
              value: companionTransfersContainer.name
            }
            {
              name: 'AudioChoice__OpenAI__WorkerEnabled'
              value: 'false'
            }
            {
              name: 'AudioChoice__Authentication__GoogleClientID'
              value: googleClientID
            }
            {
              name: 'AudioChoice__TransactionalEmail__Enabled'
              value: 'false'
            }
            {
              name: 'AudioChoice__TransactionalEmail__FromAddress'
              value: 'AudioChoice <no-reply@audiochoiceapp.com>'
            }
            {
              name: 'AudioChoice__TransactionalEmail__ReplyToAddress'
              value: 'support@audiochoiceapp.com'
            }
            {
              name: 'AudioChoice__TransactionalEmail__ActionBaseURL'
              value: 'https://audiochoiceapp.com'
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          volumeMounts: [
            {
              mountPath: '/data'
              volumeName: 'data'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
        rules: [
          {
            name: 'https-requests'
            http: {
              metadata: {
                concurrentRequests: '10'
              }
            }
          }
        ]
      }
      volumes: [
        {
          name: 'data'
          storageName: environmentStorage.name
          storageType: 'AzureFile'
        }
      ]
    }
  }
  dependsOn: [
    registryPull
    vaultSecretsRead
    temporaryBlobAccess
  ]
}

output apiHostname string = api.properties.configuration.ingress.fqdn
output apiName string = api.name
output paidWorkerEnabled bool = false
output postgresEnabled bool = true
