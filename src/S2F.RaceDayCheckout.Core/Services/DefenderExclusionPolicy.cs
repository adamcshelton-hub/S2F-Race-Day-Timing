using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// First-install Windows Defender path exclusions for local Race Day folders.
/// Not applied on checkout; not re-offered on routine updates once configured.
/// </summary>
public static class DefenderExclusionPolicy
{
    public const string ActiveFolderName = "Active";
    public const string BackupFolderName = "Backup";

    public static bool NeedsOffer(AppSettings settings) =>
        !settings.DefenderExclusionsConfigured;

    /// <summary>Record that the user was offered (Yes or No) — never nag again.</summary>
    public static void MarkConfigured(AppSettings settings) =>
        settings.DefenderExclusionsConfigured = true;

    /// <summary>Record that Add-MpPreference succeeded.</summary>
    public static void MarkApplied(AppSettings settings) =>
        settings.DefenderExclusionsApplied = true;

    public static string GetActiveRoot(string raceDayRoot) =>
        Path.Combine(raceDayRoot, ActiveFolderName);

    public static string GetBackupRoot(string raceDayRoot) =>
        Path.Combine(raceDayRoot, BackupFolderName);

    /// <summary>Create RaceDay Active/Backup roots (idempotent).</summary>
    public static void EnsureRaceDayFolders(string raceDayRoot)
    {
        if (string.IsNullOrWhiteSpace(raceDayRoot))
            return;
        Directory.CreateDirectory(GetActiveRoot(raceDayRoot));
        Directory.CreateDirectory(GetBackupRoot(raceDayRoot));
    }

    public static IReadOnlyList<string> ExclusionPaths(string raceDayRoot) =>
    [
        GetActiveRoot(raceDayRoot),
        GetBackupRoot(raceDayRoot)
    ];
}
