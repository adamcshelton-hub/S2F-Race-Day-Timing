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
        return new EventPackInfo
        {
            Office = office,
            ParentEvent = parent,
            SessionName = session,
            RelativePath = relative,
            Path = sessionPath,
            EventFiles = eventFiles,
            PrimaryEventFile = EventFileSelector.ChoosePrimary(eventFiles)
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
