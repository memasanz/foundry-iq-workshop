param location string
param searchName string
param storageName string
param searchSku string
param principalId string
param projectPrincipalId string
param tags object

resource search 'Microsoft.Search/searchServices@2025-05-01' = {
  name: searchName
  location: location
  sku: {
    name: searchSku
  }
  identity: {
    type: 'SystemAssigned'
  }
  tags: tags
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'Default'
    publicNetworkAccess: 'enabled'
    disableLocalAuth: true
    semanticSearch: 'free'
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  tags: tags
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Enabled'
    defaultToOAuthAuthentication: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource documents 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobService
  name: 'workshop-documents'
  properties: {
    publicAccess: 'None'
  }
}

var searchServiceContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
var searchDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
var searchDataReader = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '1407120a-92aa-4202-b7e9-c0e197c71c8f')
var blobDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var blobDataReader = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')

resource facilitatorSearchServiceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, principalId, searchServiceContributor)
  scope: search
  properties: {
    roleDefinitionId: searchServiceContributor
    principalId: principalId
    principalType: 'User'
  }
}

resource facilitatorSearchDataRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, principalId, searchDataContributor)
  scope: search
  properties: {
    roleDefinitionId: searchDataContributor
    principalId: principalId
    principalType: 'User'
  }
}

resource facilitatorBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(documents.id, principalId, blobDataContributor)
  scope: documents
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: principalId
    principalType: 'User'
  }
}

resource searchBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(documents.id, search.id, blobDataReader)
  scope: documents
  properties: {
    roleDefinitionId: blobDataReader
    principalId: search.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource projectSearchRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, projectPrincipalId, searchDataReader)
  scope: search
  properties: {
    roleDefinitionId: searchDataReader
    principalId: projectPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output searchPrincipalId string = search.identity.principalId
output searchServiceName string = search.name
output searchEndpoint string = 'https://${search.name}.search.windows.net'
output searchResourceId string = search.id
output storageAccountName string = storage.name
output storageBlobEndpoint string = storage.properties.primaryEndpoints.blob
output storageResourceId string = storage.id
output storageContainerName string = documents.name
