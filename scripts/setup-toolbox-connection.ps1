<#
.SYNOPSIS
Facilitator pre-workshop setup for notebook 08b: creates the shared Foundry project connection that lets toolboxes call Azure AI Search knowledge bases as the agent.

.DESCRIPTION
Creates one project connection (default 'iq-workshop-search-mcp') of kind remote-tool with the
'agentic-identity' auth type and audience https://search.azure.com. Toolboxes reference it by name
(project_connection_id), and Foundry then calls the knowledge base MCP endpoint with the calling
agent's own Entra identity. No secret is stored anywhere.

Each toolbox tool carries its own server_url (the attendee's knowledge base), so ONE shared connection
serves every attendee. Attendees cannot create project connections themselves; this is why it is a
facilitator step.

Requires the Azure Developer CLI (azd) with the Foundry agents extension:
  azd extension install azure.ai.agents
Re-running is safe (--force upserts the connection). Use -WhatIf to see the command without running it.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ConnectionName = 'iq-workshop-search-mcp',
    [string]$EnvironmentFile = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env.azure')
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command azd -ErrorAction SilentlyContinue)) {
    throw 'The Azure Developer CLI (azd) is required. Install it from https://aka.ms/azd and run: azd extension install azure.ai.agents'
}
if (-not (Test-Path -LiteralPath $EnvironmentFile -PathType Leaf)) {
    throw "Deployment settings not found: $EnvironmentFile. Run scripts\deploy.ps1 first."
}
$settings = Get-Content -LiteralPath $EnvironmentFile -Raw | ConvertFrom-StringData
foreach ($name in 'AZURE_AI_PROJECT_ENDPOINT', 'AZURE_SEARCH_ENDPOINT') {
    if (-not $settings[$name]) { throw "$name is missing from $EnvironmentFile. Re-run scripts\deploy.ps1." }
}
$projectEndpoint = $settings.AZURE_AI_PROJECT_ENDPOINT.TrimEnd('/')
$target = $settings.AZURE_SEARCH_ENDPOINT.TrimEnd('/')

function Invoke-Azd {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    & azd @Arguments
    if ($LASTEXITCODE -ne 0) { throw "azd failed: azd $($Arguments -join ' ')" }
}

if ($PSCmdlet.ShouldProcess($projectEndpoint, "Create connection '$ConnectionName' (agentic-identity, audience https://search.azure.com)")) {
    Invoke-Azd ai project set $projectEndpoint
    Invoke-Azd ai connection create $ConnectionName --kind remote-tool --force `
        --target $target --auth-type agentic-identity `
        --audience https://search.azure.com --metadata 'ApiType=Azure'
    Write-Host "Connection '$ConnectionName' is ready. Give attendees TOOLBOX_CONNECTION_NAME=$ConnectionName in .env."
}
