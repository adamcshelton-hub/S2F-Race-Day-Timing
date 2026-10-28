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

    /// <summary>RunScore Open device code (registry / export). Wire prefixes are RSBI/RSBCI/RSCI.</summary>
    public const string RunScoreOpenDeviceCode = "RSBCOI";

    /// <summary>Company TCP port for Manual Backup → RunScore Open (Event *).</summary>
    public const int RunScoreOpenPort = 3100;

    /// <summary>Export index for the RunScore Open slot (registry <c>Timer_4</c>).</summary>
    public const int RunScoreOpenSlotIndex = 3;

    /// <summary>
    /// Start2Finish default rows (MyLaps BibTag, ChronoTrack, iPico Sport, RunScore Open; slots 5–8 empty Time Machine).
    /// </summary>
    public static IReadOnlyList<Row> CompanyDefaults { get; } = new Row[]
    {
        new(0, "MYLAPS_BIBTAG", 3097, "*", 0, Start: true, Tcp: true, AdjustDay: false, ServerIp: ""),
        new(1, "CHRONOTRACK", 61611, "*", 0, Start: false, Tcp: true, AdjustDay: false, ServerIp: ""),
        new(2, "IPICO_SPORT", 10000, "1", 0, Start: false, Tcp: true, AdjustDay: false, ServerIp: "192.168.8.199"),
        new(3, RunScoreOpenDeviceCode, RunScoreOpenPort, "*", 0, Start: true, Tcp: true, AdjustDay: false, ServerIp: ""),
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
    /// Fill External Timers keys missing from an older SOP file. Does not overwrite keys already present,
    /// except upgrading an empty Time Machine slot 4 to RunScore Open when that company default is missing.
    /// </summary>
    public static bool MergeMissingCompanyDefaults(RunScoreSopProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Strings.ContainsKey("Timer_1"))
        {
            ApplyCompanyDefaults(profile);
            return true;
        }

        return EnsureRunScoreOpenSlot(profile);
    }

    /// <summary>
    /// If slot 4 is still an unused Time Machine (port 0), replace it with company RunScore Open defaults.
    /// </summary>
    public static bool EnsureRunScoreOpenSlot(RunScoreSopProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        const string timerKey = "Timer_4";
        const string comKey = "COM_4";
        if (!profile.Strings.TryGetValue(timerKey, out var device))
        {
            ApplyRow(profile, CompanyDefaults[RunScoreOpenSlotIndex]);
            return true;
        }

        if (!string.Equals(device, "TIMEMACH", StringComparison.OrdinalIgnoreCase))
            return false;
        if (profile.Ints.TryGetValue(comKey, out var com) && com is not 0 and not RunScoreOpenPort)
            return false;

        ApplyRow(profile, CompanyDefaults[RunScoreOpenSlotIndex]);
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
