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
    /// <summary>Legacy sidecar field — not used for Announcer T&amp;S list membership.</summary>
    public string? AnnouncerEventFile { get; set; }
    /// <summary>Non-primary .event names (announcer / video / callouts). Set when Announcer T&amp;S probes the session.</summary>
    public IReadOnlyList<string> SecondaryEventFiles { get; set; } = Array.Empty<string>();
    public CheckoutLock? Lock { get; set; }

    public string? SecondaryEventSummary =>
        SecondaryEventFiles.Count == 0
            ? null
            : SecondaryEventFiles.Count == 1
                ? SecondaryEventFiles[0]
                : $"{SecondaryEventFiles.Count} other .event files";

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

    /// <summary>Active checkout lock held by someone (not a Past Races folder row).</summary>
    public bool IsActivelyCheckedOut =>
        !IsNavigationFolder
        && Lock is { Status: LockLifecycleStatus.Active };

    /// <summary>
    /// Active lock older than <see cref="AppSettings.DefaultStaleLockMinutes"/> — soft take-over eligible.
    /// </summary>
    public bool IsLockStale
    {
        get
        {
            if (!IsActivelyCheckedOut || Lock is null)
                return false;
            var age = DateTimeOffset.UtcNow - Lock.LastSeenAt;
            return age > TimeSpan.FromMinutes(AppSettings.DefaultStaleLockMinutes);
        }
    }

    /// <summary>
    /// Race-day list section label (Title Case). Used when Timing shows lock grouping.
    /// </summary>
    public string ListSection
    {
        get
        {
            if (IsNavigationFolder)
                return "Folders";
            if (IsActivelyCheckedOut)
                return "Checked Out";
            return "Available";
        }
    }

    public string DisplayLock
    {
        get
        {
            if (IsNavigationFolder)
                return "Folder";

            if (!IsActivelyCheckedOut || Lock is null)
                return "Available";

            if (IsLockStale)
                return $"Can Take Over · was {Lock.HolderName} @ {Lock.MachineName}";

            return $"Checked Out · {Lock.HolderName} @ {Lock.MachineName}";
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
    /// <summary>Timing unless this Active checkout was an announcer slim pull. Restored from active.json.</summary>
    public CheckoutRole Role { get; init; } = CheckoutRole.Timing;
    public IReadOnlyList<string> ScopedPaths { get; init; } = Array.Empty<string>();
    public DateTimeOffset CheckedOutAt { get; init; }

    /// <summary>
    /// Session-relative paths still needing Graph Finish upload after a prior cloud failure.
    /// Null/empty = normal Finish (no retry pending).
    /// </summary>
    public IReadOnlyList<string>? PendingFinishUploadRelativePaths { get; init; }

    public string DisplayName =>
        string.Equals(ParentEvent, SessionName, StringComparison.OrdinalIgnoreCase)
            ? SessionName
            : $"{ParentEvent} / {SessionName}";

    public ActiveCheckout WithPendingFinishUpload(IReadOnlyList<string>? pendingPaths) => new()
    {
        Office = Office,
        ParentEvent = ParentEvent,
        SessionName = SessionName,
        RelativePath = RelativePath,
        LibrarySessionPath = LibrarySessionPath,
        ActivePath = ActivePath,
        BackupPath = BackupPath,
        AutosavePath = AutosavePath,
        DbsPath = DbsPath,
        PrimaryEventFile = PrimaryEventFile,
        Role = Role,
        ScopedPaths = ScopedPaths,
        CheckedOutAt = CheckedOutAt,
        PendingFinishUploadRelativePaths = pendingPaths is { Count: > 0 }
            ? pendingPaths.ToList()
            : null
    };
}
