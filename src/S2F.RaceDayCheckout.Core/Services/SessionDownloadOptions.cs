using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>Default (null) is a full timing pack. Announcer keeps one .event and DB's.</summary>
public sealed class SessionDownloadOptions
{
    public CheckoutRole Role { get; init; } = CheckoutRole.Timing;
    public string? AnnouncerEventFile { get; init; }

    /// <summary>
    /// Soft/force reclaim: pull only <c>RUNSCORE\_autosave</c> (tiny mid-race overlay).
    /// Normal timing downloads still skip that folder on cellular.
    /// </summary>
    public bool AutosaveOverlayOnly { get; init; }

    /// <summary>
    /// Announcer mid-race: pull only session <c>DB's</c> (no .event). Timing publishes CSVs;
    /// announcer T&amp;S auto-loads them from Active <c>DB's</c>.
    /// </summary>
    public bool DbsOnly { get; init; }

    /// <summary>
    /// Mid-race Play: pull only <c>._checkin/manual_backup</c> (phone / Backup Laptop taps).
    /// </summary>
    public bool ManualBackupOnly { get; init; }

    public static SessionDownloadOptions ForAnnouncer(string announcerEventFile) => new()
    {
        Role = CheckoutRole.Announcer,
        AnnouncerEventFile = announcerEventFile
    };

    public static SessionDownloadOptions ForAnnouncerDbsRefresh() => new()
    {
        Role = CheckoutRole.Announcer,
        DbsOnly = true
    };

    public static SessionDownloadOptions ForReclaimAutosaveOverlay() => new()
    {
        Role = CheckoutRole.Timing,
        AutosaveOverlayOnly = true
    };

    public static SessionDownloadOptions ForManualBackupOnly() => new()
    {
        Role = CheckoutRole.Timing,
        ManualBackupOnly = true
    };
}
