# Downloads the unmodified official Notepad++ installer for the Velopack pack.
# The file is stored as npp-setup.bin so packaging code-sign does not rewrite it.
param(
    [string]$Rid,
    [string]$Destination,
    [string]$SourceZip
)

$ErrorActionPreference = "Stop"
$Version = "8.9.8.1"
$Tag = "v$Version"
$Base = "https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/$Tag"

$Installers = @{
    "win-x64"   = @{
        Name = "npp.$Version.Installer.x64.exe"
        Sha256 = "26F3BCCED788FADBAC4E7E577B430EA2CE6060CF3BCAD993EF4612ECD7D743DA"
    }
    "win-arm64" = @{
        Name = "npp.$Version.Installer.arm64.exe"
        Sha256 = "A53C5116B55AC93FEDD7808E71096BADECBF10A55F2BE9B28B741591B8917A73"
    }
}

if ($Destination) {
    if (-not $Installers.ContainsKey($Rid)) { throw "-Rid must be win-x64 or win-arm64." }
    $spec = $Installers[$Rid]
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $bin = Join-Path $Destination "npp-setup.bin"
    $url = "$Base/$($spec.Name)"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $bin
    Unblock-File -Path $bin
    $actual = (Get-FileHash -Path $bin -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $spec.Sha256) {
        Remove-Item -Force $bin
        throw "Notepad++ installer hash mismatch for $($spec.Name). Expected $($spec.Sha256), got $actual."
    }
    $notice = @"
Notepad++ $Version
This folder contains the unmodified official Notepad++ installer (npp-setup.bin).
License: GNU GPL version 3. https://www.gnu.org/licenses/gpl-3.0.html
Corresponding source: https://github.com/notepad-plus-plus/notepad-plus-plus/archive/refs/tags/$Tag.zip
"@
    Set-Content -Path (Join-Path $Destination "notepad++-source.txt") -Value $notice -Encoding utf8
    Write-Host "Verified $($spec.Name) -> $bin"
}

if ($SourceZip) {
    $url = "https://github.com/notepad-plus-plus/notepad-plus-plus/archive/refs/tags/$Tag.zip"
    $dir = Split-Path -Parent $SourceZip
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $SourceZip
    Unblock-File -Path $SourceZip
    Write-Host "Wrote $SourceZip"
}
