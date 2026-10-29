namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Main Timing live SoftPull of Manual Backup Currents (phone / Backup Laptop) while MB is Active.
/// Cadence targets ~5s into RunScore Play (≤10s OK on cellular). Download-only; prefer-longer merge.
/// </summary>
public static class ManualBackupLiveSoftPullPolicy
{
    /// <summary>Delay between SoftPull attempts while Manual Backup is enabled on Timing Active.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>Per-pull budget — shorter than Interval + margin so ticks do not stack.</summary>
    public static readonly TimeSpan PullBudget = TimeSpan.FromSeconds(12);

    /// <summary>True when Main Timing should run the live SoftPull loop.</summary>
    public static bool ShouldRun(bool isTimingRole, bool manualBackupEnabled, bool cloudAvailable) =>
        isTimingRole && manualBackupEnabled && cloudAvailable;
}
