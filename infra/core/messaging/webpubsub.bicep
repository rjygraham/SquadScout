param location string
param tags object = {}

@description('Name of the Web PubSub service')
param webPubSubName string

@description('Web PubSub hub name')
param hubName string = 'squadscout'

@description('Web PubSub SKU')
@allowed([
  'Free_F1'
  'Standard_S1'
  'Premium_P1'
])
param sku string = 'Free_F1'

@description('Web PubSub capacity (number of units)')
@minValue(1)
@maxValue(100)
param capacity int = 1

resource webPubSub 'Microsoft.SignalRService/webPubSub@2023-02-01' = {
  name: webPubSubName
  location: location
  tags: tags
  sku: {
    name: sku
    capacity: capacity
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    disableLocalAuth: false
    publicNetworkAccess: 'Enabled'
    tls: {
      clientCertEnabled: false
    }
  }
}

resource hub 'Microsoft.SignalRService/webPubSub/hubs@2023-02-01' = {
  name: hubName
  parent: webPubSub
  properties: {
    eventHandlers: []
    anonymousConnectPolicy: 'deny'
  }
}

// Built-in Web PubSub Service Owner role definition
// https://learn.microsoft.com/azure/role-based-access-control/built-in-roles#web-pubsub-service-owner
var webPubSubServiceOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12cf5a90-567b-43ae-8102-96cf46c7d9b4')

output endpoint string = 'https://${webPubSub.properties.hostName}'
output hostname string = webPubSub.properties.hostName
output hubName string = hub.name
output name string = webPubSub.name
output webPubSubServiceOwnerRoleId string = webPubSubServiceOwnerRoleId
