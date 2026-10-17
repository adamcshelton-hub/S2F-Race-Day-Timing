namespace S2F.RaceDayCheckout.Core.Models;

public sealed class AppSettings
{
    /// <summary>App-managed library cache (not OneDrive Desktop).</summary>
    public const string DefaultLibraryRoot = @"C:\S2F\Library";
    public const string DefaultRaceDayRoot = @"C:\S2F\RaceDay";

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

    /// <summary>
    /// True after the first-install Defender exclusion prompt was answered (Yes or No).
    /// Prevents re-prompt on later launches and routine app updates.
    /// </summary>
    public bool DefenderExclusionsConfigured { get; set; }

    /// <summary>
    /// True when Windows Defender path exclusions were successfully applied.
    /// Diagnostics only — checkout does not depend on this.
    /// </summary>
    public bool DefenderExclusionsApplied { get; set; }

    /// <summary>
    /// Entra app (public client) Application ID for personal OneDrive Graph sync.
    /// Default is the S2F Race Day Timing registration.
    /// </summary>
    public const string DefaultGraphClientId = "1f6ccaf7-e19c-4223-92d2-c3b825fc2bfe";

    public string GraphClientId { get; set; } = DefaultGraphClientId;

    /// <summary>
    /// Folder under the signed-in OneDrive root that maps to <see cref="LibraryRoot"/>.
    /// Empty = My files root (Atlanta, Test Office, … sit directly under My files).
    /// Only set this if offices live in a subfolder on OneDrive.
    /// </summary>
    public string GraphDriveRoot { get; set; } = string.Empty;
}
