using System.Globalization;
using System.Text.RegularExpressions;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Destination layout for archiving a live office item into Past Races.
/// Matches race-day browse: <c>Past Races/{Office} Past Races/{Year}/{Item}</c>.
/// </summary>
public static class PastRacesArchivePaths
{
    private static readonly Regex YearPrefix = new(
        @"^(?<year>\d{4})\s*-\s*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>OneDrive / Library bucket under Past Races for an office (e.g. Memphis Past Races).</summary>
    public static string OfficeBucketName(string officeName)
    {
        if (string.IsNullOrWhiteSpace(officeName))
            throw new ArgumentException("Office is required.", nameof(officeName));
        return officeName.Trim() + " Past Races";
    }

    /// <summary>
    /// Top-level folder under the live office to archive — flat race or whole Race Group.
    /// Never a single leaf under a group.
    /// </summary>
    public static string ResolveArchiveItemName(EventPackInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.ParentEvent))
            throw new ArgumentException("Session has no parent/event folder name.", nameof(session));
        return session.ParentEvent.Trim();
    }

    /// <summary>
    /// Year folder under the office Past Races bucket. Prefers <c>yyyy -</c> prefix on the item name;
    /// otherwise <paramref name="fallbackYear"/>.
    /// </summary>
    public static string ResolveArchiveYear(string itemFolderName, int? fallbackYear = null)
    {
        if (TryGetYearPrefix(itemFolderName, out var year))
            return year;
        var y = fallbackYear ?? DateTime.Now.Year;
        return y.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryGetYearPrefix(string? folderName, out string year)
    {
        year = string.Empty;
        if (string.IsNullOrWhiteSpace(folderName))
            return false;
        var m = YearPrefix.Match(folderName.Trim());
        if (!m.Success)
            return false;
        year = m.Groups["year"].Value;
        return true;
    }

    /// <summary>
    /// Library-relative path (forward slashes) of the destination parent year folder:
    /// <c>Past Races/{Office} Past Races/{Year}</c>.
    /// </summary>
    public static string DestinationYearRelative(string officeName, string year) =>
        string.Join(
            "/",
            EventBuilderService.PastRacesFolderName,
            OfficeBucketName(officeName),
            year.Trim());

    /// <summary>
    /// Library-relative path of the item after move:
    /// <c>Past Races/{Office} Past Races/{Year}/{Item}</c>.
    /// </summary>
    public static string DestinationItemRelative(string officeName, string year, string itemFolderName) =>
        DestinationYearRelative(officeName, year) + "/" + itemFolderName.Trim().Trim('/', '\\');

    /// <summary>True when <paramref name="librarySessionPath"/> is under the office item being archived.</summary>
    public static bool IsLibraryPathUnderArchiveItem(
        string libraryRoot,
        string officeName,
        string itemFolderName,
        string? librarySessionPath)
    {
        if (string.IsNullOrWhiteSpace(librarySessionPath))
            return false;

        var itemLocal = Path.GetFullPath(Path.Combine(libraryRoot, officeName, itemFolderName));
        var session = Path.GetFullPath(librarySessionPath);
        if (string.Equals(session, itemLocal, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = itemLocal.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return session.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
