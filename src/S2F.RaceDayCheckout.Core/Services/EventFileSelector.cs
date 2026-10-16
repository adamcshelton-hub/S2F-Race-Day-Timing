namespace S2F.RaceDayCheckout.Core.Services;

public static class EventFileSelector
{
    /// <summary>
    /// Prefer the "base" timing .event — shortest name, or without -S2F- / machine-style suffixes.
    /// </summary>
    public static string? ChoosePrimary(IEnumerable<string> eventFileNames)
    {
        var files = eventFileNames
            .Where(f => f.EndsWith(".event", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
            return null;
        if (files.Count == 1)
            return files[0];

        var nonSecondary = files
            .Where(f => !LooksSecondary(f))
            .OrderBy(f => f.Length)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (nonSecondary.Count > 0)
            return nonSecondary[0];

        return files.OrderBy(f => f.Length).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).First();
    }

    public static bool LooksSecondary(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.Contains("-S2F-", StringComparison.OrdinalIgnoreCase)
               || name.Contains("_S2F_", StringComparison.OrdinalIgnoreCase)
               || name.Contains("announcer", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> ListEventFiles(string sessionPath)
    {
        if (!Directory.Exists(sessionPath))
            return Array.Empty<string>();
        return Directory.GetFiles(sessionPath, "*.event")
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .Cast<string>()
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
