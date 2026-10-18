namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Non-primary Timing &amp; Scoring .event files (announcer, video display, special callouts, …).
/// Primary comes from the sidecar when that file is in the list; otherwise <see cref="EventFileSelector.ChoosePrimary"/>.
/// </summary>
public static class SecondaryEventSelector
{
    public static string? ResolvePrimary(IReadOnlyList<string> eventFileNames, string? sidecarPrimary)
    {
        var files = Normalize(eventFileNames);
        if (files.Count == 0)
            return null;

        var assigned = SessionRoleStore.NormalizeFileName(sidecarPrimary);
        if (assigned is not null
            && files.Contains(assigned, StringComparer.OrdinalIgnoreCase))
            return files.First(f => f.Equals(assigned, StringComparison.OrdinalIgnoreCase));

        return EventFileSelector.ChoosePrimary(files);
    }

    public static IReadOnlyList<string> ListSecondaries(
        IReadOnlyList<string> eventFileNames,
        string? sidecarPrimary)
    {
        var files = Normalize(eventFileNames);
        var primary = ResolvePrimary(files, sidecarPrimary);
        if (primary is null)
            return Array.Empty<string>();

        return files
            .Where(f => !f.Equals(primary, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool HasSecondary(IReadOnlyList<string> eventFileNames, string? sidecarPrimary) =>
        ListSecondaries(eventFileNames, sidecarPrimary).Count > 0;

    private static List<string> Normalize(IEnumerable<string> eventFileNames) =>
        eventFileNames
            .Select(SessionRoleStore.NormalizeFileName)
            .Where(f => f is not null && f.EndsWith(".event", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
