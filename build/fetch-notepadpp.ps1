# Downloads the unmodified official Notepad++ installers for the Velopack pack.
# Stored as npp-setup-x64.bin and npp-setup-arm64.bin so packaging code-sign does not rewrite them.
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
    # Both builds ship in every package. An x64 app still runs on ARM Windows (Parallels)
    # and must install ARM64 Notepad++, not the x64 editor bundled for timing laptops.
    if ($Rid -and -not $Installers.ContainsKey($Rid)) { throw "-Rid must be win-x64 or win-arm64." }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $fileNames = @{
        "win-x64" = "npp-setup-x64.bin"
        "win-arm64" = "npp-setup-arm64.bin"
    }
    foreach ($key in $fileNames.Keys) {
        $spec = $Installers[$key]
        $bin = Join-Path $Destination $fileNames[$key]
        $url = "$Base/$($spec.Name)"
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $bin
        Unblock-File -Path $bin
        $actual = (Get-FileHash -Path $bin -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actual -ne $spec.Sha256) {
            Remove-Item -Force $bin
            throw "Notepad++ installer hash mismatch for $($spec.Name). Expected $($spec.Sha256), got $actual."
        }
        Write-Host "Verified $($spec.Name) -> $bin"
    }
    $notice = @"
Notepad++ $Version
This folder contains the unmodified official Notepad++ installers
(npp-setup-x64.bin and npp-setup-arm64.bin).
License: GNU GPL version 3. https://www.gnu.org/licenses/gpl-3.0.html
Corresponding source: https://github.com/notepad-plus-plus/notepad-plus-plus/archive/refs/tags/$Tag.zip
"@
    Set-Content -Path (Join-Path $Destination "notepad++-source.txt") -Value $notice -Encoding utf8
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
