using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public enum LaptopClockSyncKind
{
    /// <summary>Recent successful sync; clock is trusted.</summary>
    Synced,

    /// <summary>Skew warned today, or never confirmed — timers should verify.</summary>
    CheckClock,

    /// <summary>No strong signal yet (e.g. mid-launch).</summary>
    Unknown
}

/// <summary>Header badge for the military laptop clock.</summary>
public static class LaptopClockSyncStatus
{
    public static LaptopClockSyncKind Evaluate(
        AppSettings settings,
        DateTimeOffset utcNow,
        DateTimeOffset localNow)
    {
        if (SystemTimeSyncPolicy.WasSkewWarnedToday(settings, localNow))
            return LaptopClockSyncKind.CheckClock;

        if (SystemTimeSyncPolicy.HasRecentSuccess(settings, utcNow))
            return LaptopClockSyncKind.Synced;

        if (settings.LastSuccessfulTimeSyncUtc is null)
            return LaptopClockSyncKind.CheckClock;

        // Success exists but outside the recent window — ask staff to re-check.
        return LaptopClockSyncKind.CheckClock;
    }

    /// <summary>Empty when OK — UI shows a warning only for <see cref="LaptopClockSyncKind.CheckClock"/>.</summary>
    public static string WarningLabel(LaptopClockSyncKind kind) =>
        kind == LaptopClockSyncKind.CheckClock ? "Check Clock" : string.Empty;
}
