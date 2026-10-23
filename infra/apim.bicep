param location string
param apimName string
param searchName string
param publisherEmail string
param publisherName string = 'IQ workshop'
param tags object

// Consumption is serverless, pay-per-call and provisions in minutes.
resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: apimName
  location: location
  tags: tags
  sku: {
    name: 'Consumption'
    capacity: 0
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
  }
}

resource search 'Microsoft.Search/searchServices@2025-05-01' existing = {
  name: searchName
}

// The gateway calls the knowledge base as itself; the user's identity travels in x-ms-query-source-authorization.
var searchIndexDataReaderRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '1407120a-92aa-4202-b7e9-c0e197c71c8f')
resource apimSearchRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, apim.id, searchIndexDataReaderRole)
  scope: search
  properties: {
    roleDefinitionId: searchIndexDataReaderRole
    principalId: apim.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output apimName string = apim.name
output apimGatewayUrl string = apim.properties.gatewayUrl
output apimResourceId string = apim.id
