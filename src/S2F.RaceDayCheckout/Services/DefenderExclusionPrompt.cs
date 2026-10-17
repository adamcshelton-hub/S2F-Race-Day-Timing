using System.Diagnostics;
using System.IO;
using System.Windows;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Services;

/// <summary>
/// First-install opt-in: create RaceDay Active/Backup and exclude them from Defender.
/// Gated by Velopack <see cref="MarkOfferPending"/> and settings
/// <c>DefenderExclusionsConfigured</c> — not on checkout or routine updates.
/// </summary>
public static class DefenderExclusionPrompt
{
    private static bool _offerOnStartup;

    public static void MarkOfferPending() => _offerOnStartup = true;

    public static void TryOffer(Window? owner)
    {
        if (!_offerOnStartup)
            return;
        _offerOnStartup = false;

        var store = new SettingsStore();
        var settings = store.Load();
        if (!DefenderExclusionPolicy.NeedsOffer(settings))
            return;

        var raceDayRoot = string.IsNullOrWhiteSpace(settings.RaceDayRoot)
            ? Core.Models.AppSettings.DefaultRaceDayRoot
            : settings.RaceDayRoot;

        try
        {
            DefenderExclusionPolicy.EnsureRaceDayFolders(raceDayRoot);
        }
        catch (Exception ex)
        {
            TimerDialog.Alert(
                owner,
                "Race Day folders",
                "Couldn't create the local Race Day folders:\n\n"
                + raceDayRoot + "\\Active\n"
                + raceDayRoot + "\\Backup\n\n"
                + ex.Message);
            DefenderExclusionPolicy.MarkConfigured(settings);
            store.Save(settings);
            return;
        }

        var active = DefenderExclusionPolicy.GetActiveRoot(raceDayRoot);
        var backup = DefenderExclusionPolicy.GetBackupRoot(raceDayRoot);

        var accepted = TimerDialog.Confirm(
            owner,
            "Speed up race day?",
            "Exclude these local folders from Windows antivirus scanning so RunScore "
            + "isn't slowed by real-time scans during a race:\n\n"
            + active + "\n"
            + backup + "\n\n"
            + "Only these Race Day folders — not OneDrive.\n\n"
            + "Windows will ask for administrator permission.\n\n"
            + "You can skip; checkout still works either way.",
            yesLabel: "Exclude…",
            noLabel: "Skip");

        if (accepted)
        {
            var result = TryApplyExclusionsElevated(active, backup);
            if (result.Success)
            {
                DefenderExclusionPolicy.MarkApplied(settings);
                TimerDialog.Alert(
                    owner,
                    "Defender exclusions set",
                    "Windows Defender will skip scanning:\n\n"
                    + active + "\n"
                    + backup + "\n\n"
                    + "This won't be asked again on later launches or updates.");
            }
            else
            {
                TimerDialog.Alert(
                    owner,
                    "Couldn't set exclusions",
                    "Windows didn't apply the Defender exclusions"
                    + (string.IsNullOrWhiteSpace(result.Message) ? "." : ":\n\n" + result.Message)
                    + "\n\nYou can add them later in Windows Security → Virus & threat protection "
                    + "→ Manage settings → Exclusions:\n\n"
                    + active + "\n"
                    + backup);
            }
        }

        // Yes, No, or failed apply — never re-prompt on later launches / routine updates.
        DefenderExclusionPolicy.MarkConfigured(settings);
        store.Save(settings);
    }

    private readonly record struct ApplyResult(bool Success, string Message);

    /// <summary>UAC-elevated PowerShell Add-MpPreference for the two Race Day roots.</summary>
    private static ApplyResult TryApplyExclusionsElevated(string activePath, string backupPath)
    {
        string? scriptPath = null;
        try
        {
            var activePs = EscapeForSingleQuotedPowerShell(activePath);
            var backupPs = EscapeForSingleQuotedPowerShell(backupPath);
            scriptPath = Path.Combine(
                Path.GetTempPath(),
                "s2f-defender-excl-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(
                scriptPath,
                "Add-MpPreference -ExclusionPath '" + activePs + "'\r\n"
                + "Add-MpPreference -ExclusionPath '" + backupPs + "'\r\n");

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new ApplyResult(false, "Couldn't start elevated PowerShell.");

            proc.WaitForExit(120_000);
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new ApplyResult(false, "Elevated PowerShell timed out.");
            }

            if (proc.ExitCode != 0)
                return new ApplyResult(false, "PowerShell exited with code " + proc.ExitCode + ".");

            return new ApplyResult(true, string.Empty);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ApplyResult(false, "Administrator permission was cancelled.");
        }
        catch (Exception ex)
        {
            return new ApplyResult(false, ex.Message);
        }
        finally
        {
            if (scriptPath is not null)
            {
                try { File.Delete(scriptPath); } catch { /* ignore */ }
            }
        }
    }

    internal static string EscapeForSingleQuotedPowerShell(string path) =>
        path.Replace("'", "''", StringComparison.Ordinal);
}
