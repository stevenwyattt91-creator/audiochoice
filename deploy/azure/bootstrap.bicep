targetScope = 'resourceGroup'

@description('Azure region used for the AudioChoice staging resources.')
param location string = resourceGroup().location

@description('Short environment label used in resource names.')
@allowed([
  'staging'
  'production'
])
param environmentName string = 'staging'

var suffix = take(uniqueString(subscription().subscriptionId, resourceGroup().id), 8)
var compactEnvironment = environmentName == 'production' ? 'prod' : 'stg'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'audiochoice-${compactEnvironment}-logs-${suffix}'
  location: location
  properties: {
    retentionInDays: 30
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'audiochoice-${compactEnvironment}-environment'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'audiochoice${compactEnvironment}${suffix}'
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'audiochoice${compactEnvironment}${suffix}'
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource stagingData 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileService
  name: 'audiochoice-staging-data'
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: 20
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: false
    }
    containerDeleteRetentionPolicy: {
      enabled: false
    }
  }
}

resource temporaryAudio 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'temporary-audio'
  properties: {
    publicAccess: 'None'
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'audiochoice-${compactEnvironment}-${suffix}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enablePurgeProtection: true
    enableSoftDelete: true
    publicNetworkAccess: 'Enabled'
    sku: {
      family: 'A'
      name: 'standard'
    }
  }
}

output containerEnvironmentName string = containerEnvironment.name
output containerRegistryName string = registry.name
output keyVaultName string = vault.name
output storageAccountName string = storage.name
output stagingFileShareName string = stagingData.name
output temporaryAudioContainerName string = temporaryAudio.name
