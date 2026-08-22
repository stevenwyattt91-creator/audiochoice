targetScope = 'resourceGroup'

@description('Azure region used by the existing staging Container Apps environment.')
param location string = resourceGroup().location

@description('Private image tag already built in the AudioChoice registry.')
param imageTag string

@description('Maximum audio duration this worker may send for paid processing.')
@minValue(60)
param maximumAudioDurationSeconds int = 300

@description('Maximum number of audio chunks this worker may process per scan.')
@minValue(1)
param maximumChunksPerJob int = 1

@description('Keep this at zero for the first controlled paid test.')
@minValue(0)
param maximumRetries int = 0

@description('Hard stop for repeated worker claims of the same job.')
@minValue(1)
param maximumJobAttempts int = 3

var suffix = take(uniqueString(subscription().subscriptionId, resourceGroup().id), 8)
var registryName = 'audiochoicestg${suffix}'
var storageName = 'audiochoicestg${suffix}'
var environmentName = 'audiochoice-stg-environment'
var vaultName = 'audiochoice-stg-${suffix}'
var identityName = 'audiochoice-stg-api-identity'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageName
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: identityName
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'audiochoice-stg-worker'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          identity: workerIdentity.id
          server: registry.properties.loginServer
        }
      ]
      secrets: [
        {
          name: 'postgres-connection-string'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/postgres-connection-string'
          identity: workerIdentity.id
        }
        {
          name: 'openai-api-key'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/openai-api-key'
          identity: workerIdentity.id
        }
        {
          name: 'resend-api-key'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/resend-api-key'
          identity: workerIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: '${registry.properties.loginServer}/audiochoice-api:${imageTag}'
          env: [
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: workerIdentity.properties.clientId
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
              value: 'false'
            }
            {
              name: 'AudioChoice__Database__ConnectionString'
              secretRef: 'postgres-connection-string'
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
              name: 'AudioChoice__OpenAI__WorkerEnabled'
              value: 'true'
            }
            {
              name: 'AudioChoice__OpenAI__ApiKey'
              secretRef: 'openai-api-key'
            }
            {
              name: 'AudioChoice__OpenAI__TranscriptionModel'
              value: 'whisper-1'
            }
            {
              name: 'AudioChoice__OpenAI__AnalysisModel'
              value: 'gpt-5.6-luna'
            }
            {
              name: 'AudioChoice__OpenAI__SceneVerificationModel'
              value: 'gpt-5.6-terra'
            }
            {
              name: 'AudioChoice__OpenAI__SceneEscalationModel'
              value: 'gpt-5.6-sol'
            }
            {
              name: 'AudioChoice__OpenAI__ScannerVersion'
              value: '3.2'
            }
            {
              name: 'AudioChoice__OpenAI__MaximumSceneVerificationRequestsPerJob'
              value: '50'
            }
            {
              name: 'AudioChoice__OpenAI__MaximumSceneEscalationRequestsPerJob'
              value: '5'
            }
            {
              name: 'AudioChoice__OpenAI__MaximumRetries'
              value: string(maximumRetries)
            }
            {
              name: 'AudioChoice__OpenAI__MaximumJobAttempts'
              value: string(maximumJobAttempts)
            }
            {
              name: 'AudioChoice__OpenAI__MaximumChunksPerJob'
              value: string(maximumChunksPerJob)
            }
            {
              name: 'AudioChoice__OpenAI__MaximumAudioDurationSeconds'
              value: string(maximumAudioDurationSeconds)
            }
            {
              name: 'AudioChoice__Ffmpeg__MaximumInputDurationSeconds'
              value: '108000'
            }
            {
              name: 'AudioChoice__OpenAI__MaximumTranscriptSegmentsPerJob'
              value: '100000'
            }
            {
              name: 'AudioChoice__TransactionalEmail__Enabled'
              value: 'true'
            }
            {
              name: 'AudioChoice__TransactionalEmail__ApiKey'
              secretRef: 'resend-api-key'
            }
            {
              name: 'AudioChoice__TransactionalEmail__FromAddress'
              value: 'AudioChoice <no-reply@audiochoiceapp.com>'
            }
            {
              name: 'AudioChoice__TransactionalEmail__ReplyToAddress'
              value: 'support@audiochoiceapp.com'
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
              initialDelaySeconds: 15
              periodSeconds: 30
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
      }
      volumes: [
        {
          name: 'data'
          storageName: 'staging-data'
          storageType: 'AzureFile'
        }
      ]
    }
  }
}

output workerName string = worker.name
output paidWorkerEnabled bool = true
output maximumPaidAudioSeconds int = maximumAudioDurationSeconds
