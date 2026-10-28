namespace S2F.RaceDayCheckout.Core.Models;

/// <summary>
/// One race-day category start on the Active timing laptop.
/// Date is always today (local); <see cref="StartTimeOfDay"/> is scheduled 24h wall clock.
/// <see cref="GunStartTimeOfDay"/> is the actual gun (second precision) when captured.
/// </summary>
public sealed class RaceStartEntry
{
    public required string Category { get; init; }

    /// <summary>Scheduled local time-of-day (minute precision, or 24:00 end-of-day).</summary>
    public TimeSpan StartTimeOfDay { get; init; }

    /// <summary>
    /// Actual gun time as <c>HH:MM:SS.ff</c> (centiseconds). Null until Capture Gun Start.
    /// Live countdown/count-up uses this when set; otherwise <see cref="StartTimeOfDay"/>.
    /// </summary>
    public TimeSpan? GunStartTimeOfDay { get; init; }
}
