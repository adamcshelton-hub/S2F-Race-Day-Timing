namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>While a checkout is held, mirror newer files from local Backup → OneDrive RUNSCORE\_autosave.</summary>
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
        if (!Directory.Exists(active.BackupPath))
            return;

        Directory.CreateDirectory(active.AutosavePath);
        await _copy.MirrorNewerFilesAsync(active.BackupPath, active.AutosavePath, cancellationToken: cancellationToken);
    }
}
