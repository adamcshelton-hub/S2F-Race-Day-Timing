using System.Text;
using System.Text.Json;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// JSONL helpers for Manual Backup <c>current.jsonl</c> Graph RMW (Staffing-compatible).
/// </summary>
public static class ManualBackupJsonl
{
    private static readonly JsonSerializerOptions LineOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null
    };

    public static List<ManualBackupTap> Parse(string? text)
    {
        var list = new List<ManualBackupTap>();
        if (string.IsNullOrWhiteSpace(text))
            return list;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var tap = JsonSerializer.Deserialize<ManualBackupTap>(line, LineOpts);
                if (tap is not null && !string.IsNullOrWhiteSpace(tap.Id))
                    list.Add(tap);
            }
            catch
            {
                /* skip bad line */
            }
        }

        return list;
    }

    public static string Serialize(IEnumerable<ManualBackupTap> taps)
    {
        var sb = new StringBuilder();
        foreach (var t in taps)
            sb.Append(JsonSerializer.Serialize(t, LineOpts)).Append('\n');
        return sb.ToString();
    }

    public static bool TapsHaveIds(IReadOnlyList<ManualBackupTap> taps, IEnumerable<string> ids)
    {
        var have = new HashSet<string>(
            taps.Select(t => t.Id),
            StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || !have.Contains(id))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Append incoming taps whose ids are not already present (idempotent Graph merge).
    /// </summary>
    public static List<ManualBackupTap> MergeIncoming(
        IReadOnlyList<ManualBackupTap> existing,
        IReadOnlyList<ManualBackupTap> incoming,
        out List<ManualBackupTap> added)
    {
        var merged = existing.ToList();
        var have = new HashSet<string>(merged.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        added = new List<ManualBackupTap>();
        foreach (var tap in incoming)
        {
            if (string.IsNullOrWhiteSpace(tap.Id) || string.IsNullOrWhiteSpace(tap.Bib))
                continue;
            if (have.Contains(tap.Id))
                continue;
            have.Add(tap.Id);
            merged.Add(tap);
            added.Add(tap);
        }

        return merged;
    }
}
