namespace S2F.RaceDayCheckout.Core.Models;

/// <summary>
/// Editable RunScore Preferences SOP. Stored as JSON next to settings.json.
/// Path fields may use <see cref="BackupPathToken"/>; expanded at apply time.
/// </summary>
public sealed class RunScoreSopProfile
{
    public const string BackupPathToken = "{{BackupPath}}";

    /// <summary>Registry value RunScore reads for listing output (F7). Blank means Windows Notepad.</summary>
    public const string ExternalEditorName = "External Editor";

    /// <summary>
    /// Notepad++ install path. x64 and ARM64 official setups both use this file.
    /// The app installs the build that matches the PC (ARM64 on Parallels).
    /// Same path on every machine that used the official installer and left the default folder.
    /// </summary>
    public const string NotepadPlusPlusExePath = @"C:\Program Files\Notepad++\notepad++.exe";

    /// <summary>Friendly minutes; converted to registry "Auto save counts" (= minutes × 300).</summary>
    public double AutoSaveMinutes { get; set; } = 1;

    public string SaveEntriesDir { get; set; } = BackupPathToken;
    public string SaveResultsDir { get; set; } = BackupPathToken;

    public Dictionary<string, bool> Bools { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Ints { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Strings { get; set; } = new(StringComparer.Ordinal);

    public static RunScoreSopProfile CreateDefaults()
    {
        var profile = new RunScoreSopProfile
        {
            AutoSaveMinutes = 1,
            SaveEntriesDir = BackupPathToken,
            SaveResultsDir = BackupPathToken,
            Bools = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["Auto increment bibs"] = true,
                ["Save ASCII bibs"] = false,
                ["Save all results"] = true,
                ["Fractional times"] = true,
                ["New switch"] = true,
                ["F9 clears fields"] = false,
                ["Time from TM enter"] = false,
                ["Auto enable timer"] = true,
                ["Lock keyboard"] = false,
                ["Pop-up for duplicates"] = true,
                ["Enter same as Tab"] = false,
                ["Use record number in Enter-Edit"] = false,
                ["Print merge errors"] = false,
                ["No merge popup"] = false,
                ["Must arm to delete"] = false,
                ["Warn on delete"] = true,
                ["Check for record changed"] = true,
                ["Listings beep"] = false,
                ["Type ahead"] = true,
                ["Edit res both"] = true,
                ["Auto login"] = true,
                ["Auto connect to cloud"] = false,
                ["Use Cloud test server"] = false,
                ["Runtime Validator"] = true,
                ["Always delete results"] = true,
                ["Always ask to delete results"] = false,
                ["Always keep results"] = false,
                ["Page number top"] = true,
                ["Printer dialog"] = false,
                ["Multiple printers"] = false,
            },
            Ints = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Maximum difference"] = 2,
                ["HTML char width"] = 0,
                ["TCP server port"] = 51760,
            },
            Strings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Bandit number"] = "0",
                [ExternalEditorName] = NotepadPlusPlusExePath,
                ["Default font"] = "Courier New",
                ["Language"] = "English",
            }
        };

        // Tools → External Timers (company export timers-server.json).
        Services.RunScoreExternalTimers.ApplyCompanyDefaults(profile);
        return profile;
    }
}
