# Copies the company RemotePC deploy package into a Velopack publish redist folder.
# Place files at repo root redist/ (gitignored) or set REMOTEPC_MSI / REMOTEPC_DEPLOY_JSON.
param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$repoRoot = Split-Path $PSScriptRoot -Parent
$msiSrc = if ($env:REMOTEPC_MSI -and (Test-Path $env:REMOTEPC_MSI)) {
    $env:REMOTEPC_MSI
} elseif (Test-Path (Join-Path $repoRoot "redist\remotepc-host.bin")) {
    Join-Path $repoRoot "redist\remotepc-host.bin"
} else {
    $null
}

$cfgSrc = if ($env:REMOTEPC_DEPLOY_JSON -and (Test-Path $env:REMOTEPC_DEPLOY_JSON)) {
    $env:REMOTEPC_DEPLOY_JSON
} elseif (Test-Path (Join-Path $repoRoot "redist\remotepc-deploy.json")) {
    Join-Path $repoRoot "redist\remotepc-deploy.json"
} else {
    $null
}

if (-not $msiSrc -or -not $cfgSrc) {
    Write-Host "RemotePC redist skipped (need remotepc-host.bin + remotepc-deploy.json, or REMOTEPC_MSI + REMOTEPC_DEPLOY_JSON)."
    exit 0
}

Copy-Item -Force $msiSrc (Join-Path $Destination "remotepc-host.bin")
Copy-Item -Force $cfgSrc (Join-Path $Destination "remotepc-deploy.json")
Write-Host "Bundled RemotePC deploy into $Destination"
