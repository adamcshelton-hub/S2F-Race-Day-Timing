namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// While a checkout is held, mirror newer race files to OneDrive RUNSCORE\_autosave
/// so a 2nd laptop can recover mid-race after emergency reclaim.
/// Sources: local Backup (RunScore alternate), Active RUNSCORE, and any misplaced
/// sibling *ENTRIES* files beside Backup (RunScore missing trailing-slash bug).
/// Requires the desktop app to stay open (not a Windows service).
/// </summary>
public sealed class AutosaveMirrorService
{
    private readonly IFileCopyService _copy;
    private readonly ActiveCheckoutStore _activeStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AutosaveMirrorService(IFileCopyService copy, ActiveCheckoutStore activeStore)
    {
        _copy = copy;
        _activeStore = activeStore;
    }

    public async Task MirrorOnceAsync(CancellationToken cancellationToken = default)
    {
        // Skip overlapping ticks — never stack parallel mirrors on the same race folder.
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            var active = _activeStore.Load();
            if (active is null)
                return;

            Directory.CreateDirectory(active.AutosavePath);

            if (Directory.Exists(active.BackupPath))
                await _copy.MirrorNewerFilesAsync(active.BackupPath, active.AutosavePath, cancellationToken: cancellationToken);

            // Recover files RunScore wrote beside Backup when the registry path lacked '\'.
            await MirrorMisplacedEntriesSiblingsAsync(active.BackupPath, active.AutosavePath, cancellationToken);

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
    /// Copies <c>*ENTRIES*</c> files that sit next to the Backup folder into _autosave
    /// under the normal ENTRIES.* names when possible.
    /// </summary>
    public static async Task MirrorMisplacedEntriesSiblingsAsync(
        string backupPath,
        string autosavePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || string.IsNullOrWhiteSpace(autosavePath))
            return;

        var parent = Path.GetDirectoryName(
            backupPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            return;

        var folderName = Path.GetFileName(
            backupPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName))
            return;

        Directory.CreateDirectory(autosavePath);

        foreach (var file in Directory.EnumerateFiles(parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (!name.Contains("ENTRIES", StringComparison.OrdinalIgnoreCase))
                continue;

            // Prefer demangling "EventNameENTRIES.DTA" → "ENTRIES.DTA" for reclaim merge.
            var destName = name;
            if (name.StartsWith(folderName, StringComparison.OrdinalIgnoreCase)
                && name.Length > folderName.Length)
            {
                var rest = name[folderName.Length..];
                if (rest.StartsWith("ENTRIES", StringComparison.OrdinalIgnoreCase))
                    destName = rest;
            }

            var dest = Path.Combine(autosavePath, destName);
            if (File.Exists(dest))
            {
                var srcInfo = new FileInfo(file);
                var dstInfo = new FileInfo(dest);
                if (srcInfo.LastWriteTimeUtc <= dstInfo.LastWriteTimeUtc && srcInfo.Length == dstInfo.Length)
                    continue;
            }

            await using (var src = File.OpenRead(file))
            await using (var dst = File.Create(dest))
                await src.CopyToAsync(dst, cancellationToken);

            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file));
        }
    }
}
