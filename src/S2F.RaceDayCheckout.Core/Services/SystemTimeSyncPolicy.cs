using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Launch clock-sync decisions. Corrects Windows time when skew is material; never blocks checkout.
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
    /// Whether to run elevated correction. Only when HTTPS measured a material skew —
    /// offline / probe failure does not pop UAC (NTP would fail too on the same hotspot).
    /// </summary>
    public static bool ShouldAttemptCorrection(
        TimeSpan? absoluteSkew,
        AppSettings settings,
        DateTimeOffset localNow)
    {
        if (WasUacDeclinedToday(settings, localNow))
            return false;

        return absoluteSkew is { } skew && skew >= CorrectThreshold;
    }

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
    }

    public static void MarkUacDeclined(AppSettings settings, DateTimeOffset localNow) =>
        settings.TimeSyncUacDeclinedLocalDate = LocalCalendarDate(localNow);

    public static void MarkSkewWarned(AppSettings settings, DateTimeOffset localNow) =>
        settings.TimeSyncSkewWarnedLocalDate = LocalCalendarDate(localNow);

    public static TimeSpan AbsoluteSkew(DateTimeOffset referenceUtc, DateTimeOffset localUtcNow) =>
        (referenceUtc - localUtcNow).Duration();

    /// <summary>
    /// Build elevated PowerShell: start W32Time, force resync, optionally set clock from HTTPS UTC.
    /// Setting from HTTPS covers cellular hotspots that block NTP UDP 123.
    /// </summary>
    public static string BuildElevatedSyncScript(DateTimeOffset? setClockFromUtc)
    {
        // Keep script ASCII-simple for temp .ps1 files.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("try {");
        sb.AppendLine("  $svc = Get-Service -Name W32Time -ErrorAction SilentlyContinue");
        sb.AppendLine("  if ($null -ne $svc -and $svc.Status -ne 'Running') { Start-Service -Name W32Time -ErrorAction SilentlyContinue }");
        sb.AppendLine("} catch { }");
        sb.AppendLine("try { & w32tm.exe /resync /force | Out-Null } catch { }");
        if (setClockFromUtc is { } utc)
        {
            // Round-trip UTC → local via .NET so DST/zone match the laptop.
            var roundTrip = utc.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            sb.AppendLine(
                "try { Set-Date -Date ([DateTime]::Parse('"
                + roundTrip
                + "', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToLocalTime()) | Out-Null } catch { }");
        }

        sb.AppendLine("exit 0");
        return sb.ToString();
    }
}
