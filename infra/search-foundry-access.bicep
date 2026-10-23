param accountName string
param searchPrincipalId string

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: accountName
}

var cognitiveServicesUserRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
resource searchRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, searchPrincipalId, cognitiveServicesUserRole)
  scope: account
  properties: {
    roleDefinitionId: cognitiveServicesUserRole
    principalId: searchPrincipalId
    principalType: 'ServicePrincipal'
  }
}
