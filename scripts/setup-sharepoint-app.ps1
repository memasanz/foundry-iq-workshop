<#
.SYNOPSIS
Facilitator pre-workshop setup: Entra app registration for the SharePoint indexer (notebook 04; notebook 05 needs no app).

.DESCRIPTION
Creates (or reuses) the app, adds the Graph and SharePoint application permissions, and adds a
federated credential that trusts the Search service's system-assigned managed identity. No client
secret is created. Admin consent is opt-in (-GrantAdminConsent). The per-site grant is printed
because it needs SharePoint admin rights that Azure CLI tokens usually lack.
Requires: az login as a user who can create app registrations (and, for consent, a privileged role).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$AppName = 'iq-workshop-sharepoint-indexer',
    [string]$SiteUrl,
    [switch]$GrantAdminConsent,
    [string]$EnvironmentFile = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env.azure')
)

$ErrorActionPreference = 'Stop'
$graph = 'https://graph.microsoft.com/v1.0'

function Invoke-Az {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
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

$existing = (Invoke-Graph GET "$graph/applications?`$filter=displayName eq '$AppName'&`$select=id,appId").value
if ($existing.Count -gt 1) { throw "More than one app named $AppName exists. Remove duplicates first." }
if ($existing.Count -eq 1) {
    $app = $existing[0]
    Write-Host "Reusing app $AppName ($($app.appId))."
    if ($PSCmdlet.ShouldProcess($AppName, 'Update required permissions')) {
        Invoke-Graph PATCH "$graph/applications/$($app.id)" @{ requiredResourceAccess = $requiredAccess } | Out-Null
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

Write-Host "`nAdd these to .env for notebook 04:"
Write-Host "SHAREPOINT_APP_CLIENT_ID=$($app.appId)"
Write-Host "SHAREPOINT_APP_OBJECT_ID=$($app.id)"
Write-Host "SHAREPOINT_SEARCH_MI_CLIENT_ID=$miClientId"

Write-Host "`nStill required: grant the app access to the workshop site (SharePoint admin, one time):"
$site = if ($SiteUrl) { $SiteUrl } else { '<https://tenant.sharepoint.com/sites/workshop>' }
Write-Host "Grant-PnPEntraIDAppSitePermission -AppId $($app.appId) -DisplayName '$AppName' -Site '$site' -Permissions FullControl"
Write-Warning 'Files.Read.All is tenant-wide read; use a dedicated test tenant or site and non-sensitive content.'
