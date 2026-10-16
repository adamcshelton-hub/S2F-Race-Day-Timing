# S2F Race Day Timing

Windows app for race-day timing: check out **RUNSCORE + primary MyLaps `.event`** to local disk, leave **`DB's` / Pre-Race / announcer `.event`s** on OneDrive, launch vendors from the shell, then **Finish** with a sync settle check before shutdown.

## Defaults

| Setting | Value |
|---------|--------|
| Library root | `C:\OneDrive` |
| Local race day root | `C:\RaceDay` |
| Admin PIN (new installs) | `7690` |
| Active | `C:\RaceDay\Active\{Office}\{session}\` |

Admin settings: **triple-click the header**, enter PIN.

## Install (timers)

1. Open the latest [GitHub Release](https://github.com/adamcshelton-hub/S2F-Race-Day-Timing/releases).
2. Download the **Setup** for your PC:
   - Most timing laptops → **win-x64**
   - Parallels / ARM Windows → **win-arm64**
3. Run Setup.exe (signed builds avoid SmartScreen warnings).
4. App checks GitHub for updates on launch. It will **not** restart while an event is checked out.

Admin → **Check for updates** forces a check anytime.

## Publish a release (ops)

```bash
# Tag + publish a GitHub Release (CI packs both arches and uploads Velopack assets)
git tag v0.3.0
git push origin v0.3.0
# Then create the Release for that tag in GitHub (or gh release create v0.3.0)
```

CI (`build.yml`):

- Tests on every PR/push
- On `main` push: packs win-x64 + win-arm64 (CI version) as artifacts
- On **published Release**: packs, optionally **code-signs**, uploads Velopack packages to that Release

### Code signing (SmartScreen)

Add repo secrets:

| Secret | Contents |
|--------|----------|
| `WINDOWS_CERT_PFX_BASE64` | Base64 of your `.pfx` (Authenticode) |
| `WINDOWS_CERT_PASSWORD` | PFX password |

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes(".\codesign.pfx")) | Set-Clipboard
```

Without these secrets, Setup still builds; Windows may show “Unknown publisher” until a cert is configured.

## Dev / Parallels

Copy the repo to `C:\` (not `\\Mac\`), then:

```powershell
cd C:\S2F-Race-Day-Timing
dotnet test
dotnet run --project src\S2F.RaceDayCheckout
```

## What gets checked out

| Local + locked | Stays on OneDrive |
|----------------|-------------------|
| `RUNSCORE\` | `DB's\` (live CSV) |
| Primary timing `.event` | `Pre-Race\`, secondary `.event`s |

Locks / history / finish backups live under `._checkin\` on the session leaf.

## Fixtures

`fixtures/SampleLibrary` — Memphis + Nashville sample sessions for safe QA.
