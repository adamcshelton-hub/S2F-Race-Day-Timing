namespace S2F.RaceDayCheckout.Core.Models;

/// <summary>
/// Ops assignment for one session. Written to <c>._checkin/s2f_session.json</c>.
/// Announcer is optional — most races are primary-only.
/// </summary>
public sealed class SessionRoles
{
    public string? PrimaryEventFile { get; set; }
    public string? AnnouncerEventFile { get; set; }
}
