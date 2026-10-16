using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class PathService
{
    public const string RunScoreFolderName = "RUNSCORE";

    /// <summary>Hidden app metadata under each session (locks, history, finish backups).</summary>
    public const string AppMetaFolderName = "._checkin";

    /// <summary>
    /// Only these Library root folders are offered as offices.
    /// (Hides Templates, Attachments, and other OneDrive clutter.)
    /// </summary>
    public static readonly string[] AllowedOfficeFolders =
    [
        "Atlanta",
        "Charlotte",
        "Memphis",
        "Northwest Arkansas",
        "St. Louis",
        "Past Races",
        "Test Office" // temporary — sandbox events on OneDrive
    ];

    private static readonly HashSet<string> AllowedOfficeSet =
        new(AllowedOfficeFolders, StringComparer.OrdinalIgnoreCase);

    public string GetActivePath(AppSettings settings, string office, string relativePath) =>
        Path.Combine(settings.RaceDayRoot, "Active", office, relativePath);

    public string GetBackupPath(AppSettings settings, string office, string relativePath) =>
        Path.Combine(settings.RaceDayRoot, "Backup", office, relativePath);

    public string GetAutosavePath(string librarySessionPath) =>
        Path.Combine(librarySessionPath, RunScoreFolderName, "_autosave");

    public string GetAppMetaFolder(string librarySessionPath) =>
        Path.Combine(librarySessionPath, AppMetaFolderName);

    public string GetLockPath(string librarySessionPath) =>
        Path.Combine(GetAppMetaFolder(librarySessionPath), "_checkout.json");

    public string GetLockHistoryPath(string librarySessionPath) =>
        Path.Combine(GetAppMetaFolder(librarySessionPath), "_checkout_history.jsonl");

    /// <summary>Pre-v0.2 location (session root). Still read for migration.</summary>
    public string GetLegacyLockPath(string librarySessionPath) =>
        Path.Combine(librarySessionPath, "_checkout.json");

    /// <summary>Pre-v0.2 location (session root). Still read for migration.</summary>
    public string GetLegacyLockHistoryPath(string librarySessionPath) =>
        Path.Combine(librarySessionPath, "_checkout_history.jsonl");

    public string GetCheckinBackupPath(string librarySessionPath, DateTimeOffset at) =>
        Path.Combine(GetAppMetaFolder(librarySessionPath), "backup", at.ToString("yyyyMMdd_HHmmss"));

    public string GetDbsPath(string librarySessionPath) =>
        Path.Combine(librarySessionPath, "DB's");

    public bool IsAllowedOfficeFolder(string name) => AllowedOfficeSet.Contains(name);

    public bool IsSessionLeaf(string directoryPath) =>
        Directory.Exists(Path.Combine(directoryPath, RunScoreFolderName));

    /// <summary>Office + path relative to office for a session leaf under LibraryRoot.</summary>
    public (string Office, string RelativePath)? TryParseSessionRelative(AppSettings settings, string librarySessionPath)
    {
        var root = Path.GetFullPath(settings.LibraryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(librarySessionPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        var relative = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        var office = parts[0];
        var relativePath = string.Join(Path.DirectorySeparatorChar.ToString(), parts.Skip(1));
        return (office, relativePath);
    }
}
