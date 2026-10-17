using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Read/write the three per-event fields staff always set in RUNSCORE\Entries.ini.
/// Does not touch [control] blocks or other static SOP lines.
/// </summary>
public static class EntriesIniService
{
    public const string KeyRaceName = "race name";
    public const string KeyRaceDate = "race date";
    public const string KeyResultsId = "%resultsid%";

    /// <summary>RunScore custom variables place the value at character index 20 (1-based column 21).</summary>
    public const int ValueColumnIndex = 20;

    public sealed record Fields(string RaceName, string RaceDate, string ResultsId);

    public static string GetEntriesIniPath(string sessionPath) =>
        Path.Combine(sessionPath, PathService.RunScoreFolderName, "Entries.ini");

    public static bool TryFindEntriesIni(string sessionPath, out string path)
    {
        path = GetEntriesIniPath(sessionPath);
        if (File.Exists(path))
            return true;

        // Template / prior packs sometimes use Entries.INI
        var alt = Path.Combine(sessionPath, PathService.RunScoreFolderName, "Entries.INI");
        if (File.Exists(alt))
        {
            path = alt;
            return true;
        }

        path = string.Empty;
        return false;
    }

    public static Fields Read(string entriesIniPath)
    {
        var text = File.ReadAllText(entriesIniPath);
        return new Fields(
            ReadKey(text, KeyRaceName),
            ReadKey(text, KeyRaceDate),
            ReadKey(text, KeyResultsId));
    }

    public static void Write(string entriesIniPath, Fields fields)
    {
        var text = File.Exists(entriesIniPath) ? File.ReadAllText(entriesIniPath) : string.Empty;
        text = UpsertKey(text, KeyRaceName, fields.RaceName.Trim());
        text = UpsertKey(text, KeyRaceDate, NormalizeRaceDate(fields.RaceDate));
        text = UpsertKey(text, KeyResultsId, NormalizeResultsId(fields.ResultsId));
        var dir = Path.GetDirectoryName(entriesIniPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(entriesIniPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Accept bare id or Race Roster URL; return the event id segment only.
    /// </summary>
    public static string NormalizeResultsId(string? input)
    {
        var s = (input ?? string.Empty).Trim();
        if (s.Length == 0)
            return string.Empty;

        // …/v3/events/{id} or …/v3/events/{id}/…
        var v3 = Regex.Match(s, @"/v3/events/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
        if (v3.Success)
            return v3.Groups[1].Value;

        // …/results/{id}/… (older / results paths)
        var results = Regex.Match(s, @"raceroster\.com/results/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
        if (results.Success)
            return results.Groups[1].Value;

        // Bare id (no slashes/spaces)
        if (!s.Contains('/') && !s.Contains(' ') && !s.Contains('?'))
            return s;

        // Last path segment that looks like an id
        try
        {
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri))
            {
                var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                    return parts[^1];
            }
        }
        catch
        {
            // fall through
        }

        return s;
    }

    /// <summary>Normalize to MM/DD/YYYY when parseable; otherwise trim as entered.</summary>
    public static string NormalizeRaceDate(string? input)
    {
        var s = (input ?? string.Empty).Trim();
        if (s.Length == 0)
            return string.Empty;

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            || DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt))
            return dt.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);

        return s;
    }

    /// <summary>Library session folder name: "yyyy - {race name}".</summary>
    public static string BuildSessionFolderName(string raceName, string raceDate)
    {
        var name = (raceName ?? string.Empty).Trim();
        if (name.Length == 0)
            throw new ArgumentException("Race name is required.", nameof(raceName));

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        var year = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
        var normalized = NormalizeRaceDate(raceDate);
        if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            year = dt.Year.ToString(CultureInfo.InvariantCulture);

        return year + " - " + name;
    }

    public static string ReadKey(string fileText, string key)
    {
        foreach (var line in SplitLines(fileText))
        {
            if (TryParseKeyedLine(line, key, out var value))
                return value;
        }

        return string.Empty;
    }

    public static string UpsertKey(string fileText, string key, string value)
    {
        var lines = SplitLines(fileText).ToList();
        var formatted = FormatKeyedLine(key, value);
        for (var i = 0; i < lines.Count; i++)
        {
            if (TryParseKeyedLine(lines[i], key, out _))
            {
                lines[i] = formatted;
                return JoinLines(lines, fileText);
            }
        }

        // Insert near top: after other race keys if present, else at start of file.
        var insertAt = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (TryParseKeyedLine(lines[i], KeyRaceName, out _)
                || TryParseKeyedLine(lines[i], KeyRaceDate, out _)
                || TryParseKeyedLine(lines[i], KeyResultsId, out _)
                || lines[i].StartsWith("Date format", StringComparison.OrdinalIgnoreCase))
                insertAt = i + 1;
        }

        lines.Insert(insertAt, formatted);
        return JoinLines(lines, fileText);
    }

    public static string FormatKeyedLine(string key, string value)
    {
        // Real S2F Templates / Past Races use spaces (not dots) to column 21.
        // RunScore rejects "race name...........value" as Invalid command.
        if (key.Length >= ValueColumnIndex)
            return key + " " + value;

        var sb = new StringBuilder(key);
        while (sb.Length < ValueColumnIndex)
            sb.Append(' ');
        sb.Append(value);
        return sb.ToString();
    }

    private static bool TryParseKeyedLine(string line, string key, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(line))
            return false;

        // Ignore section headers
        if (line.TrimStart().StartsWith("[", StringComparison.Ordinal))
            return false;

        if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = line[key.Length..];
        // Must be end, dots, or spaces then value — not another longer key prefix.
        if (rest.Length == 0)
            return true;
        if (rest[0] is not ('.' or ' ' or '\t'))
            return false;

        var i = 0;
        while (i < rest.Length && (rest[i] == '.' || rest[i] == ' ' || rest[i] == '\t'))
            i++;
        value = rest[i..].Trim();
        return true;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }

    private static string JoinLines(IReadOnlyList<string> lines, string original)
    {
        var nl = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var body = string.Join(nl, lines);
        if (original.EndsWith("\n", StringComparison.Ordinal) || original.EndsWith("\r", StringComparison.Ordinal))
            return body + nl;
        return body;
    }
}
