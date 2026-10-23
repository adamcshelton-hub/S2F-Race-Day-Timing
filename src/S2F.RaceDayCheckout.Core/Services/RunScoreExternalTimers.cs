using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Company External Timers (RunScore Tools → External Timers) → HKCU RunScore registry.
/// Source: RunScore export format <c>runscore-external-timers</c> (timers-server.json).
/// Export <c>index</c> 0 maps to registry suffix <c>_1</c>.
/// </summary>
public static class RunScoreExternalTimers
{
    /// <summary>One External Timers row from the RunScore JSON export.</summary>
    public readonly record struct Row(
        int Index,
        string DeviceCode,
        int Port,
        string Lane,
        int Baud,
        bool Start,
        bool Tcp,
        bool AdjustDay,
        string ServerIp);

    /// <summary>
    /// Start2Finish default rows (MyLaps BibTag, ChronoTrack, iPico Sport; slots 4–8 empty Time Machine).
    /// </summary>
    public static IReadOnlyList<Row> CompanyDefaults { get; } = new Row[]
    {
        new(0, "MYLAPS_BIBTAG", 3097, "*", 0, Start: true, Tcp: true, AdjustDay: false, ServerIp: ""),
        new(1, "CHRONOTRACK", 61611, "*", 0, Start: false, Tcp: true, AdjustDay: false, ServerIp: ""),
        new(2, "IPICO_SPORT", 10000, "1", 0, Start: false, Tcp: true, AdjustDay: false, ServerIp: "192.168.8.199"),
        new(3, "TIMEMACH", 0, "*", 0, Start: false, Tcp: false, AdjustDay: false, ServerIp: ""),
        new(4, "TIMEMACH", 0, "*", 0, Start: false, Tcp: false, AdjustDay: false, ServerIp: ""),
        new(5, "TIMEMACH", 0, "*", 0, Start: false, Tcp: false, AdjustDay: false, ServerIp: ""),
        new(6, "TIMEMACH", 0, "*", 0, Start: false, Tcp: false, AdjustDay: false, ServerIp: ""),
        new(7, "TIMEMACH", 0, "*", 0, Start: false, Tcp: false, AdjustDay: false, ServerIp: ""),
    };

    public static void ApplyCompanyDefaults(RunScoreSopProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var row in CompanyDefaults)
            ApplyRow(profile, row);
    }

    /// <summary>
    /// Fill External Timers keys missing from an older SOP file. Does not overwrite keys already present.
    /// </summary>
    public static bool MergeMissingCompanyDefaults(RunScoreSopProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Strings.ContainsKey("Timer_1"))
            return false;

        ApplyCompanyDefaults(profile);
        return true;
    }

    public static IReadOnlyList<SopPreference> BuildPrefs(RunScoreSopProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var list = new List<SopPreference>();
        for (var i = 1; i <= 8; i++)
        {
            var suffix = "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (profile.Strings.TryGetValue("Timer" + suffix, out var device))
                list.Add(SopPreference.String("Timer" + suffix, device));
            if (profile.Ints.TryGetValue("COM" + suffix, out var com))
                list.Add(SopPreference.Int("COM" + suffix, com));
            if (profile.Ints.TryGetValue("Baud" + suffix, out var baud))
                list.Add(SopPreference.Int("Baud" + suffix, baud));
            if (profile.Strings.TryGetValue("Lane" + suffix, out var lane))
                list.Add(SopPreference.String("Lane" + suffix, lane));
            if (profile.Strings.TryGetValue("TCP server address" + suffix, out var addr))
                list.Add(SopPreference.String("TCP server address" + suffix, addr));
            if (profile.Bools.TryGetValue("TCP" + suffix, out var tcp))
                list.Add(SopPreference.Bool("TCP" + suffix, tcp));
            if (profile.Bools.TryGetValue("ETS" + suffix, out var ets))
                list.Add(SopPreference.Bool("ETS" + suffix, ets));
            if (profile.Bools.TryGetValue("ADJUST_DAY" + suffix, out var adj))
                list.Add(SopPreference.Bool("ADJUST_DAY" + suffix, adj));
        }

        return list;
    }

    public static bool IsExternalTimerPrefName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Timer_1 … Timer_8 (not the unsuffixed "Timer" default).
        if (name.StartsWith("Timer_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("COM_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("Baud_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("Lane_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("TCP server address_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("TCP_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("ETS_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("ADJUST_DAY_", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static void ApplyRow(RunScoreSopProfile profile, Row row)
    {
        var n = row.Index + 1;
        var suffix = "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        profile.Strings["Timer" + suffix] = row.DeviceCode;
        profile.Ints["COM" + suffix] = row.Port;
        profile.Ints["Baud" + suffix] = row.Baud;
        profile.Strings["Lane" + suffix] = row.Lane ?? "*";
        profile.Strings["TCP server address" + suffix] = row.ServerIp ?? string.Empty;
        profile.Bools["TCP" + suffix] = row.Tcp;
        profile.Bools["ETS" + suffix] = row.Start;
        profile.Bools["ADJUST_DAY" + suffix] = row.AdjustDay;
    }
}
