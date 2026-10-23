[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [string]$ResourceGroup = 'rg-iq-workshop',
    [string]$Location = 'centralus',
    [string]$NamePrefix = 'iq-workshop',
    [string]$PrincipalId,
    [string]$ModelName = 'gpt-5-mini',
    [string]$ModelVersion = '2025-08-07',
    [int]$ModelCapacity = 30,
    [ValidateSet('basic', 'standard')][string]$SearchSku = 'standard',
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Invoke-Az {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI failed: az $($Arguments -join ' ')"
    }
    return $result
}

$account = Invoke-Az account show --subscription $SubscriptionId --output json | ConvertFrom-Json
if (-not $PrincipalId) {
    $PrincipalId = Invoke-Az ad signed-in-user show --query id --output tsv
}
Write-Host "Subscription: $($account.name) ($SubscriptionId)"
Write-Host "Resource group: $ResourceGroup | Foundry, Search, Storage: $Location | Web IQ: global"
Write-Host "Model: $ModelName/$ModelVersion, GlobalStandard, capacity $ModelCapacity"
Write-Host "Search: $SearchSku, 1 replica/partition | Storage: Standard_LRS, Hot, Entra authentication"

Invoke-Az bicep build --file (Join-Path $root 'infra\main.bicep') --stdout | Out-Null

if (-not $WhatIf) {
    foreach ($provider in @('Microsoft.WebIQ', 'Microsoft.CognitiveServices', 'Microsoft.Search', 'Microsoft.Storage', 'Microsoft.ApiManagement')) {
        Invoke-Az provider register --namespace $provider --subscription $SubscriptionId --wait | Out-Null
    }
    $groupExists = Invoke-Az group exists --name $ResourceGroup --subscription $SubscriptionId --output tsv
    if ($groupExists.Trim() -eq 'false') {
        Invoke-Az group create --name $ResourceGroup --location $Location --subscription $SubscriptionId `
            --tags workload=iq-workshop environment=workshop --output none
    }
}

$parameters = @(
    "location=$Location", "namePrefix=$NamePrefix", "principalId=$PrincipalId",
    "modelName=$ModelName", "modelVersion=$ModelVersion", "modelCapacity=$ModelCapacity",
    "searchSku=$SearchSku"
)
$operation = if ($WhatIf) { 'what-if' } else { 'create' }
$deployment = Invoke-Az deployment group $operation --name 'iq-workshop' `
    --subscription $SubscriptionId --resource-group $ResourceGroup `
    --template-file (Join-Path $root 'infra\main.bicep') --parameters @parameters --output json
if ($WhatIf) {
    $deployment
    return
}

$outputs = ($deployment | ConvertFrom-Json).properties.outputs
$groupLocation = Invoke-Az group show --name $ResourceGroup --subscription $SubscriptionId --query location --output tsv
$settings = [ordered]@{
    AZURE_SUBSCRIPTION_ID = $SubscriptionId
    AZURE_TENANT_ID = $account.tenantId
    AZURE_RESOURCE_GROUP = $ResourceGroup
    AZURE_LOCATION = $Location
    AZURE_RESOURCE_GROUP_LOCATION = $groupLocation
    WEBIQ_RESOURCE_ID = $outputs.webiqResourceId.value
    WEBIQ_ACCOUNT_NAME = $outputs.webiqAccountName.value
    AZURE_AI_PROJECT_ENDPOINT = $outputs.projectEndpoint.value
    AZURE_AI_PROJECT_RESOURCE_ID = $outputs.projectResourceId.value
    AZURE_AI_ACCOUNT_NAME = $outputs.foundryAccountName.value
    AZURE_AI_MODEL_DEPLOYMENT_NAME = $outputs.modelDeploymentName.value
AZURE_AI_EMBEDDING_DEPLOYMENT_NAME = $outputs.embeddingDeploymentName.value
    APIM_NAME = $outputs.apimName.value
    APIM_GATEWAY_URL = $outputs.apimGatewayUrl.value
    APIM_RESOURCE_ID = $outputs.apimResourceId.value
    WEBIQ_CONNECTION_NAME = 'webiq-mcp'
    AZURE_SEARCH_SERVICE_NAME = $outputs.searchServiceName.value
    AZURE_SEARCH_ENDPOINT = $outputs.searchEndpoint.value
    AZURE_SEARCH_RESOURCE_ID = $outputs.searchResourceId.value
    AZURE_STORAGE_ACCOUNT_NAME = $outputs.storageAccountName.value
    AZURE_STORAGE_BLOB_ENDPOINT = $outputs.storageBlobEndpoint.value
    AZURE_STORAGE_RESOURCE_ID = $outputs.storageResourceId.value
    AZURE_STORAGE_CONTAINER_NAME = $outputs.storageContainerName.value
}
$settings.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } |
    Set-Content (Join-Path $root '.env.azure') -Encoding utf8
$stateDirectory = Join-Path $root '.deployment'
New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
$deployment | Set-Content (Join-Path $stateDirectory 'deployment.json') -Encoding utf8
Write-Host 'Deployed. Non-secret notebook settings saved to .env.azure.'
Write-Host "Link this resource at https://webiq.microsoft.ai/profiles: $($outputs.webiqResourceId.value)"
Write-Host 'Create an API key there, then enter it in the notebook password prompt or in .env.'
