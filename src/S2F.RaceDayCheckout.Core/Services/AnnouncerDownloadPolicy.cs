namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Slim announcer Graph pull: the assigned announcer .event at the session root,
/// plus files under DB's. Skips RUNSCORE, Pre-Race, other .event files, _autosave, and ._checkin.
/// </summary>
public static class AnnouncerDownloadPolicy
{
    public static IReadOnlyList<string> SelectRelativePaths(
        IEnumerable<string> relativePaths,
        string? announcerEventFile)
    {
        var kept = new List<string>();
        foreach (var raw in relativePaths)
        {
            if (ShouldIncludeFile(raw, announcerEventFile))
                kept.Add(raw);
        }

        return kept;
    }

    public static bool ShouldIncludeFile(string relativePath, string? announcerEventFile)
    {
        var rel = NormalizeRelative(relativePath);
        if (string.IsNullOrEmpty(rel) || HasSkippedSegment(rel))
            return false;

        if (IsUnderDbs(rel))
            return true;

        // Session-root file only — never a nested .event.
        if (rel.Contains('/'))
            return false;

        var want = SessionRoleStore.NormalizeFileName(announcerEventFile);
        return want is not null && rel.Equals(want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for the session root and for DB's (so the children list can run).
    /// Every other folder is left in the cloud.
    /// </summary>
    public static bool ShouldRecurse(string relativeFolderPath)
    {
        var rel = NormalizeRelative(relativeFolderPath);
        if (string.IsNullOrEmpty(rel))
            return true;
        if (HasSkippedSegment(rel))
            return false;
        return IsDbsRoot(rel) || IsUnderDbs(rel);
    }

    private static bool IsDbsRoot(string normalized) =>
        normalized.Equals(SharedFolderLinkService.DbsFolderName, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderDbs(string normalized) =>
        normalized.StartsWith(SharedFolderLinkService.DbsFolderName + "/", StringComparison.OrdinalIgnoreCase);

    private static bool HasSkippedSegment(string normalized)
    {
        foreach (var part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (GraphSyncPolicy.ShouldSkipFolder(part))
                return true;
            if (part.StartsWith("._", StringComparison.Ordinal))
                return true;
            if (string.Equals(part, "Pre-Race", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(part, PathService.RunScoreFolderName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeRelative(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        var rel = relativePath.Replace('\\', '/').Trim();
        while (rel.StartsWith("./", StringComparison.Ordinal))
            rel = rel[2..];
        rel = rel.TrimStart('/');
        if (rel is "." or "./")
            return string.Empty;
        return rel.TrimEnd('/');
    }
}
