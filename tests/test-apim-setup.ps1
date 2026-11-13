$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$brokerScript = Join-Path $root 'scripts\setup-broker-app.ps1'
$policyScript = Join-Path $root 'scripts\setup-apim-policy.ps1'
$fixture = New-TemporaryFile
$secrets = New-TemporaryFile
$scriptArguments = @{ EnvironmentFile = $fixture.FullName; SecretsFile = $secrets.FullName }
$subscription = '00000000-0000-0000-0000-000000000001'
$apimSubscription = '00000000-0000-0000-0000-000000000003'
$tenant = '00000000-0000-0000-0000-000000000002'
$apimId = "/subscriptions/$apimSubscription/resourceGroups/shared-gateways/providers/Microsoft.ApiManagement/service/existing-gateway"
$fixtureText = @"
AZURE_SUBSCRIPTION_ID=$subscription
AZURE_TENANT_ID=$tenant
AZURE_RESOURCE_GROUP=workshop
AZURE_SEARCH_ENDPOINT=https://workshop.search.windows.net
"@
$fixtureText | Set-Content -LiteralPath $fixture.FullName
$global:TestApimTenant = $tenant
$global:TestApimIdentity = '00000000-0000-0000-0000-000000000004'
$global:TestApimCalls = [System.Collections.Generic.List[object]]::new()
$global:TestApimWrites = [System.Collections.Generic.List[object]]::new()
$global:TestBrokerExists = $true
$global:TestBrokerScopes = @()
$testKey = ConvertTo-SecureString 'synthetic-test-key' -AsPlainText -Force

function global:az {
    $arguments = @($args)
    $global:TestApimCalls.Add($arguments)
    $global:LASTEXITCODE = 0
    switch ($arguments[0..1] -join ' ') {
        'account show' {
            $subscriptionId = $arguments[$arguments.IndexOf('--subscription') + 1]
            $tenantId = if ($subscriptionId -eq '00000000-0000-0000-0000-000000000003') {
                $global:TestApimTenant
            }
            else { '00000000-0000-0000-0000-000000000002' }
            @{ tenantId = $tenantId } | ConvertTo-Json -Compress
        }
        'resource show' {
            @{
                identity = @{ principalId = $global:TestApimIdentity }
                properties = @{ gatewayUrl = 'https://gateway.example.test/' }
            } | ConvertTo-Json -Depth 5 -Compress
        }
        'rest --method' {
            $method = $arguments[$arguments.IndexOf('--method') + 1]
            $url = $arguments[$arguments.IndexOf('--url') + 1]
            if ($method -ne 'GET') {
                $bodyPath = $arguments[$arguments.IndexOf('--body') + 1].Substring(1)
                $bodyText = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($bodyPath))
                Assert-Equal ($bodyText[0] -eq [char]0xfeff) $false 'Azure CLI JSON request files must not contain a UTF-8 BOM'
                $body = $bodyText | ConvertFrom-Json
                $responsePath = $null
                if ($url -like 'https://management.azure.com/*/apis/iq-knowledge-base/policies/policy?api-version=*') {
                    if ('--output-file' -notin $arguments) {
                        throw 'Policy XML responses must bypass Azure CLI console output to avoid cp1252 encoding failures.'
                    }
                    $responsePath = $arguments[$arguments.IndexOf('--output-file') + 1]
                    [System.IO.File]::WriteAllText($responsePath, '<policies />', [System.Text.UTF8Encoding]::new($true))
                }
                if ($url -eq 'https://graph.microsoft.com/v1.0/applications/broker-object' -and $method -eq 'PATCH') {
                    foreach ($preAuthorized in $body.api.preAuthorizedApplications) {
                        foreach ($scopeId in $preAuthorized.delegatedPermissionIds) {
                            if ($scopeId -notin @($global:TestBrokerScopes.id)) {
                                throw 'Pre-authorization must reference an already saved scope.'
                            }
                        }
                    }
                    if ($body.api.oauth2PermissionScopes) {
                        $global:TestBrokerScopes = @($body.api.oauth2PermissionScopes)
                    }
                }
                $global:TestApimWrites.Add([pscustomobject]@{
                    Method = $method
                    Url = $url
                    Body = $body
                    BodyPath = $bodyPath
                    ResponsePath = $responsePath
                })
                if ($url -eq 'https://graph.microsoft.com/v1.0/applications' -and $method -eq 'POST') {
                    $global:TestBrokerExists = $true
                    return '{"id":"broker-object","appId":"broker-client","api":{"oauth2PermissionScopes":[]}}'
                }
                return '{}'
            }
            if ($url.StartsWith('https://graph.microsoft.com/v1.0/servicePrincipals?')) {
                if ($url.Contains('oauth2PermissionScopes')) {
                    @{ value = @(@{
                        appId = 'search-api'
                        oauth2PermissionScopes = @(@{ id = 'search-scope'; value = 'user_impersonation'; isEnabled = $true })
                    }) } | ConvertTo-Json -Depth 8 -Compress
                }
                else { '{"value":[{"id":"broker-service-principal"}]}' }
            }
            elseif ($url.StartsWith('https://graph.microsoft.com/v1.0/applications?')) {
                $apps = if ($global:TestBrokerExists) {
                    @(@{ id = 'broker-object'; appId = 'broker-client'; api = @{ oauth2PermissionScopes = @($global:TestBrokerScopes) } })
                }
                else { @() }
                @{ value = @($apps) } | ConvertTo-Json -Depth 8 -Compress
            }
            elseif ($url -like 'https://management.azure.com/*/namedValues/broker-client-secret?api-version=*') {
                '{}'
            }
            else { throw "Unexpected REST read: $url" }
        }
        default { throw "Unexpected Azure CLI command: $arguments" }
    }
}

function Assert-Equal($Actual, $Expected, $Message) {
    if ($Actual -ne $Expected) { throw "$Message. Expected: $Expected; actual: $Actual" }
}

function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike $Pattern) { throw }
        return
    }
    throw "Expected a failure matching: $Pattern"
}

try {
    foreach ($script in $brokerScript, $policyScript) {
        Assert-Fails { & $script @scriptArguments } 'Set APIM_RESOURCE_ID*'
        Assert-Fails { & $script @scriptArguments -ApimResourceId '/invalid/resource' } 'Set APIM_RESOURCE_ID*'
    }
    Assert-Equal $global:TestApimCalls.Count 0 'Missing or invalid gateway IDs must fail before Azure calls'

    $fixtureText + "`nAPIM_RESOURCE_ID=/subscriptions/$subscription/resourceGroups/workshop/providers/Microsoft.ApiManagement/service/old-gateway" |
        Set-Content -LiteralPath $fixture.FullName
    'APIM_RESOURCE_ID=/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/workshop/providers/Microsoft.ApiManagement/service/old-gateway' |
        Set-Content -LiteralPath $secrets.FullName
    & $brokerScript @scriptArguments -ApimResourceId $apimId | Out-Null
    $armWrites = @($global:TestApimWrites | Where-Object { $_.Url.StartsWith('https://management.azure.com/') })
    Assert-Equal $armWrites.Count 2 'Broker must store its identifiers in the existing gateway'
    foreach ($write in $armWrites) {
        Assert-Equal $write.Url.StartsWith("https://management.azure.com$apimId/namedValues/") $true 'Explicit gateway must override old settings, including across resource groups and subscriptions'
    }
    $scopeId = $global:TestBrokerScopes[0].id
    Assert-Equal ([bool]$scopeId) $true 'A partially created broker must gain a scope before pre-authorization'
    $appWrites = @($global:TestApimWrites | Where-Object { $_.Url -eq 'https://graph.microsoft.com/v1.0/applications/broker-object' })
    Assert-Equal $appWrites.Count 2 'Scope creation and pre-authorization must use separate updates'
    Assert-Equal $appWrites[1].Body.api.preAuthorizedApplications[0].delegatedPermissionIds[0] $scopeId 'Azure CLI must be authorized for the saved scope'

    $global:TestApimWrites.Clear()
    & $brokerScript @scriptArguments -ApimResourceId $apimId | Out-Null
    Assert-Equal $global:TestBrokerScopes[0].id $scopeId 'Re-running setup must preserve the existing scope ID'

    $global:TestApimWrites.Clear()
    $global:TestBrokerExists = $false
    $global:TestBrokerScopes = @()
    & $brokerScript @scriptArguments -ApimResourceId $apimId | Out-Null
    Assert-Equal $global:TestBrokerExists $true 'Setup must create a missing app'
    Assert-Equal $global:TestBrokerScopes.Count 1 'A new app must save its scope before pre-authorization'

    $global:TestApimWrites.Clear()
    "APIM_RESOURCE_ID=`"$apimId`"" | Set-Content -LiteralPath $secrets.FullName
    $output = & $policyScript @scriptArguments -WebIqApiKey $testKey 6>&1 | Out-String
    Assert-Equal $global:TestApimWrites.Count 8 'Policy setup must create the shared source, named value, API, four operations and policy'
    $sources = @($global:TestApimWrites | Where-Object { $_.Url.StartsWith('https://workshop.search.windows.net/knowledgesources/') })
    Assert-Equal $sources.Count 1 'The facilitator must create exactly one shared Search source'
    Assert-Equal $sources[0].Body.name 'iq-workshop-webiq' 'Source and policy must use the same default name'
    Assert-Equal $sources[0].Body.kind 'mcpServer' 'The shared source must use Web IQ MCP'
    Assert-Equal $sources[0].Body.mcpServerParameters.serverURL 'https://api.microsoft.ai/v3/mcp' 'The shared source must point at Web IQ'
    Assert-Equal $sources[0].Body.mcpServerParameters.authentication $null 'The source must not store the Web IQ key'
    Assert-Equal $sources[0].Body.mcpServerParameters.tools[0].name 'web' 'The shared source must expose only the workshop web tool'
    Assert-Equal (Test-Path -LiteralPath $sources[0].BodyPath) $false 'The shared-source request file must be deleted'
    $armWrites = @($global:TestApimWrites | Where-Object { $_.Url.StartsWith('https://management.azure.com/') })
    foreach ($write in $armWrites) {
        Assert-Equal $write.Url.StartsWith("https://management.azure.com$apimId/") $true '.env must override old deployment settings and target the existing gateway only'
    }
    $api = $global:TestApimWrites | Where-Object { $_.Body.properties.serviceUrl }
    Assert-Equal $api.Body.properties.serviceUrl 'https://workshop.search.windows.net' 'Existing gateway must use the workshop Search backend'
    Assert-Equal $output.Contains('APIM_GATEWAY_URL=https://gateway.example.test') $true 'Gateway URL must come from APIM metadata'
    $policy = $global:TestApimWrites | Where-Object { $_.ResponsePath }
    Assert-Equal (Test-Path -LiteralPath $policy.ResponsePath) $false 'The unused policy response file must be deleted'

    $global:TestApimWrites.Clear()
    & $brokerScript @scriptArguments | Out-Null
    $armWrites = @($global:TestApimWrites | Where-Object { $_.Url.StartsWith('https://management.azure.com/') })
    Assert-Equal $armWrites.Count 2 'Broker must read the existing gateway from .env'
    foreach ($write in $armWrites) {
        Assert-Equal $write.Url.StartsWith("https://management.azure.com$apimId/namedValues/") $true 'Broker and policy scripts must select the same gateway from .env'
    }

    $global:TestApimWrites.Clear()
    $fixtureText + "`nAPIM_RESOURCE_ID=$apimId" | Set-Content -LiteralPath $fixture.FullName
    & $brokerScript @scriptArguments -WhatIf | Out-Null
    & $policyScript @scriptArguments -WebIqApiKey $testKey -WhatIf | Out-Null
    Assert-Equal $global:TestApimWrites.Count 0 'WhatIf must preserve the existing gateway and app'
    Clear-Content -LiteralPath $secrets.FullName
    $output = & $policyScript @scriptArguments -WebIqApiKey $testKey -WebIqSourceName 'custom-shared-webiq' 6>&1 | Out-String
    Assert-Equal $global:TestApimWrites.Count 8 'Legacy deployment resource-ID settings must remain usable'
    Assert-Equal $output.Contains('APIM_GATEWAY_URL=https://gateway.example.test') $true 'Settings fallback must select the existing gateway'
    $source = $global:TestApimWrites | Where-Object { $_.Body.kind -eq 'mcpServer' }
    Assert-Equal $source.Body.name 'custom-shared-webiq' 'A custom source name must be used for source creation'
    Assert-Equal $source.Url.Contains('/custom-shared-webiq?api-version=2026-08-01-preview') $true 'The Search URL must use the same custom source name'
    $policy = $global:TestApimWrites | Where-Object { $_.ResponsePath }
    Assert-Equal $policy.Body.properties.value.Contains('name="custom-shared-webiq-header-value"') $true 'The policy must inject the key for the same custom source'
    Assert-Equal $output.Contains('WEBIQ_APIM_SOURCE_NAME=custom-shared-webiq') $true 'Attendees must receive the configured shared source name'

    $global:TestApimWrites.Clear()
    $global:TestApimTenant = 'another-tenant'
    foreach ($script in $brokerScript, $policyScript) {
        Assert-Fails { & $script @scriptArguments -ApimResourceId $apimId } 'APIM must belong to the workshop tenant.'
    }
    Assert-Equal $global:TestApimWrites.Count 0 'Tenant mismatches must fail before mutations'

    $global:TestApimTenant = $tenant
    $global:TestApimIdentity = $null
    Assert-Fails { & $policyScript @scriptArguments -ApimResourceId $apimId -WebIqApiKey $testKey } 'Enable a system-assigned managed identity*'
    Assert-Equal $global:TestApimWrites.Count 0 'Missing managed identity must fail before gateway changes'
    Write-Host 'Existing APIM checks passed: .env configuration, explicit resource ID, settings fallback, metadata URL, tenant/identity validation and WhatIf.'
}
finally {
    Remove-Item -LiteralPath $fixture.FullName
    Remove-Item -LiteralPath $secrets.FullName
    Remove-Item Function:\az
}
