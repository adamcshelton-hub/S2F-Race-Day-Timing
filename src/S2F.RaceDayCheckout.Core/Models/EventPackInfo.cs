namespace S2F.RaceDayCheckout.Core.Models;

public sealed class OfficeInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
}

/// <summary>
/// A timing session leaf: a folder that contains RUNSCORE (flat event or nested day/phase).
/// </summary>
public sealed class EventPackInfo
{
    public required string Office { get; init; }

    /// <summary>Parent event folder name (e.g. 2026 - LRM Weekend), or same as SessionName when flat.</summary>
    public required string ParentEvent { get; init; }

    /// <summary>Session leaf name (e.g. Sunday, or 2026 - Heavens Cradle when flat).</summary>
    public required string SessionName { get; init; }

    /// <summary>Path relative to office (e.g. 2026 - LRM Weekend\Sunday).</summary>
    public required string RelativePath { get; init; }

    public required string Path { get; init; }

    public IReadOnlyList<string> EventFiles { get; init; } = Array.Empty<string>();
    public string? PrimaryEventFile { get; set; }
    public CheckoutLock? Lock { get; set; }

    /// <summary>True when this row is a Past Races (or similar) folder to open, not a checkoutable session.</summary>
    public bool IsNavigationFolder { get; init; }

    public string DisplayName =>
        IsNavigationFolder
            ? SessionName
            : string.Equals(ParentEvent, SessionName, StringComparison.OrdinalIgnoreCase)
                ? SessionName
                : $"{ParentEvent} / {SessionName}";

    public string DbsPath => System.IO.Path.Combine(Path, "DB's");
    public string PreRacePath => System.IO.Path.Combine(Path, "Pre-Race");
    public string RunScorePath => System.IO.Path.Combine(Path, "RUNSCORE");

    public string DisplayLock
    {
        get
        {
            if (IsNavigationFolder)
                return "Folder";

            if (Lock is null || Lock.Status != LockLifecycleStatus.Active)
                return "Available";

            // Match AppSettings.StaleLockMinutes default — list should not look "free" after a crash.
            var staleAfter = TimeSpan.FromMinutes(AppSettings.DefaultStaleLockMinutes);
            var age = DateTimeOffset.UtcNow - Lock.LastSeenAt;
            if (age > staleAfter)
                return $"Stale — was {Lock.HolderName} @ {Lock.MachineName} (can take over)";

            return $"{Lock.HolderName} @ {Lock.MachineName}";
        }
    }
}

public sealed class ActiveCheckout
{
    public required string Office { get; init; }
    public required string ParentEvent { get; init; }
    public required string SessionName { get; init; }
    public required string RelativePath { get; init; }
    public required string LibrarySessionPath { get; init; }
    public required string ActivePath { get; init; }
    public required string BackupPath { get; init; }
    public required string AutosavePath { get; init; }
    public required string DbsPath { get; init; }
    public required string PrimaryEventFile { get; init; }
    public IReadOnlyList<string> ScopedPaths { get; init; } = Array.Empty<string>();
    public DateTimeOffset CheckedOutAt { get; init; }

    public string DisplayName =>
        string.Equals(ParentEvent, SessionName, StringComparison.OrdinalIgnoreCase)
            ? SessionName
            : $"{ParentEvent} / {SessionName}";
}
