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

            var who = FormatLockWho(Lock);
            // Present tense — the lock is still Active (even when heartbeat is stale).
            // Soft take-over is available when the other laptop looks idle, not "was" (past).
            if (IsLockStale)
            {
                var lastSeen = FormatLockLastSeen(Lock.LastSeenAt);
                return $"Open on {who} · Last Seen {lastSeen} · Soft Take Over Available";
            }

            return $"Checked Out · {who}";
        }
    }

    /// <summary>
    /// Relative age of lock <see cref="CheckoutLock.LastSeenAt"/> for Soft Take Over rows
    /// (heartbeat already on the lock — no extra Graph call).
    /// </summary>
    public static string FormatLockLastSeen(DateTimeOffset lastSeenAt)
    {
        var age = DateTimeOffset.UtcNow - lastSeenAt;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        if (age < TimeSpan.FromMinutes(1))
            return "Just Now";
        if (age < TimeSpan.FromHours(1))
        {
            var min = Math.Max(1, (int)age.TotalMinutes);
            return min == 1 ? "1 Min Ago" : $"{min} Min Ago";
        }

        if (age < TimeSpan.FromHours(36))
        {
            var hr = Math.Max(1, (int)age.TotalHours);
            return hr == 1 ? "1 Hr Ago" : $"{hr} Hr Ago";
        }

        if (age < TimeSpan.FromDays(14))
        {
            var days = Math.Max(1, (int)age.TotalDays);
            return days == 1 ? "1 Day Ago" : $"{days} Days Ago";
        }

        // Old leftovers — calendar date in local time is clearer than "47 Days Ago".
        return lastSeenAt.ToLocalTime().ToString("MMM d");
    }

    /// <summary>
    /// Holder is usually the PC name (same as <see cref="CheckoutLock.MachineName"/>) —
    /// never show "NAME on NAME".
    /// </summary>
    public static string FormatLockWho(CheckoutLock lockInfo) =>
        FormatLockWho(lockInfo.HolderName, lockInfo.MachineName);

    /// <summary>Same dedupe for dialogs that only have the two strings.</summary>
    public static string FormatLockWho(string? holderName, string? machineName)
    {
        var holder = holderName?.Trim() ?? string.Empty;
        var machine = machineName?.Trim() ?? string.Empty;
        if (holder.Length == 0 && machine.Length == 0)
            return "another laptop";
        if (holder.Length == 0)
            return machine;
        if (machine.Length == 0)
            return holder;
        if (string.Equals(holder, machine, StringComparison.OrdinalIgnoreCase))
            return holder;
        return $"{holder} on {machine}";
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
