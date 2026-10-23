<#
.SYNOPSIS
Facilitator pre-workshop setup for notebook 07: creates the APIM API and policy that sit in front of the Foundry IQ knowledge base.

.DESCRIPTION
Run scripts\setup-broker-app.ps1 first. This script then:
  1. stores the Web IQ API key as a secret APIM named value (it never reaches attendees or Search)
  2. creates the API 'iq-knowledge-base' (path /iq) in front of the Search service, with operations
       POST   /knowledgebases/{kbName}/retrieve
       POST/GET/DELETE /knowledgebases/{kbName}/mcp
  3. sets the API policy, which for every call:
       - validates the caller's Entra token (audience = broker app, scope user_impersonation)
       - exchanges it on-behalf-of for an Azure AI Search token for the SAME user
         and sends it as x-ms-query-source-authorization, so SharePoint ACLs and remote SharePoint
         run as the user
       - injects the Web IQ key as the per-source control headers for -WebIqSourceName
       - removes any caller-supplied x-ms-enable-elevated-read header (which would bypass ACLs)
       - replaces Authorization with the APIM managed identity's Search token
         (that identity holds Search Index Data Reader, assigned by Bicep)
Re-running is safe; everything is created or overwritten in place.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$WebIqSourceName = 'iq-workshop-webiq',
    [securestring]$WebIqApiKey,
    [string]$EnvironmentFile = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env.azure'),
    [string]$SecretsFile = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env')
)

$ErrorActionPreference = 'Stop'

function Invoke-Az {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: az $($Arguments -join ' ')" }
    return $result
}

function Invoke-Arm {
    param([string]$Path, $Body)
    $file = New-TemporaryFile
    try {
        $Body | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $file -Encoding utf8
        Invoke-Az rest --method PUT --url "$apimUrl/$Path" --resource 'https://management.azure.com' `
            --headers 'Content-Type=application/json' --body "@$file" | Out-Null
    }
    finally { Remove-Item -LiteralPath $file -Force }
}

if (-not (Test-Path -LiteralPath $EnvironmentFile -PathType Leaf)) {
    throw "Deployment settings not found: $EnvironmentFile. Run scripts\deploy.ps1 first."
}
$settings = Get-Content -LiteralPath $EnvironmentFile -Raw | ConvertFrom-StringData
foreach ($name in 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID', 'AZURE_RESOURCE_GROUP', 'APIM_NAME', 'AZURE_SEARCH_ENDPOINT') {
    if (-not $settings[$name]) { throw "$name is missing from $EnvironmentFile. Re-run scripts\deploy.ps1." }
}
$context = Invoke-Az account show --subscription $settings.AZURE_SUBSCRIPTION_ID --output json | ConvertFrom-Json
if ($context.tenantId -ne $settings.AZURE_TENANT_ID) { throw 'Azure login tenant does not match the deployment settings.' }
$apimUrl = "https://management.azure.com/subscriptions/$($settings.AZURE_SUBSCRIPTION_ID)/resourceGroups/$($settings.AZURE_RESOURCE_GROUP)" +
    "/providers/Microsoft.ApiManagement/service/$($settings.APIM_NAME)"
$version = 'api-version=2024-05-01'

# The Web IQ key: parameter, then WEBIQ_API_KEY in .env, then a hidden prompt.
if (-not $WebIqApiKey) {
    $line = if (Test-Path -LiteralPath $SecretsFile) { Select-String -LiteralPath $SecretsFile -Pattern '^\s*WEBIQ_API_KEY\s*=\s*(.+)$' | Select-Object -First 1 }
    $WebIqApiKey = if ($line) { ConvertTo-SecureString $line.Matches[0].Groups[1].Value.Trim() -AsPlainText -Force }
    else { Read-Host 'Web IQ API key' -AsSecureString }
}
$webIqKey = [System.Net.NetworkCredential]::new('', $WebIqApiKey).Password
if (-not $webIqKey) { throw 'A Web IQ API key is required.' }

if ($PSCmdlet.ShouldProcess($settings.APIM_NAME, 'Store the Web IQ key as secret named value webiq-api-key')) {
    Invoke-Arm "namedValues/webiq-api-key?$version" @{
        properties = @{ displayName = 'webiq-api-key'; value = $webIqKey; secret = $true }
    }
}
Remove-Variable webIqKey

if ($PSCmdlet.ShouldProcess($settings.APIM_NAME, 'Create API iq-knowledge-base')) {
    Invoke-Arm "apis/iq-knowledge-base?$version" @{
        properties = @{
            displayName = 'IQ workshop knowledge base broker'
            description = 'Validates the caller, exchanges the token on-behalf-of for Azure AI Search, injects the Web IQ key, and calls the knowledge base as the APIM managed identity.'
            path = 'iq'; protocols = @('https'); serviceUrl = $settings.AZURE_SEARCH_ENDPOINT; subscriptionRequired = $false
        }
    }
    $operations = @(
        @{ Name = 'retrieve'; Method = 'POST'; Template = '/knowledgebases/{kbName}/retrieve' },
        @{ Name = 'mcp-post'; Method = 'POST'; Template = '/knowledgebases/{kbName}/mcp' },
        @{ Name = 'mcp-get'; Method = 'GET'; Template = '/knowledgebases/{kbName}/mcp' },
        @{ Name = 'mcp-delete'; Method = 'DELETE'; Template = '/knowledgebases/{kbName}/mcp' }
    )
    foreach ($operation in $operations) {
        Invoke-Arm "apis/iq-knowledge-base/operations/$($operation.Name)?$version" @{
            properties = @{
                displayName = $operation.Name; method = $operation.Method; urlTemplate = $operation.Template
                templateParameters = @(@{ name = 'kbName'; type = 'string'; required = $true })
            }
        }
    }
}

# Single-quoted here-string: nothing is interpolated; __WEBIQ__ is replaced below.
$policy = @'
<policies>
  <inbound>
    <base />
    <validate-jwt header-name="Authorization" failed-validation-httpcode="401" failed-validation-error-message="Unauthorized. Request a token for the workshop broker app.">
      <openid-config url="https://login.microsoftonline.com/{{tenant-id}}/v2.0/.well-known/openid-configuration" />
      <audiences>
        <audience>api://{{broker-client-id}}</audience>
        <audience>{{broker-client-id}}</audience>
      </audiences>
      <issuers>
        <issuer>https://login.microsoftonline.com/{{tenant-id}}/v2.0</issuer>
      </issuers>
      <required-claims>
        <claim name="scp" match="any"><value>user_impersonation</value></claim>
      </required-claims>
    </validate-jwt>
    <set-variable name="assertion" value="@(context.Request.Headers.GetValueOrDefault(&quot;Authorization&quot;, &quot;&quot;).Replace(&quot;Bearer &quot;, &quot;&quot;))" />
    <send-request mode="new" response-variable-name="oboResponse" timeout="20" ignore-error="false">
      <set-url>https://login.microsoftonline.com/{{tenant-id}}/oauth2/v2.0/token</set-url>
      <set-method>POST</set-method>
      <set-header name="Content-Type" exists-action="override"><value>application/x-www-form-urlencoded</value></set-header>
      <set-body>@("grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer&amp;requested_token_use=on_behalf_of&amp;client_id={{broker-client-id}}&amp;client_secret=" + System.Uri.EscapeDataString("{{broker-client-secret}}") + "&amp;assertion=" + (string)context.Variables["assertion"] + "&amp;scope=" + System.Uri.EscapeDataString("https://search.azure.com/.default"))</set-body>
    </send-request>
    <choose>
      <when condition="@(((IResponse)context.Variables[&quot;oboResponse&quot;]).StatusCode != 200)">
        <return-response>
          <set-status code="401" reason="On-behalf-of exchange failed" />
          <set-header name="Content-Type" exists-action="override"><value>application/json</value></set-header>
          <set-body>@(((IResponse)context.Variables["oboResponse"]).Body.As&lt;string&gt;(preserveContent: true))</set-body>
        </return-response>
      </when>
    </choose>
    <set-variable name="searchUserToken" value="@(((IResponse)context.Variables[&quot;oboResponse&quot;]).Body.As&lt;JObject&gt;(preserveContent: true)[&quot;access_token&quot;].ToString())" />
    <set-header name="x-ms-enable-elevated-read" exists-action="delete" />
    <set-header name="x-ms-query-source-authorization" exists-action="override"><value>@((string)context.Variables["searchUserToken"])</value></set-header>
    <set-header name="__WEBIQ__-header-name" exists-action="override"><value>x-apikey</value></set-header>
    <set-header name="__WEBIQ__-header-value" exists-action="override"><value>{{webiq-api-key}}</value></set-header>
    <authentication-managed-identity resource="https://search.azure.com" output-token-variable-name="gatewayToken" ignore-error="false" />
    <set-header name="Authorization" exists-action="override"><value>@("Bearer " + (string)context.Variables["gatewayToken"])</value></set-header>
  </inbound>
  <backend><base /></backend>
  <outbound><base /></outbound>
  <on-error><base /></on-error>
</policies>
'@.Replace('__WEBIQ__', $WebIqSourceName)

if ($PSCmdlet.ShouldProcess($settings.APIM_NAME, 'Set the iq-knowledge-base policy')) {
    Invoke-Arm "apis/iq-knowledge-base/policies/policy?$version" @{ properties = @{ format = 'rawxml'; value = $policy } }
    Write-Host 'Policy set.'
}

$gateway = "https://$($settings.APIM_NAME).azure-api.net"
Write-Host "`nAdd this to .env for notebook 07:"
Write-Host "APIM_GATEWAY_URL=$gateway"
Write-Host "WEBIQ_APIM_SOURCE_NAME=$WebIqSourceName"
Write-Host "`nRetrieve URL: $gateway/iq/knowledgebases/<knowledge-base>/retrieve?api-version=2026-08-01-preview"
