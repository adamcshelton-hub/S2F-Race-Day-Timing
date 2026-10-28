using System.Text.Json;
using System.Text.Json.Serialization;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Category start times for one session pack. Lives in <c>._checkin/race-starts.json</c>
/// so Admin setup and Finish write-back share the same Graph-synced source of truth.
/// </summary>
public static class RaceStartsStore
{
    public const string FileName = "race-starts.json";

    /// <summary>Session-relative path for Finish upload planning.</summary>
    public static string RelativePath =>
        Path.Combine(PathService.AppMetaFolderName, FileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetPath(string librarySessionPath) =>
        Path.Combine(librarySessionPath, PathService.AppMetaFolderName, FileName);

    public static IReadOnlyList<RaceStartEntry> Load(string? librarySessionPath)
    {
        if (string.IsNullOrWhiteSpace(librarySessionPath))
            return Array.Empty<RaceStartEntry>();

        var path = GetPath(librarySessionPath);
        if (!File.Exists(path))
            return Array.Empty<RaceStartEntry>();

        try
        {
            var doc = JsonSerializer.Deserialize<RaceStartsDocument>(File.ReadAllText(path), JsonOptions);
            return Normalize(doc?.Starts);
        }
        catch
        {
            return Array.Empty<RaceStartEntry>();
        }
    }

    public static void Save(string librarySessionPath, IReadOnlyList<RaceStartEntry>? starts)
    {
        if (string.IsNullOrWhiteSpace(librarySessionPath))
            throw new ArgumentException("Session path required.", nameof(librarySessionPath));

        var folder = Path.Combine(librarySessionPath, PathService.AppMetaFolderName);
        Directory.CreateDirectory(folder);
        PathService.TryMarkHidden(folder);

        var normalized = Normalize(starts);
        var doc = new RaceStartsDocument { Starts = normalized.ToList() };
        File.WriteAllText(GetPath(librarySessionPath), JsonSerializer.Serialize(doc, JsonOptions));
    }

    public static IReadOnlyList<RaceStartEntry> Normalize(IEnumerable<RaceStartEntry>? starts)
    {
        if (starts is null)
            return Array.Empty<RaceStartEntry>();

        return starts
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Category))
            .Select(s => new RaceStartEntry
            {
                Category = s.Category.Trim(),
                StartTimeOfDay = StripToMinute(s.StartTimeOfDay),
                GunStartTimeOfDay = s.GunStartTimeOfDay is { } gun ? StripToCentisecond(gun) : null
            })
            .GroupBy(s => s.Category + "\0" + RaceStartClock.FormatStartHhMm(s.StartTimeOfDay), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(s => s.StartTimeOfDay)
            .ThenBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TimeSpan StripToMinute(TimeSpan tod)
    {
        var hours = (int)tod.TotalHours;
        var minutes = tod.Minutes;
        // Preserve end-of-day midnight (24:00); do not clamp to 23:00.
        if (hours == 24 && minutes == 0)
            return new TimeSpan(24, 0, 0);
        if (hours is < 0 or > 23)
            hours = Math.Clamp(hours, 0, 23);
        if (minutes is < 0 or > 59)
            minutes = Math.Clamp(minutes, 0, 59);
        return new TimeSpan(hours, minutes, 0);
    }

    /// <summary>Gun times keep HH:MM:SS.ff (centiseconds).</summary>
    private static TimeSpan StripToCentisecond(TimeSpan tod)
    {
        var hours = (int)tod.TotalHours;
        if (hours is < 0 or > 23)
            hours = Math.Clamp(hours, 0, 23);
        var minutes = Math.Clamp(tod.Minutes, 0, 59);
        var seconds = Math.Clamp(tod.Seconds, 0, 59);
        var ms = Math.Clamp((tod.Milliseconds / 10) * 10, 0, 990);
        return new TimeSpan(0, hours, minutes, seconds, ms);
    }

    private sealed class RaceStartsDocument
    {
        public List<RaceStartEntry> Starts { get; set; } = new();
    }
}
