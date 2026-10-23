# Copies the company RemotePC deploy package into a Velopack publish redist folder.
#
# MSI sources (first match wins):
#   1. Env REMOTEPC_HOST_BIN_URL  — HTTPS download (required for CI; MSI is too large for Actions secrets)
#   2. Env REMOTEPC_MSI           — local file path
#   3. Repo redist/remotepc-host.bin
#
# Deploy JSON sources (first match wins):
#   1. Env REMOTEPC_DEPLOY_JSON_B64 — base64 (fits easily in Actions secrets)
#   2. Env REMOTEPC_DEPLOY_JSON     — raw JSON string or file path
#   3. Repo redist/remotepc-deploy.json
#
# Skips cleanly when either half is missing (Setup still builds; deploy just won't run).
param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$repoRoot = Split-Path $PSScriptRoot -Parent
$msiDest = Join-Path $Destination "remotepc-host.bin"
$cfgDest = Join-Path $Destination "remotepc-deploy.json"

function Write-FromBase64([string]$B64, [string]$OutPath) {
    if ([string]::IsNullOrWhiteSpace($B64)) { return $false }
    $bytes = [Convert]::FromBase64String($B64.Trim())
    [IO.File]::WriteAllBytes($OutPath, $bytes)
    return $true
}

$msiOk = $false
$cfgOk = $false

if (-not [string]::IsNullOrWhiteSpace($env:REMOTEPC_HOST_BIN_URL)) {
    Write-Host "Downloading RemotePC MSI from REMOTEPC_HOST_BIN_URL…"
    Invoke-WebRequest -Uri $env:REMOTEPC_HOST_BIN_URL.Trim() -OutFile $msiDest -UseBasicParsing
    $msiOk = $true
    Write-Host "RemotePC MSI downloaded ($((Get-Item $msiDest).Length) bytes)"
} elseif ($env:REMOTEPC_MSI -and (Test-Path $env:REMOTEPC_MSI)) {
    Copy-Item -Force $env:REMOTEPC_MSI $msiDest
    $msiOk = $true
    Write-Host "RemotePC MSI from REMOTEPC_MSI path"
} elseif (Test-Path (Join-Path $repoRoot "redist\remotepc-host.bin")) {
    Copy-Item -Force (Join-Path $repoRoot "redist\remotepc-host.bin") $msiDest
    $msiOk = $true
    Write-Host "RemotePC MSI from repo redist/remotepc-host.bin"
}

if (Write-FromBase64 $env:REMOTEPC_DEPLOY_JSON_B64 $cfgDest) {
    $cfgOk = $true
    Write-Host "RemotePC deploy.json from REMOTEPC_DEPLOY_JSON_B64"
} elseif (-not [string]::IsNullOrWhiteSpace($env:REMOTEPC_DEPLOY_JSON)) {
    $raw = $env:REMOTEPC_DEPLOY_JSON.Trim()
    if (Test-Path $raw) {
        Copy-Item -Force $raw $cfgDest
        Write-Host "RemotePC deploy.json from REMOTEPC_DEPLOY_JSON path"
    } else {
        [IO.File]::WriteAllText($cfgDest, $raw)
        Write-Host "RemotePC deploy.json from REMOTEPC_DEPLOY_JSON string"
    }
    $cfgOk = $true
} elseif (Test-Path (Join-Path $repoRoot "redist\remotepc-deploy.json")) {
    Copy-Item -Force (Join-Path $repoRoot "redist\remotepc-deploy.json") $cfgDest
    $cfgOk = $true
    Write-Host "RemotePC deploy.json from repo redist/remotepc-deploy.json"
}

if (-not $msiOk -or -not $cfgOk) {
    if (Test-Path $msiDest) { Remove-Item -Force $msiDest -ErrorAction SilentlyContinue }
    if (Test-Path $cfgDest) { Remove-Item -Force $cfgDest -ErrorAction SilentlyContinue }
    Write-Host "RemotePC redist skipped (need MSI via URL/path/redist + deploy.json via secret/path/redist)."
    exit 0
}

$cfgText = Get-Content -LiteralPath $cfgDest -Raw
if ($cfgText -notmatch 'configurationId') {
    throw "RemotePC deploy.json is missing configurationId."
}
$len = (Get-Item $msiDest).Length
if ($len -lt 100000) {
    throw "RemotePC MSI looks too small ($len bytes) — check REMOTEPC_HOST_BIN_URL."
}

Write-Host "Bundled RemotePC deploy into $Destination"
