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
        catch (Exception ex)
        {
            RaceDayTrace.Write($"lock read failed path={path} error={ex.GetType().Name}: {ex.Message}");
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
        RaceDayTrace.Write(
            $"lock acquire emergency={emergency} path={librarySessionPath} lock={RaceDayTrace.FormatLock(lockInfo)}");
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
        {
            RaceDayTrace.Write(
                $"lock heartbeat skipped path={libraryEventPath} reason={(existing is null ? "missing" : "status-" + existing.Status)} " +
                $"ours={_machine.MachineId} lock={RaceDayTrace.FormatLock(existing)}");
            return existing;
        }

        if (!string.Equals(existing.MachineId, _machine.MachineId, StringComparison.OrdinalIgnoreCase))
        {
            RaceDayTrace.Write(
                $"lock heartbeat skipped path={libraryEventPath} reason=machine-mismatch " +
                $"ours={_machine.MachineId} oursMachine={_machine.MachineName} oursUser={_machine.WindowsUser} " +
                $"lock={RaceDayTrace.FormatLock(existing)}");
            return existing;
        }

        existing.LastSeenAt = _clock();
        WriteLockWithRetry(libraryEventPath, existing);
        return existing;
    }

    /// <summary>
    /// True only when the on-disk lock is Active and held by this machine.
    /// Call after <c>DownloadCheckoutLockAsync</c> so a soft-takeover on OneDrive is visible locally.
    /// </summary>
    public bool ThisMachineHoldsActiveLock(string libraryEventPath)
    {
        var existing = TryRead(libraryEventPath);
        return existing is { Status: LockLifecycleStatus.Active }
               && string.Equals(existing.MachineId, _machine.MachineId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Active lock held by another machine (after refresh), or null if free / ours / missing.</summary>
    public CheckoutLock? TryGetOtherMachineActiveLock(string libraryEventPath)
    {
        var existing = TryRead(libraryEventPath);
        if (existing is null || existing.Status != LockLifecycleStatus.Active)
            return null;
        if (string.Equals(existing.MachineId, _machine.MachineId, StringComparison.OrdinalIgnoreCase))
            return null;
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
        RaceDayTrace.Write(
            $"lock release path={libraryEventPath} reason={reason} lock={RaceDayTrace.FormatLock(existing)}");
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
        if (!string.Equals(adminPin, AppSettings.DefaultAdminPin, StringComparison.Ordinal))
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

    /// <summary>
    /// Put a pre-download copy of _checkout.json back after OneDrive replaced it
    /// with something that is not another laptop's Active lock.
    /// </summary>
    public bool TryReplaceLockFile(string libraryEventPath, byte[] contents)
    {
        _paths.EnsureAppMetaFolder(libraryEventPath);
        var path = _paths.GetLockPath(libraryEventPath);
        const int attempts = 12;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                fs.Write(contents, 0, contents.Length);
                return true;
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(80 * (i + 1));
            }
            catch (IOException ex)
            {
                RaceDayTrace.Write($"lock restore failed path={path} error={ex.Message}");
                return false;
            }
        }

        return false;
    }

    private void WriteLockWithRetry(string libraryEventPath, CheckoutLock lockInfo)
    {
        _paths.EnsureAppMetaFolder(libraryEventPath);
        var path = _paths.GetLockPath(libraryEventPath);

        var json = JsonSerializer.Serialize(lockInfo, JsonOptions);
        const int attempts = 12;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                // FileShare.Read — OneDrive / Graph may have the file open briefly after upload.
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(fs);
                writer.Write(json);
                TryDeleteLegacyLockFiles(libraryEventPath);
                return;
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(80 * (i + 1));
            }
            catch (IOException ex)
            {
                // Last attempt — don't fail Finish/checkout solely on lock write (race files already saved).
                RaceDayTrace.Write($"lock write failed path={path} error={ex.Message}");
                return;
            }
        }
    }

    private void AppendHistory(string libraryEventPath, LockHistoryEntry entry)
    {
        _paths.EnsureAppMetaFolder(libraryEventPath);
        var path = _paths.GetLockHistoryPath(libraryEventPath);

        // One-time migrate: keep prior root history when first writing under ._checkin.
        var legacy = _paths.GetLegacyLockHistoryPath(libraryEventPath);
        if (File.Exists(legacy) && !File.Exists(path))
        {
            try { File.Copy(legacy, path); }
            catch { /* best effort */ }
        }

        var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
        const int attempts = 12;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fs);
                writer.Write(line);
                TryDeleteQuiet(legacy);
                return;
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(80 * (i + 1));
            }
            catch (IOException)
            {
                // History is audit-only — never fail Finish because OneDrive briefly locks this file.
                return;
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
