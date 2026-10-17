# S2F Race Day Timing

Windows app for race-day timing: check out **RUNSCORE + primary MyLaps `.event`** to local disk, leave **`DB's` / Pre-Race / announcer `.event`s** on OneDrive, launch vendors from the shell, then **Finish** with a sync settle check before shutdown.

## Defaults

| Setting | Value |
|---------|--------|
| Library root | `C:\OneDrive` |
| Local race day root | `C:\RaceDay` |
| Admin PIN (fixed) | `7690` |
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
- On **published Release**: packs, **Azure Artifact Signing**, uploads Velopack packages to that Release

### Code signing (SmartScreen)

Uses [Azure Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/) via Velopack `--azureTrustedSignFile` and GitHub OIDC (`azure/login`). No PFX.

**Repo secrets**

| Secret | From |
|--------|------|
| `AZURE_CLIENT_ID` | Entra App registration → Application (client) ID |
| `AZURE_TENANT_ID` | Entra App registration → Directory (tenant) ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |

**Repo variables**

| Variable | Example |
|----------|---------|
| `AZURE_TRUSTED_SIGNING_ENDPOINT` | `https://eus.codesigning.azure.net` (must match account region) |
| `AZURE_TRUSTED_SIGNING_ACCOUNT` | Artifact Signing account name |
| `AZURE_TRUSTED_SIGNING_PROFILE` | Certificate profile name |

Also required once in Azure: federated credentials on that app for this repo — at least **Environment** `production` (CI pack/release jobs use that environment) — and role **Artifact Signing Certificate Profile Signer** on the signing account. A Branch `main` credential alone is not enough for Release (tag) builds.

If secrets/vars are missing, packs still succeed unsigned (SmartScreen may warn).

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

## RunScore preferences (SOP)

On **checkout** and again on **Open RunScore**, the app writes company SOP values into
`HKCU\Software\RunTime Software\RunScore` (same store RunScore Preferences uses).

Editable file (seeded on first use):

`%AppData%\S2FRaceDayCheckout\runscore-sop.json`

Admin → **Edit RunScore SOP…** opens that file. Use `"{{BackupPath}}"` for alternate folders (filled per checkout). `autoSaveMinutes` is converted to RunScore counts (`× 300`).

| Preference | Default SOP |
|------------|-------------|
| Alternate folder to save entries (`Save entries dir`) | `{{BackupPath}}` → this checkout’s Backup |
| Alternate folder to save results (`Save results dir`) | Same |
| Minutes for Auto Save | **1** (`autoSaveMinutes`; → `300` counts) |
| Other Server Preferences | See `bools` / `ints` / `strings` in the JSON |

Timers should not need to open Tools → Preferences on race day. If RunScore was already open, close and relaunch so it reloads the registry. Save the JSON, then checkout or Open RunScore to apply.

## Fixtures

`fixtures/SampleLibrary` — Memphis + Nashville sample sessions for safe QA.
