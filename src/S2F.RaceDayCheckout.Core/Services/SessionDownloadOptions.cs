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

    public static SessionDownloadOptions ForAnnouncer(string announcerEventFile) => new()
    {
        Role = CheckoutRole.Announcer,
        AnnouncerEventFile = announcerEventFile
    };

    public static SessionDownloadOptions ForReclaimAutosaveOverlay() => new()
    {
        Role = CheckoutRole.Timing,
        AutosaveOverlayOnly = true
    };
}
