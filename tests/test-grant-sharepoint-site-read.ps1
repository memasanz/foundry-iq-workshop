$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\grant-sharepoint-site-read.ps1'
$clientId = '11111111-1111-1111-1111-111111111111'
$siteUrl = 'https://contoso.sharepoint.com/sites/workshop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Reset-Fixture {
    $global:SiteReadApps = @(@{ appId = $clientId; displayName = "Workshop's Indexer" })
    $global:SiteReadPages = @(@{ value = @() })
    $global:SiteReadWrites = @()
    $global:SiteReadDenied = $false
    $global:SiteReadWrongVerification = $false
    $global:SiteReadFilter = ''
}

function New-TestGrant {
    param([string]$Role = 'read', [string]$IdentityProperty = 'grantedToIdentitiesV2')
    return @{ id = 'grant-id'; roles = @($Role); $IdentityProperty = @(@{ application = @{ id = $clientId } }) }
}

function global:az {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    if (($arguments[0..1] -join ' ') -eq 'account show') {
        return (@{ tenantId = 'test-tenant' } | ConvertTo-Json -Compress)
    }
    Assert-True ($arguments[0] -eq 'rest') 'Only account show and Graph requests are allowed'
    $method = $arguments[$arguments.IndexOf('--method') + 1]
    $url = [Uri]$arguments[$arguments.IndexOf('--url') + 1]
    if ($method -ne 'GET') {
        Assert-True ($method -eq 'POST' -and $url.AbsolutePath -eq '/v1.0/sites/site-id/permissions') 'Only a site permission POST is allowed'
        $bodyPath = $arguments[$arguments.IndexOf('--body') + 1].Substring(1)
        $body = Get-Content -LiteralPath $bodyPath -Raw | ConvertFrom-Json
        $global:SiteReadWrites += $body
        Assert-True ($body.roles.Count -eq 1 -and $body.roles[0] -eq 'read') 'Grant must be Read only'
        Assert-True ($body.grantedToIdentities[0].application.id -eq $clientId) 'Grant must target the client ID'
        return (@{ id = 'grant-id' } | ConvertTo-Json -Compress)
    }
    switch ($url.AbsolutePath) {
        '/v1.0/applications' {
            $global:SiteReadFilter = [Uri]::UnescapeDataString($url.Query)
            @{ value = $global:SiteReadApps } | ConvertTo-Json -Depth 10 -Compress
        }
        '/v1.0/sites/contoso.sharepoint.com:/sites/workshop' {
            @{ id = 'site-id' } | ConvertTo-Json -Compress
        }
        '/v1.0/sites/site-id/permissions' {
            if ($global:SiteReadDenied) { $global:LASTEXITCODE = 1; return }
            $index = if ($url.Query -eq '?page=2') { 1 } else { 0 }
            $global:SiteReadPages[$index] | ConvertTo-Json -Depth 10 -Compress
        }
        '/v1.0/sites/site-id/permissions/grant-id' {
            $grant = New-TestGrant
            if ($global:SiteReadWrongVerification) { $grant.grantedToIdentitiesV2[0].application.id = 'other-app' }
            $grant | ConvertTo-Json -Depth 10 -Compress
        }
        default { throw "Unexpected Graph request: $method $url" }
    }
}

function Assert-Fails {
    param([scriptblock]$Action, [string]$Expected)
    $message = ''
    try { & $Action } catch { $message = $_.Exception.Message }
    Assert-True ($message -like "*$Expected*") "Expected failure containing '$Expected', got '$message'"
}

try {
    Reset-Fixture
    & $scriptPath -AppName "Workshop's Indexer" -SiteUrl $siteUrl
    Assert-True ($global:SiteReadWrites.Count -eq 1) 'Must create a missing grant'
    Assert-True ($global:SiteReadFilter.Contains("displayName eq 'Workshop''s Indexer'")) 'App name filter must escape apostrophes'

    Reset-Fixture
    & $scriptPath -AppId $clientId -SiteUrl $siteUrl -WhatIf
    Assert-True ($global:SiteReadWrites.Count -eq 0) 'WhatIf must not write'
    Assert-True ($global:SiteReadFilter.Contains("appId eq '$clientId'")) 'AppId must filter by client ID'

    foreach ($identityProperty in @('grantedToIdentities', 'grantedToIdentitiesV2')) {
        Reset-Fixture
        $global:SiteReadPages = @(
            @{ value = @(); '@odata.nextLink' = 'https://graph.microsoft.com/v1.0/sites/site-id/permissions?page=2' },
            @{ value = @((New-TestGrant -IdentityProperty $identityProperty)) }
        )
        & $scriptPath -AppName 'Indexer' -SiteUrl $siteUrl
        Assert-True ($global:SiteReadWrites.Count -eq 0) 'Existing grants on later pages must not be duplicated'
    }

    Reset-Fixture
    $global:SiteReadPages = @(@{ value = @((New-TestGrant -Role 'write')) })
    $warnings = @()
    & $scriptPath -AppName 'Indexer' -SiteUrl $siteUrl -WarningVariable warnings
    Assert-True ($global:SiteReadWrites.Count -eq 0 -and $warnings.Count -gt 0) 'Broader grants must be preserved with a warning'

    Reset-Fixture
    $global:SiteReadPages = @(@{ value = @((New-TestGrant -Role 'unknown')) })
    Assert-Fails { & $scriptPath -AppName 'Indexer' -SiteUrl $siteUrl } 'does not confirm read access'
    Assert-True ($global:SiteReadWrites.Count -eq 0) 'Unknown grants must not be duplicated'

    Reset-Fixture
    $global:SiteReadDenied = $true
    Assert-Fails { & $scriptPath -AppName 'Indexer' -SiteUrl $siteUrl } 'Sites.FullControl.All'
    Assert-True ($global:SiteReadWrites.Count -eq 0) 'Denied inspection must not write'

    foreach ($apps in @(@(), @(@{ appId = $clientId }, @{ appId = $clientId }))) {
        Reset-Fixture
        $global:SiteReadApps = $apps
        Assert-Fails { & $scriptPath -AppName 'Indexer' -SiteUrl $siteUrl } 'Expected exactly one'
        Assert-True ($global:SiteReadWrites.Count -eq 0) 'Missing or ambiguous apps must not write'
    }

    Reset-Fixture
    $global:SiteReadWrongVerification = $true
    Assert-Fails { & $scriptPath -AppName 'Indexer' -SiteUrl $siteUrl } 'could not be verified'

    Reset-Fixture
    Assert-Fails { & $scriptPath -AppName 'Indexer' -SiteUrl 'https://contoso.sharepoint.com.evil.example/sites/workshop' } 'SharePoint HTTPS'
    Assert-True ($global:SiteReadWrites.Count -eq 0) 'Invalid site URL must not write'
    Write-Host 'All standalone SharePoint site Read tests passed.'
}
finally {
    Remove-Item Function:\az
    Get-Variable -Name 'SiteRead*' -Scope Global | Remove-Variable -Scope Global
}
