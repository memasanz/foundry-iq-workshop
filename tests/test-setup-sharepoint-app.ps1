$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\setup-sharepoint-app.ps1'
$fixture = New-TemporaryFile
@'
AZURE_SUBSCRIPTION_ID=test-subscription
AZURE_TENANT_ID=test-tenant
AZURE_SEARCH_RESOURCE_ID=/subscriptions/test-subscription/resourceGroups/test/providers/Microsoft.Search/searchServices/test
'@ | Set-Content -LiteralPath $fixture.FullName

$global:TestAppMatches = @()
$global:TestAppWrites = [System.Collections.Generic.List[object]]::new()
$global:TestAppFilter = ''

function global:az {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    switch ($arguments[0..1] -join ' ') {
        'account show' { @{ tenantId = 'test-tenant' } | ConvertTo-Json -Compress }
        'resource show' { @{ identity = @{ principalId = 'search-principal' } } | ConvertTo-Json -Compress }
        'rest --method' {
            $method = $arguments[$arguments.IndexOf('--method') + 1]
            $url = $arguments[$arguments.IndexOf('--url') + 1]
            $uri = [Uri]$url
            $path = $uri.AbsolutePath
            $query = [Uri]::UnescapeDataString($uri.Query)
            if ($method -ne 'GET') {
                $bodyPath = $arguments[$arguments.IndexOf('--body') + 1].Substring(1)
                $body = Get-Content -LiteralPath $bodyPath -Raw | ConvertFrom-Json
                $global:TestAppWrites.Add(@{ Method = $method; Path = $path; Body = $body })
                if ($method -eq 'POST' -and $path -eq '/v1.0/applications') {
                    @{ id = 'app-object'; appId = 'app-client' } | ConvertTo-Json -Compress
                }
            }
            elseif ($path -eq '/v1.0/servicePrincipals/search-principal') {
                @{ id = 'search-principal'; appId = 'search-client' } | ConvertTo-Json -Compress
            }
            elseif ($path -eq '/v1.0/applications') {
                $global:TestAppFilter = $query
                @{ value = @($global:TestAppMatches) } | ConvertTo-Json -Depth 10 -Compress
            }
            elseif ($path -eq '/v1.0/applications/app-object/federatedIdentityCredentials') {
                @{ value = @(@{
                    issuer = 'https://login.microsoftonline.com/test-tenant/v2.0'
                    subject = 'search-principal'
                }) } | ConvertTo-Json -Depth 5 -Compress
            }
            elseif ($path -eq '/v1.0/servicePrincipals') {
                if ($query -like "*appId eq 'app-client'*") {
                    @{ value = @(@{ id = 'app-service-principal' }) } | ConvertTo-Json -Depth 5 -Compress
                }
                else {
                    $roles = foreach ($name in @('Files.Read.All', 'Sites.Selected', 'User.Read.All')) {
                        @{ id = $name; value = $name; isEnabled = $true; allowedMemberTypes = @('Application') }
                    }
                    @{ value = @(@{ appRoles = @($roles) }) } | ConvertTo-Json -Depth 5 -Compress
                }
            }
            else { throw "Unexpected Graph request: $method $url" }
        }
        default { throw "Unexpected Azure CLI command: $arguments" }
    }
}

function Assert-Equal($Actual, $Expected, $Message) {
    if ($Actual -ne $Expected) { throw "$Message. Expected: $Expected; actual: $Actual" }
}

try {
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName 'New Workshop App' | Out-Null
    Assert-Equal $global:TestAppWrites.Count 1 'A missing app must be created'
    Assert-Equal $global:TestAppWrites[0].Body.displayName 'New Workshop App' 'Creation must use the supplied name'
    Assert-Equal $global:TestAppWrites[0].Body.signInAudience 'AzureADMyOrg' 'New apps must be single-tenant'

    $global:TestAppWrites.Clear()
    & $scriptPath -EnvironmentFile $fixture.FullName | Out-Null
    Assert-Equal $global:TestAppWrites[0].Body.displayName 'iq-workshop-sharepoint-indexer' 'Omitting AppName must retain the default'

    $global:TestAppMatches = @(@{
        id = 'app-object'; appId = 'app-client'
        requiredResourceAccess = @(
            @{
                resourceAppId = '00000003-0000-0000-c000-000000000000'
                resourceAccess = @(
                    @{ id = 'Files.Read.All'; type = 'Role' },
                    @{ id = 'existing-delegated-permission'; type = 'Scope' }
                )
            },
            @{ resourceAppId = 'another-api'; resourceAccess = @(@{ id = 'another-role'; type = 'Role' }) }
        )
    })
    $global:TestAppWrites.Clear()
    $appName = "Workshop's App & Search"
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName $appName -InformationVariable messages | Out-Null
    Assert-Equal $global:TestAppFilter "?`$filter=displayName eq 'Workshop''s App & Search'&`$select=id,appId,requiredResourceAccess" 'The exact-name filter must escape and encode special characters'
    Assert-Equal $global:TestAppWrites.Count 1 'Reusing an app must update it without creating another'
    Assert-Equal $global:TestAppWrites[0].Method 'PATCH' 'Existing apps must be updated'
    Assert-Equal $global:TestAppWrites[0].Path '/v1.0/applications/app-object' 'The matching app must be selected'
    $merged = $global:TestAppWrites[0].Body.requiredResourceAccess
    $graphAccess = @($merged | Where-Object { $_.resourceAppId -eq '00000003-0000-0000-c000-000000000000' })[0].resourceAccess
    Assert-Equal $merged.Count 3 'Existing API entries must be preserved alongside required APIs'
    Assert-Equal $graphAccess.Count 3 'Existing permissions must be preserved without duplicating required roles'
    Assert-Equal @($graphAccess | Where-Object { $_.id -eq 'existing-delegated-permission' -and $_.type -eq 'Scope' }).Count 1 'Delegated permissions must survive reuse'
    Assert-Equal @($merged | Where-Object { $_.resourceAppId -eq 'another-api' }).Count 1 'Other APIs must survive reuse'
    Assert-Equal @($messages | Where-Object { $_.MessageData.ToString() -like "*-DisplayName 'Workshop''s App & Search'*" }).Count 1 'The printed site grant must quote the selected app name safely'

    $global:TestAppMatches[0].requiredResourceAccess = $merged
    $global:TestAppWrites.Clear()
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName $appName | Out-Null
    $repeated = $global:TestAppWrites[0].Body.requiredResourceAccess
    Assert-Equal $repeated.Count 3 'Reruns must not duplicate API entries'
    Assert-Equal (@($repeated | Where-Object { $_.resourceAppId -eq '00000003-0000-0000-c000-000000000000' })[0].resourceAccess.Count) 3 'Reruns must not duplicate permissions'

    $global:TestAppWrites.Clear()
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName $appName -WhatIf | Out-Null
    Assert-Equal $global:TestAppWrites.Count 0 'WhatIf must not modify an existing app'

    $global:TestAppMatches = @()
    & $scriptPath -EnvironmentFile $fixture.FullName -AppName 'Preview New App' -WhatIf | Out-Null
    Assert-Equal $global:TestAppWrites.Count 0 'WhatIf must not create a new app'

    $global:TestAppMatches = @(@{ id = 'first' }, @{ id = 'second' })
    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -AppName 'Duplicate' | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'More than one app named*') { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Duplicate display names must fail explicitly'
    Assert-Equal $global:TestAppWrites.Count 0 'Duplicate matches must not modify any app'

    $failed = $false
    try { & $scriptPath -EnvironmentFile $fixture.FullName -AppName ' ' | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*parameter 'AppName'*") { throw }
        $failed = $true
    }
    Assert-Equal $failed $true 'Whitespace-only app names must be rejected'
    Write-Host 'SharePoint app checks passed: new/default/existing names, permission preservation, special characters, duplicates and WhatIf.'
}
finally {
    Remove-Item -LiteralPath $fixture.FullName
    Remove-Item Function:\az
    Remove-Variable TestAppMatches, TestAppWrites, TestAppFilter -Scope Global
}

if ($env:OS -eq 'Windows_NT') {
    $launcherDirectory = Join-Path ([IO.Path]::GetTempPath()) "sharepoint-cli-test-$([Guid]::NewGuid().ToString('N'))"
    $launcher = Join-Path $launcherDirectory 'az.cmd'
    $originalPath = $env:PATH
    try {
        New-Item -ItemType Directory -Path $launcherDirectory | Out-Null
        "@echo off`r`necho %*`r`n" | Set-Content -LiteralPath $launcher -Encoding ascii
        $env:PATH = "$launcherDirectory;$originalPath"
        $tokens = $null
        $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
        $wrapper = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Az'
        }, $true)
        & {
            Invoke-Expression $wrapper.Extent.Text
            $url = 'https://graph.microsoft.com/v1.0/applications?$filter=displayName%20eq%20%27test%27&$select=id,appId'
            $result = Invoke-Az rest --method GET --url $url
            Assert-Equal $LASTEXITCODE 0 'The Windows launcher must not execute $select as a command'
            Assert-Equal $result "rest --method GET --url `"$url`" --only-show-errors" 'The Windows launcher must receive the complete URL'
        }
        Write-Host 'Windows az.cmd URL regression passed.'
    }
    finally {
        $env:PATH = $originalPath
        Remove-Item -LiteralPath $launcher -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $launcherDirectory -ErrorAction SilentlyContinue
    }
}
