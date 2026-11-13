$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\setup-sharepoint-app.ps1'
$fixture = New-TemporaryFile
@'
AZURE_SUBSCRIPTION_ID=test-subscription
AZURE_TENANT_ID=test-tenant
AZURE_SEARCH_RESOURCE_ID=/subscriptions/test-subscription/resourceGroups/test/providers/Microsoft.Search/searchServices/test
'@ | Set-Content -LiteralPath $fixture.FullName

$global:TestAppMatches = @()
$global:TestAppWrites = [System.Collections.Generic.List[object]]::new()
$global:TestAppFilter = ''
$global:TestSitePermissions = @()
$global:TestSiteNextPage = @()
$global:TestSiteDenied = $false
$global:TestVerifiedGrant = $null
$global:TestRoleAssignments = @()
$global:TestPasswords = @(@{ keyId = 'existing-key'; displayName = 'existing-credential' })
$global:TestBootstrapFailure = ''
$global:TestTokenAttempts = 0
$global:TestSleepCalls = 0
$global:TestContextTenant = 'test-tenant'
$global:TestAzureCalls = [System.Collections.Generic.List[string]]::new()
$global:TestConsentFailure = ''

function global:az {
    $arguments = @($args)
    $global:TestAzureCalls.Add($arguments -join ' ')
    $global:LASTEXITCODE = 0
    switch ($arguments[0..1] -join ' ') {
        'account show' { @{ tenantId = $global:TestContextTenant } | ConvertTo-Json -Compress }
        'resource show' { @{ identity = @{ principalId = 'search-principal' } } | ConvertTo-Json -Compress }
        'rest --method' {
            $method = $arguments[$arguments.IndexOf('--method') + 1]
            $url = $arguments[$arguments.IndexOf('--url') + 1]
            $uri = [Uri]$url
            $path = $uri.AbsolutePath
            $query = [Uri]::UnescapeDataString($uri.Query)
            if ($method -ne 'GET') {
                $body = $null
                if ($arguments -contains '--body') {
                    $bodyPath = $arguments[$arguments.IndexOf('--body') + 1].Substring(1)
                    $body = Get-Content -LiteralPath $bodyPath -Raw | ConvertFrom-Json
                }
                $global:TestAppWrites.Add(@{ Method = $method; Path = $path; Body = $body })
                if ($method -eq 'POST' -and $path -eq '/v1.0/applications') {
                    @{ id = 'app-object'; appId = 'app-client' } | ConvertTo-Json -Compress
                }
                elseif ($method -eq 'POST' -and $path -eq '/v1.0/sites/site-id/permissions') {
                    $grant = @{ id = 'site-grant'; roles = $body.roles; grantedToIdentitiesV2 = $body.grantedToIdentities }
                    $global:TestSitePermissions += $grant
                    $grant | ConvertTo-Json -Depth 10 -Compress
                }
                elseif ($method -eq 'POST' -and $path -eq '/v1.0/servicePrincipals/app-service-principal/appRoleAssignments') {
                    if ($body.appRoleId -ne 'Sites.FullControl.All') {
                        if ($global:TestConsentFailure -eq 'denied') { $global:LASTEXITCODE = 1; return }
                        $assignment = @{ id = "$($body.resourceId)-$($body.appRoleId)"; resourceId = $body.resourceId; appRoleId = $body.appRoleId }
                        if ($global:TestConsentFailure -ne 'verification') { $global:TestRoleAssignments += $assignment }
                        $assignment | ConvertTo-Json -Compress
                        return
                    }
                    $assignment = @{ id = 'bootstrap-role'; resourceId = $body.resourceId; appRoleId = $body.appRoleId }
                    $global:TestRoleAssignments += $assignment
                    if ($global:TestBootstrapFailure -eq 'role-response') {
                        $global:LASTEXITCODE = 1
                        return
                    }
                    $assignment | ConvertTo-Json -Compress
                }
                elseif ($method -eq 'DELETE' -and $path -eq '/v1.0/servicePrincipals/app-service-principal/appRoleAssignments/bootstrap-role') {
                    if ($global:TestBootstrapFailure -eq 'role-cleanup') {
                        $global:LASTEXITCODE = 1
                        return
                    }
                    $global:TestRoleAssignments = @($global:TestRoleAssignments | Where-Object { $_.id -ne 'bootstrap-role' })
                }
                elseif ($method -eq 'POST' -and $path -eq '/v1.0/applications/app-object/addPassword') {
                    $global:TestPasswords += @{ keyId = 'bootstrap-key'; displayName = $body.passwordCredential.displayName }
                    if ($global:TestBootstrapFailure -eq 'credential-response') {
                        $global:LASTEXITCODE = 1
                        return
                    }
                    @{ keyId = 'bootstrap-key'; secretText = 'test-secret-not-for-output' } | ConvertTo-Json -Compress
                }
                elseif ($method -eq 'POST' -and $path -eq '/v1.0/applications/app-object/removePassword') {
                    if ($global:TestBootstrapFailure -eq 'credential-cleanup') {
                        $global:LASTEXITCODE = 1
                        return
                    }
                    $global:TestPasswords = @($global:TestPasswords | Where-Object { $_.keyId -ne $body.keyId })
                }
                elseif ($method -ne 'PATCH') { throw "Unexpected Graph write: $method $url" }
            }
            elseif ($path -eq '/v1.0/servicePrincipals/app-service-principal/appRoleAssignments') {
                @{ value = @($global:TestRoleAssignments) } | ConvertTo-Json -Depth 5 -Compress
            }
            elseif ($path -eq '/v1.0/applications/app-object') {
                @{ passwordCredentials = @($global:TestPasswords) } | ConvertTo-Json -Depth 5 -Compress
            }
            elseif ($path -eq '/v1.0/sites/contoso.sharepoint.com:/sites/workshop') {
                @{ id = 'site-id' } | ConvertTo-Json -Compress
            }
            elseif ($path -eq '/v1.0/sites/site-id/permissions') {
                if ($global:TestSiteDenied) {
                    $global:LASTEXITCODE = 1
                    return
                }
                if ($query -eq '?page=2') {
                    @{ value = @($global:TestSiteNextPage) } | ConvertTo-Json -Depth 10 -Compress
                }
                else {
                    $page = @{ value = @($global:TestSitePermissions) }
                    if ($global:TestSiteNextPage.Count) {
                        $page['@odata.nextLink'] = 'https://graph.microsoft.com/v1.0/sites/site-id/permissions?page=2'
                    }
                    $page | ConvertTo-Json -Depth 10 -Compress
                }
            }
            elseif ($path -eq '/v1.0/sites/site-id/permissions/site-grant') {
                if ($null -ne $global:TestVerifiedGrant) {
                    $global:TestVerifiedGrant | ConvertTo-Json -Depth 10 -Compress
                }
                else { $global:TestSitePermissions[-1] | ConvertTo-Json -Depth 10 -Compress }
            }
            elseif ($path -eq '/v1.0/servicePrincipals/search-principal') {
                @{ id = 'search-principal'; appId = 'search-client' } | ConvertTo-Json -Compress
            }
            elseif ($path -eq '/v1.0/applications') {
                $global:TestAppFilter = $query
                @{ value = @($global:TestAppMatches) } | ConvertTo-Json -Depth 10 -Compress
            }
            elseif ($path -eq '/v1.0/applications/app-object/federatedIdentityCredentials') {
                @{ value = @(@{
                    issuer = 'https://login.microsoftonline.com/test-tenant/v2.0'
                    subject = 'search-principal'
                }) } | ConvertTo-Json -Depth 5 -Compress
            }
            elseif ($path -eq '/v1.0/servicePrincipals') {
                if ($query -like "*appId eq 'app-client'*" -or
                    ($global:TestAppMatches.Count -eq 1 -and $query -like "*appId eq '$($global:TestAppMatches[0].appId)'*")) {
                    @{ value = @(@{ id = 'app-service-principal' }) } | ConvertTo-Json -Depth 5 -Compress
                }
                else {
                    $roles = foreach ($name in @('Files.Read.All', 'Sites.Selected', 'User.Read.All', 'Sites.FullControl.All')) {
                        @{ id = $name; value = $name; isEnabled = $true; allowedMemberTypes = @('Application') }
                    }
                    $apiId = if ($query -like '*00000003-0000-0ff1-ce00-000000000000*') { 'sharepoint-api-principal' } else { 'graph-api-principal' }
                    @{ value = @(@{ id = $apiId; appRoles = @($roles) }) } | ConvertTo-Json -Depth 5 -Compress
                }
            }
            else { throw "Unexpected Graph request: $method $url" }
        }
        default { throw "Unexpected Azure CLI command: $arguments" }
    }
}

function global:Invoke-RestMethod {
    param($Method, $Uri, $Headers, $Body, $TimeoutSec, $ContentType)
    if ($Uri -like 'https://login.microsoftonline.com/*/oauth2/v2.0/token') {
        $global:TestTokenAttempts++
        Assert-Equal $Body.client_secret 'test-secret-not-for-output' 'Bootstrap must use only its temporary credential'
        if ($global:TestBootstrapFailure -eq 'token') { throw 'Token failure with test-secret-not-for-output' }
        if ($global:TestBootstrapFailure -eq 'propagation-timeout' -or
            ($global:TestBootstrapFailure -eq 'propagation' -and $global:TestTokenAttempts -eq 1)) {
            $failure = [InvalidOperationException]::new('Propagation pending')
            $failure.Data['StatusCode'] = 401
            throw $failure
        }
        return @{ access_token = 'test-bootstrap-token' }
    }
    Assert-Equal $Headers.Authorization 'Bearer test-bootstrap-token' 'Site requests must use the app-only token'
    if ($global:TestBootstrapFailure -eq 'site-grant' -and $Method -eq 'POST') { throw 'Site grant failed' }
    $arguments = @('rest', '--method', $Method, '--url', $Uri)
    $file = $null
    $wasDenied = $global:TestSiteDenied
    try {
        if ($Body) {
            $file = New-TemporaryFile
            $Body | Set-Content -LiteralPath $file.FullName
            $arguments += @('--body', "@$($file.FullName)")
        }
        $global:TestSiteDenied = $false
        $result = az @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Mock app-only request failed' }
        if ($result) { return ($result | ConvertFrom-Json) }
    }
    finally {
        $global:TestSiteDenied = $wasDenied
        if ($file) { Remove-Item -LiteralPath $file.FullName }
    }
}

function global:Start-Sleep {
    param($Seconds)
    $global:TestSleepCalls++
}

function Assert-Equal($Actual, $Expected, $Message) {
    if ($Actual -ne $Expected) { throw "$Message. Expected: $Expected; actual: $Actual" }
}

try {
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName 'New Workshop App' | Out-Null
    Assert-Equal $global:TestAppWrites.Count 1 'A missing app must be created'
    Assert-Equal $global:TestAppWrites[0].Body.displayName 'New Workshop App' 'Creation must use the supplied name'
    Assert-Equal $global:TestAppWrites[0].Body.signInAudience 'AzureADMyOrg' 'New apps must be single-tenant'

    $global:TestAppWrites.Clear()
    & $scriptPath -EnvironmentFile $fixture.FullName | Out-Null
    Assert-Equal $global:TestAppWrites[0].Body.displayName 'iq-workshop-sharepoint-indexer' 'Omitting AppName must retain the default'

    $global:TestAppMatches = @(@{
        id = 'app-object'; appId = 'app-client'
        requiredResourceAccess = @(
            @{
                resourceAppId = '00000003-0000-0000-c000-000000000000'
                resourceAccess = @(
                    @{ id = 'Files.Read.All'; type = 'Role' },
                    @{ id = 'existing-delegated-permission'; type = 'Scope' }
                )
            },
            @{ resourceAppId = 'another-api'; resourceAccess = @(@{ id = 'another-role'; type = 'Role' }) }
        )
    })
    $global:TestAppWrites.Clear()
    $appName = "Workshop's App & Search"
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName $appName -InformationVariable messages | Out-Null
    Assert-Equal $global:TestAppFilter "?`$filter=displayName eq 'Workshop''s App & Search'&`$select=id,appId,requiredResourceAccess" 'The exact-name filter must escape and encode special characters'
    Assert-Equal $global:TestAppWrites.Count 1 'Reusing an app must update it without creating another'
    Assert-Equal $global:TestAppWrites[0].Method 'PATCH' 'Existing apps must be updated'
    Assert-Equal $global:TestAppWrites[0].Path '/v1.0/applications/app-object' 'The matching app must be selected'
    $merged = $global:TestAppWrites[0].Body.requiredResourceAccess
    $graphAccess = @($merged | Where-Object { $_.resourceAppId -eq '00000003-0000-0000-c000-000000000000' })[0].resourceAccess
    Assert-Equal $merged.Count 3 'Existing API entries must be preserved alongside required APIs'
    Assert-Equal $graphAccess.Count 3 'Existing permissions must be preserved without duplicating required roles'
    Assert-Equal @($graphAccess | Where-Object { $_.id -eq 'existing-delegated-permission' -and $_.type -eq 'Scope' }).Count 1 'Delegated permissions must survive reuse'
    Assert-Equal @($merged | Where-Object { $_.resourceAppId -eq 'another-api' }).Count 1 'Other APIs must survive reuse'

    $global:TestAppMatches[0].requiredResourceAccess = $merged
    $global:TestAppWrites.Clear()
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName $appName | Out-Null
    $repeated = $global:TestAppWrites[0].Body.requiredResourceAccess
    Assert-Equal $repeated.Count 3 'Reruns must not duplicate API entries'
    Assert-Equal (@($repeated | Where-Object { $_.resourceAppId -eq '00000003-0000-0000-c000-000000000000' })[0].resourceAccess.Count) 3 'Reruns must not duplicate permissions'

    $global:TestAppWrites.Clear()
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName $appName -WhatIf | Out-Null
    Assert-Equal $global:TestAppWrites.Count 0 'WhatIf must not modify an existing app'

    $global:TestAppMatches = @()
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName 'Preview New App' -WhatIf | Out-Null
    Assert-Equal $global:TestAppWrites.Count 0 'WhatIf must not create a new app'

    $global:TestAppMatches = @(@{ id = 'first' }, @{ id = 'second' })
    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -AppName 'Duplicate' | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'More than one app named*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Duplicate display names must fail explicitly'
    Assert-Equal $global:TestAppWrites.Count 0 'Duplicate matches must not modify any app'

    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -AppName ' ' | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*parameter 'AppName'*") { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Whitespace-only app names must be rejected'

    $siteUrl = 'https://contoso.sharepoint.com/sites/workshop'
    $global:TestAppMatches = @(@{ id = 'app-object'; appId = 'app-client'; requiredResourceAccess = @() })
    $global:TestAppWrites.Clear()
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName $appName -SiteUrl $siteUrl | Out-Null
    $siteWrites = @($global:TestAppWrites | Where-Object { $_.Path -eq '/v1.0/sites/site-id/permissions' })
    Assert-Equal $siteWrites.Count 1 'SiteUrl must cause a direct site grant'
    Assert-Equal ($siteWrites[0].Body.roles -join ',') 'read' 'New site grants must request only Read'
    Assert-Equal $siteWrites[0].Body.grantedToIdentities[0].application.id 'app-client' 'The grant must target the ingestion app'
    Assert-Equal $siteWrites[0].Body.grantedToIdentities[0].application.displayName $appName 'The grant must use the selected app name'

    $global:TestAppWrites.Clear()
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName $appName -SiteUrl $siteUrl | Out-Null
    Assert-Equal @($global:TestAppWrites | Where-Object { $_.Path -like '/v1.0/sites/*' }).Count 0 'Reruns must reuse an existing Read grant'

    $global:TestSiteNextPage = $global:TestSitePermissions
    $global:TestSitePermissions = @(@{
        id = 'unrelated'; roles = @('fullcontrol')
        grantedToIdentitiesV2 = @(@{ application = @{ id = 'another-app' } })
    })
    $global:TestAppWrites.Clear()
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName $appName -SiteUrl $siteUrl | Out-Null
    Assert-Equal @($global:TestAppWrites | Where-Object { $_.Path -like '/v1.0/sites/*' }).Count 0 'Existing grants must be found on later pages'

    $global:TestSiteNextPage = @()
    $global:TestSitePermissions = @(@{
        id = 'unrelated'; roles = @('fullcontrol')
        grantedToIdentities = @(@{ application = @{ id = 'another-app' } })
    })
    $global:TestAppWrites.Clear()
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName $appName -SiteUrl $siteUrl -WhatIf | Out-Null
    Assert-Equal $global:TestAppWrites.Count 0 'WhatIf must not modify apps or grant site access'

    & $scriptPath -EnvironmentFile $fixture.FullName -AppName $appName -SiteUrl $siteUrl | Out-Null
    Assert-Equal @($global:TestAppWrites | Where-Object { $_.Path -eq '/v1.0/sites/site-id/permissions' }).Count 1 'Another app grant must not suppress the ingestion app grant'

    $global:TestSiteDenied = $true
    $global:TestAppWrites.Clear()
    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'Cannot inspect site access*Sites.FullControl.All*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Unauthorized callers must receive actionable site permission guidance'
    Assert-Equal $global:TestAppWrites.Count 0 'Site authorization must be checked before app changes'
    $global:TestSiteDenied = $false

    foreach ($role in @('read', 'write', 'owner', 'fullcontrol')) {
        $global:TestSitePermissions = @(@{
            id = 'existing-grant'; roles = @($role)
            grantedToIdentities = @(@{ application = @{ id = 'app-client' } })
        })
        $global:TestAppWrites.Clear()
        & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl -WarningVariable warnings | Out-Null
        Assert-Equal @($global:TestAppWrites | Where-Object { $_.Path -like '/v1.0/sites/*' }).Count 0 "Existing $role grants must not be duplicated, escalated or downgraded"
        $expectedWarnings = if ($role -eq 'read') { 0 } else { 1 }
        Assert-Equal @($warnings | Where-Object { "$_" -eq 'An existing site grant is broader than Read and was left unchanged.' }).Count $expectedWarnings 'Broader existing grants must be reported'
    }

    $global:TestSitePermissions = @(@{
        id = 'unknown-role'; roles = @('unknown')
        grantedToIdentities = @(@{ application = @{ id = 'app-client' } })
    })
    $global:TestAppWrites.Clear()
    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'The app already has a site grant*roles do not confirm read access*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Unrecognized existing roles must not be silently duplicated or revoked'
    Assert-Equal @($global:TestAppWrites | Where-Object { $_.Path -like '/v1.0/sites/*' }).Count 0 'Unrecognized grants must remain unchanged'

    $global:TestSitePermissions = @()
    $global:TestVerifiedGrant = @{ roles = @(); grantedToIdentitiesV2 = @(@{ application = @{ id = 'app-client' } }) }
    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'Site Read could not be verified*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'The script must verify the granted role rather than claiming success'

    $global:TestSitePermissions = @()
    $global:TestVerifiedGrant = @{ roles = @('read'); grantedToIdentitiesV2 = @(@{ application = @{ id = 'another-app' } }) }
    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'Site Read could not be verified*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Read granted to a different app must not pass verification'

    $global:TestAppWrites.Clear()
    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl 'https://example.com/sites/workshop' | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'SiteUrl must be*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Non-SharePoint site URLs must be rejected'
    Assert-Equal $global:TestAppWrites.Count 0 'Invalid URLs must not change the app'

    $global:TestVerifiedGrant = $null
    $global:TestSitePermissions = @()
    $global:TestSiteDenied = $true
    $global:TestAppWrites.Clear()
    $bootstrapOutput = & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl -BootstrapSiteAccess *>&1
    Assert-Equal $global:TestTokenAttempts 1 'Bootstrap must acquire an app-only token instead of using delegated site access'
    Assert-Equal $global:TestRoleAssignments.Count 0 'Temporary Graph FullControl consent must be revoked'
    Assert-Equal ($global:TestPasswords.keyId -join ',') 'existing-key' 'Only the temporary credential must be removed'
    Assert-Equal ($global:TestSitePermissions[0].roles -join ',') 'read' 'Bootstrap must leave only Read on the site'
    Assert-Equal (($bootstrapOutput | Out-String) -match 'test-secret-not-for-output|test-bootstrap-token') $false 'Credentials and tokens must not be printed'
    $passwordWrite = @($global:TestAppWrites | Where-Object { $_.Path -like '*/addPassword' })[0]
    $expiryMinutes = ([DateTimeOffset]$passwordWrite.Body.passwordCredential.endDateTime - [DateTimeOffset]::UtcNow).TotalMinutes
    Assert-Equal ($expiryMinutes -gt 14 -and $expiryMinutes -le 15) $true 'Bootstrap credentials must expire in 15 minutes'

    foreach ($failure in @('site-grant', 'token', 'role-response', 'credential-response', 'propagation-timeout')) {
        $global:TestBootstrapFailure = $failure
        $global:TestSitePermissions = @()
        $global:TestAppWrites.Clear()
        $failed = $false
        try { & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl -BootstrapSiteAccess | Out-Null }
        catch {
            if ($_.Exception.Message -like '*test-secret-not-for-output*') { throw 'Bootstrap errors must not expose credentials' }
            $failed = $true
        }
        Assert-Equal $failed $true "Bootstrap $failure must fail explicitly"
        Assert-Equal $global:TestRoleAssignments.Count 0 "Temporary role cleanup must run after $failure"
        Assert-Equal ($global:TestPasswords.keyId -join ',') 'existing-key' "Temporary credential cleanup must run after $failure"
    }

    $global:TestBootstrapFailure = 'propagation'
    $global:TestTokenAttempts = 0
    $global:TestSleepCalls = 0
    $global:TestSitePermissions = @()
    & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl -BootstrapSiteAccess | Out-Null
    Assert-Equal $global:TestTokenAttempts 2 'Bootstrap must retry temporary authentication propagation failures'
    Assert-Equal $global:TestSleepCalls 1 'Propagation retries must wait before requesting another token'
    Assert-Equal $global:TestRoleAssignments.Count 0 'Cleanup must also run after a successful retry'

    $global:TestBootstrapFailure = ''
    $global:TestRoleAssignments = @(@{ id = 'existing-fullcontrol'; resourceId = 'graph-api-principal'; appRoleId = 'Sites.FullControl.All' })
    $global:TestSitePermissions = @()
    & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl -BootstrapSiteAccess | Out-Null
    Assert-Equal ($global:TestRoleAssignments.id -join ',') 'existing-fullcontrol' 'Pre-existing Graph FullControl consent must not be revoked'
    Assert-Equal ($global:TestPasswords.keyId -join ',') 'existing-key' 'Pre-existing credentials must remain unchanged'

    $global:TestRoleAssignments = @(@{ id = 'existing-selected'; resourceId = 'graph-api-principal'; appRoleId = 'Sites.Selected' })
    & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl -BootstrapSiteAccess | Out-Null
    Assert-Equal ($global:TestRoleAssignments.id -join ',') 'existing-selected' 'Temporary consent cleanup must preserve other existing app-role assignments'

    $global:TestRoleAssignments = @()
    $global:TestAppWrites.Clear()
    $global:TestTokenAttempts = 0
    & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl -BootstrapSiteAccess -WhatIf | Out-Null
    Assert-Equal $global:TestAppWrites.Count 0 'Bootstrap WhatIf must not change any app or site permissions or credentials'
    Assert-Equal $global:TestTokenAttempts 0 'Bootstrap WhatIf must not request app-only tokens'

    $global:TestBootstrapFailure = 'role-cleanup'
    $global:TestSitePermissions = @()
    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl -BootstrapSiteAccess | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'BOOTSTRAP CLEANUP FAILED*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Cleanup failure must be explicit, not reported as success'
    Assert-Equal ($global:TestPasswords.keyId -join ',') 'existing-key' 'Credential cleanup must still run when role cleanup fails'

    $global:TestRoleAssignments = @()
    $global:TestBootstrapFailure = 'credential-cleanup'
    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -SiteUrl $siteUrl -BootstrapSiteAccess | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "BOOTSTRAP CLEANUP FAILED*Remove temporary credential*") { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Credential cleanup failure must include manual-action instructions'
    Assert-Equal $global:TestRoleAssignments.Count 0 'Role cleanup must finish even when credential cleanup fails'

    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -BootstrapSiteAccess | Out-Null }
    catch {
        if ($_.Exception.Message -notlike '*requires -SiteUrl*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Bootstrap must require an explicit site URL'

    $approvalScriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\approve-sharepoint-app.ps1'
    $approvalParams = @{
        TenantId = '11111111-1111-1111-1111-111111111111'
        AppId = '22222222-2222-2222-2222-222222222222'
        SiteUrl = $siteUrl
    }
    $global:TestContextTenant = $approvalParams.TenantId
    $global:TestBootstrapFailure = ''
    $global:TestPasswords = @(@{ keyId = 'existing-key'; displayName = 'existing-credential' })
    $global:TestRoleAssignments = @()
    $global:TestSitePermissions = @()
    $global:TestSiteNextPage = @()
    $runtimeRequests = @(
        @{ resourceAppId = '00000003-0000-0000-c000-000000000000'; resourceAccess = @(
            @{ id = 'Files.Read.All'; type = 'Role' }, @{ id = 'Sites.Selected'; type = 'Role' },
            @{ id = 'unrelated-delegated'; type = 'Scope' }, @{ id = 'unrelated-application'; type = 'Role' }
        ) },
        @{ resourceAppId = '00000003-0000-0ff1-ce00-000000000000'; resourceAccess = @(
            @{ id = 'Sites.Selected'; type = 'Role' }, @{ id = 'User.Read.All'; type = 'Role' }
        ) }
    )
    $approvalApp = @{
        id = 'app-object'; appId = $approvalParams.AppId; displayName = 'Existing Workshop App'
        requiredResourceAccess = $runtimeRequests
    }
    $global:TestAppMatches = @($approvalApp)
    $global:TestAppWrites.Clear()
    $global:TestTokenAttempts = 0
    & $approvalScriptPath @approvalParams -WhatIf | Out-Null
    Assert-Equal $global:TestAppWrites.Count 0 'Administrator entry point must honor WhatIf across the child script'
    Assert-Equal $global:TestTokenAttempts 0 'Administrator WhatIf must not acquire bootstrap tokens'

    $global:TestAzureCalls.Clear()
    $approvalOutput = & $approvalScriptPath @approvalParams *>&1
    Assert-Equal ($global:TestAppFilter -like "*appId eq '$($approvalParams.AppId)'*") $true 'Approval must select an existing app by client ID'
    Assert-Equal @($global:TestAzureCalls | Where-Object { $_ -like 'resource show*' -or $_ -like '*federatedIdentityCredentials*' }).Count 0 'Admin approval must not require Search reads or touch federation'
    Assert-Equal @($global:TestAppWrites | Where-Object { $_.Method -eq 'PATCH' -or $_.Path -eq '/v1.0/applications' }).Count 0 'Approval must not modify the manifest or create an app'
    Assert-Equal $global:TestRoleAssignments.Count 4 'All four runtime application consents must persist after cleanup'
    Assert-Equal @($global:TestRoleAssignments | Where-Object { $_.appRoleId -like '*FullControl*' -or $_.appRoleId -like 'unrelated*' }).Count 0 'Temporary FullControl must be removed; unrelated requested permissions must not be approved'
    Assert-Equal @($global:TestRoleAssignments | Where-Object { $_.resourceId -eq 'sharepoint-api-principal' }).Count 2 'SharePoint API consents must target its own service principal'
    Assert-Equal ($global:TestPasswords.keyId -join ',') 'existing-key' 'Admin approval must clean up temporary credentials'
    Assert-Equal ($global:TestSitePermissions[0].roles -join ',') 'read' 'Admin approval must grant only site Read'
    Assert-Equal (($approvalOutput | Out-String) -match 'test-secret-not-for-output|test-bootstrap-token') $false 'Approval output must not leak bootstrap secrets'

    $global:TestAppWrites.Clear()
    & $approvalScriptPath @approvalParams | Out-Null
    Assert-Equal @($global:TestAppWrites | Where-Object { $_.Path -like '*/appRoleAssignments' -and $_.Body.appRoleId -ne 'Sites.FullControl.All' }).Count 0 'Rerunning approval must not duplicate runtime consents'
    Assert-Equal @($global:TestAppWrites | Where-Object { $_.Path -eq '/v1.0/sites/site-id/permissions' }).Count 0 'Rerunning approval must preserve the existing site grant'

    $global:TestAppWrites.Clear()
    & $scriptPath -ApprovalOnly @approvalParams -GrantAdminConsent -BootstrapSiteAccess -WhatIf -EnvironmentFile 'Z:\nonexistent-workshop-settings.env' | Out-Null
    Assert-Equal $global:TestAppWrites.Count 0 'Approval mode must not need a deployment settings file'

    $global:TestAppMatches = @()
    $failed = $false
    try { & $approvalScriptPath @approvalParams | Out-Null }
    catch { $failed = $_.Exception.Message -like 'Approval requires exactly one existing app*' }
    Assert-Equal $failed $true 'Approval must reject a missing app rather than create it'
    Assert-Equal $global:TestAppWrites.Count 0 'A missing app must not cause approval writes'

    $global:TestAppMatches = @(@{ id = 'app-object'; appId = $approvalParams.AppId; displayName = 'Unconfigured App'; requiredResourceAccess = @() })
    $failed = $false
    try { & $approvalScriptPath @approvalParams | Out-Null }
    catch {
        if ($_.Exception.Message -notlike '*missing a required workshop application permission*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Approval must reject missing runtime permission requests'
    Assert-Equal $global:TestAppWrites.Count 0 'Missing requests must not cause permission or manifest writes'

    $global:TestAppMatches = @($approvalApp)
    $global:TestContextTenant = 'different-tenant'
    $failed = $false
    try { & $approvalScriptPath @approvalParams | Out-Null }
    catch { $failed = $_.Exception.Message -like '*tenant does not match*' }
    Assert-Equal $failed $true 'Approval must reject the wrong signed-in tenant'
    Assert-Equal $global:TestAppWrites.Count 0 'Wrong-tenant approval must not write'
    $global:TestContextTenant = $approvalParams.TenantId

    foreach ($consentFailure in @('denied', 'verification')) {
        $global:TestConsentFailure = $consentFailure
        $global:TestRoleAssignments = @()
        $global:TestAppWrites.Clear()
        $failed = $false
        try { & $approvalScriptPath @approvalParams | Out-Null } catch { $failed = $true }
        Assert-Equal $failed $true "Runtime consent $consentFailure must stop approval"
        Assert-Equal @($global:TestAppWrites | Where-Object { $_.Path -like '*/addPassword' }).Count 0 'Failed runtime approval must not start temporary site bootstrap'
    }
    $global:TestConsentFailure = ''
    $global:TestRoleAssignments = @()
    $global:TestSitePermissions = @()
    $global:TestBootstrapFailure = 'site-grant'
    $failed = $false
    try { & $approvalScriptPath @approvalParams | Out-Null } catch { $failed = $true }
    Assert-Equal $failed $true 'Admin approval must fail if the site grant fails'
    Assert-Equal $global:TestRoleAssignments.Count 4 'Runtime consent must remain while temporary FullControl is cleaned up after site failure'
    Assert-Equal ($global:TestPasswords.keyId -join ',') 'existing-key' 'Admin approval site failure must still remove its temporary credential'
    Write-Host 'Administrator approval checks passed: targeted application consent, no Search dependency, tenant checks, WhatIf, reruns and failure cleanup.'
    Write-Host 'SharePoint app checks passed: names, preserved permissions, Read grants, bootstrap retries/cleanup, pagination, authorization and WhatIf.'
}
finally {
    Remove-Item -LiteralPath $fixture.FullName
    Remove-Item Function:\az
    Remove-Item Function:\Invoke-RestMethod
    Remove-Item Function:\Start-Sleep
    Remove-Variable TestAppMatches, TestAppWrites, TestAppFilter -Scope Global
    Remove-Variable TestSitePermissions, TestSiteNextPage, TestSiteDenied, TestVerifiedGrant -Scope Global
    Remove-Variable TestRoleAssignments, TestPasswords, TestBootstrapFailure, TestTokenAttempts, TestSleepCalls -Scope Global
    Remove-Variable TestContextTenant, TestAzureCalls, TestConsentFailure -Scope Global
}

if ($env:OS -eq 'Windows_NT') {
    $launcherDirectory = Join-Path ([IO.Path]::GetTempPath()) "sharepoint-cli-test-$([Guid]::NewGuid().ToString('N'))"
    $launcher = Join-Path $launcherDirectory 'az.cmd'
    $originalPath = $env:PATH
    try {
        New-Item -ItemType Directory -Path $launcherDirectory | Out-Null
        "@echo off`r`necho %*`r`n" | Set-Content -LiteralPath $launcher -Encoding ascii
        $env:PATH = "$launcherDirectory;$originalPath"
        $tokens = $null
        $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
        $wrapper = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Az'
        }, $true)
        & {
            Invoke-Expression $wrapper.Extent.Text
            $url = 'https://graph.microsoft.com/v1.0/applications?$filter=displayName%20eq%20%27test%27&$select=id,appId'
            $result = Invoke-Az rest --method GET --url $url
            Assert-Equal $LASTEXITCODE 0 'The Windows launcher must not execute $select as a command'
            Assert-Equal $result "rest --method GET --url `"$url`" --only-show-errors" 'The Windows launcher must receive the complete URL'
        }
        Write-Host 'Windows az.cmd URL regression passed.'
    }
    finally {
        $env:PATH = $originalPath
        Remove-Item -LiteralPath $launcher -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $launcherDirectory -ErrorAction SilentlyContinue
    }
}
