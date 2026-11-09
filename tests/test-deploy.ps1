$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "iq-deploy-test-$([guid]::NewGuid().ToString('N'))"
$testScripts = Join-Path $testRoot 'scripts'
New-Item -ItemType Directory -Path $testScripts | Out-Null
$scriptPath = Join-Path $testScripts 'deploy.ps1'
Copy-Item -LiteralPath (Join-Path $root 'scripts\deploy.ps1') -Destination $scriptPath
$subscription = '00000000-0000-0000-0000-000000000001'
$groupId = "/subscriptions/$subscription/resourceGroups/workshop"
$searchId = "$groupId/providers/Microsoft.Search/searchServices/test-search"
$projectId = "$groupId/providers/Microsoft.CognitiveServices/accounts/test-ai/projects/workshop"
$readerRole = "/subscriptions/$subscription/providers/Microsoft.Authorization/roleDefinitions/1407120a-92aa-4202-b7e9-c0e197c71c8f"
$global:TestDeployPrincipal = '00000000-0000-0000-0000-000000000003'
$global:TestDeployAssignments = [System.Collections.Generic.List[object]]::new()
$global:TestDeployAssignments.Add(@{ principalId = 'old-project-identity'; scope = $searchId; roleDefinitionId = $readerRole })
$global:TestDeployCreates = 0
$global:TestDeployRoleReads = 0
$global:TestDeployFail = $false
$global:TestDeployOutputs = @{}
$values = @{
    projectEndpoint = 'https://test-ai.services.ai.azure.com/api/projects/workshop'
    projectResourceId = $projectId
    foundryAccountName = 'test-ai'
    modelDeploymentName = 'workshop-model'
    embeddingDeploymentName = 'workshop-embedding'
    searchServiceName = 'test-search'
    searchEndpoint = 'https://test-search.search.windows.net'
    searchResourceId = $searchId
    storageAccountName = 'teststorage'
    storageBlobEndpoint = 'https://teststorage.blob.core.windows.net/'
    storageResourceId = "$groupId/providers/Microsoft.Storage/storageAccounts/teststorage"
    storageContainerName = 'workshop-documents'
    webiqResourceId = "$groupId/providers/Microsoft.WebIQ/accounts/test-web"
    webiqAccountName = 'test-web'
}
foreach ($key in $values.Keys) { $global:TestDeployOutputs[$key] = @{ value = $values[$key] } }
$manualEnv = Join-Path $testRoot '.env'
$generatedEnv = Join-Path $testRoot '.env.azure'
'APIM_RESOURCE_ID=/subscriptions/test/resourceGroups/shared/providers/Microsoft.ApiManagement/service/existing' |
    Set-Content -LiteralPath $manualEnv
$manualHash = (Get-FileHash -LiteralPath $manualEnv).Hash
$scriptArguments = @{ SubscriptionId = $subscription; ResourceGroup = 'workshop'; PrincipalId = 'facilitator-user' }

function global:az {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    switch ($arguments[0..1] -join ' ') {
        'account show' { '{"name":"Test","tenantId":"test-tenant"}' }
        'bicep build' { '{}' }
        'provider register' { }
        'group exists' { 'true' }
        'group show' { 'eastus2' }
        'deployment group' {
            if ($global:TestDeployFail) { $global:LASTEXITCODE = 1; return }
            @{ properties = @{ provisioningState = 'Succeeded'; outputs = $global:TestDeployOutputs } } |
                ConvertTo-Json -Depth 10 -Compress
        }
        'resource show' { $global:TestDeployPrincipal }
        'role assignment' {
            if ($arguments[2] -eq 'list') {
                $global:TestDeployRoleReads++
                ConvertTo-Json -InputObject @($global:TestDeployAssignments.ToArray()) -Compress
            }
            elseif ($arguments[2] -eq 'create') {
                if ($arguments[$arguments.IndexOf('--assignee-principal-type') + 1] -ne 'ServicePrincipal') {
                    throw 'Project role must target a service principal.'
                }
                $global:TestDeployAssignments.Add(@{
                    principalId = $arguments[$arguments.IndexOf('--assignee-object-id') + 1]
                    scope = $arguments[$arguments.IndexOf('--scope') + 1]
                    roleDefinitionId = $arguments[$arguments.IndexOf('--role') + 1]
                })
                $global:TestDeployCreates++
            }
            else { throw "Unexpected role command: $arguments" }
        }
        default { throw "Unexpected Azure CLI command: $arguments" }
    }
}

function Assert-Equal($Actual, $Expected, $Message) {
    if ($Actual -ne $Expected) { throw "$Message. Expected: $Expected; actual: $Actual" }
}

try {
    & $scriptPath @scriptArguments | Out-Null
    Assert-Equal $global:TestDeployCreates 1 'A stale deployment identity must result in a grant to the live project'
    $grant = $global:TestDeployAssignments[-1]
    Assert-Equal $grant.principalId $global:TestDeployPrincipal 'Grant must use the live project identity'
    Assert-Equal $grant.scope $searchId 'Grant must be scoped to the deployed Search service'
    Assert-Equal $grant.roleDefinitionId $readerRole 'Grant must be Search Index Data Reader'
    $settings = Get-Content -LiteralPath $generatedEnv -Raw | ConvertFrom-StringData
    Assert-Equal $settings.AZURE_AI_PROJECT_ENDPOINT $values.projectEndpoint 'Configuration must use deployed outputs'
    Assert-Equal $settings.AZURE_LOCATION 'eastus' 'Configuration must use the deployed region'
    Assert-Equal (Get-FileHash -LiteralPath $manualEnv).Hash $manualHash 'Existing APIM configuration in .env must be preserved'

    & $scriptPath @scriptArguments | Out-Null
    Assert-Equal $global:TestDeployCreates 1 'A matching live identity grant must not be duplicated'
    $generatedHash = (Get-FileHash -LiteralPath $generatedEnv).Hash
    $roleReads = $global:TestDeployRoleReads
    & $scriptPath @scriptArguments -WhatIf | Out-Null
    Assert-Equal $global:TestDeployRoleReads $roleReads 'WhatIf must skip post-deployment role reconciliation'
    Assert-Equal $global:TestDeployCreates 1 'WhatIf must not grant roles'
    Assert-Equal (Get-FileHash -LiteralPath $generatedEnv).Hash $generatedHash 'WhatIf must not rewrite configuration'

    $global:TestDeployPrincipal = ''
    $failed = $false
    try { & $scriptPath @scriptArguments | Out-Null }
    catch {
        if ($_.Exception.Message -notlike '*no system-assigned identity*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Missing live identity must fail before configuration is rewritten'
    Assert-Equal (Get-FileHash -LiteralPath $generatedEnv).Hash $generatedHash 'Reconciliation failure must retain the previous configuration'
    Assert-Equal $global:TestDeployCreates 1 'Missing identity must not grant roles'
    Write-Host 'Deployment checks passed: live identity repair, idempotency, generated settings, preserved .env, WhatIf and identity failure.'
}
finally {
    Remove-Item Function:\az
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
