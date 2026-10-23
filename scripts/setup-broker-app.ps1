<#
.SYNOPSIS
Facilitator pre-workshop setup for notebook 07: the Entra app registration that APIM uses to call Azure AI Search for each user.

.DESCRIPTION
Creates (or reuses) a middle-tier "broker" app:
  - exposes the delegated scope api://<appId>/user_impersonation, which callers request a token for
  - pre-authorizes Azure CLI, so notebooks can get that token with AzureCliCredential and no consent prompt
  - requests the delegated Azure Cognitive Search user_impersonation permission, which APIM uses to exchange
    the caller's token (on-behalf-of) for a Search token that carries the same user
  - creates a client secret and stores it ONLY in the APIM named value 'broker-client-secret'
    (never printed, never written to disk)
Admin consent for the Search permission is opt-in (-GrantAdminConsent).
Run scripts\deploy.ps1 first (it creates APIM). Then run scripts\setup-apim-policy.ps1.
Requires: az login as a user who can create app registrations.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$AppName = 'iq-workshop-apim-broker',
    [switch]$GrantAdminConsent,
    [switch]$RotateSecret,
    [string]$EnvironmentFile = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env.azure')
)

$ErrorActionPreference = 'Stop'
$graph = 'https://graph.microsoft.com/v1.0'
$azureCliAppId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'

function Invoke-Az {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: az $($Arguments -join ' ')" }
    return $result
}

# az rest needs the body in a file to avoid PowerShell quoting problems.
function Invoke-Rest {
    param([string]$Method, [string]$Url, $Body, [string]$Resource)
    $arguments = @('rest', '--method', $Method, '--url', $Url, '--resource', $Resource)
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
function Invoke-Graph { param([string]$Method, [string]$Url, $Body) Invoke-Rest $Method $Url $Body 'https://graph.microsoft.com' }

if (-not (Test-Path -LiteralPath $EnvironmentFile -PathType Leaf)) {
    throw "Deployment settings not found: $EnvironmentFile. Run scripts\deploy.ps1 first."
}
$settings = Get-Content -LiteralPath $EnvironmentFile -Raw | ConvertFrom-StringData
foreach ($name in 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID', 'AZURE_RESOURCE_GROUP', 'APIM_NAME') {
    if (-not $settings[$name]) { throw "$name is missing from $EnvironmentFile. Re-run scripts\deploy.ps1 to create APIM." }
}
$context = Invoke-Az account show --subscription $settings.AZURE_SUBSCRIPTION_ID --output json | ConvertFrom-Json
if ($context.tenantId -ne $settings.AZURE_TENANT_ID) { throw 'Azure login tenant does not match the deployment settings.' }

# Delegated permission to call Azure AI Search as the signed-in user, looked up by name.
$searchApi = (Invoke-Graph GET "$graph/servicePrincipals?`$filter=servicePrincipalNames/any(n:n eq 'https://search.azure.com')&`$select=appId,oauth2PermissionScopes").value
if ($searchApi.Count -ne 1) { throw 'The Azure Cognitive Search service principal was not found in this tenant.' }
$searchScope = $searchApi[0].oauth2PermissionScopes | Where-Object { $_.value -eq 'user_impersonation' -and $_.isEnabled }
if (-not $searchScope) { throw 'Delegated permission user_impersonation not found on Azure Cognitive Search.' }
$requiredAccess = @(@{ resourceAppId = $searchApi[0].appId; resourceAccess = @(@{ id = $searchScope.id; type = 'Scope' }) })

$existing = (Invoke-Graph GET "$graph/applications?`$filter=displayName eq '$AppName'&`$select=id,appId,api").value
if ($existing.Count -gt 1) { throw "More than one app named $AppName exists. Remove duplicates first." }
if ($existing.Count -eq 1) {
    $app = $existing[0]
    Write-Host "Reusing app $AppName ($($app.appId))."
}
elseif ($PSCmdlet.ShouldProcess($AppName, 'Create app registration')) {
    $app = Invoke-Graph POST "$graph/applications" @{
        displayName = $AppName; signInAudience = 'AzureADMyOrg'; requiredResourceAccess = $requiredAccess
    }
    Write-Host "Created app $AppName ($($app.appId))."
}
else { return }

# Expose user_impersonation, keeping its ID stable on re-runs, and pre-authorize Azure CLI.
$scope = $app.api.oauth2PermissionScopes | Where-Object { $_.value -eq 'user_impersonation' }
$scopeId = if ($scope) { $scope.id } else { [guid]::NewGuid().ToString() }
if ($PSCmdlet.ShouldProcess($AppName, 'Set identifier URI, scope, pre-authorization and permissions')) {
    Invoke-Graph PATCH "$graph/applications/$($app.id)" @{
        identifierUris = @("api://$($app.appId)")
        requiredResourceAccess = $requiredAccess
        api = @{
            requestedAccessTokenVersion = 2
            oauth2PermissionScopes = @(@{
                id = $scopeId; value = 'user_impersonation'; type = 'User'; isEnabled = $true
                adminConsentDisplayName = 'Use the IQ workshop knowledge base as the signed-in user'
                adminConsentDescription = 'Lets the app call the IQ workshop APIM gateway on behalf of the signed-in user.'
                userConsentDisplayName = 'Use the IQ workshop knowledge base as you'
                userConsentDescription = 'Lets the app call the IQ workshop APIM gateway on your behalf.'
            })
            preAuthorizedApplications = @(@{ appId = $azureCliAppId; delegatedPermissionIds = @($scopeId) })
        }
    } | Out-Null
}

$servicePrincipal = (Invoke-Graph GET "$graph/servicePrincipals?`$filter=appId eq '$($app.appId)'&`$select=id").value
if (-not $servicePrincipal -and $PSCmdlet.ShouldProcess($AppName, 'Create service principal')) {
    Invoke-Graph POST "$graph/servicePrincipals" @{ appId = $app.appId } | Out-Null
}

if ($GrantAdminConsent) {
    if ($PSCmdlet.ShouldProcess($AppName, 'Grant tenant admin consent')) {
        Invoke-Az ad app permission admin-consent --id $app.appId | Out-Null
        Write-Host 'Admin consent granted.'
    }
}
else {
    Write-Host 'Admin consent NOT granted. Re-run with -GrantAdminConsent, or approve in Entra > App registrations > API permissions.'
}

# The OBO exchange needs a client credential. Put it straight into APIM as a secret named value.
$apimUrl = "https://management.azure.com/subscriptions/$($settings.AZURE_SUBSCRIPTION_ID)/resourceGroups/$($settings.AZURE_RESOURCE_GROUP)" +
    "/providers/Microsoft.ApiManagement/service/$($settings.APIM_NAME)/namedValues"
$secretExists = $true
try { Invoke-Rest GET "$apimUrl/broker-client-secret?api-version=2024-05-01" $null 'https://management.azure.com' | Out-Null }
catch { $secretExists = $false }

if ($RotateSecret -or -not $secretExists) {
    if ($PSCmdlet.ShouldProcess($settings.APIM_NAME, 'Create a client secret and store it as APIM named value broker-client-secret')) {
        $secret = Invoke-Az ad app credential reset --id $app.appId --append --display-name 'apim-broker' --years 1 --query password --output tsv
        Invoke-Rest PUT "$apimUrl/broker-client-secret?api-version=2024-05-01" @{
            properties = @{ displayName = 'broker-client-secret'; value = $secret.Trim(); secret = $true }
        } 'https://management.azure.com' | Out-Null
        Remove-Variable secret
        Write-Host 'Client secret stored in APIM (valid 1 year). It was not displayed or saved locally.'
    }
}
else { Write-Host 'APIM already has broker-client-secret. Use -RotateSecret to replace it.' }

if ($PSCmdlet.ShouldProcess($settings.APIM_NAME, 'Store broker-client-id and tenant-id named values')) {
    Invoke-Rest PUT "$apimUrl/broker-client-id?api-version=2024-05-01" @{
        properties = @{ displayName = 'broker-client-id'; value = $app.appId }
    } 'https://management.azure.com' | Out-Null
    Invoke-Rest PUT "$apimUrl/tenant-id?api-version=2024-05-01" @{
        properties = @{ displayName = 'tenant-id'; value = $settings.AZURE_TENANT_ID }
    } 'https://management.azure.com' | Out-Null
}

Write-Host "`nAdd this to .env for notebook 07:"
Write-Host "BROKER_APP_CLIENT_ID=$($app.appId)"
Write-Host 'Next: run scripts\setup-apim-policy.ps1'
