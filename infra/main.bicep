targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment (e.g., dev, staging, prod)')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string

@description('Id of the user or app to assign application roles')
param principalId string = ''

// Tags applied to all resources
var tags = {
  'azd-env-name': environmentName
  'squadscout-phase': 'phase-1'
}

var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))

resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: '${abbrs.resourcesResourceGroups}${environmentName}'
  location: location
  tags: tags
}

module monitoring './core/monitor/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    location: location
    tags: tags
    logAnalyticsName: '${abbrs.operationalInsightsWorkspaces}${resourceToken}'
    applicationInsightsName: '${abbrs.insightsComponents}${resourceToken}'
  }
}

module webpubsub './core/messaging/webpubsub.bicep' = {
  name: 'webpubsub'
  scope: rg
  params: {
    location: location
    tags: tags
    webPubSubName: '${abbrs.webPubSubWebPubSub}${resourceToken}'
    hubName: 'squadscout'
  }
}

module functions './core/host/functions.bicep' = {
  name: 'functions'
  scope: rg
  params: {
    location: location
    tags: tags
    functionAppName: '${abbrs.webSitesFunctions}${resourceToken}'
    functionStorageName: '${abbrs.storageStorageAccounts}${resourceToken}'
    appServicePlanName: '${abbrs.webServerFarms}${resourceToken}'
    applicationInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
    webPubSubEndpoint: webpubsub.outputs.endpoint
    webPubSubHub: webpubsub.outputs.hubName
  }
}

// Grant Functions managed identity permission to Web PubSub
module webPubSubRoleAssignment './core/security/role.bicep' = {
  name: 'webpubsub-functions-role'
  scope: rg
  params: {
    principalId: functions.outputs.identityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: webpubsub.outputs.webPubSubServiceOwnerRoleId
  }
}

// Output values for azd and local.settings.json
output AZURE_LOCATION string = location
output AZURE_TENANT_ID string = tenant().tenantId
output AZURE_RESOURCE_GROUP string = rg.name

output APPLICATIONINSIGHTS_CONNECTION_STRING string = monitoring.outputs.applicationInsightsConnectionString

output FUNCTIONS_APP_NAME string = functions.outputs.functionAppName
output FUNCTIONS_APP_URL string = functions.outputs.functionAppUrl
output FUNCTIONS_IDENTITY_PRINCIPAL_ID string = functions.outputs.identityPrincipalId

output WEBPUBSUB_ENDPOINT string = webpubsub.outputs.endpoint
output WEBPUBSUB_HUB_NAME string = webpubsub.outputs.hubName
output WEBPUBSUB_HOSTNAME string = webpubsub.outputs.hostname
