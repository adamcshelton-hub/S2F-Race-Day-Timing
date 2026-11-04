using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Clock-sync decisions. The Windows clock is corrected by a one-time scheduled task;
/// these helpers decide when to run it and when a fresh HTTPS probe counts as success.
/// Never blocks checkout.
/// </summary>
public static class SystemTimeSyncPolicy
{
    /// <summary>
    /// Skew at or above this triggers an elevated correction attempt.
    /// 1s is the practical floor for HTTPS <c>Date</c> (second resolution); <c>w32tm</c> may land closer when NTP works.
    /// </summary>
    public static readonly TimeSpan CorrectThreshold = TimeSpan.FromSeconds(1);

    /// <summary>
    /// After sync failure, warn staff when remaining skew reaches this.
    /// Slightly above <see cref="CorrectThreshold"/> so HTTP Date quantization does not false-alarm.
    /// </summary>
    public static readonly TimeSpan WarnThreshold = TimeSpan.FromSeconds(2);

    /// <summary>Skip another success-path elevate when last OK sync was this recent and skew is unknown.</summary>
    public static readonly TimeSpan RecentSuccessWindow = TimeSpan.FromHours(12);

    /// <summary>Hard budget for HTTPS Date probe (cellular-safe).</summary>
    public static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(8);

    /// <summary>Hard budget for elevated PowerShell (includes UAC wait).</summary>
    public static readonly TimeSpan ElevateBudget = TimeSpan.FromSeconds(90);

    public static string LocalCalendarDate(DateTimeOffset localNow) =>
        localNow.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static bool WasUacDeclinedToday(AppSettings settings, DateTimeOffset localNow) =>
        string.Equals(
            settings.TimeSyncUacDeclinedLocalDate,
            LocalCalendarDate(localNow),
            StringComparison.Ordinal);

    public static bool WasSkewWarnedToday(AppSettings settings, DateTimeOffset localNow) =>
        string.Equals(
            settings.TimeSyncSkewWarnedLocalDate,
            LocalCalendarDate(localNow),
            StringComparison.Ordinal);

    public static bool HasRecentSuccess(AppSettings settings, DateTimeOffset utcNow) =>
        settings.LastSuccessfulTimeSyncUtc is { } last
        && utcNow - last <= RecentSuccessWindow
        && utcNow - last >= TimeSpan.Zero;

    /// <summary>
    /// Whether a measured skew is large enough to correct. Offline / probe failure is not —
    /// there is no reference time, and NTP is often blocked on the same hotspot.
    /// A prior UAC cancel does not suppress this: the scheduled task does not prompt.
    /// </summary>
    public static bool ShouldAttemptCorrection(TimeSpan? absoluteSkew) =>
        absoluteSkew is { } skew && skew >= CorrectThreshold;

    /// <summary>What a fresh HTTPS measurement says about the laptop clock.</summary>
    public static ClockSyncReading ClassifySkew(TimeSpan? absoluteSkew)
    {
        if (absoluteSkew is not { } skew)
            return ClockSyncReading.Unknown;

        if (skew < CorrectThreshold)
            return ClockSyncReading.Confirmed;

        if (skew < WarnThreshold)
            return ClockSyncReading.Close;

        return ClockSyncReading.Off;
    }

    /// <summary>
    /// Persist a success only when a fresh probe landed inside the warn band.
    /// A missing probe is not success — the elevated script used to exit 0 either way.
    /// </summary>
    public static bool ShouldPersistSuccess(ClockSyncReading reading) =>
        reading is ClockSyncReading.Confirmed or ClockSyncReading.Close;

    public static bool ShouldWarnAboutSkew(
        TimeSpan? absoluteSkew,
        AppSettings settings,
        DateTimeOffset localNow)
    {
        if (absoluteSkew is not { } skew || skew < WarnThreshold)
            return false;
        return !WasSkewWarnedToday(settings, localNow);
    }

    public static void MarkSuccess(AppSettings settings, DateTimeOffset utcNow)
    {
        settings.LastSuccessfulTimeSyncUtc = utcNow;
        settings.TimeSyncUacDeclinedLocalDate = null;
        // A verified sync clears today's warning so the header does not stay on Check Clock.
        settings.TimeSyncSkewWarnedLocalDate = null;
    }

    public static void MarkUacDeclined(AppSettings settings, DateTimeOffset localNow) =>
        settings.TimeSyncUacDeclinedLocalDate = LocalCalendarDate(localNow);

    public static void MarkSkewWarned(AppSettings settings, DateTimeOffset localNow) =>
        settings.TimeSyncSkewWarnedLocalDate = LocalCalendarDate(localNow);

    public static TimeSpan AbsoluteSkew(DateTimeOffset referenceUtc, DateTimeOffset localUtcNow) =>
        (referenceUtc - localUtcNow).Duration();
}

/// <summary>Fresh HTTPS reading of the laptop clock.</summary>
public enum ClockSyncReading
{
    /// <summary>Probe failed or did not run.</summary>
    Unknown,

    /// <summary>Skew is under the 1s correction floor.</summary>
    Confirmed,

    /// <summary>Inside the 2s warn band. Close enough to clear the badge.</summary>
    Close,

    /// <summary>At or past the warn threshold.</summary>
    Off
}
