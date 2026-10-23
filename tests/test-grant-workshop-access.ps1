$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\grant-workshop-access.ps1'
$fixture = New-TemporaryFile
$subscription = '00000000-0000-0000-0000-000000000001'
$tenant = '00000000-0000-0000-0000-000000000002'
$group = "/subscriptions/$subscription/resourceGroups/test-workshop"
$search = "$group/providers/Microsoft.Search/searchServices/test-search"
$storage = "$group/providers/Microsoft.Storage/storageAccounts/teststorage"
$project = "$group/providers/Microsoft.CognitiveServices/accounts/test-ai/projects/test-project"
$fixtureText = @"
AZURE_SUBSCRIPTION_ID=$subscription
AZURE_TENANT_ID=$tenant
AZURE_RESOURCE_GROUP=test-workshop
AZURE_SEARCH_RESOURCE_ID=$search
AZURE_STORAGE_RESOURCE_ID=$storage
AZURE_STORAGE_CONTAINER_NAME=workshop-documents
AZURE_AI_PROJECT_RESOURCE_ID=$project
"@
$fixtureText | Set-Content -LiteralPath $fixture.FullName
$global:TestAssignments = [System.Collections.Generic.List[object]]::new()
$global:TestCreates = 0
$global:TestDirectory = @{
    'alice@example.com' = @{ id = '00000000-0000-0000-0000-000000000003'; userPrincipalName = 'alice@example.com' }
    'bob@example.com' = @{ id = '00000000-0000-0000-0000-000000000004'; userPrincipalName = 'bob@example.com' }
    'charlie@example.com' = @{ id = '00000000-0000-0000-0000-000000000005'; userPrincipalName = 'charlie@example.com' }
}
$global:TestDirectory['00000000-0000-0000-0000-000000000003'] = $global:TestDirectory['alice@example.com']

function global:az {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    switch ($arguments[0..1] -join ' ') {
        'account show' {
            @{ name = 'Test subscription'; tenantId = '00000000-0000-0000-0000-000000000002' } |
                ConvertTo-Json -Compress
        }
        'ad user' {
            $id = $arguments[$arguments.IndexOf('--id') + 1]
            if (-not $global:TestDirectory.ContainsKey($id)) {
                $global:LASTEXITCODE = 1
                return
            }
            $global:TestDirectory[$id] | ConvertTo-Json -Compress
        }
        'role assignment' {
            $scope = $arguments[$arguments.IndexOf('--scope') + 1]
            if ($arguments[2] -eq 'list') {
                $matches = @($global:TestAssignments | Where-Object { $_.scope -eq $scope })
                ConvertTo-Json -InputObject $matches -Depth 5 -Compress
            }
            elseif ($arguments[2] -eq 'create') {
                $assignment = [pscustomobject]@{
                    principalId = $arguments[$arguments.IndexOf('--assignee-object-id') + 1]
                    roleDefinitionId = $arguments[$arguments.IndexOf('--role') + 1]
                    scope = $scope
                }
                $global:TestAssignments.Add($assignment)
                $global:TestCreates++
                $assignment | ConvertTo-Json -Compress
            }
            else {
                throw "Unexpected role command: $arguments"
            }
        }
        default { throw "Unexpected Azure CLI command: $arguments" }
    }
}

function Assert-Equal($Actual, $Expected, $Message) {
    if ($Actual -ne $Expected) {
        throw "$Message. Expected: $Expected; actual: $Actual"
    }
}

try {
    & $scriptPath -EnvironmentFile $fixture.FullName `
        -Users 'alice@example.com', 'bob@example.com', '00000000-0000-0000-0000-000000000003' | Out-Null
    Assert-Equal $global:TestCreates 8 'Two unique users should each receive four roles'
    Assert-Equal @($global:TestAssignments | Where-Object { $_.scope -eq $search }).Count 4 'Search roles must be service-scoped'
    $container = "$storage/blobServices/default/containers/workshop-documents"
    Assert-Equal @($global:TestAssignments | Where-Object { $_.scope -eq $container }).Count 2 'Blob roles must be container-scoped'
    Assert-Equal @($global:TestAssignments | Where-Object { $_.scope -eq $project }).Count 2 'Foundry roles must be project-scoped'

    & $scriptPath -EnvironmentFile $fixture.FullName -Users 'alice@example.com', 'bob@example.com' | Out-Null
    Assert-Equal $global:TestCreates 8 'Repeated execution must not create duplicate assignments'

    & $scriptPath -EnvironmentFile $fixture.FullName -Users 'charlie@example.com' -WhatIf | Out-Null
    Assert-Equal $global:TestCreates 8 'WhatIf must not grant any role'

    $failed = $false
    try {
        & $scriptPath -EnvironmentFile $fixture.FullName -Users 'charlie@example.com', 'missing@example.com' | Out-Null
    }
    catch {
        if ($_.Exception.Message -notlike 'Azure CLI failed:*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Unresolvable attendees must cause an explicit failure'
    Assert-Equal $global:TestCreates 8 'All attendees must be resolved before permissions change'

    $fixtureText.Replace("AZURE_SEARCH_RESOURCE_ID=$search", 'AZURE_SEARCH_RESOURCE_ID=/subscriptions/another/resourceGroups/other/providers/Microsoft.Search/searchServices/other') |
        Set-Content -LiteralPath $fixture.FullName
    $failed = $false
    try {
        & $scriptPath -EnvironmentFile $fixture.FullName -Users 'alice@example.com' | Out-Null
    }
    catch {
        if ($_.Exception.Message -notlike 'Resource scope is outside*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Scopes outside the configured workshop must be rejected'
    Assert-Equal $global:TestCreates 8 'Invalid scopes must not change assignments'
    Write-Host 'Permissions script checks passed: scoped roles, duplicate attendees, reruns, WhatIf and explicit failures.'
}
finally {
    Remove-Item -LiteralPath $fixture.FullName
    Remove-Item Function:\az
}
