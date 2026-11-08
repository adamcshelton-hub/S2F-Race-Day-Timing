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

    /// <summary>
    /// One known-path GET for <c>._s2f/runscore-vendor-secrets.json</c> — tiny file, fail fast on cellular.
    /// </summary>
    public static readonly TimeSpan VendorSecretsBudget = TimeSpan.FromSeconds(20);

    /// <summary>Index one office’s event leaves — cancel if cellular stalls.</summary>
    public static readonly TimeSpan OfficeSessionsBudget = TimeSpan.FromSeconds(75);

    /// <summary>
    /// Whole Finish / Announcer Done cloud upload budget (after local save).
    /// Matches <see cref="SessionDownloadBudget"/> — Graph PUTs are latency-bound; a short
    /// 90s cap was falsely blaming "cellular" when speedtest Mbps looked fine.
    /// </summary>
    public static readonly TimeSpan FinishUploadBudget = TimeSpan.FromMinutes(3);

    /// <summary>Checkout session pack download (one event). Progress must stay live — cellular.</summary>
    public static readonly TimeSpan SessionDownloadBudget = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Concurrent file transfers for one session pack (download or upload). Folder-ZIP download is
    /// optional on Business OneDrive later; parallel GETs/PUTs are the launch path. Not an integrity
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
    /// Events and Logs are not downloaded — RunScore appends to an existing same-date file.
    /// A daily file over <see cref="FinishMaxFileBytes"/> is still attempted on upload.
    /// If that upload does not finish inside <see cref="FinishUploadBudget"/>, it stays
    /// on the laptop and does not keep the checkout lock.
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

    /// <summary>
    /// Checkout must not pull <c>RUNSCORE/Logs</c> or <c>RUNSCORE/Events</c>.
    /// Any date already on OneDrive stays there. RunScore creates today's file locally
    /// and Finish uploads it beside an existing name.
    /// </summary>
    public static bool ShouldSkipRunScoreDownloadFolder(string folderName) =>
        string.Equals(folderName, "Logs", StringComparison.OrdinalIgnoreCase)
        || string.Equals(folderName, "Events", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Second file name when <paramref name="fileName"/> is already on OneDrive.
    /// The existing cloud file is left in place.
    /// </summary>
    public static string DisambiguatedLogFileName(string fileName, string machineName, DateTime localTime)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var machine = SanitizeMachineName(machineName);
        return stem + "_" + machine + "_" + localTime.ToString("HHmmss") + ext;
    }

    /// <summary>
    /// Drop local <c>RUNSCORE\Logs</c> and <c>RUNSCORE\Events</c> so RunScore cannot
    /// append to a file copied from a previous checkout. RunScore creates today's files.
    /// </summary>
    public static void RemoveLocalRunScoreLogs(string runScoreDirectory)
    {
        if (string.IsNullOrWhiteSpace(runScoreDirectory))
            return;
        foreach (var folder in new[] { "Logs", "Events" })
        {
            var path = Path.Combine(runScoreDirectory, folder);
            if (!Directory.Exists(path))
                continue;
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // In use — RunScore may append. Checkout still does not download these folders.
            }
        }
    }

    private static string SanitizeMachineName(string machineName)
    {
        var raw = string.IsNullOrWhiteSpace(machineName) ? "laptop" : machineName.Trim();
        var chars = new char[Math.Min(raw.Length, 40)];
        var n = 0;
        foreach (var c in raw)
        {
            if (n >= chars.Length)
                break;
            chars[n++] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }

        var cleaned = new string(chars, 0, n).Trim('_');
        return cleaned.Length == 0 ? "laptop" : cleaned;
    }

    /// <summary>
    /// Dated ignored-chip files RunScore writes in the RUNSCORE folder itself
    /// (<c>yyyy-MM-dd_IgnoredRawChipReads.txt</c> and <c>yyyy-MM-dd_IgnoredCloudChipReads.txt</c>).
    /// </summary>
    public static bool IsIgnoredChipReadFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
            return false;
        return name.EndsWith("_IgnoredRawChipReads.txt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_IgnoredCloudChipReads.txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for an ignored-chip file whose date is before <paramref name="today"/>.
    /// Today's file is still uploaded. Older files stay on OneDrive and are not sent again.
    /// </summary>
    public static bool IsStaleIgnoredChipRead(string path, DateTime today)
    {
        if (!IsIgnoredChipReadFile(path))
            return false;
        var name = Path.GetFileName(path);
        if (name.Length < 10
            || !DateTime.TryParseExact(
                name.AsSpan(0, 10),
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var date))
            return false;
        return date.Date < today.Date;
    }

    /// <summary>Delete ignored-chip files sitting directly in a local RUNSCORE folder.</summary>
    public static void RemoveLocalIgnoredChipReads(string runScoreDirectory)
    {
        if (string.IsNullOrWhiteSpace(runScoreDirectory) || !Directory.Exists(runScoreDirectory))
            return;
        foreach (var file in Directory.EnumerateFiles(runScoreDirectory))
        {
            if (!IsIgnoredChipReadFile(file))
                continue;
            try
            {
                File.Delete(file);
            }
            catch
            {
                // In use — checkout still does not download these from OneDrive.
            }
        }
    }

    /// <summary>
    /// Session-relative path under <c>RUNSCORE/Logs/</c> or <c>RUNSCORE/Events/</c>.
    /// </summary>
    public static bool IsRunScoreLogRelative(string sessionRelativePath)
    {
        if (string.IsNullOrWhiteSpace(sessionRelativePath))
            return false;
        var norm = sessionRelativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        foreach (var folder in new[] { "Logs", "Events" })
        {
            var prefix = PathService.RunScoreFolderName
                + Path.DirectorySeparatorChar
                + folder
                + Path.DirectorySeparatorChar;
            if (norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when this path is a RunScore daily log that exists and is over the Finish cap.
    /// Finish still attempts the upload. The Finish time budget is the cutoff: if the
    /// log does not land before that budget ends, it stays on the laptop.
    /// </summary>
    public static bool IsOversizeRunScoreLog(string sessionRelativePath, string localFile, long maxBytes)
    {
        if (!IsRunScoreLogRelative(sessionRelativePath))
            return false;
        try
        {
            var info = new FileInfo(localFile);
            return info.Exists && info.Length > maxBytes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Normal Timing/Announcer session pack pull: download when local is missing, sizes differ,
    /// or OneDrive <c>lastModified</c> is clearly newer. Same-size content edits (e.g. Entries.ini
    /// timestamps) must not leave a stale <c>C:\S2F\Library</c> copy on the next laptop.
    /// Uses children-list metadata only — no extra Graph round-trip.
    /// </summary>
    public static bool NeedsSessionFileDownload(
        bool localExists,
        long localLength,
        DateTime localLastWriteUtc,
        long? remoteSize,
        DateTime? remoteLastModifiedUtc)
    {
        if (!localExists || remoteSize is not long remote)
            return true;
        if (localLength != remote)
            return true;
        // Same size — still pull when cloud is newer (2s skew). Matches Announcer DB's behavior.
        if (remoteLastModifiedUtc is { } remoteUtc
            && localLastWriteUtc < remoteUtc.AddSeconds(-2))
            return true;
        return false;
    }

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
