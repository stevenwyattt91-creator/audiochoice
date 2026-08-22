targetScope = 'resourceGroup'

@description('Azure region used by the staging database. West US 3 currently exposes the required low-cost SKU and PostgreSQL version.')
param location string = 'westus3'

@description('Business-only PostgreSQL administrator name.')
param administratorLogin string = 'audiochoice_admin'

@secure()
@minLength(20)
@description('Generated staging database password. Never store this in source control.')
param administratorPassword string

var suffix = take(uniqueString(subscription().subscriptionId, resourceGroup().id), 8)
var serverName = 'audiochoice-stg-db-${suffix}'

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: serverName
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    authConfig: {
      activeDirectoryAuth: 'Disabled'
      passwordAuth: 'Enabled'
      tenantId: subscription().tenantId
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    createMode: 'Create'
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
    storage: {
      autoGrow: 'Disabled'
      storageSizeGB: 32
      type: 'Premium_LRS'
    }
    version: '16'
  }
}

// Azure interprets 0.0.0.0 as access from Azure-hosted services. It does not
// permit arbitrary public internet addresses. TLS and database credentials remain
// required, and production will move to private networking before public beta.
resource azureServicesOnly 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: server
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: server
  name: 'audiochoice'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

output serverName string = server.name
output serverHostname string = server.properties.fullyQualifiedDomainName
output databaseName string = database.name
output administratorLogin string = administratorLogin
output stagingSku string = 'Standard_B1ms'
output highAvailabilityEnabled bool = false
