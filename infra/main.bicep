targetScope = 'resourceGroup'

@description('Region for Foundry, AI Search and Blob Storage. Web IQ always uses global.')
param location string = 'centralus'

@minLength(3)
@maxLength(20)
param namePrefix string = 'iq-workshop'

@description('Object ID of the facilitator who will run the notebooks.')
param principalId string

param modelName string = 'gpt-5-mini'
param modelVersion string = '2025-08-07'
param modelDeploymentName string = 'workshop-model'

@allowed([
  'basic'
  'standard'
])
param searchSku string = 'standard'

@minValue(1)
param modelCapacity int = 30

param embeddingDeploymentName string = 'workshop-embedding'
param embeddingModelName string = 'text-embedding-3-small'
param embeddingModelVersion string = '1'

@minValue(1)
param embeddingCapacity int = 50

var suffix = uniqueString(resourceGroup().id)
var tags = {
  workload: 'iq-workshop'
  module: 'shared'
  environment: 'workshop'
}

// The provider is new; Bicep might not yet have its type definitions.
resource webiq 'Microsoft.WebIQ/accounts@2026-06-01-preview' = {
  name: '${namePrefix}-web-${suffix}'
  location: 'global'
  kind: 'WebIQ.Grounding'
  sku: {
    name: 'G3'
  }
  tags: tags
  properties: {
    statisticsEnabled: false
  }
}

// Do not provision Foundry if the subscription cannot create a Web IQ account.
module foundry 'foundry.bicep' = {
  name: 'foundry-${location}'
  params: {
    location: location
    accountName: '${namePrefix}-ai-${location}-${suffix}'
    projectName: 'iq-workshop'
    principalId: principalId
    modelName: modelName
    modelVersion: modelVersion
    modelDeploymentName: modelDeploymentName
    modelCapacity: modelCapacity
    embeddingDeploymentName: embeddingDeploymentName
    embeddingModelName: embeddingModelName
    embeddingModelVersion: embeddingModelVersion
    embeddingCapacity: embeddingCapacity
    tags: tags
  }
  dependsOn: [
    webiq
  ]
}

module data 'data.bicep' = {
  name: 'data-${location}'
  params: {
    location: location
    searchName: '${namePrefix}-search-${location}-${suffix}'
    storageName: 'stiq${uniqueString(resourceGroup().id, location)}'
    searchSku: searchSku
    principalId: principalId
    projectPrincipalId: foundry.outputs.projectPrincipalId
    tags: tags
  }
}

// Lets Search call the embedding and chat deployments with its managed identity (no keys).
module searchFoundryAccess 'search-foundry-access.bicep' = {
  name: 'search-foundry-access'
  params: {
    accountName: foundry.outputs.accountName
    searchPrincipalId: data.outputs.searchPrincipalId
  }
}

@description('Contact address APIM requires; defaults to a placeholder, used only for service notifications.')
param apimPublisherEmail string = 'workshop@example.com'

// Notebook 07: gateway in front of the knowledge base.
module apim 'apim.bicep' = {
  name: 'apim-${location}'
  params: {
    location: location
    apimName: '${namePrefix}-apim-${suffix}'
    searchName: data.outputs.searchServiceName
    publisherEmail: apimPublisherEmail
    tags: tags
  }
}

output apimName string = apim.outputs.apimName
output apimGatewayUrl string = apim.outputs.apimGatewayUrl
output apimResourceId string = apim.outputs.apimResourceId
output embeddingDeploymentName string = embeddingDeploymentName
output webiqResourceId string = webiq.id
output webiqAccountName string = webiq.name
output projectEndpoint string = foundry.outputs.projectEndpoint
output projectResourceId string = foundry.outputs.projectResourceId
output foundryAccountName string = foundry.outputs.accountName
output modelDeploymentName string = modelDeploymentName
output searchServiceName string = data.outputs.searchServiceName
output searchEndpoint string = data.outputs.searchEndpoint
output searchResourceId string = data.outputs.searchResourceId
output storageAccountName string = data.outputs.storageAccountName
output storageBlobEndpoint string = data.outputs.storageBlobEndpoint
output storageResourceId string = data.outputs.storageResourceId
output storageContainerName string = data.outputs.storageContainerName
