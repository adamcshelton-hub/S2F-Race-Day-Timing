namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Race-day Graph sync rules for cellular: small payloads + short timeouts.
/// Local library save is always authoritative on this laptop; cloud upload is required
/// before releasing the checkout lock so another machine cannot take a stale pack.
/// Mid-race Graph work must never block local autosave mirroring.
/// </summary>
public static class GraphSyncPolicy
{
    /// <summary>
    /// Soft per-call guidance. HttpClient uses infinite timeout; each op supplies a CancellationToken budget
    /// (ZIP pack download can exceed 30s on cellular).
    /// </summary>
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Probe allowed office folders only (never list entire OneDrive root).</summary>
    public static readonly TimeSpan OfficeListBudget = TimeSpan.FromSeconds(20);

    /// <summary>Index one office’s event leaves — cancel if cellular stalls.</summary>
    public static readonly TimeSpan OfficeSessionsBudget = TimeSpan.FromSeconds(75);

    /// <summary>Whole Finish cloud upload budget (after local save).</summary>
    public static readonly TimeSpan FinishUploadBudget = TimeSpan.FromSeconds(90);

    /// <summary>Checkout session pack download (one event). Progress must stay live — cellular.</summary>
    public static readonly TimeSpan SessionDownloadBudget = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Concurrent file transfers for one session pack (download or upload). Graph has no folder-ZIP
    /// for personal OneDrive; parallel GETs/PUTs are the bulk option that works. Not an integrity
    /// limit — guarded by Retry-After cooldown + temporary concurrency backoff on 429/503.
    /// </summary>
    public const int DownloadParallelism = 8;

    /// <summary>Same default as download — Finish / mid-race folder uploads.</summary>
    public const int UploadParallelism = DownloadParallelism;

    /// <summary>Floor when Graph throttles — never crawl at 1 if we can avoid it.</summary>
    public const int DownloadParallelismFloor = 2;

    public const int UploadParallelismFloor = DownloadParallelismFloor;

    /// <summary>Attempts per file (1 try + retries) on 429/503/transient network.</summary>
    public const int DownloadMaxAttempts = 4;

    public const int UploadMaxAttempts = DownloadMaxAttempts;

    /// <summary>
    /// Remote _autosave clear — never block Finish on this.
    /// Folder-level DELETE is one call; budget covers weak-cell + rare child fallback.
    /// </summary>
    public static readonly TimeSpan RemoteClearBudget = TimeSpan.FromSeconds(8);

    /// <summary>Mid-race autosave + DB's publish budget per mirror tick.</summary>
    public static readonly TimeSpan MidRacePublishBudget = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Admin Move to Past Races — ensure destination folders + Graph move (few calls, not a pack transfer).
    /// </summary>
    public static readonly TimeSpan MoveToPastRacesBudget = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Skip mid-race / backup noise on cellular.
    /// Logs and Events stay (small daily files — useful for support).
    /// </summary>
    public static readonly HashSet<string> SkipFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        PathService.AutosaveFolderName,
        PathService.AutosaveFolderName + PathService.AutosaveTombstoneSuffix,
        "._checkin_backup"
    };

    /// <summary>Mid-race: do not push large artifacts over cellular.</summary>
    public const long MidRaceMaxFileBytes = 2_000_000; // 2 MB

    /// <summary>Finish: allow primary .event and race DBs; still skip multi‑10MB junk.</summary>
    public const long FinishMaxFileBytes = 25_000_000; // 25 MB

    public static bool ShouldSkipFolder(string folderName) =>
        !string.IsNullOrEmpty(folderName) && SkipFolderNames.Contains(folderName);

    public static bool ShouldSkipFile(string filePath, long maxBytes)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
                return true;
            if (info.Length > maxBytes)
                return true;
            // Temp / lock noise
            var name = info.Name;
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("~", StringComparison.Ordinal))
                return true;
            return false;
        }
        catch
        {
            return true;
        }
    }
}
