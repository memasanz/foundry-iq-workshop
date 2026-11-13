<#
.SYNOPSIS
Grants an existing app registration Read access to one SharePoint site.

.DESCRIPTION
Run after az login in the site's tenant. The caller needs delegated Microsoft Graph
Sites.FullControl.All, SharePoint Administrator (or higher), and site membership.
This script does not acquire or consent those permissions. It never gives FullControl
to the target app, creates credentials, or changes the app's API permissions.
The app's Sites.Selected API permission still requires separate tenant admin consent.
No workshop deployment settings or Search access are required.
This script uses the delegated Azure CLI session, not Connect-MgGraph or an app-only
token. The Graph API also supports application Sites.FullControl.All, but app-only
administration must use separate approved tooling or setup-sharepoint-app.ps1 bootstrap.

.PARAMETER AppName
Exact display name of an existing app registration in the signed-in tenant.

.PARAMETER AppId
Application (client) ID, not an application or service-principal object ID.

.PARAMETER SiteUrl
Public-cloud SharePoint HTTPS site URL, not a document sharing link.

.EXAMPLE
.\scripts\grant-sharepoint-site-read.ps1 -AppName 'Workshop Indexer' -SiteUrl 'https://contoso.sharepoint.com/sites/workshop' -WhatIf

.EXAMPLE
.\scripts\grant-sharepoint-site-read.ps1 -AppId '11111111-1111-1111-1111-111111111111' -SiteUrl 'https://contoso.sharepoint.com/sites/workshop'
#>
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Name')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Name')]
    [ValidateNotNullOrEmpty()]
    [string]$AppName,
    [Parameter(Mandatory, ParameterSetName = 'Id')]
    [Guid]$AppId,
    [Parameter(Mandatory)]
    [string]$SiteUrl
)

$ErrorActionPreference = 'Stop'
$graph = 'https://graph.microsoft.com/v1.0'

function Invoke-Az {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    if ((Get-Command az).Source -like '*.cmd') {
        $urlIndex = [Array]::IndexOf($Arguments, '--url')
        if ($urlIndex -ge 0) {
            $Arguments[$urlIndex + 1] = '"' + $Arguments[$urlIndex + 1] + '"'
        }
    }
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Azure CLI request failed. Review the error above.' }
    if ($result) { return ($result | ConvertFrom-Json) }
}

function Invoke-Graph {
    param([string]$Method, [string]$Url, $Body)
    $arguments = @('rest', '--method', $Method, '--url', $Url, '--resource', 'https://graph.microsoft.com')
    if ($Body) {
        $file = New-TemporaryFile
        try {
            $Body | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $file -Encoding utf8
            Invoke-Az @arguments --headers 'Content-Type=application/json' --body "@$file"
        }
        finally { Remove-Item -LiteralPath $file -Force }
    }
    else { Invoke-Az @arguments }
}

function Test-AppGrant {
    param($Permission, [string]$ClientId)
    $identities = @($Permission.grantedToIdentitiesV2) + @($Permission.grantedToIdentities)
    return @($identities | Where-Object { $_.application.id -eq $ClientId }).Count -gt 0
}

$siteUri = $null
if (-not [Uri]::TryCreate($SiteUrl, [UriKind]::Absolute, [ref]$siteUri) -or
    $siteUri.Scheme -ne 'https' -or -not $siteUri.Host.EndsWith('.sharepoint.com') -or
    $siteUri.UserInfo -or $siteUri.Port -ne 443 -or $siteUri.Query -or $siteUri.Fragment) {
    throw 'SiteUrl must be a public-cloud SharePoint HTTPS site URL, not a sharing link.'
}

$context = Invoke-Az account show --output json
Write-Host "Tenant: $($context.tenantId). Ensure this is the SharePoint site's tenant."
$filter = if ($PSCmdlet.ParameterSetName -eq 'Id') { "appId eq '$AppId'" }
else { "displayName eq '$($AppName.Replace("'", "''"))'" }
$apps = @((Invoke-Graph GET "$graph/applications?`$filter=$([Uri]::EscapeDataString($filter))&`$select=appId,displayName").value)
if ($apps.Count -ne 1) {
    throw "Expected exactly one existing app in the signed-in tenant; found $($apps.Count). Use -AppId to disambiguate duplicate names."
}
$app = $apps[0]
Write-Host "App: $($app.displayName) ($($app.appId))."
$sitePath = $siteUri.AbsolutePath.TrimEnd('/')
if (-not $sitePath) { $sitePath = '/' }
try {
    $site = Invoke-Graph GET "$graph/sites/$($siteUri.Host):$sitePath"
    if (-not $site.id) { throw 'Graph did not return a site ID.' }
    $permissionsUrl = "$graph/sites/$($site.id)/permissions"
    $permissions = @()
    $next = $permissionsUrl
    do {
        $page = Invoke-Graph GET $next
        $permissions += @($page.value)
        $next = $page.'@odata.nextLink'
    } while ($next)
}
catch {
    throw "Cannot inspect site permissions. The caller needs delegated Graph Sites.FullControl.All, SharePoint Administrator (or higher), and site membership in the correct tenant. No changes were made. Details: $($_.Exception.Message)"
}

$matching = @($permissions | Where-Object { Test-AppGrant $_ $app.appId })
if ($matching.Count) {
    $readable = @($matching | Where-Object {
        @($_.roles | Where-Object { $_ -in @('read', 'write', 'owner', 'fullcontrol') }).Count -gt 0
    })
    if (-not $readable.Count) {
        throw 'An existing app grant does not confirm read access. Review it manually; no duplicate grant was created.'
    }
    Write-Host 'Site read access already exists; no changes made.'
    if ($matching | Where-Object { @($_.roles | Where-Object { $_ -ne 'read' }).Count -gt 0 }) {
        Write-Warning 'An existing grant is broader than Read and was left unchanged.'
    }
    return
}

if ($PSCmdlet.ShouldProcess("$($app.displayName) ($($app.appId)) on $SiteUrl in tenant $($context.tenantId)", 'Grant site-scoped Read')) {
    $grant = Invoke-Graph POST $permissionsUrl @{
        roles = @('read')
        grantedToIdentities = @(@{ application = @{ id = $app.appId; displayName = $app.displayName } })
    }
    if (-not $grant.id) { throw 'The grant response has no permission ID. A grant may have been created, but is not verified.' }
    $verified = Invoke-Graph GET "$permissionsUrl/$([Uri]::EscapeDataString($grant.id))"
    if ('read' -notin $verified.roles -or -not (Test-AppGrant $verified $app.appId)) {
        throw 'The new site Read grant could not be verified. Inspect site permissions before retrying.'
    }
    Write-Host "Site Read granted and verified for $($app.displayName) on $SiteUrl."
}
