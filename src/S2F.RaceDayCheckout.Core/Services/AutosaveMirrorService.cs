namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// While a checkout is held, mirror newer race files to OneDrive RUNSCORE\_autosave
/// so a 2nd laptop can recover mid-race after emergency reclaim.
/// Sources: local Backup (RunScore alternate) and Active RUNSCORE.
/// </summary>
public sealed class AutosaveMirrorService
{
    private readonly IFileCopyService _copy;
    private readonly ActiveCheckoutStore _activeStore;

    public AutosaveMirrorService(IFileCopyService copy, ActiveCheckoutStore activeStore)
    {
        _copy = copy;
        _activeStore = activeStore;
    }

    public async Task MirrorOnceAsync(CancellationToken cancellationToken = default)
    {
        var active = _activeStore.Load();
        if (active is null)
            return;

        Directory.CreateDirectory(active.AutosavePath);

        if (Directory.Exists(active.BackupPath))
            await _copy.MirrorNewerFilesAsync(active.BackupPath, active.AutosavePath, cancellationToken: cancellationToken);

        var activeRunScore = Path.Combine(active.ActivePath, PathService.RunScoreFolderName);
        if (Directory.Exists(activeRunScore))
            await _copy.MirrorNewerFilesAsync(activeRunScore, active.AutosavePath, cancellationToken: cancellationToken);
    }
}
