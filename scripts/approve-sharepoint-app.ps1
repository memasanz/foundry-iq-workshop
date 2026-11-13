<#
.SYNOPSIS
One-time administrator approval of an existing workshop SharePoint ingestion app.

.DESCRIPTION
Run az login --tenant <tenant-id> --allow-no-subscriptions first, as an authorized
Global Administrator or Privileged Role Administrator. The Azure CLI session needs
Graph authorization to read the app and manage application credentials and app-role
assignments. The admin role does not automatically supply missing API token permissions.

Consents only the four workshop runtime application permissions already requested on
the app: Graph Files.Read.All and Sites.Selected; SharePoint Sites.Selected and
User.Read.All. Does not consent unrelated or delegated permissions.

Temporarily consents application Graph Sites.FullControl.All and creates a 15-minute
credential to grant/verify site Read with an app-only token. Attempts and verifies
cleanup on success/failure; preserves existing permissions and credentials. Existing
broader site grants are not downgraded. Tokens can remain usable until expiry.
Runtime consent and site Read remain after temporary bootstrap cleanup.

No Azure resource permissions, .env.azure, Search lookup or federation changes are
required. The facilitator must run setup-sharepoint-app.ps1 without SiteUrl first.
Requires the sibling setup-sharepoint-app.ps1 file. WhatIf makes no changes.

.PARAMETER TenantId
Microsoft Entra tenant ID containing the existing app and SharePoint site.

.PARAMETER AppId
Existing application's client ID, not an object ID.

.PARAMETER SiteUrl
Public-cloud SharePoint HTTPS site URL to grant Read on.

.EXAMPLE
.\scripts\approve-sharepoint-app.ps1 -TenantId '<tenant-id>' -AppId '<app-client-id>' -SiteUrl 'https://contoso.sharepoint.com/sites/workshop' -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [Guid]$TenantId,
    [Parameter(Mandatory)]
    [Guid]$AppId,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SiteUrl
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'setup-sharepoint-app.ps1') `
    -ApprovalOnly -TenantId $TenantId -AppId $AppId -SiteUrl $SiteUrl `
    -GrantAdminConsent -BootstrapSiteAccess
