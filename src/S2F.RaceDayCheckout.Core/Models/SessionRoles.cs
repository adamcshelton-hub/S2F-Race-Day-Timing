namespace S2F.RaceDayCheckout.Core.Models;

/// <summary>
/// Ops assignment for one session. Written to <c>._checkin/s2f_session.json</c>.
/// Only <see cref="PrimaryEventFile"/> is written by Admin now. Legacy
/// <see cref="AnnouncerEventFile"/> may still appear on old sidecars and is ignored for
/// Announcer T&amp;S list membership (any non-primary .event is a secondary).
/// </summary>
public sealed class SessionRoles
{
    public string? PrimaryEventFile { get; set; }

    /// <summary>Legacy field — read for compat; new saves omit it.</summary>
    public string? AnnouncerEventFile { get; set; }
}
