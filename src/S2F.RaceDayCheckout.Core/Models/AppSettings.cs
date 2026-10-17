namespace S2F.RaceDayCheckout.Core.Models;

public sealed class AppSettings
{
    public const string DefaultLibraryRoot = @"C:\OneDrive";
    public const string DefaultRaceDayRoot = @"C:\RaceDay";

    public string LibraryRoot { get; set; } = DefaultLibraryRoot;
    public string RaceDayRoot { get; set; } = DefaultRaceDayRoot;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Fixed company Admin PIN — not editable in the app or settings.json.</summary>
    public const string DefaultAdminPin = "7690";

    /// <summary>Always <see cref="DefaultAdminPin"/> (7690). Ignored if present in settings.json.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string AdminPin => DefaultAdminPin;
    public const int DefaultStaleLockMinutes = 18;

    public int HeartbeatSeconds { get; set; } = 120;
    public int StaleLockMinutes { get; set; } = DefaultStaleLockMinutes;
    public int AutosaveMirrorSeconds { get; set; } = 60;
    public int OneDriveSyncWaitSeconds { get; set; } = 180;

    /// <summary>Optional full path to rsserver.exe. Empty = auto-detect.</summary>
    public string RunScoreExePath { get; set; } = string.Empty;

    /// <summary>Optional full path to mylaps_ts_launch.exe. Empty = shell-open .event.</summary>
    public string TimingAndScoringExePath { get; set; } = string.Empty;
}
