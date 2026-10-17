namespace S2F.RaceDayCheckout.Core.Models;

public sealed class AppSettings
{
    public const string DefaultLibraryRoot = @"C:\OneDrive";
    public const string DefaultRaceDayRoot = @"C:\RaceDay";

    public string LibraryRoot { get; set; } = DefaultLibraryRoot;
    public string RaceDayRoot { get; set; } = DefaultRaceDayRoot;
    public string DisplayName { get; set; } = string.Empty;
    public string AdminPin { get; set; } = "7698";
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
