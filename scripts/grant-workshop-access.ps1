[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]]$Users,
    [string]$EnvironmentFile = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env.azure')
)

$ErrorActionPreference = 'Stop'

function Invoke-Az {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI failed: az $($Arguments -join ' ')"
    }
    return $result
}

if (-not (Test-Path -LiteralPath $EnvironmentFile -PathType Leaf)) {
    throw "Deployment settings not found: $EnvironmentFile. Run scripts\deploy.ps1 first."
}
$settings = Get-Content -LiteralPath $EnvironmentFile -Raw | ConvertFrom-StringData
foreach ($key in @('AZURE_SUBSCRIPTION_ID', 'AZURE_TENANT_ID', 'AZURE_RESOURCE_GROUP',
        'AZURE_SEARCH_RESOURCE_ID', 'AZURE_STORAGE_RESOURCE_ID',
        'AZURE_STORAGE_CONTAINER_NAME', 'AZURE_AI_PROJECT_RESOURCE_ID')) {
    if ([string]::IsNullOrWhiteSpace($settings[$key])) {
        throw "Missing $key in $EnvironmentFile. Run the updated deployment script first."
    }
}

$subscriptionId = $settings.AZURE_SUBSCRIPTION_ID
$subscriptionScope = "/subscriptions/$subscriptionId"
$groupScope = "$subscriptionScope/resourceGroups/$($settings.AZURE_RESOURCE_GROUP)"
$searchScope = $settings.AZURE_SEARCH_RESOURCE_ID
$projectScope = $settings.AZURE_AI_PROJECT_RESOURCE_ID
$storageScope = $settings.AZURE_STORAGE_RESOURCE_ID
$containerScope = "$storageScope/blobServices/default/containers/$($settings.AZURE_STORAGE_CONTAINER_NAME)"
foreach ($scope in @($searchScope, $projectScope, $storageScope)) {
    if (-not $scope.StartsWith("$groupScope/providers/", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Resource scope is outside the configured workshop resource group: $scope"
    }
}
$context = Invoke-Az account show --subscription $subscriptionId --output json | ConvertFrom-Json
if ($context.tenantId -ne $settings.AZURE_TENANT_ID) {
    throw 'Subscription tenant does not match the deployment settings. Check the environment file and Azure login.'
}

# Resolve every attendee before changing permissions.
$participants = [ordered]@{}
foreach ($user in $Users) {
    if ([string]::IsNullOrWhiteSpace($user)) {
        throw 'Users must contain Microsoft Entra user principal names or user object IDs.'
    }
    $identity = Invoke-Az ad user show --id $user --query '{id:id,userPrincipalName:userPrincipalName}' `
        --subscription $subscriptionId --output json | ConvertFrom-Json
    if (-not $identity.id) {
        throw "Microsoft Entra user could not be resolved: $user"
    }
    $participants[$identity.id] = $identity
}

$grants = @(
    @{ Role = 'Search Service Contributor'; Id = '7ca78c08-252a-4471-8644-bb5ff32d4ba0'; Scope = $searchScope },
    @{ Role = 'Search Index Data Contributor'; Id = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'; Scope = $searchScope },
    @{ Role = 'Storage Blob Data Contributor'; Id = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'; Scope = $containerScope },
    @{ Role = 'Foundry User'; Id = '53ca6127-db72-4b80-b1b0-d745d6d5456d'; Scope = $projectScope }
)
# Reader lets attendees open the Web IQ resource in the portal to create their own profile API key.
if ($settings.WEBIQ_RESOURCE_ID -and $settings.WEBIQ_RESOURCE_ID.StartsWith("$groupScope/providers/", [StringComparison]::OrdinalIgnoreCase)) {
    $grants += @{ Role = 'Reader'; Id = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'; Scope = $settings.WEBIQ_RESOURCE_ID }
}
$assignments = @()
foreach ($scope in ($grants | ForEach-Object { $_.Scope } | Select-Object -Unique)) {
    $assignments += @(Invoke-Az role assignment list --scope $scope --all --subscription $subscriptionId `
        --output json | ConvertFrom-Json)
}

Write-Host "Subscription: $($context.name) | Workshop: $($settings.AZURE_RESOURCE_GROUP)"
Write-Warning 'Attendees share Search objects and blob contents. Search Service Contributor also allows service administration. Use only trusted attendees and non-sensitive workshop data.'

$results = @()
foreach ($participant in $participants.Values) {
    foreach ($grant in $grants) {
        $roleId = "$subscriptionScope/providers/Microsoft.Authorization/roleDefinitions/$($grant.Id)"
        $existing = $assignments | Where-Object {
            $_.principalId -eq $participant.id -and $_.roleDefinitionId -eq $roleId -and $_.scope -eq $grant.Scope
        }
        $status = 'Already assigned'
        if (-not $existing) {
            $status = 'Not changed'
            if ($PSCmdlet.ShouldProcess("$($participant.userPrincipalName) on $($grant.Scope)", "Assign $($grant.Role)")) {
                $assignment = Invoke-Az role assignment create --assignee-object-id $participant.id `
                    --assignee-principal-type User --role $roleId --scope $grant.Scope `
                    --subscription $subscriptionId --output json | ConvertFrom-Json
                $assignments += $assignment
                $status = 'Assigned'
            }
        }
        $results += [pscustomobject]@{
            User = $participant.userPrincipalName
            Role = $grant.Role
            Status = $status
        }
    }
}
$results | Format-Table -AutoSize
Write-Host 'Role changes can take several minutes to propagate. Attendees must sign in to the workshop tenant.'
