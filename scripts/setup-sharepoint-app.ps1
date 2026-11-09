<#
.SYNOPSIS
Facilitator pre-workshop setup: Entra app registration for the SharePoint indexer (notebook 07; notebook 08 needs no app).

.DESCRIPTION
Creates (or reuses) the app, adds the Graph and SharePoint application permissions, and adds a
federated credential that trusts the Search service's system-assigned managed identity. No client
secret is created. Admin consent is opt-in (-GrantAdminConsent). When SiteUrl is supplied, grants
and verifies FullControl for the ingestion app on that site through Microsoft Graph.
Requires: az login as a user who can create app registrations (and, for consent, a privileged role).
Site grants also require Graph Sites.FullControl.All delegated permission on the caller's token,
a SharePoint Administrator (or higher) role, and membership of the target site.

.PARAMETER AppName
Exact display name of an existing app registration to configure, or a new app registration to
create in the deployment tenant. Defaults to iq-workshop-sharepoint-indexer. Existing API
permissions are preserved. Multiple apps with the same name cause an error.

.PARAMETER SiteUrl
SharePoint HTTPS site URL to authorize for the ingestion app. Grants site-scoped FullControl
for notebook 07's ACL ingestion. Omit to configure the app without changing site access.

.EXAMPLE
.\scripts\setup-sharepoint-app.ps1 -AppName 'Workshop SharePoint' -WhatIf

.EXAMPLE
.\scripts\setup-sharepoint-app.ps1 -AppName 'Workshop SharePoint' -SiteUrl 'https://contoso.sharepoint.com/sites/workshop'
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateScript({ -not [string]::IsNullOrWhiteSpace($_) })]
    [string]$AppName = 'iq-workshop-sharepoint-indexer',
    [string]$SiteUrl,
    [switch]$GrantAdminConsent,
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
    param([string]$Method, [string]$Url, $Body)
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

if (-not (Test-Path -LiteralPath $EnvironmentFile -PathType Leaf)) {
    throw "Deployment settings not found: $EnvironmentFile. Run scripts\deploy.ps1 first."
}
$settings = Get-Content -LiteralPath $EnvironmentFile -Raw | ConvertFrom-StringData
$tenantId = $settings.AZURE_TENANT_ID
$searchId = $settings.AZURE_SEARCH_RESOURCE_ID
$context = Invoke-Az account show --subscription $settings.AZURE_SUBSCRIPTION_ID --output json | ConvertFrom-Json
if ($context.tenantId -ne $tenantId) { throw 'Azure login tenant does not match the deployment settings.' }

$sitePermissions = @()
if ($PSBoundParameters.ContainsKey('SiteUrl')) {
    $siteUri = $null
    if (-not [Uri]::TryCreate($SiteUrl, [UriKind]::Absolute, [ref]$siteUri) -or
        $siteUri.Scheme -ne 'https' -or -not $siteUri.Host.EndsWith('.sharepoint.com') -or
        $siteUri.UserInfo -or $siteUri.Port -ne 443 -or $siteUri.Query -or $siteUri.Fragment) {
        throw 'SiteUrl must be a public-cloud SharePoint HTTPS site URL, not a sharing link.'
    }
    try {
        $sitePath = $siteUri.AbsolutePath.TrimEnd('/')
        if (-not $sitePath) { $sitePath = '/' }
        $site = Invoke-Graph GET "$graph/sites/$($siteUri.Host):$sitePath"
        if (-not $site.id) { throw 'Graph did not return a site ID.' }
        $sitePermissionsUrl = "$graph/sites/$($site.id)/permissions"
        $nextPage = $sitePermissionsUrl
        do {
            $page = Invoke-Graph GET $nextPage
            $sitePermissions += @($page.value)
            $nextPage = $page.'@odata.nextLink'
        } while ($nextPage)
    }
    catch {
        throw "Cannot inspect site access for $SiteUrl. The signed-in caller needs Graph Sites.FullControl.All delegated permission, SharePoint Administrator (or higher), and site membership. Entra app ownership and Azure subscription Owner are not sufficient. No app or site changes were made. Details: $($_.Exception.Message)"
    }
}

# The Search service identity is what the app will trust.
$search = Invoke-Az resource show --ids $searchId --output json | ConvertFrom-Json
if (-not $search.identity.principalId) { throw 'The Search service has no system-assigned managed identity.' }
$miPrincipalId = $search.identity.principalId
$miServicePrincipal = Invoke-Graph GET "$graph/servicePrincipals/$miPrincipalId`?`$select=id,appId"
$miClientId = $miServicePrincipal.appId

# Resolve permission IDs by name so nothing is hard-coded.
$permissions = @(
    @{ Api = '00000003-0000-0000-c000-000000000000'; Names = @('Files.Read.All', 'Sites.Selected') },
    @{ Api = '00000003-0000-0ff1-ce00-000000000000'; Names = @('Sites.Selected', 'User.Read.All') }
)
$requiredAccess = @()
foreach ($permission in $permissions) {
    $api = (Invoke-Graph GET "$graph/servicePrincipals?`$filter=appId eq '$($permission.Api)'&`$select=appRoles").value
    if ($api.Count -ne 1) { throw "Expected one API service principal for $($permission.Api)." }
    $access = foreach ($name in $permission.Names) {
        $role = $api[0].appRoles | Where-Object { $_.value -eq $name -and $_.isEnabled -and 'Application' -in $_.allowedMemberTypes }
        if (-not $role) { throw "Application permission $name not found on $($permission.Api)." }
        @{ id = $role.id; type = 'Role' }
    }
    $requiredAccess += @{ resourceAppId = $permission.Api; resourceAccess = @($access) }
}

$appFilter = [Uri]::EscapeDataString("displayName eq '$($AppName.Replace("'", "''"))'")
$existing = (Invoke-Graph GET "$graph/applications?`$filter=$appFilter&`$select=id,appId,requiredResourceAccess").value
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

$servicePrincipal = (Invoke-Graph GET "$graph/servicePrincipals?`$filter=appId eq '$($app.appId)'&`$select=id").value
if (-not $servicePrincipal) {
    if ($PSCmdlet.ShouldProcess($AppName, 'Create service principal')) {
        Invoke-Graph POST "$graph/servicePrincipals" @{ appId = $app.appId } | Out-Null
    }
}

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

if ($GrantAdminConsent) {
    if ($PSCmdlet.ShouldProcess($AppName, 'Grant tenant admin consent')) {
        Invoke-Az ad app permission admin-consent --id $app.appId | Out-Null
        Write-Host 'Admin consent granted.'
    }
}
else {
    Write-Host 'Admin consent NOT granted. Re-run with -GrantAdminConsent, or approve in Entra > App registrations > API permissions.'
}

Write-Host "`nAdd these to .env for notebook 07:"
Write-Host "SHAREPOINT_APP_CLIENT_ID=$($app.appId)"
Write-Host "SHAREPOINT_APP_OBJECT_ID=$($app.id)"
Write-Host "SHAREPOINT_SEARCH_MI_CLIENT_ID=$miClientId"

if ($SiteUrl) {
    $matchingGrants = @($sitePermissions | Where-Object {
        $identities = @($_.grantedToIdentitiesV2) + @($_.grantedToIdentities)
        @($identities | Where-Object { $_.application.id -eq $app.appId }).Count -gt 0
    })
    if ($matchingGrants | Where-Object { 'fullcontrol' -in $_.roles }) {
        Write-Host "Site FullControl already granted to $AppName on $SiteUrl."
    }
    elseif ($matchingGrants.Count) {
        throw "The app already has a site grant without FullControl on $SiteUrl. No duplicate grant was created. Have an authorized administrator upgrade that permission using an approved workflow; Graph permission updates require application authentication."
    }
    elseif ($PSCmdlet.ShouldProcess("$AppName on $SiteUrl", 'Grant site-scoped FullControl for ACL ingestion')) {
        $grant = Invoke-Graph POST $sitePermissionsUrl @{
            roles = @('fullcontrol')
            grantedToIdentities = @(@{ application = @{ id = $app.appId; displayName = $AppName } })
        }
        if (-not $grant.id) { throw 'The site grant response did not contain a permission ID; site access is not verified.' }
        $grantId = [Uri]::EscapeDataString($grant.id)
        $verified = Invoke-Graph GET "$sitePermissionsUrl/$grantId"
        $identities = @($verified.grantedToIdentitiesV2) + @($verified.grantedToIdentities)
        if ('fullcontrol' -notin $verified.roles -or
            -not ($identities | Where-Object { $_.application.id -eq $app.appId })) {
            throw 'Site FullControl could not be verified for the ingestion app.'
        }
        Write-Host "Site FullControl granted and verified for $AppName on $SiteUrl."
    }
}
else {
    Write-Warning 'Site access was not configured. Re-run with -SiteUrl to grant the ingestion app access to the workshop site.'
}
Write-Warning 'Files.Read.All is tenant-wide read; use a dedicated test tenant or site and non-sensitive content.'
