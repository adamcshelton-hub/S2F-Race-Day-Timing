using System.Globalization;

namespace S2F.RaceDayCheckout.Core.Services;

public enum ClockHeaderKind
{
    /// <summary>No line under the clock. Last check was fine, or there is no reading.</summary>
    Hidden,

    /// <summary>HTTPS check or the scheduled task is in progress.</summary>
    Checking,

    /// <summary>Manual sync finished under the warn threshold. Clears on its own.</summary>
    Synced,

    /// <summary>Last check is at or over the warn threshold, and no correction has been applied yet.</summary>
    CheckClock,

    /// <summary>A correction ran and the follow-up check is still at or over the warn threshold.</summary>
    StillOff,

    /// <summary>Manual sync could not reach internet time. Clears on its own.</summary>
    Unconfirmed
}

/// <summary>What to draw under the laptop clock. Empty label means the line is hidden.</summary>
public readonly record struct ClockHeaderNotice(ClockHeaderKind Kind, string Label, bool Transient)
{
    public static ClockHeaderNotice Hidden { get; } = new(ClockHeaderKind.Hidden, "", false);

    public static ClockHeaderNotice Checking { get; } = new(ClockHeaderKind.Checking, "Checking…", false);

    public static ClockHeaderNotice Synced { get; } = new(ClockHeaderKind.Synced, "Synced", true);

    public static ClockHeaderNotice CheckClock { get; } = new(ClockHeaderKind.CheckClock, "Check Clock", false);

    public static ClockHeaderNotice WaitingForAdmin { get; } =
        new(ClockHeaderKind.Checking, "Waiting for administrator approval…", false);

    public static ClockHeaderNotice Unconfirmed { get; } =
        new(ClockHeaderKind.Unconfirmed, "Couldn't confirm internet time.", true);

    public static ClockHeaderNotice StillOff(TimeSpan skew) =>
        new(ClockHeaderKind.StillOff, "Still off by " + FormatDuration(skew.TotalSeconds), false);

    /// <summary>
    /// Header after a fresh HTTPS reading. Startup stays blank unless the clock is off.
    /// A manual click reports Synced when the reading is under the warn threshold.
    /// </summary>
    public static ClockHeaderNotice ForReading(
        ClockSyncReading reading,
        TimeSpan? skew,
        bool attemptedCorrection,
        bool manual)
    {
        switch (reading)
        {
            case ClockSyncReading.Confirmed:
            case ClockSyncReading.Close:
                return manual ? Synced : Hidden;
            case ClockSyncReading.Off when attemptedCorrection && skew is { } off:
                return StillOff(off);
            case ClockSyncReading.Off:
                return CheckClock;
            default:
                return manual ? Unconfirmed : Hidden;
        }
    }

    public static string FormatDuration(double seconds)
    {
        var abs = Math.Abs(seconds);
        if (abs >= 120)
        {
            var minutes = (int)Math.Round(abs / 60.0, MidpointRounding.AwayFromZero);
            return minutes == 1 ? "1 minute" : minutes.ToString(CultureInfo.InvariantCulture) + " minutes";
        }

        var whole = (int)Math.Round(abs, MidpointRounding.AwayFromZero);
        return whole == 1 ? "1 second" : whole.ToString(CultureInfo.InvariantCulture) + " seconds";
    }
}
