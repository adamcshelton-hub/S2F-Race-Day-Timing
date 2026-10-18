using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class LibraryScanner
{
    private readonly PathService _paths;
    private readonly CheckoutLockService _locks;

    public LibraryScanner(PathService paths, CheckoutLockService locks)
    {
        _paths = paths;
        _locks = locks;
    }

    public IReadOnlyList<OfficeInfo> ListOffices(string libraryRoot)
    {
        if (!Directory.Exists(libraryRoot))
            return Array.Empty<OfficeInfo>();

        var found = Directory.GetDirectories(libraryRoot)
            .Select(path => new DirectoryInfo(path))
            .Where(d => _paths.IsAllowedOfficeFolder(d.Name))
            .ToDictionary(d => d.Name, d => d, StringComparer.OrdinalIgnoreCase);

        // Keep the canonical office order (Atlanta → … → Test Office), skip missing folders.
        return PathService.AllowedOfficeFolders
            .Where(name => found.ContainsKey(name))
            .Select(name =>
            {
                var d = found[name];
                return new OfficeInfo { Name = d.Name, Path = d.FullName };
            })
            .ToList();
    }

    /// <summary>All session leaves (folders containing RUNSCORE) under the library.</summary>
    public IReadOnlyList<EventPackInfo> ListSessions(string libraryRoot, string? officeFilter = null, string? search = null)
    {
        var offices = ListOffices(libraryRoot);
        if (!string.IsNullOrWhiteSpace(officeFilter))
            offices = offices.Where(o => o.Name.Equals(officeFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var results = new List<EventPackInfo>();
        foreach (var office in offices)
        {
            foreach (var sessionPath in FindSessionLeaves(office.Path))
            {
                var pack = BuildPack(office.Name, office.Path, sessionPath);
                if (!MatchesSearch(pack, search))
                    continue;
                pack.Lock = _locks.TryRead(sessionPath);
                results.Add(pack);
            }
        }

        return results
            .OrderBy(e => e.Office, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(e => e.ParentEvent, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.SessionName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Past Races drill-down: immediate child folders that are not session leaves,
    /// plus session leaves directly under <paramref name="folderPath"/> (not deep).
    /// </summary>
    public IReadOnlyList<EventPackInfo> ListBrowserEntries(
        string libraryRoot,
        string officeName,
        string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return Array.Empty<EventPackInfo>();

        var officeRoot = Path.Combine(libraryRoot, officeName);
        var results = new List<EventPackInfo>();

        foreach (var sessionPath in FindSessionLeavesShallow(folderPath))
        {
            var pack = BuildPack(officeName, officeRoot, sessionPath);
            pack.Lock = _locks.TryRead(sessionPath);
            results.Add(pack);
        }

        var sessionSet = new HashSet<string>(
            results.Select(r => r.Path),
            StringComparer.OrdinalIgnoreCase);

        foreach (var child in ListImmediateChildFolders(folderPath))
        {
            if (sessionSet.Contains(child))
                continue;
            if (_paths.IsSessionLeaf(child))
                continue;

            var name = Path.GetFileName(child);
            var relative = Path.GetRelativePath(officeRoot, child);
            results.Add(new EventPackInfo
            {
                Office = officeName,
                ParentEvent = name,
                SessionName = name,
                RelativePath = relative,
                Path = child,
                IsNavigationFolder = true
            });
        }

        return results
            .OrderByDescending(e => e.SessionName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Immediate child directories, skipping RUNSCORE / DB's / Pre-Race / ._ / _autosave.</summary>
    public static IReadOnlyList<string> ListImmediateChildFolders(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return Array.Empty<string>();

        return Directory.GetDirectories(folderPath)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return !string.Equals(name, PathService.RunScoreFolderName, StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(name, "DB's", StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(name, "Pre-Race", StringComparison.OrdinalIgnoreCase)
                       && !name.StartsWith("._", StringComparison.Ordinal)
                       && !string.Equals(name, PathService.AutosaveFolderName, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Session leaves whose parent is exactly <paramref name="folderPath"/> (not deeper).</summary>
    public IEnumerable<string> FindSessionLeavesShallow(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            yield break;

        foreach (var child in Directory.GetDirectories(folderPath))
        {
            var name = Path.GetFileName(child);
            if (string.Equals(name, PathService.RunScoreFolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "DB's", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Pre-Race", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("._", StringComparison.Ordinal))
                continue;

            if (_paths.IsSessionLeaf(child))
                yield return child;
        }
    }

    /// <summary>Backward-compatible alias.</summary>
    public IReadOnlyList<EventPackInfo> ListEvents(string libraryRoot, string? officeFilter = null, string? search = null) =>
        ListSessions(libraryRoot, officeFilter, search);

    private IEnumerable<string> FindSessionLeaves(string officePath)
    {
        if (!Directory.Exists(officePath))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(officePath, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, PathService.RunScoreFolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "DB's", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Pre-Race", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("._", StringComparison.Ordinal))
                continue;

            // App meta lives at …/._checkin/… (locks + finish backups with RUNSCORE).
            // Timestamp folders are not named "._*", so skip any path under a "._" segment.
            if (HasHiddenAppSegment(officePath, dir))
                continue;

            if (_paths.IsSessionLeaf(dir))
                yield return dir;
        }
    }

    private static bool HasHiddenAppSegment(string officePath, string directoryPath)
    {
        var relative = Path.GetRelativePath(officePath, directoryPath);
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => p.StartsWith("._", StringComparison.Ordinal));
    }

    private static EventPackInfo BuildPack(string office, string officePath, string sessionPath)
    {
        var relative = Path.GetRelativePath(officePath, sessionPath);
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var parent = parts[0];
        var session = parts[^1];
        var eventFiles = EventFileSelector.ListEventFiles(sessionPath);
        var roles = SessionRoleStore.TryLoad(sessionPath);
        var primary = !string.IsNullOrWhiteSpace(roles?.PrimaryEventFile)
            ? roles!.PrimaryEventFile
            : EventFileSelector.ChoosePrimary(eventFiles);
        return new EventPackInfo
        {
            Office = office,
            ParentEvent = parent,
            SessionName = session,
            RelativePath = relative,
            Path = sessionPath,
            EventFiles = eventFiles,
            PrimaryEventFile = primary,
            AnnouncerEventFile = roles?.AnnouncerEventFile
        };
    }

    private static bool MatchesSearch(EventPackInfo pack, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        return pack.ParentEvent.Contains(search, StringComparison.OrdinalIgnoreCase)
               || pack.SessionName.Contains(search, StringComparison.OrdinalIgnoreCase)
               || pack.Office.Contains(search, StringComparison.OrdinalIgnoreCase)
               || pack.EventFiles.Any(f => f.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}
