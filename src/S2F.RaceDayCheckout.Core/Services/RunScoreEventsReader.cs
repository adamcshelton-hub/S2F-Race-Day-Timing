using System.Xml.Linq;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Reads scoring event names from the Active race's RunScore folder —
/// same names as Results on the Master Menu.
/// Prefers <c>Events.xml</c>; falls back to legacy <c>EVENTS</c> text file, then <c>*.FN2</c> stems.
/// </summary>
public static class RunScoreEventsReader
{
    public const string EventsXmlFileName = "Events.xml";
    public const string LegacyEventsFileName = "EVENTS";

    /// <summary>
    /// Returns event names in Results order when XML is readable; otherwise FN2 discovery order.
    /// </summary>
    public static IReadOnlyList<string> ListEventNames(string? runScoreFolderOrSessionPath)
    {
        var rsRoot = ResolveRunScoreFolder(runScoreFolderOrSessionPath);
        if (rsRoot is null)
            return Array.Empty<string>();

        foreach (var xmlPath in EnumerateEventsXmlFiles(rsRoot))
        {
            var names = TryReadXmlEventNames(xmlPath);
            if (names.Count > 0)
                return names;
        }

        foreach (var legacyPath in EnumerateLegacyEventsFiles(rsRoot))
        {
            var names = TryReadLegacyEventsFile(legacyPath);
            if (names.Count > 0)
                return names;
        }

        return ListEventNamesFromFn2(rsRoot);
    }

    /// <summary>Prefer Active session; fall back to Library copy of the race.</summary>
    public static IReadOnlyList<string> ListEventNamesFromSession(
        string? activeSessionPath,
        string? librarySessionPath = null)
    {
        var names = ListEventNames(activeSessionPath);
        if (names.Count > 0)
            return names;
        return ListEventNames(librarySessionPath);
    }

    public static string? ResolveEventsXmlPath(string? runScoreFolderOrSessionPath)
    {
        var rsRoot = ResolveRunScoreFolder(runScoreFolderOrSessionPath);
        return rsRoot is null ? null : EnumerateEventsXmlFiles(rsRoot).FirstOrDefault();
    }

    private static string? ResolveRunScoreFolder(string? runScoreFolderOrSessionPath)
    {
        if (string.IsNullOrWhiteSpace(runScoreFolderOrSessionPath))
            return null;

        var root = runScoreFolderOrSessionPath.Trim();
        if (!Directory.Exists(root))
            return null;

        // Prefer …\RUNSCORE when the caller passed the session folder.
        var under = Path.Combine(root, PathService.RunScoreFolderName);
        if (Directory.Exists(under))
            return under;

        // Caller already passed the RUNSCORE folder.
        if (string.Equals(Path.GetFileName(root), PathService.RunScoreFolderName, StringComparison.OrdinalIgnoreCase))
            return root;

        return root;
    }

    private static IEnumerable<string> EnumerateEventsXmlFiles(string rsRoot)
    {
        string[] files;
        try
        {
            // Matches Events.xml / events.xml; does not match the Events\ directory.
            files = Directory.GetFiles(rsRoot, "Events.xml");
        }
        catch
        {
            yield break;
        }

        foreach (var f in files)
            yield return f;
    }

    private static IEnumerable<string> EnumerateLegacyEventsFiles(string rsRoot)
    {
        // Legacy plain file named EVENTS (no extension). Skip if it's a directory (RS uses Events\ for logs).
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(rsRoot, "EVENTS");
        }
        catch
        {
            yield break;
        }

        foreach (var f in candidates)
        {
            if (Directory.Exists(f))
                continue;
            yield return f;
        }
    }

    private static IReadOnlyList<string> ListEventNamesFromFn2(string rsRoot)
    {
        try
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(rsRoot, "*.FN2")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file)?.Trim() ?? "";
                if (name.Length == 0 || !seen.Add(name))
                    continue;
                names.Add(name);
            }

            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> TryReadXmlEventNames(string xmlPath)
    {
        try
        {
            // RunScore often keeps Events.xml open — need ReadWrite share.
            using var stream = new FileStream(
                xmlPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return ParseXmlEventNames(XDocument.Load(stream));
        }
        catch
        {
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), "s2f-events-" + Guid.NewGuid().ToString("N") + ".xml");
                File.Copy(xmlPath, tmp, overwrite: true);
                try
                {
                    using var stream = File.OpenRead(tmp);
                    return ParseXmlEventNames(XDocument.Load(stream));
                }
                finally
                {
                    try { File.Delete(tmp); } catch { /* ignore */ }
                }
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    private static IReadOnlyList<string> ParseXmlEventNames(XDocument doc)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in doc.Descendants().Where(e =>
                     e.Name.LocalName.Equals("event", StringComparison.OrdinalIgnoreCase)))
        {
            var name = (el.Attribute("name")?.Value ?? "").Trim();
            if (name.Length == 0 || !seen.Add(name))
                continue;
            names.Add(name);
        }

        return names;
    }

    private static IReadOnlyList<string> TryReadLegacyEventsFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.ReadLine() is { } line)
            {
                var name = line.Trim();
                if (name.Length == 0 || name.StartsWith('#') || name.StartsWith(';'))
                    continue;
                // Some legacy lines are "Name,type" or just "Name".
                var comma = name.IndexOf(',');
                if (comma > 0)
                    name = name[..comma].Trim();
                if (name.Length == 0 || !seen.Add(name))
                    continue;
                names.Add(name);
            }

            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
