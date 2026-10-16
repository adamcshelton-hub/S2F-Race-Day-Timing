using System.Text.Json;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public enum LockAcquireOutcome
{
    Acquired,
    SameMachineReclaim,
    BlockedAlive,
    NeedsEmergencyConfirm,
    Failed
}

public sealed class LockAcquireResult
{
    public required LockAcquireOutcome Outcome { get; init; }
    public CheckoutLock? Lock { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class CheckoutLockService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly PathService _paths;
    private readonly IMachineIdentity _machine;
    private readonly Func<DateTimeOffset> _clock;

    public CheckoutLockService(PathService paths, IMachineIdentity machine, Func<DateTimeOffset>? clock = null)
    {
        _paths = paths;
        _machine = machine;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public CheckoutLock? TryRead(string libraryEventPath)
    {
        var path = _paths.GetLockPath(libraryEventPath);
        if (!File.Exists(path))
            path = _paths.GetLegacyLockPath(libraryEventPath);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CheckoutLock>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public LockAcquireResult EvaluateAcquire(string libraryEventPath, AppSettings settings)
    {
        var existing = TryRead(libraryEventPath);
        if (existing is null || existing.Status != LockLifecycleStatus.Active)
        {
            return new LockAcquireResult
            {
                Outcome = LockAcquireOutcome.Acquired,
                Message = "Lock is free."
            };
        }

        if (string.Equals(existing.MachineId, _machine.MachineId, StringComparison.OrdinalIgnoreCase))
        {
            return new LockAcquireResult
            {
                Outcome = LockAcquireOutcome.SameMachineReclaim,
                Lock = existing,
                Message = "Same machine holds the lock — continuing."
            };
        }

        var age = _clock() - existing.LastSeenAt;
        if (age <= TimeSpan.FromMinutes(settings.StaleLockMinutes))
        {
            return new LockAcquireResult
            {
                Outcome = LockAcquireOutcome.BlockedAlive,
                Lock = existing,
                Message =
                    $"Checked out by {existing.HolderName} on {existing.MachineName} since {existing.CheckedOutAt.ToLocalTime():g} (last seen {existing.LastSeenAt.ToLocalTime():g})."
            };
        }

        return new LockAcquireResult
        {
            Outcome = LockAcquireOutcome.NeedsEmergencyConfirm,
            Lock = existing,
            Message =
                $"Lock looks stale (last seen {existing.LastSeenAt.ToLocalTime():g} by {existing.HolderName} on {existing.MachineName}). Confirm the other laptop is out before Emergency reclaim."
        };
    }

    public CheckoutLock Acquire(
        string librarySessionPath,
        string office,
        string parentEvent,
        string sessionName,
        string holderName,
        IReadOnlyList<string> scopedPaths,
        string? primaryEventFile = null,
        string? reclaimReason = null,
        bool emergency = false)
    {
        var now = _clock();
        var displayName = string.Equals(parentEvent, sessionName, StringComparison.OrdinalIgnoreCase)
            ? sessionName
            : $"{parentEvent} / {sessionName}";
        var lockInfo = new CheckoutLock
        {
            EventPath = librarySessionPath,
            Office = office,
            EventName = displayName,
            ParentEvent = parentEvent,
            SessionName = sessionName,
            HolderName = holderName,
            WindowsUser = _machine.WindowsUser,
            MachineId = _machine.MachineId,
            MachineName = _machine.MachineName,
            CheckedOutAt = now,
            LastSeenAt = now,
            Status = LockLifecycleStatus.Active,
            ReclaimReason = reclaimReason,
            ScopedPaths = scopedPaths.ToList(),
            PrimaryEventFile = primaryEventFile
        };

        WriteLockWithRetry(librarySessionPath, lockInfo);
        AppendHistory(librarySessionPath, new LockHistoryEntry
        {
            At = now,
            Action = emergency ? "emergency_reclaim" : "acquire",
            HolderName = holderName,
            MachineName = _machine.MachineName,
            MachineId = _machine.MachineId,
            Reason = reclaimReason
        });
        return lockInfo;
    }

    public CheckoutLock? Heartbeat(string libraryEventPath)
    {
        var existing = TryRead(libraryEventPath);
        if (existing is null || existing.Status != LockLifecycleStatus.Active)
            return existing;
        if (!string.Equals(existing.MachineId, _machine.MachineId, StringComparison.OrdinalIgnoreCase))
            return existing;

        existing.LastSeenAt = _clock();
        WriteLockWithRetry(libraryEventPath, existing);
        return existing;
    }

    public void Release(string libraryEventPath, string holderName, string? reason = null)
    {
        var existing = TryRead(libraryEventPath) ?? new CheckoutLock
        {
            EventPath = libraryEventPath,
            HolderName = holderName,
            MachineId = _machine.MachineId,
            MachineName = _machine.MachineName,
            WindowsUser = _machine.WindowsUser
        };

        existing.Status = LockLifecycleStatus.Released;
        existing.LastSeenAt = _clock();
        existing.ReclaimReason = reason;
        WriteLockWithRetry(libraryEventPath, existing);
        AppendHistory(libraryEventPath, new LockHistoryEntry
        {
            At = existing.LastSeenAt,
            Action = "release",
            HolderName = holderName,
            MachineName = _machine.MachineName,
            MachineId = _machine.MachineId,
            Reason = reason
        });
    }

    public bool ForceUnlock(string libraryEventPath, string adminName, string reason, string adminPin, AppSettings settings)
    {
        if (!string.Equals(adminPin, settings.AdminPin, StringComparison.Ordinal))
            return false;

        var existing = TryRead(libraryEventPath);
        if (existing is not null)
        {
            existing.Status = LockLifecycleStatus.Released;
            existing.LastSeenAt = _clock();
            existing.ReclaimReason = reason;
            WriteLockWithRetry(libraryEventPath, existing);
        }

        AppendHistory(libraryEventPath, new LockHistoryEntry
        {
            At = _clock(),
            Action = "admin_force_unlock",
            HolderName = adminName,
            MachineName = _machine.MachineName,
            MachineId = _machine.MachineId,
            Reason = reason
        });
        return true;
    }

    private void WriteLockWithRetry(string libraryEventPath, CheckoutLock lockInfo)
    {
        var path = _paths.GetLockPath(libraryEventPath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(lockInfo, JsonOptions);
        const int attempts = 8;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(fs);
                writer.Write(json);
                TryDeleteLegacyLockFiles(libraryEventPath);
                return;
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(50 * (i + 1));
            }
        }
    }

    private void AppendHistory(string libraryEventPath, LockHistoryEntry entry)
    {
        var path = _paths.GetLockHistoryPath(libraryEventPath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // One-time migrate: keep prior root history when first writing under ._checkin.
        var legacy = _paths.GetLegacyLockHistoryPath(libraryEventPath);
        if (File.Exists(legacy) && !File.Exists(path))
        {
            try { File.Copy(legacy, path); }
            catch { /* best effort */ }
        }

        var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
        for (var i = 0; i < 5; i++)
        {
            try
            {
                File.AppendAllText(path, line);
                TryDeleteQuiet(legacy);
                return;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(40 * (i + 1));
            }
        }
    }

    private void TryDeleteLegacyLockFiles(string libraryEventPath)
    {
        TryDeleteQuiet(_paths.GetLegacyLockPath(libraryEventPath));
        TryDeleteQuiet(_paths.GetLegacyLockHistoryPath(libraryEventPath));
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // OneDrive may briefly lock; next write retries cleanup.
        }
    }
}
