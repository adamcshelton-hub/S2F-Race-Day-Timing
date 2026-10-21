using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Mid-race failover overlay — <b>not</b> a full RUNSCORE sync.
/// <para>
/// Intended model: last finished pack on OneDrive + small RS alternate-save files
/// (Save entries/results dir → RaceDay\Backup). On reclaim, download the pack then
/// overlay <c>RUNSCORE\_autosave</c>. This service only pushes those small Backup
/// artifacts into Library <c>_autosave</c> (and Graph). Never mirrors Active RUNSCORE.
/// </para>
/// Graph publish retries until confirmed: MirrorNewer alone is not enough — after a failed
/// PUT the local files match Backup so a naive "only publish what just copied" never retries.
/// Also heals mangled siblings beside Backup (missing trailing-slash registry bug).
/// Requires the desktop app to stay open (not a Windows service).
/// </summary>
public sealed class AutosaveMirrorService
{
    private readonly IFileCopyService _copy;
    private readonly ActiveCheckoutStore _activeStore;
    private readonly ICloudLibrarySync _cloud;
    private readonly Func<AppSettings> _settings;
    /// <summary>
    /// When set, mid-race Library/_autosave + Graph publish run only if this returns true
    /// (this machine still holds the cloud checkout lock).
    /// </summary>
    private readonly Func<CancellationToken, Task<bool>>? _stillOwnsLockAsync;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _paused;

    /// <summary>Local paths that still need a successful Graph PUT this process lifetime.</summary>
    private readonly HashSet<string> _pendingCloudPublish = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fingerprint of last successful mid-race PUT (path → length + mtime).
    /// Cleared on process restart so a cold start re-publishes any leftover _autosave once.
    /// </summary>
    private readonly Dictionary<string, (long Length, long MtimeTicks)> _publishedFingerprint =
        new(StringComparer.OrdinalIgnoreCase);

    public AutosaveMirrorService(
        IFileCopyService copy,
        ActiveCheckoutStore activeStore,
        ICloudLibrarySync? cloud = null,
        Func<AppSettings>? settings = null,
        Func<CancellationToken, Task<bool>>? stillOwnsLockAsync = null)
    {
        _copy = copy;
        _activeStore = activeStore;
        _cloud = cloud ?? new NullCloudLibrarySync();
        _settings = settings ?? (() => new AppSettings());
        _stillOwnsLockAsync = stillOwnsLockAsync;
    }

    /// <summary>Stop accepting new mirror ticks (Finish / check-in).</summary>
    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    /// <summary>Wait until any in-flight mirror finishes so Finish can delete _autosave safely.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _gate.Release();
    }

    public async Task MirrorOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_paused)
            return;

        // Skip overlapping ticks — never stack parallel mirrors on the same race folder.
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        var released = false;
        try
        {
            if (_paused)
                return;

            var active = _activeStore.Load();
            if (active is null || active.Role == CheckoutRole.Announcer)
                return;

            // Soft/force takeover on another laptop — do not write Library _autosave or Graph.
            if (_stillOwnsLockAsync is not null
                && !await _stillOwnsLockAsync(cancellationToken).ConfigureAwait(false))
                return;

            Directory.CreateDirectory(active.BackupPath);
            Directory.CreateDirectory(active.AutosavePath);
            PathService.TryMarkHidden(active.AutosavePath);

            // Heal files RunScore wrote beside Backup when the registry path lacked '\'
            // (e.g. "2026 - EventENTRIES.DTA" in the office folder).
            var healedAutosave = await RecoverMisplacedBackupSiblingsAsync(
                active.BackupPath,
                active.AutosavePath,
                cancellationToken);

            // Small overlay only — RS alternate Save entries/results → Backup → _autosave.
            // Do NOT copy Active RUNSCORE here (that was the cellular full-pack tax).
            IReadOnlyList<string> autosaveCopied = Array.Empty<string>();
            if (Directory.Exists(active.BackupPath))
            {
                autosaveCopied = await _copy.MirrorNewerFilesAsync(
                    active.BackupPath, active.AutosavePath, cancellationToken: cancellationToken);
            }

            var activeDbs = Path.Combine(active.ActivePath, SharedFolderLinkService.DbsFolderName);
            var libraryDbs = Path.Combine(active.LibrarySessionPath, SharedFolderLinkService.DbsFolderName);
            IReadOnlyList<string> dbsCopied = Array.Empty<string>();
            if (Directory.Exists(activeDbs)
                && (File.GetAttributes(activeDbs) & FileAttributes.ReparsePoint) == 0)
            {
                Directory.CreateDirectory(libraryDbs);
                dbsCopied = await _copy.MirrorNewerFilesAsync(
                    activeDbs, libraryDbs, cancellationToken: cancellationToken);
            }

            EnqueuePublish(healedAutosave);
            EnqueuePublish(autosaveCopied);
            EnqueuePublish(dbsCopied);
            // Catch files already in _autosave that never got a confirmed Graph PUT
            // (failed prior tick, or app restarted with leftover overlay on disk).
            EnqueueUnpublishedAutosaveFiles(active.AutosavePath);

            var publishPaths = SnapshotPendingPublish();

            // Release gate before Graph so the next local mirror tick is never blocked by cellular.
            _gate.Release();
            released = true;

            // Skip Graph when local mirror copied nothing AND nothing is pending — cellular.
            if (_cloud.IsAvailable && publishPaths.Count > 0)
                await PublishMidRaceCloudAsync(publishPaths, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!released)
                _gate.Release();
        }
    }

    private void EnqueuePublish(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
                _pendingCloudPublish.Add(path);
        }
    }

    private void EnqueueUnpublishedAutosaveFiles(string autosavePath)
    {
        if (!Directory.Exists(autosavePath))
            return;

        foreach (var file in Directory.EnumerateFiles(autosavePath, "*", SearchOption.AllDirectories))
        {
            if (NeedsCloudPublish(file))
                _pendingCloudPublish.Add(file);
        }
    }

    private bool NeedsCloudPublish(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
                return false;
            if (!_publishedFingerprint.TryGetValue(file, out var prior))
                return true;
            return prior.Length != info.Length || prior.MtimeTicks != info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return true;
        }
    }

    private List<string> SnapshotPendingPublish() =>
        _pendingCloudPublish.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private async Task PublishMidRaceCloudAsync(
        IReadOnlyList<string> localFilesToPublish,
        CancellationToken cancellationToken)
    {
        try
        {
            using var publishCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            publishCts.CancelAfter(GraphSyncPolicy.MidRacePublishBudget);
            var settings = _settings();
            var result = await _cloud.UploadLocalFilesAsync(
                localFilesToPublish,
                settings.LibraryRoot,
                settings.GraphDriveRoot,
                publishCts.Token).ConfigureAwait(false);

            if (result.Success)
                MarkPublished(localFilesToPublish);
            // Failure: leave pending + fingerprints unchanged so the next tick retries.
        }
        catch
        {
            // Weak signal — next mirror tick retries; local Active/Backup/autosave remain authoritative.
        }
    }

    private void MarkPublished(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    _pendingCloudPublish.Remove(path);
                    _publishedFingerprint.Remove(path);
                    continue;
                }

                var info = new FileInfo(path);
                _publishedFingerprint[path] = (info.Length, info.LastWriteTimeUtc.Ticks);
                _pendingCloudPublish.Remove(path);
            }
            catch
            {
                // Keep pending — next tick retries.
            }
        }
    }

    /// <summary>
    /// Demangles <c>EventNameENTRIES.DTA</c> / <c>EventNameFinish.FN2</c> siblings next to the
    /// Backup folder into the race Backup (and optional _autosave) under normal names.
    /// </summary>
    public static Task<IReadOnlyList<string>> MirrorMisplacedEntriesSiblingsAsync(
        string backupPath,
        string autosavePath,
        CancellationToken cancellationToken = default) =>
        RecoverMisplacedBackupSiblingsAsync(backupPath, autosavePath, cancellationToken);

    /// <returns>Absolute paths written under <paramref name="autosavePath"/> (for Graph mid-race PUT).</returns>
    public static async Task<IReadOnlyList<string>> RecoverMisplacedBackupSiblingsAsync(
        string backupPath,
        string? autosavePath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            return Array.Empty<string>();

        var trimmedBackup = backupPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmedBackup);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            return Array.Empty<string>();

        var folderName = Path.GetFileName(trimmedBackup);
        if (string.IsNullOrWhiteSpace(folderName))
            return Array.Empty<string>();

        Directory.CreateDirectory(trimmedBackup);
        if (!string.IsNullOrWhiteSpace(autosavePath))
        {
            Directory.CreateDirectory(autosavePath);
            PathService.TryMarkHidden(autosavePath);
        }

        var autosaveWritten = new List<string>();
        foreach (var file in Directory.EnumerateFiles(parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (!TryDemangleMisplacedSibling(name, folderName, out var destName))
                continue;

            var backupDest = Path.Combine(trimmedBackup, destName);
            var copiedToBackup = await CopyIfNewerAsync(file, backupDest, cancellationToken);

            if (!string.IsNullOrWhiteSpace(autosavePath))
            {
                var autosaveDest = Path.Combine(autosavePath, destName);
                if (await CopyIfNewerAsync(file, autosaveDest, cancellationToken))
                    autosaveWritten.Add(autosaveDest);
            }

            // Remove the mangled sibling once the race folder has a usable copy.
            if (copiedToBackup || File.Exists(backupDest))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // File may still be open in RunScore — next mirror tick will retry.
                }
            }
        }

        return autosaveWritten;
    }

    /// <summary>
    /// RunScore missing-'\' bug concatenates folder + artifact with no separator
    /// (e.g. "2026 - EventENTRIES.DTA"). Reject prefix collisions like
    /// "2026 - Event TEST…" or unrelated "2026 - Events.pdf".
    /// </summary>
    public static bool TryDemangleMisplacedSibling(string fileName, string folderName, out string destName)
    {
        destName = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(folderName))
            return false;
        if (!fileName.StartsWith(folderName, StringComparison.OrdinalIgnoreCase)
            || fileName.Length <= folderName.Length)
            return false;

        var rest = fileName[folderName.Length..];
        // Next char must start the artifact name (not space/dash from a longer event name).
        if (rest.Length == 0 || !char.IsLetter(rest[0]))
            return false;

        if (!MangledRunScoreStems.Any(stem => rest.StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Require an extension so we don't swallow odd extensionless leftovers.
        if (rest.IndexOf('.') < 1)
            return false;

        destName = rest;
        return true;
    }

    private static readonly string[] MangledRunScoreStems =
    [
        "ENTRIES",
        "Finish",
        "status",
        "Results",
        "TIMES",
        "BIBS"
    ];

    private static async Task<bool> CopyIfNewerAsync(
        string source,
        string dest,
        CancellationToken cancellationToken)
    {
        if (File.Exists(dest))
        {
            var srcInfo = new FileInfo(source);
            var dstInfo = new FileInfo(dest);
            if (srcInfo.LastWriteTimeUtc <= dstInfo.LastWriteTimeUtc && srcInfo.Length == dstInfo.Length)
                return false;
        }

        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        await using (var src = File.OpenRead(source))
        await using (var dst = File.Create(dest))
            await src.CopyToAsync(dst, cancellationToken);

        File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
        return true;
    }
}
