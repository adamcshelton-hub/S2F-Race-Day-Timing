namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Local <c>C:\S2F\Library</c> vs OneDrive.
/// Staff move finished events into Past Races, and sometimes remove a whole office folder.
/// Prune only after a completed Graph check — never after a timeout or a partial list.
/// </summary>
public static class LibrarySessionCache
{
    /// <summary>Session leaf paths (folders that contain RUNSCORE) under an office.</summary>
    public static IEnumerable<string> EnumerateLocalSessionLeaves(string officeLocal)
    {
        if (!Directory.Exists(officeLocal))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(officeLocal, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, PathService.RunScoreFolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, SharedFolderLinkService.DbsFolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Pre-Race", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("._", StringComparison.Ordinal)
                || string.Equals(name, PathService.AutosaveFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (HasHiddenAppSegment(officeLocal, dir))
                continue;

            if (Directory.Exists(Path.Combine(dir, PathService.RunScoreFolderName)))
                yield return dir;
        }
    }

    /// <summary>
    /// Delete local allowed-office folders confirmed missing on a completed Graph probe.
    /// Skips the office that contains <paramref name="protectSessionPath"/> (active checkout).
    /// </summary>
    public static int PruneOfficesConfirmedAbsent(
        string libraryRoot,
        IReadOnlyCollection<string> confirmedAbsent,
        string? protectSessionPath = null)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot)
            || !Directory.Exists(libraryRoot)
            || confirmedAbsent.Count == 0)
            return 0;

        var protect = string.IsNullOrWhiteSpace(protectSessionPath)
            ? null
            : Path.GetFullPath(protectSessionPath);
        var removed = 0;

        foreach (var name in confirmedAbsent)
        {
            if (string.IsNullOrWhiteSpace(name)
                || !PathService.AllowedOfficeFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            var dir = Path.Combine(libraryRoot, name);
            if (!Directory.Exists(dir))
                continue;

            var full = Path.GetFullPath(dir);
            if (protect is not null && IsSameOrUnder(protect, full))
                continue;

            try
            {
                Directory.Delete(full, recursive: true);
                removed++;
            }
            catch
            {
                // Locked / in use — leave it on the office list until the next check.
            }
        }

        return removed;
    }

    private static bool IsSameOrUnder(string path, string folder)
    {
        if (path.Equals(folder, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Delete local session leaves (and empty parents) that are not in the completed remote set.
    /// Never call this with a partial remote list (e.g. after timeout) — that would wipe valid events.
    /// </summary>
    public static int PruneSessionsNotOnRemote(
        string officeLocal,
        IReadOnlySet<string> remoteRelativePaths,
        string? protectSessionPath = null)
    {
        if (!Directory.Exists(officeLocal) || remoteRelativePaths is null)
            return 0;

        var protect = string.IsNullOrWhiteSpace(protectSessionPath)
            ? null
            : Path.GetFullPath(protectSessionPath);
        var pruned = 0;

        foreach (var localSession in EnumerateLocalSessionLeaves(officeLocal).ToList())
        {
            var rel = Path.GetRelativePath(officeLocal, localSession).Replace('\\', '/');
            if (remoteRelativePaths.Contains(rel))
                continue;

            var full = Path.GetFullPath(localSession);
            if (protect is not null
                && full.Equals(protect, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                Directory.Delete(localSession, recursive: true);
                pruned++;
                RemoveEmptyParents(officeLocal, Path.GetDirectoryName(localSession));
            }
            catch
            {
                // Locked / in use — leave for next reconcile.
            }
        }

        return pruned;
    }

    /// <summary>
    /// Delete immediate child folders that are not in a completed one-level remote list.
    /// Does not walk deeper. Never call with a partial list.
    /// </summary>
    public static int PruneImmediateFoldersNotOnRemote(
        string folderLocal,
        IReadOnlyCollection<string> remoteChildNames,
        string? protectSessionPath = null)
    {
        if (!Directory.Exists(folderLocal) || remoteChildNames is null)
            return 0;

        var remote = new HashSet<string>(remoteChildNames, StringComparer.OrdinalIgnoreCase);
        var protect = string.IsNullOrWhiteSpace(protectSessionPath)
            ? null
            : Path.GetFullPath(protectSessionPath);
        var pruned = 0;

        foreach (var child in Directory.EnumerateDirectories(folderLocal).ToList())
        {
            var name = Path.GetFileName(child);
            if (string.IsNullOrEmpty(name) || remote.Contains(name))
                continue;

            var full = Path.GetFullPath(child);
            if (protect is not null && IsSameOrUnder(protect, full))
                continue;

            try
            {
                Directory.Delete(full, recursive: true);
                pruned++;
            }
            catch
            {
                // Locked / in use — leave it until the next list.
            }
        }

        return pruned;
    }

    private static bool HasHiddenAppSegment(string officePath, string directoryPath)
    {
        var relative = Path.GetRelativePath(officePath, directoryPath);
        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => p.StartsWith("._", StringComparison.Ordinal));
    }

    private static void RemoveEmptyParents(string officeLocal, string? start)
    {
        var officeFull = Path.GetFullPath(officeLocal);
        var current = start;
        while (!string.IsNullOrEmpty(current))
        {
            var full = Path.GetFullPath(current);
            if (full.Equals(officeFull, StringComparison.OrdinalIgnoreCase))
                break;
            if (!full.StartsWith(officeFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                break;

            try
            {
                if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                    Directory.Delete(full);
                else
                    break;
            }
            catch
            {
                break;
            }

            current = Path.GetDirectoryName(full);
        }
    }
}
