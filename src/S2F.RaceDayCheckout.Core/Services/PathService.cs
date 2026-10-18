using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class PathService
{
    public const string RunScoreFolderName = "RUNSCORE";

    /// <summary>Under RaceDayRoot — RunScore alternate-save target while checked out.</summary>
    public const string BackupFolderName = "Backup";

    /// <summary>OneDrive folder under RUNSCORE where live Backup mirrors land for mid-race failover.</summary>
    public const string AutosaveFolderName = "_autosave";

    /// <summary>Leftover rename target when OneDrive blocks deleting <see cref="AutosaveFolderName"/>.</summary>
    public const string AutosaveTombstoneSuffix = ".~delete";

    /// <summary>Hidden app metadata under each session (locks, history, finish backups).</summary>
    public const string AppMetaFolderName = "._checkin";

    /// <summary>
    /// Only these Library root folders are offered as offices.
    /// Matches folders under OneDrive <c>Start 2 - Personal</c>.
    /// Templates stays excluded (New Event source, not a checkout office).
    /// <c>Other</c> is intentionally excluded — OneDrive-only storage outside this app.
    /// </summary>
    public static readonly string[] AllowedOfficeFolders =
    [
        "Atlanta",
        "Charlotte",
        "Memphis",
        "Northwest Arkansas",
        "St. Louis",
        // Always last on race-day + Admin office lists (not alpha with live offices).
        "Past Races",
        "Test Office"
    ];

    private static readonly HashSet<string> AllowedOfficeSet =
        new(AllowedOfficeFolders, StringComparer.OrdinalIgnoreCase);

    public string GetActivePath(AppSettings settings, string office, string relativePath) =>
        Path.Combine(settings.RaceDayRoot, "Active", office, relativePath);

    public string GetBackupPath(AppSettings settings, string office, string relativePath) =>
        Path.Combine(settings.RaceDayRoot, "Backup", office, relativePath);

    public string GetAutosavePath(string librarySessionPath) =>
        Path.Combine(librarySessionPath, RunScoreFolderName, AutosaveFolderName);

    public string GetAppMetaFolder(string librarySessionPath) =>
        Path.Combine(librarySessionPath, AppMetaFolderName);

    /// <summary>
    /// Create <see cref="AppMetaFolderName"/> if needed and mark it Hidden.
    /// (A leading "." does not hide folders in Windows Explorer.)
    /// </summary>
    public string EnsureAppMetaFolder(string librarySessionPath)
    {
        var folder = GetAppMetaFolder(librarySessionPath);
        Directory.CreateDirectory(folder);
        TryMarkHidden(folder);
        return folder;
    }

    public static void TryMarkHidden(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path))
                return;
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Hidden) == 0)
                File.SetAttributes(path, attrs | FileAttributes.Hidden);
        }
        catch
        {
            // OneDrive / permissions — folder still works if visible.
        }
    }

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
