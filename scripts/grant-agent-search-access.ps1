<#
.SYNOPSIS
Gives hosted agent identities read access to the Search service so their toolbox can call a knowledge base (notebook 08b).

.DESCRIPTION
A hosted agent calls the knowledge base with its own Entra identity (agentic identity). That identity is created
when the agent version is deployed, so it cannot be granted in advance. Notebook 08b prints each attendee's
principal ID (and tries to assign the role itself). If the attendee lacks permission to assign roles, the
facilitator runs this script with the printed IDs:

  .\scripts\grant-agent-search-access.ps1 -PrincipalId <id1>,<id2>

The role is Search Index Data Reader (read-only queries) scoped to the workshop Search service only.
Re-running is safe; existing assignments are left alone.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string[]]$PrincipalId,
    [string]$EnvironmentFile = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env.azure')
)

$ErrorActionPreference = 'Stop'
$searchIndexDataReader = '1407120a-92aa-4202-b7e9-c0e197c71c8f'

if (-not (Test-Path -LiteralPath $EnvironmentFile -PathType Leaf)) {
    throw "Deployment settings not found: $EnvironmentFile. Run scripts\deploy.ps1 first."
}
$settings = Get-Content -LiteralPath $EnvironmentFile -Raw | ConvertFrom-StringData
if (-not $settings.AZURE_SEARCH_RESOURCE_ID) { throw "AZURE_SEARCH_RESOURCE_ID is missing from $EnvironmentFile." }
$scope = $settings.AZURE_SEARCH_RESOURCE_ID

foreach ($id in $PrincipalId) {
    $existing = & az role assignment list --assignee $id --role $searchIndexDataReader --scope $scope --query '[0].id' --output tsv --only-show-errors
    if ($existing) { Write-Host "$id already has Search Index Data Reader."; continue }
    if ($PSCmdlet.ShouldProcess($id, 'Assign Search Index Data Reader on the Search service')) {
        & az role assignment create --assignee-object-id $id --assignee-principal-type ServicePrincipal `
            --role $searchIndexDataReader --scope $scope --only-show-errors | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Role assignment failed for $id." }
        Write-Host "Granted Search Index Data Reader to $id."
    }
}
