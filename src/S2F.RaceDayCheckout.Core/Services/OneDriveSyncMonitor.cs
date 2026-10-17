namespace S2F.RaceDayCheckout.Core.Services;

public sealed class SyncWaitResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public interface IOneDriveSyncMonitor
{
    Task<SyncWaitResult> WaitUntilSettledAsync(
        string path,
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pragmatic sync wait: after check-in, wait until scoped files under the session stop changing
/// (OneDrive upload burst settles). Not a full OneDrive API; Finish UI still tells staff to verify the cloud icon.
/// </summary>
public sealed class SettlingOneDriveSyncMonitor : IOneDriveSyncMonitor
{
    public async Task<SyncWaitResult> WaitUntilSettledAsync(
        string path,
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path))
        {
            return new SyncWaitResult
            {
                Success = false,
                Message = $"Path missing for sync wait: {path}"
            };
        }

        var deadline = DateTime.UtcNow + timeout;
        string? lastFingerprint = null;
        var stableTicks = 0;
        const int requiredStable = 3;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = FingerprintScoped(path);
            if (fingerprint == lastFingerprint)
            {
                stableTicks++;
                progress?.Report($"OneDrive settle check ({stableTicks}/{requiredStable})…");
                if (stableTicks >= requiredStable)
                {
                    return new SyncWaitResult
                    {
                        Success = true,
                        Message = "File activity settled."
                    };
                }
            }
            else
            {
                stableTicks = 0;
                lastFingerprint = fingerprint;
                progress?.Report("Waiting for OneDrive file activity to settle…");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return new SyncWaitResult
        {
            Success = false,
            Message = "Timed out waiting for file activity to settle. Confirm the OneDrive icon shows synced before shutdown."
        };
    }

    private static string FingerprintScoped(string sessionPath)
    {
        long count = 0;
        long bytes = 0;
        long ticks = 0;

        var runScore = Path.Combine(sessionPath, PathService.RunScoreFolderName);
        if (Directory.Exists(runScore))
        {
            foreach (var file in Directory.EnumerateFiles(runScore, "*", SearchOption.AllDirectories))
            {
                // Ignore live mid-race mirror — Finish deletes it separately; waiting on it
                // only slows check-in and keeps OneDrive locks on the folder we need to remove.
                if (file.Contains($"{Path.DirectorySeparatorChar}{PathService.AutosaveFolderName}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    || file.Contains($"{Path.DirectorySeparatorChar}{PathService.AppMetaFolderName}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    || file.Contains($"{Path.DirectorySeparatorChar}._checkin_backup{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var info = new FileInfo(file);
                    count++;
                    bytes += info.Length;
                    ticks ^= info.LastWriteTimeUtc.Ticks;
                }
                catch
                {
                    // ignore transient OneDrive locks
                }
            }
        }

        foreach (var eventFile in Directory.EnumerateFiles(sessionPath, "*.event"))
        {
            try
            {
                var info = new FileInfo(eventFile);
                count++;
                bytes += info.Length;
                ticks ^= info.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                // ignore
            }
        }

        return $"{count}:{bytes}:{ticks}";
    }
}
