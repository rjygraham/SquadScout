param location string
param tags object = {}

@description('Name of the Function App')
param functionAppName string

@description('Name of the storage account for Functions runtime')
param functionStorageName string

@description('Name of the App Service Plan')
param appServicePlanName string

@description('Application Insights connection string')
param applicationInsightsConnectionString string

@description('Web PubSub endpoint URL')
param webPubSubEndpoint string

@description('Web PubSub hub name')
param webPubSubHub string

@description('Function App runtime')
param runtime string = 'dotnet-isolated'

@description('Function App runtime version')
param runtimeVersion string = '8.0'

@description('Flex Consumption plan SKU')
@allowed([
  'FC1'
])
param appServicePlanSku string = 'FC1'

@description('Maximum scale-out instance count for the Function App')
@minValue(40)
@maxValue(1000)
param maximumInstanceCount int = 100

@description('Instance memory size in MB for the Function App')
@allowed([
  2048
  4096
])
param instanceMemoryMB int = 2048

var deploymentContainerName = 'deployments'
var storageBlobDataContributorRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource functionStorage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: functionStorageName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

resource functionStorageBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: functionStorage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: functionStorageBlobService
  name: deploymentContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: appServicePlanSku
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
  kind: 'functionapp'
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: union(tags, {
    'azd-service-name': 'functions'
  })
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      cors: {
        allowedOrigins: [
          '*'
        ]
      }
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${functionStorage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: runtime
        version: runtimeVersion
      }
    }
  }
}

resource functionAppSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: functionApp
  name: 'appsettings'
  properties: {
    AzureWebJobsStorage: 'DefaultEndpointsProtocol=https;AccountName=${functionStorage.name};AccountKey=${functionStorage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
    APPLICATIONINSIGHTS_CONNECTION_STRING: applicationInsightsConnectionString
    Functions__WebPubSubEndpoint: webPubSubEndpoint
    Functions__WebPubSubHub: webPubSubHub
    Functions__BrokerBaseUrl: 'http://127.0.0.1:5071'
    Functions__TokenLifetimeMinutes: '60'
    Functions__EnableLocalDevelopmentIdentity: 'false'
  }
}

resource deploymentContainerBlobDataContributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(functionStorage.id, functionApp.id, storageBlobDataContributorRoleDefinitionId)
  scope: functionStorage
  properties: {
    roleDefinitionId: storageBlobDataContributorRoleDefinitionId
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output identityPrincipalId string = functionApp.identity.principalId
