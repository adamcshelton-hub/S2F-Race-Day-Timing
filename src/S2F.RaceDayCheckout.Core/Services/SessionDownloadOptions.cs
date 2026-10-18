using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>Default (null) is a full timing pack. Announcer keeps one .event and DB's.</summary>
public sealed class SessionDownloadOptions
{
    public CheckoutRole Role { get; init; } = CheckoutRole.Timing;
    public string? AnnouncerEventFile { get; init; }

    public static SessionDownloadOptions ForAnnouncer(string announcerEventFile) => new()
    {
        Role = CheckoutRole.Announcer,
        AnnouncerEventFile = announcerEventFile
    };
}
