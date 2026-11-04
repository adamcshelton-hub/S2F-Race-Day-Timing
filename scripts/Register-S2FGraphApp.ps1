#Requires -Version 5.1
<#
.SYNOPSIS
  Creates the S2F Race Day Timing public-client Entra app in the start2finish tenant
  and prints Application (client) ID for AppSettings.DefaultGraphClientId.

.NOTES
  Run while signed in as timing@start2finish.onmicrosoft.com (Global Admin).
  Install once:  Install-Module Microsoft.Graph -Scope CurrentUser
#>
$ErrorActionPreference = 'Stop'
$tenantId = '570b27a7-46ae-45cc-88cd-8f887958546d'
$displayName = 'S2F Race Day Timing'

Write-Host "Connecting to Microsoft Graph (device code)…"
Connect-MgGraph -TenantId $tenantId -Scopes 'Application.ReadWrite.All','DelegatedPermissionGrant.ReadWrite.All','Directory.Read.All' -NoWelcome

$existing = Get-MgApplication -Filter "displayName eq '$displayName'" -ConsistencyLevel eventual -CountVariable c -ErrorAction SilentlyContinue
if ($existing) {
    $app = @($existing)[0]
    Write-Host "App already exists: $($app.AppId)"
} else {
    $app = New-MgApplication -DisplayName $displayName -SignInAudience 'AzureADMyOrg' `
        -PublicClient @{ RedirectUris = @('http://localhost') } `
        -IsFallbackPublicClient
    Write-Host "Created app: $($app.AppId)"
}

# Ensure public client + localhost redirect
Update-MgApplication -ApplicationId $app.Id `
    -PublicClient @{ RedirectUris = @('http://localhost') } `
    -IsFallbackPublicClient `
    -SignInAudience 'AzureADMyOrg'

# Delegated Graph permissions: Files.ReadWrite, User.Read, offline_access
$graphSp = Get-MgServicePrincipal -Filter "appId eq '00000003-0000-0000-c000-000000000000'"
$wanted = @{
    'Files.ReadWrite' = $null
    'User.Read'       = $null
    'offline_access'  = $null
}
$oauth = @()
foreach ($perm in $graphSp.Oauth2PermissionScopes) {
    if ($wanted.ContainsKey($perm.Value)) {
        $oauth += @{
            Id = $perm.Id
            Type = 'Scope'
        }
        $wanted[$perm.Value] = $perm.Id
    }
}
Update-MgApplication -ApplicationId $app.Id -RequiredResourceAccess @(
    @{
        ResourceAppId = '00000003-0000-0000-c000-000000000000'
        ResourceAccess = $oauth
    }
)

# Admin consent for this tenant
$sp = Get-MgServicePrincipal -Filter "appId eq '$($app.AppId)'" -ErrorAction SilentlyContinue
if (-not $sp) {
    $sp = New-MgServicePrincipal -AppId $app.AppId
}
$graphSpId = $graphSp.Id
foreach ($name in @('Files.ReadWrite','User.Read','offline_access')) {
    $scopeId = $wanted[$name]
    if (-not $scopeId) { continue }
    $has = Get-MgOauth2PermissionGrant -Filter "clientId eq '$($sp.Id)' and resourceId eq '$graphSpId'" -ErrorAction SilentlyContinue |
        Where-Object { $_.Scope -split ' ' -contains $name }
    if (-not $has) {
        try {
            New-MgOauth2PermissionGrant -ClientId $sp.Id -ConsentType 'AllPrincipals' `
                -ResourceId $graphSpId -Scope "$name" | Out-Null
        } catch {
            # Grant may already exist as a combined scope string — non-fatal.
            Write-Warning $_.Exception.Message
        }
    }
}

Write-Host ""
Write-Host "=== Paste into AppSettings.cs ==="
Write-Host "DefaultGraphClientId = `"$($app.AppId)`";"
Write-Host "DefaultGraphTenantId = `"$tenantId`";"  # already set
Write-Host ""
Disconnect-MgGraph | Out-Null
