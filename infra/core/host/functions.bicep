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

@description('App Service Plan SKU')
@allowed([
  'Y1'     // Consumption
  'EP1'    // Elastic Premium
  'EP2'
  'EP3'
])
param appServicePlanSku string = 'Y1'

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

resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: appServicePlanSku
  }
  properties: {
    reserved: true  // Required for Linux
  }
  kind: 'functionapp'
}

resource functionApp 'Microsoft.Web/sites@2023-01-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: '${toUpper(runtime)}|${runtimeVersion}'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      use32BitWorkerProcess: false
      cors: {
        allowedOrigins: [
          '*'
        ]
      }
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${functionStorage.name};AccountKey=${functionStorage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
        }
        {
          name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
          value: 'DefaultEndpointsProtocol=https;AccountName=${functionStorage.name};AccountKey=${functionStorage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
        }
        {
          name: 'WEBSITE_CONTENTSHARE'
          value: toLower(functionAppName)
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: runtime
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: applicationInsightsConnectionString
        }
        {
          name: 'Functions__WebPubSubEndpoint'
          value: webPubSubEndpoint
        }
        {
          name: 'Functions__WebPubSubHub'
          value: webPubSubHub
        }
        {
          name: 'Functions__BrokerBaseUrl'
          value: 'http://127.0.0.1:5071'
        }
        {
          name: 'Functions__TokenLifetimeMinutes'
          value: '60'
        }
        {
          name: 'Functions__EnableLocalDevelopmentIdentity'
          value: 'false'
        }
      ]
    }
  }
}

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output identityPrincipalId string = functionApp.identity.principalId
