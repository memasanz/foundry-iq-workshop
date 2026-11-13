<#
.SYNOPSIS
Facilitator pre-workshop setup: Entra app registration for the SharePoint indexer (notebook 07; notebook 08 needs no app).

.DESCRIPTION
Creates (or reuses) the app, adds the Graph and SharePoint application permissions, and adds a
federated credential that trusts the Search service's system-assigned managed identity. No runtime
client secret is created. Admin consent is opt-in (-GrantAdminConsent). When SiteUrl is supplied, grants
and verifies Read for the ingestion app on that site through Microsoft Graph.
Requires: az login as a user who can create app registrations (and, for consent, a privileged role).
Default site grants also require Graph Sites.FullControl.All delegated permission on the caller's token,
a SharePoint Administrator (or higher) role, and membership of the target site.
Alternatively, BootstrapSiteAccess uses a temporary app-only permission and credential.

.PARAMETER AppName
Exact display name of an existing app registration to configure, or a new app registration to
create in the deployment tenant. Defaults to iq-workshop-sharepoint-indexer. Existing API
permissions are preserved. Multiple apps with the same name cause an error.

.PARAMETER SiteUrl
SharePoint HTTPS site URL to authorize for the ingestion app. Grants site-scoped Read.
Existing grants that already allow reading are preserved. Omit to configure the app without
changing site access.

.PARAMETER BootstrapSiteAccess
Opt in to temporarily consenting Graph Sites.FullControl.All directly on the app's service
principal and adding a 15-minute credential to create the site Read grant. Requires SiteUrl,
Privileged Role Administrator or Global Administrator, and Graph authorization to manage app
role assignments and credentials. Cleanup is attempted on success and failure. Pre-existing
credentials and role assignments are preserved. Runtime permissions still need admin consent.

.PARAMETER ApprovalOnly
Administrator handoff mode, normally invoked by approve-sharepoint-app.ps1. Requires
TenantId, AppId, SiteUrl, GrantAdminConsent and BootstrapSiteAccess. Approves only the
four workshop application permissions already requested on an existing app and grants
site Read. Does not read deployment settings, configure Search, or change the app manifest.

.EXAMPLE
.\scripts\setup-sharepoint-app.ps1 -AppName 'Workshop SharePoint' -WhatIf

.EXAMPLE
.\scripts\setup-sharepoint-app.ps1 -AppName 'Workshop SharePoint' -SiteUrl 'https://contoso.sharepoint.com/sites/workshop'

.EXAMPLE
.\scripts\setup-sharepoint-app.ps1 -AppName 'Workshop SharePoint' -SiteUrl 'https://contoso.sharepoint.com/sites/workshop' -BootstrapSiteAccess -GrantAdminConsent
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateScript({ -not [string]::IsNullOrWhiteSpace($_) })]
    [string]$AppName = 'iq-workshop-sharepoint-indexer',
    [string]$SiteUrl,
    [switch]$GrantAdminConsent,
    [switch]$BootstrapSiteAccess,
    [switch]$ApprovalOnly,
    [string]$TenantId,
    [string]$AppId,
    [string]$EnvironmentFile = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env.azure')
)

$ErrorActionPreference = 'Stop'
$graph = 'https://graph.microsoft.com/v1.0'

function Invoke-Az {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    # az.cmd passes through cmd.exe, which treats an unquoted URL's & as a command separator.
    if ((Get-Command az).Source -like '*.cmd') {
        $urlIndex = [Array]::IndexOf($Arguments, '--url')
        if ($urlIndex -ge 0) {
            $Arguments[$urlIndex + 1] = '"' + $Arguments[$urlIndex + 1] + '"'
        }
    }
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: az $($Arguments -join ' ')" }
    return $result
}

# az rest needs the body in a file to avoid PowerShell quoting problems.
function Invoke-Graph {
    param([string]$Method, [string]$Url, $Body, [string]$AccessToken)
    if ($AccessToken) {
        $request = @{
            Method = $Method; Uri = $Url; TimeoutSec = 60
            Headers = @{ Authorization = "Bearer $AccessToken" }
        }
        if ($Body) {
            $request.ContentType = 'application/json'
            $request.Body = $Body | ConvertTo-Json -Depth 10
        }
        try { return Invoke-RestMethod @request }
        catch {
            $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
            $failure = [InvalidOperationException]::new("App-only Graph request failed: $Method $Url (HTTP $status).")
            $failure.Data['StatusCode'] = $status
            throw $failure
        }
    }
    $arguments = @('rest', '--method', $Method, '--url', $Url, '--resource', 'https://graph.microsoft.com')
    if ($Body) {
        $file = New-TemporaryFile
        try {
            $Body | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $file -Encoding utf8
            $arguments += @('--headers', 'Content-Type=application/json', '--body', "@$file")
            $result = Invoke-Az @arguments
        }
        finally { Remove-Item -LiteralPath $file -Force }
    }
    else { $result = Invoke-Az @arguments }
    if ($result) { return ($result | ConvertFrom-Json) }
}

function Get-GraphCollection {
    param([string]$Url, [string]$AccessToken)
    do {
        $page = Invoke-Graph GET $Url -AccessToken $AccessToken
        $page.value
        $Url = $page.'@odata.nextLink'
    } while ($Url)
}

if ($BootstrapSiteAccess -and -not $SiteUrl) {
    throw '-BootstrapSiteAccess requires -SiteUrl.'
}

if ($ApprovalOnly) {
    if (-not $TenantId -or -not $AppId -or -not $SiteUrl -or -not $GrantAdminConsent -or -not $BootstrapSiteAccess) {
        throw 'ApprovalOnly requires TenantId, AppId, SiteUrl, GrantAdminConsent and BootstrapSiteAccess.'
    }
    $context = Invoke-Az account show --output json | ConvertFrom-Json
}
else {
    if ($TenantId -or $AppId) { throw 'TenantId and AppId are only supported with ApprovalOnly.' }
    if (-not (Test-Path -LiteralPath $EnvironmentFile -PathType Leaf)) {
        throw "Deployment settings not found: $EnvironmentFile. Run scripts\deploy.ps1 first."
    }
    $settings = Get-Content -LiteralPath $EnvironmentFile -Raw | ConvertFrom-StringData
    $tenantId = $settings.AZURE_TENANT_ID
    $searchId = $settings.AZURE_SEARCH_RESOURCE_ID
    $context = Invoke-Az account show --subscription $settings.AZURE_SUBSCRIPTION_ID --output json | ConvertFrom-Json
}
if ($context.tenantId -ne $tenantId) { throw 'Azure login tenant does not match the requested tenant or deployment settings.' }

$sitePermissions = @()
if ($PSBoundParameters.ContainsKey('SiteUrl')) {
    $siteUri = $null
    if (-not [Uri]::TryCreate($SiteUrl, [UriKind]::Absolute, [ref]$siteUri) -or
        $siteUri.Scheme -ne 'https' -or -not $siteUri.Host.EndsWith('.sharepoint.com') -or
        $siteUri.UserInfo -or $siteUri.Port -ne 443 -or $siteUri.Query -or $siteUri.Fragment) {
        throw 'SiteUrl must be a public-cloud SharePoint HTTPS site URL, not a sharing link.'
    }
    $sitePath = $siteUri.AbsolutePath.TrimEnd('/')
    if (-not $sitePath) { $sitePath = '/' }
    $siteLookupUrl = "$graph/sites/$($siteUri.Host):$sitePath"
    if (-not $BootstrapSiteAccess) {
        try {
            $site = Invoke-Graph GET $siteLookupUrl
            if (-not $site.id) { throw 'Graph did not return a site ID.' }
            $sitePermissionsUrl = "$graph/sites/$($site.id)/permissions"
            $sitePermissions = @(Get-GraphCollection $sitePermissionsUrl)
        }
        catch {
            throw "Cannot inspect site access for $SiteUrl. The signed-in caller needs Graph Sites.FullControl.All delegated permission, SharePoint Administrator (or higher), and site membership. Alternatively, an authorized Entra administrator can opt in with -BootstrapSiteAccess. No app or site changes were made. Details: $($_.Exception.Message)"
        }
    }
}

if (-not $ApprovalOnly) {
    # The Search service identity is what the app will trust.
    $search = Invoke-Az resource show --ids $searchId --output json | ConvertFrom-Json
    if (-not $search.identity.principalId) { throw 'The Search service has no system-assigned managed identity.' }
    $miPrincipalId = $search.identity.principalId
    $miServicePrincipal = Invoke-Graph GET "$graph/servicePrincipals/$miPrincipalId`?`$select=id,appId"
    $miClientId = $miServicePrincipal.appId
}

# Resolve permission IDs by name so nothing is hard-coded.
$permissions = @(
    @{ Api = '00000003-0000-0000-c000-000000000000'; Names = @('Files.Read.All', 'Sites.Selected') },
    @{ Api = '00000003-0000-0ff1-ce00-000000000000'; Names = @('Sites.Selected', 'User.Read.All') }
)
$requiredAccess = @()
$apiPrincipals = @{}
foreach ($permission in $permissions) {
    $api = (Invoke-Graph GET "$graph/servicePrincipals?`$filter=appId eq '$($permission.Api)'&`$select=id,appRoles").value
    if ($api.Count -ne 1) { throw "Expected one API service principal for $($permission.Api)." }
    $apiPrincipals[$permission.Api] = $api[0]
    if ($permission.Api -eq '00000003-0000-0000-c000-000000000000') { $graphApi = $api[0] }
    $access = foreach ($name in $permission.Names) {
        $role = $api[0].appRoles | Where-Object { $_.value -eq $name -and $_.isEnabled -and 'Application' -in $_.allowedMemberTypes }
        if (-not $role) { throw "Application permission $name not found on $($permission.Api)." }
        @{ id = $role.id; type = 'Role' }
    }
    $requiredAccess += @{ resourceAppId = $permission.Api; resourceAccess = @($access) }
}

$appFilter = if ($ApprovalOnly) { [Uri]::EscapeDataString("appId eq '$($AppId.Replace("'", "''"))'") }
else { [Uri]::EscapeDataString("displayName eq '$($AppName.Replace("'", "''"))'") }
$select = if ($ApprovalOnly) { 'id,appId,displayName,requiredResourceAccess' } else { 'id,appId,requiredResourceAccess' }
$existing = (Invoke-Graph GET "$graph/applications?`$filter=$appFilter&`$select=$select").value
if ($ApprovalOnly) {
    if ($existing.Count -ne 1) { throw 'Approval requires exactly one existing app with the supplied client ID in the signed-in tenant.' }
    $app = $existing[0]
    $AppName = $app.displayName
    foreach ($required in $requiredAccess) {
        $requested = @($app.requiredResourceAccess |
            Where-Object { $_.resourceAppId -eq $required.resourceAppId } |
            ForEach-Object { $_.resourceAccess })
        foreach ($access in $required.resourceAccess) {
            if (-not ($requested | Where-Object { $_.id -eq $access.id -and $_.type -eq 'Role' })) {
                throw 'The existing app is missing a required workshop application permission. Run setup-sharepoint-app.ps1 without SiteUrl first; no approval changes were made.'
            }
        }
    }
    Write-Host "Approving existing app $AppName ($($app.appId)) in tenant $tenantId."
    Write-Warning 'Runtime consent includes Microsoft Graph Files.Read.All (tenant-wide file read), not only site-scoped access.'
}
else {
    if ($existing.Count -gt 1) { throw "More than one app named $AppName exists. Use a unique app display name." }
    if ($existing.Count -eq 1) {
        $app = $existing[0]
        Write-Host "Reusing app $AppName ($($app.appId))."
        $mergedAccess = @($app.requiredResourceAccess | Where-Object { $_ })
        foreach ($required in $requiredAccess) {
            $resource = $mergedAccess | Where-Object { $_.resourceAppId -eq $required.resourceAppId }
            if ($resource) {
                foreach ($access in $required.resourceAccess) {
                    if (-not ($resource.resourceAccess | Where-Object { $_.id -eq $access.id -and $_.type -eq $access.type })) {
                        $resource.resourceAccess = @($resource.resourceAccess) + $access
                    }
                }
            }
            else { $mergedAccess += $required }
        }
        if ($PSCmdlet.ShouldProcess($AppName, 'Update required permissions')) {
            Invoke-Graph PATCH "$graph/applications/$($app.id)" @{ requiredResourceAccess = $mergedAccess } | Out-Null
        }
    }
    elseif ($PSCmdlet.ShouldProcess($AppName, 'Create app registration')) {
        $app = Invoke-Graph POST "$graph/applications" @{
            displayName = $AppName; signInAudience = 'AzureADMyOrg'; requiredResourceAccess = $requiredAccess
        }
        Write-Host "Created app $AppName ($($app.appId))."
    }
    else { return }
}

$servicePrincipal = (Invoke-Graph GET "$graph/servicePrincipals?`$filter=appId eq '$($app.appId)'&`$select=id").value
if (-not $servicePrincipal) {
    if ($PSCmdlet.ShouldProcess($AppName, 'Create service principal')) {
        $servicePrincipal = Invoke-Graph POST "$graph/servicePrincipals" @{ appId = $app.appId }
    }
}

if (-not $ApprovalOnly) {
    $issuer = "https://login.microsoftonline.com/$tenantId/v2.0"
    $trusts = (Invoke-Graph GET "$graph/applications/$($app.id)/federatedIdentityCredentials").value
    if (-not ($trusts | Where-Object { $_.issuer -eq $issuer -and $_.subject -eq $miPrincipalId })) {
        if ($PSCmdlet.ShouldProcess($AppName, 'Add federated credential for the Search managed identity')) {
            Invoke-Graph POST "$graph/applications/$($app.id)/federatedIdentityCredentials" @{
                name = 'search-managed-identity'; issuer = $issuer; subject = $miPrincipalId
                audiences = @('api://AzureADTokenExchange')
            } | Out-Null
        }
    }
    else { Write-Host 'Federated credential already present.' }
}

if ($GrantAdminConsent) {
    if ($PSCmdlet.ShouldProcess($AppName, 'Grant tenant admin consent')) {
        if ($ApprovalOnly) {
            $runtimeAssignmentUrl = "$graph/servicePrincipals/$($servicePrincipal.id)/appRoleAssignments"
            $runtimeAssignments = @(Get-GraphCollection $runtimeAssignmentUrl)
            foreach ($required in $requiredAccess) {
                $resourceId = $apiPrincipals[$required.resourceAppId].id
                foreach ($access in $required.resourceAccess) {
                    if (-not ($runtimeAssignments | Where-Object { $_.resourceId -eq $resourceId -and $_.appRoleId -eq $access.id })) {
                        Invoke-Graph POST $runtimeAssignmentUrl @{
                            principalId = $servicePrincipal.id; resourceId = $resourceId; appRoleId = $access.id
                        } | Out-Null
                    }
                }
            }
            $runtimeAssignments = @(Get-GraphCollection $runtimeAssignmentUrl)
            foreach ($required in $requiredAccess) {
                $resourceId = $apiPrincipals[$required.resourceAppId].id
                foreach ($access in $required.resourceAccess) {
                    if (-not ($runtimeAssignments | Where-Object { $_.resourceId -eq $resourceId -and $_.appRoleId -eq $access.id })) {
                        throw 'Runtime application consent could not be verified. Some approvals may already have succeeded; review before retrying.'
                    }
                }
            }
            Write-Host 'The four workshop runtime application permissions are consented and verified. Other requested permissions were not consented.'
        }
        else {
            Invoke-Az ad app permission admin-consent --id $app.appId | Out-Null
            Write-Host 'Admin consent granted.'
        }
    }
}
else {
    Write-Host 'Admin consent NOT granted. Re-run with -GrantAdminConsent, or approve in Entra > App registrations > API permissions.'
}

if (-not $ApprovalOnly) {
    Write-Host "`nAdd these to .env for notebook 07:"
    Write-Host "SHAREPOINT_APP_CLIENT_ID=$($app.appId)"
    Write-Host "SHAREPOINT_APP_OBJECT_ID=$($app.id)"
    Write-Host "SHAREPOINT_SEARCH_MI_CLIENT_ID=$miClientId"
}

if ($SiteUrl) {
    $siteToken = $null
    $temporaryRolePending = $false
    $temporaryCredentialPending = $false
    $temporaryRoleId = $null
    $temporaryCredential = $null
    $tokenResponse = $null
    try {
        if ($BootstrapSiteAccess) {
            if (-not $PSCmdlet.ShouldProcess("$AppName on $SiteUrl", 'Temporarily consent Graph Sites.FullControl.All and add a credential to grant site-scoped Read')) {
                return
            }
            $fullControl = $graphApi.appRoles | Where-Object {
                $_.value -eq 'Sites.FullControl.All' -and $_.isEnabled -and 'Application' -in $_.allowedMemberTypes
            }
            if (-not $fullControl -or -not $graphApi.id -or -not $servicePrincipal.id) {
                throw 'Cannot resolve the Graph FullControl role or the ingestion service principal for bootstrap.'
            }
            $assignmentUrl = "$graph/servicePrincipals/$($servicePrincipal.id)/appRoleAssignments"
            $previousAssignments = @(Get-GraphCollection $assignmentUrl)
            $existingFullControl = @($previousAssignments | Where-Object {
                $_.resourceId -eq $graphApi.id -and $_.appRoleId -eq $fullControl.id
            })
            if (-not $existingFullControl.Count) {
                # Direct role consent avoids adding temporary permissions to the app manifest.
                $temporaryRolePending = $true
                $temporaryRole = Invoke-Graph POST $assignmentUrl @{
                    principalId = $servicePrincipal.id; resourceId = $graphApi.id; appRoleId = $fullControl.id
                }
                $temporaryRoleId = $temporaryRole.id
            }
            else { Write-Warning 'The app already has Graph Sites.FullControl.All; that pre-existing grant will not be removed.' }
            $credentialName = "workshop-site-bootstrap-$([Guid]::NewGuid().ToString('N'))"
            $temporaryCredentialPending = $true
            $temporaryCredential = Invoke-Graph POST "$graph/applications/$($app.id)/addPassword" @{
                passwordCredential = @{
                    displayName = $credentialName
                    endDateTime = [DateTime]::UtcNow.AddMinutes(15).ToString('o')
                }
            }
            if (-not $temporaryCredential.keyId -or -not $temporaryCredential.secretText) {
                throw 'The temporary credential response was incomplete; bootstrap cannot continue.'
            }
            for ($attempt = 0; $attempt -lt 12; $attempt++) {
                try {
                    $tokenResponse = Invoke-RestMethod -Method POST `
                        -Uri "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token" `
                        -Body @{
                            client_id = $app.appId; client_secret = $temporaryCredential.secretText
                            scope = 'https://graph.microsoft.com/.default'; grant_type = 'client_credentials'
                        } -TimeoutSec 60
                    $siteToken = $tokenResponse.access_token
                    if (-not $siteToken) { throw 'No bootstrap access token was returned.' }
                    $site = Invoke-Graph GET $siteLookupUrl -AccessToken $siteToken
                    if (-not $site.id) { throw 'Graph did not return a site ID.' }
                    $sitePermissionsUrl = "$graph/sites/$($site.id)/permissions"
                    $sitePermissions = @(Get-GraphCollection $sitePermissionsUrl -AccessToken $siteToken)
                    break
                }
                catch {
                    $status = $_.Exception.Data['StatusCode']
                    if (-not $status -and $_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
                    if ($status -notin @(400, 401, 403, 429)) { throw 'Bootstrap token or site lookup failed; temporary access cleanup will be attempted.' }
                    if ($attempt -eq 11) { throw 'Bootstrap authorization did not become ready. Check credential/permission propagation and tenant policies.' }
                    Start-Sleep -Seconds 10
                }
            }
        }
        $matchingGrants = @($sitePermissions | Where-Object {
            $identities = @($_.grantedToIdentitiesV2) + @($_.grantedToIdentities)
            @($identities | Where-Object { $_.application.id -eq $app.appId }).Count -gt 0
        })
        $readableGrants = @($matchingGrants | Where-Object {
            @($_.roles | Where-Object { $_ -in @('read', 'write', 'owner', 'fullcontrol') }).Count -gt 0
        })
        if ($readableGrants.Count) {
            Write-Host "Site read access already granted to $AppName on $SiteUrl."
            if ($readableGrants | Where-Object { @($_.roles | Where-Object { $_ -ne 'read' }).Count -gt 0 }) {
                Write-Warning 'An existing site grant is broader than Read and was left unchanged.'
            }
        }
        elseif ($matchingGrants.Count) {
            throw "The app already has a site grant on $SiteUrl, but its roles do not confirm read access. No duplicate grant was created; review the existing permission."
        }
        elseif ($PSCmdlet.ShouldProcess("$AppName on $SiteUrl", 'Grant site-scoped Read')) {
            $grant = Invoke-Graph POST $sitePermissionsUrl -AccessToken $siteToken -Body @{
                roles = @('read')
                grantedToIdentities = @(@{ application = @{ id = $app.appId; displayName = $AppName } })
            }
            if (-not $grant.id) { throw 'The site grant response did not contain a permission ID; site access is not verified.' }
            $grantId = [Uri]::EscapeDataString($grant.id)
            $verified = Invoke-Graph GET "$sitePermissionsUrl/$grantId" -AccessToken $siteToken
            $identities = @($verified.grantedToIdentitiesV2) + @($verified.grantedToIdentities)
            if ('read' -notin $verified.roles -or
                -not ($identities | Where-Object { $_.application.id -eq $app.appId })) {
                throw 'Site Read could not be verified for the ingestion app.'
            }
            Write-Host "Site Read granted and verified for $AppName on $SiteUrl."
        }
    }
    finally {
        $cleanupErrors = @()
        if ($temporaryRolePending) {
            try {
                $rolesToRemove = @(Get-GraphCollection $assignmentUrl | Where-Object {
                    $_.resourceId -eq $graphApi.id -and $_.appRoleId -eq $fullControl.id -and
                    $_.id -notin $previousAssignments.id -and
                    (-not $temporaryRoleId -or $_.id -eq $temporaryRoleId)
                })
                foreach ($role in $rolesToRemove) {
                    Invoke-Graph DELETE "$assignmentUrl/$($role.id)" | Out-Null
                }
                if (Get-GraphCollection $assignmentUrl | Where-Object {
                    $_.resourceId -eq $graphApi.id -and $_.appRoleId -eq $fullControl.id -and
                    $_.id -notin $previousAssignments.id -and
                    (-not $temporaryRoleId -or $_.id -eq $temporaryRoleId)
                }) { throw 'The temporary Graph role is still present.' }
            }
            catch { $cleanupErrors += "Remove temporary Graph Sites.FullControl.All from service principal $($servicePrincipal.id)." }
        }
        if ($temporaryCredentialPending) {
            try {
                $passwords = (Invoke-Graph GET "$graph/applications/$($app.id)?`$select=passwordCredentials").passwordCredentials
                foreach ($password in @($passwords | Where-Object { $_.displayName -eq $credentialName })) {
                    Invoke-Graph POST "$graph/applications/$($app.id)/removePassword" @{ keyId = $password.keyId } | Out-Null
                }
                $passwords = (Invoke-Graph GET "$graph/applications/$($app.id)?`$select=passwordCredentials").passwordCredentials
                if ($passwords | Where-Object { $_.displayName -eq $credentialName }) { throw 'The temporary credential is still present.' }
            }
            catch { $cleanupErrors += "Remove temporary credential '$credentialName' from app $($app.id)." }
        }
        $siteToken = $null
        $tokenResponse = $null
        $temporaryCredential = $null
        if ($cleanupErrors.Count) {
            throw "BOOTSTRAP CLEANUP FAILED. Manual action required: $($cleanupErrors -join ' ')"
        }
        if ($temporaryRolePending -or $temporaryCredentialPending) {
            Write-Host 'Temporary bootstrap permission and credential cleanup verified.'
        }
    }
}
else {
    Write-Warning 'Site access was not configured. Re-run with -SiteUrl to grant the ingestion app access to the workshop site.'
}
Write-Warning 'Files.Read.All is tenant-wide read; use a dedicated test tenant or site and non-sensitive content.'
