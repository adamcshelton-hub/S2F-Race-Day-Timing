using System.Globalization;
using System.Text.RegularExpressions;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Parse 24h start times and format countdown / count-up for race-day category clocks.
/// Today’s local date is always inferred — never stores a calendar date.
/// </summary>
public static class RaceStartClock
{
    private static readonly Regex CompactHHmm = new(
        @"^\s*(\d{1,2})([:.]?(\d{2}))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Gun / actual start: <c>HH:MM:SS</c> or <c>HH:MM:SS.ff</c> (centiseconds).
    /// </summary>
    private static readonly Regex GunHhMmSs = new(
        @"^\s*(\d{1,2}):(\d{2}):(\d{2})([.:](\d{1,2}))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Accepts <c>8</c>, <c>830</c>, <c>8:30</c>, <c>08:30</c>, <c>8.30</c> (24h).
    /// </summary>
    public static bool TryParse24h(string? input, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var raw = input.Trim();

        // Digits only: H, HH, HMM, HHMM
        if (Regex.IsMatch(raw, @"^\d{1,4}$", RegexOptions.CultureInvariant))
        {
            if (raw.Length <= 2)
            {
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var hourOnly))
                    return false;
                if (!TryNormalizeHourMinute(hourOnly, 0, out timeOfDay))
                    return false;
                return true;
            }

            if (raw.Length == 3)
            {
                // HMM → H:MM
                if (!int.TryParse(raw[..1], NumberStyles.None, CultureInfo.InvariantCulture, out var h3)
                    || !int.TryParse(raw[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var m3))
                    return false;
                return TryNormalizeHourMinute(h3, m3, out timeOfDay);
            }

            // HHMM
            if (!int.TryParse(raw[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var h4)
                || !int.TryParse(raw[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var m4))
                return false;
            return TryNormalizeHourMinute(h4, m4, out timeOfDay);
        }

        var m = CompactHHmm.Match(raw);
        if (!m.Success)
            return false;

        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour))
            return false;
        var minute = 0;
        if (m.Groups[3].Success
            && !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minute))
            return false;

        return TryNormalizeHourMinute(hour, minute, out timeOfDay);
    }

    /// <summary>
    /// Parse gun / actual start: <c>8:00:17</c>, <c>08:00:17.12</c> (centiseconds).
    /// </summary>
    public static bool TryParseGun(string? input, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var m = GunHhMmSs.Match(input.Trim());
        if (!m.Success)
            return false;

        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
            || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var second))
            return false;

        var centiseconds = 0;
        if (m.Groups[5].Success)
        {
            var frac = m.Groups[5].Value;
            if (frac.Length == 1)
                frac += "0";
            if (!int.TryParse(frac[..2], NumberStyles.None, CultureInfo.InvariantCulture, out centiseconds))
                return false;
        }

        return TryNormalizeGun(hour, minute, second, centiseconds, out timeOfDay);
    }

    /// <summary>
    /// Valid 24h clock: 0–23 hours, or <c>24:00</c> only.
    /// <c>24:00</c> is end-of-day midnight (today + 24h → countdown until then), not 00:00 AM.
    /// </summary>
    public static bool TryNormalizeHourMinute(int hour, int minute, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        if (minute is < 0 or > 59)
            return false;
        if (hour == 24)
        {
            if (minute != 0)
                return false;
            // Keep 24:00 as 24h so ResolveStartLocal lands on tonight's midnight, not this morning's.
            timeOfDay = new TimeSpan(24, 0, 0);
            return true;
        }

        if (hour is < 0 or > 23)
            return false;
        timeOfDay = new TimeSpan(hour, minute, 0);
        return true;
    }

    public static bool TryNormalizeGun(
        int hour, int minute, int second, int centiseconds, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59
            || centiseconds is < 0 or > 99)
            return false;
        timeOfDay = new TimeSpan(0, hour, minute, second, centiseconds * 10);
        return true;
    }

    public static string FormatStartHhMm(TimeSpan timeOfDay) =>
        $"{(int)timeOfDay.TotalHours:00}:{timeOfDay.Minutes:00}";

    /// <summary>Gun / actual start as <c>HH:MM:SS.ff</c> (centiseconds always shown).</summary>
    public static string FormatGunHhMmSs(TimeSpan timeOfDay)
    {
        var cs = timeOfDay.Milliseconds / 10;
        return $"{(int)timeOfDay.TotalHours:00}:{timeOfDay.Minutes:00}:{timeOfDay.Seconds:00}.{cs:00}";
    }

    /// <summary>Wall clock → gun TimeSpan truncated to centiseconds.</summary>
    public static TimeSpan CaptureGunTimeOfDay(DateTime localNow)
    {
        var ms = (localNow.Millisecond / 10) * 10;
        return new TimeSpan(0, localNow.Hour, localNow.Minute, localNow.Second, ms);
    }

    public static DateTime ResolveStartLocal(DateTime localNow, TimeSpan timeOfDay)
    {
        var day = localNow.Date;
        return day + timeOfDay;
    }

    /// <summary>
    /// Drop sub-second so countdown matches the header clock (<c>HH:mm:ss</c> display).
    /// Without this, 22:47:56.7 → header "22:47:56" but remaining to 23:00 showed 12:03.
    /// </summary>
    public static DateTime TruncateToSecond(DateTime localNow) =>
        new(localNow.Year, localNow.Month, localNow.Day,
            localNow.Hour, localNow.Minute, localNow.Second, localNow.Kind);

    /// <summary>Truncate to centiseconds (10ms) for gun / race-clock math.</summary>
    public static DateTime TruncateToCentisecond(DateTime localNow)
    {
        var ms = (localNow.Millisecond / 10) * 10;
        return new DateTime(
            localNow.Year, localNow.Month, localNow.Day,
            localNow.Hour, localNow.Minute, localNow.Second, ms, localNow.Kind);
    }

    /// <summary>
    /// Countdown uses leading minus; count-up uses leading plus.
    /// Whole seconds by default; set <paramref name="centiseconds"/> for <c>H:MM:SS.ff</c> (gun clocks).
    /// </summary>
    public static string FormatRelative(DateTime localNow, TimeSpan timeOfDay, bool centiseconds = false)
    {
        var now = centiseconds ? TruncateToCentisecond(localNow) : TruncateToSecond(localNow);
        var start = ResolveStartLocal(now, timeOfDay);
        if (!centiseconds)
            start = TruncateToSecond(start);

        var delta = now - start;
        var sign = delta < TimeSpan.Zero ? "−" : "+";
        var abs = delta.Duration();
        var hours = (int)abs.TotalHours;
        if (!centiseconds)
            return $"{sign}{hours}:{abs.Minutes:00}:{abs.Seconds:00}";

        var cs = abs.Milliseconds / 10;
        return $"{sign}{hours}:{abs.Minutes:00}:{abs.Seconds:00}.{cs:00}";
    }

    public static bool IsCountUp(DateTime localNow, TimeSpan timeOfDay, bool centiseconds = false)
    {
        var now = centiseconds ? TruncateToCentisecond(localNow) : TruncateToSecond(localNow);
        var start = ResolveStartLocal(now, timeOfDay);
        if (!centiseconds)
            start = TruncateToSecond(start);
        return now >= start;
    }
}
