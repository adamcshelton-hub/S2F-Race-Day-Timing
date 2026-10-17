namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// While a checkout is held, mirror newer race files to OneDrive RUNSCORE\_autosave
/// so a 2nd laptop can recover mid-race after emergency reclaim.
/// Sources: local Backup (RunScore alternate), Active RUNSCORE, and any misplaced
/// sibling files beside Backup (RunScore missing trailing-slash bug).
/// Requires the desktop app to stay open (not a Windows service).
/// </summary>
public sealed class AutosaveMirrorService
{
    private readonly IFileCopyService _copy;
    private readonly ActiveCheckoutStore _activeStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _paused;

    public AutosaveMirrorService(IFileCopyService copy, ActiveCheckoutStore activeStore)
    {
        _copy = copy;
        _activeStore = activeStore;
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

        try
        {
            if (_paused)
                return;

            var active = _activeStore.Load();
            if (active is null)
                return;

            Directory.CreateDirectory(active.BackupPath);
            Directory.CreateDirectory(active.AutosavePath);

            // Heal files RunScore wrote beside Backup when the registry path lacked '\'
            // (e.g. "2026 - EventENTRIES.DTA" in the office folder).
            await RecoverMisplacedBackupSiblingsAsync(
                active.BackupPath,
                active.AutosavePath,
                cancellationToken);

            if (Directory.Exists(active.BackupPath))
                await _copy.MirrorNewerFilesAsync(active.BackupPath, active.AutosavePath, cancellationToken: cancellationToken);

            var activeRunScore = Path.Combine(active.ActivePath, PathService.RunScoreFolderName);
            if (Directory.Exists(activeRunScore))
                await _copy.MirrorNewerFilesAsync(activeRunScore, active.AutosavePath, cancellationToken: cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Demangles <c>EventNameENTRIES.DTA</c> / <c>EventNameFinish.FN2</c> siblings next to the
    /// Backup folder into the race Backup (and optional _autosave) under normal names.
    /// </summary>
    public static Task MirrorMisplacedEntriesSiblingsAsync(
        string backupPath,
        string autosavePath,
        CancellationToken cancellationToken = default) =>
        RecoverMisplacedBackupSiblingsAsync(backupPath, autosavePath, cancellationToken);

    public static async Task RecoverMisplacedBackupSiblingsAsync(
        string backupPath,
        string? autosavePath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            return;

        var trimmedBackup = backupPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmedBackup);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            return;

        var folderName = Path.GetFileName(trimmedBackup);
        if (string.IsNullOrWhiteSpace(folderName))
            return;

        Directory.CreateDirectory(trimmedBackup);
        if (!string.IsNullOrWhiteSpace(autosavePath))
            Directory.CreateDirectory(autosavePath);

        foreach (var file in Directory.EnumerateFiles(parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (!name.StartsWith(folderName, StringComparison.OrdinalIgnoreCase)
                || name.Length <= folderName.Length)
                continue;

            // "2026 - EventENTRIES.DTA" → "ENTRIES.DTA"; same for Finish/status/etc.
            var destName = name[folderName.Length..];
            if (string.IsNullOrWhiteSpace(destName) || destName.StartsWith('.'))
                continue;

            var backupDest = Path.Combine(trimmedBackup, destName);
            var copiedToBackup = await CopyIfNewerAsync(file, backupDest, cancellationToken);

            if (!string.IsNullOrWhiteSpace(autosavePath))
                await CopyIfNewerAsync(file, Path.Combine(autosavePath, destName), cancellationToken);

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
    }

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
