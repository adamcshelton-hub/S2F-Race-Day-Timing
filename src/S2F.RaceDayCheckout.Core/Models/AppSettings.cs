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
    /// <summary>
    /// Soft (no-PIN) takeover when lock lastSeen is older than this. With a 1-minute heartbeat,
    /// 6 minutes ≈ several missed beats + cellular Graph lag — short enough for 5K failover.
    /// Force take-over + Admin PIN remains available immediately while the lock still looks alive.
    /// </summary>
    public const int DefaultStaleLockMinutes = 6;

    /// <summary>How often Active timing writes/uploads _checkout.json lastSeen (minimum 30s in UI timer).</summary>
    public const int DefaultHeartbeatSeconds = 60;

    /// <summary>
    /// While sitting on a live office event list (no Active checkout), how often to re-pull
    /// each race's tiny <c>_checkout.json</c> so Checked Out / Available stay current.
    /// Floor 60s in the UI timer — cellular-safe, not aggressive.
    /// </summary>
    public const int DefaultSessionLockPollSeconds = 90;

    public int HeartbeatSeconds { get; set; } = DefaultHeartbeatSeconds;
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

    /// <summary>
    /// Optional laptop override for RunScore RaceRoster login. When non-empty, OneDrive vendor
    /// secrets will not overwrite RaceRoster credentials on this PC.
    /// </summary>
    public string RaceRosterLoginOverride { get; set; } = string.Empty;

    /// <summary>
    /// Optional RaceRoster password for <see cref="RaceRosterLoginOverride"/>.
    /// Blank keeps the current registry password when only the login is customized.
    /// </summary>
    public string RaceRosterPasswordOverride { get; set; } = string.Empty;

    /// <summary>
    /// Optional laptop override for RunScore RunSignUp login. When non-empty, OneDrive vendor
    /// secrets will not overwrite RunSignUp credentials on this PC.
    /// </summary>
    public string RunSignUpLoginOverride { get; set; } = string.Empty;

    /// <summary>
    /// Optional RunSignUp password for <see cref="RunSignUpLoginOverride"/>.
    /// Blank keeps the current registry password when only the login is customized.
    /// </summary>
    public string RunSignUpPasswordOverride { get; set; } = string.Empty;

    /// <summary>
    /// Company RunScore License name for Help → Register RunScore on new installs.
    /// Filled from OneDrive <c>runscore-vendor-secrets.json</c> when present; editable in Admin.
    /// </summary>
    public string RunScoreLicenseName { get; set; } = string.Empty;

    /// <summary>
    /// Company RunScore License key for Help → Register RunScore on new installs.
    /// Filled from OneDrive <c>runscore-vendor-secrets.json</c> when present; editable in Admin.
    /// </summary>
    public string RunScoreLicenseKey { get; set; } = string.Empty;
}
