using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class CheckoutRequest
{
    public required EventPackInfo Pack { get; init; }
    public required AppSettings Settings { get; init; }
    public required string HolderName { get; init; }
    public string? PrimaryEventFileOverride { get; init; }
    public bool ConfirmEmergencyReclaim { get; init; }
    public string? EmergencyReason { get; init; }
}

public sealed class CheckoutResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public ActiveCheckout? Active { get; init; }
    public LockAcquireOutcome? Outcome { get; init; }
    public CheckoutLock? BlockingLock { get; init; }
}

public sealed class CheckinResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockingProcesses { get; init; } = Array.Empty<string>();
    public bool AwaitingOneDriveConfirm { get; init; }
}

public sealed class CheckoutService
{
    private readonly PathService _paths;
    private readonly CheckoutLockService _locks;
    private readonly IFileCopyService _copy;
    private readonly IProcessGuard _processes;
    private readonly ActiveCheckoutStore _activeStore;
    private readonly IOneDriveSyncMonitor _syncMonitor;
    private readonly IOneDriveHydrationService _hydrate;
    private readonly RunScorePreferencesService _runScorePrefs;

    public CheckoutService(
        PathService paths,
        CheckoutLockService locks,
        IFileCopyService copy,
        IProcessGuard processes,
        ActiveCheckoutStore activeStore,
        IOneDriveSyncMonitor? syncMonitor = null,
        IOneDriveHydrationService? hydrate = null,
        RunScorePreferencesService? runScorePrefs = null)
    {
        _paths = paths;
        _locks = locks;
        _copy = copy;
        _processes = processes;
        _activeStore = activeStore;
        _syncMonitor = syncMonitor ?? new SettlingOneDriveSyncMonitor();
        _hydrate = hydrate ?? new OneDriveHydrationService();
        _runScorePrefs = runScorePrefs ?? new RunScorePreferencesService();
    }

    public ActiveCheckout? GetActive() => _activeStore.Load();

    public async Task<CheckoutResult> CheckoutAsync(
        CheckoutRequest request,
        IProgress<CopyProgress>? progress = null,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        var existingActive = _activeStore.Load();
        if (existingActive is not null
            && !string.Equals(existingActive.LibrarySessionPath, request.Pack.Path, StringComparison.OrdinalIgnoreCase))
        {
            return new CheckoutResult
            {
                Success = false,
                Message =
                    $"This machine already has an Active checkout: {existingActive.DisplayName}. Finish & check in first."
            };
        }

        if (string.IsNullOrWhiteSpace(request.HolderName))
        {
            return new CheckoutResult
            {
                Success = false,
                Message = "Display name is required before checkout."
            };
        }

        var primary = request.PrimaryEventFileOverride
                      ?? request.Pack.PrimaryEventFile
                      ?? EventFileSelector.ChoosePrimary(request.Pack.EventFiles);
        if (string.IsNullOrWhiteSpace(primary))
        {
            return new CheckoutResult
            {
                Success = false,
                Message = "No .event file found in this session. Add a primary Timing & Scoring .event before checkout."
            };
        }

        var libraryRunScore = Path.Combine(request.Pack.Path, PathService.RunScoreFolderName);
        if (!Directory.Exists(libraryRunScore))
        {
            return new CheckoutResult
            {
                Success = false,
                Message = $"RUNSCORE folder missing: {libraryRunScore}"
            };
        }

        var libraryEventFile = Path.Combine(request.Pack.Path, primary);
        if (!File.Exists(libraryEventFile))
        {
            return new CheckoutResult
            {
                Success = false,
                Message = $"Primary .event not found: {libraryEventFile}"
            };
        }

        var eval = _locks.EvaluateAcquire(request.Pack.Path, request.Settings);
        switch (eval.Outcome)
        {
            case LockAcquireOutcome.BlockedAlive:
                return new CheckoutResult
                {
                    Success = false,
                    Outcome = eval.Outcome,
                    BlockingLock = eval.Lock,
                    Message = eval.Message
                };
            case LockAcquireOutcome.NeedsEmergencyConfirm:
                if (!request.ConfirmEmergencyReclaim || string.IsNullOrWhiteSpace(request.EmergencyReason))
                {
                    return new CheckoutResult
                    {
                        Success = false,
                        Outcome = eval.Outcome,
                        BlockingLock = eval.Lock,
                        Message = eval.Message
                    };
                }
                break;
            case LockAcquireOutcome.Acquired:
            case LockAcquireOutcome.SameMachineReclaim:
                break;
            default:
                return new CheckoutResult { Success = false, Message = eval.Message, Outcome = eval.Outcome };
        }

        var activePath = _paths.GetActivePath(request.Settings, request.Pack.Office, request.Pack.RelativePath);
        var backupPath = _paths.GetBackupPath(request.Settings, request.Pack.Office, request.Pack.RelativePath);
        var autosavePath = _paths.GetAutosavePath(request.Pack.Path);
        var activeRunScore = Path.Combine(activePath, PathService.RunScoreFolderName);
        var activeEventFile = Path.Combine(activePath, primary);

        Directory.CreateDirectory(activePath);
        Directory.CreateDirectory(backupPath);
        Directory.CreateDirectory(autosavePath);

        // Pull OneDrive Files On-Demand placeholders local before copy.
        statusProgress?.Report("Making sure race files are downloaded…");
        await _hydrate.HydrateDirectoryAsync(libraryRunScore, statusProgress, cancellationToken);
        await _hydrate.HydrateFileAsync(libraryEventFile, statusProgress, cancellationToken);
        if (Directory.Exists(autosavePath))
            await _hydrate.HydrateDirectoryAsync(autosavePath, statusProgress, cancellationToken);

        statusProgress?.Report("Copying race files to this laptop…");
        await _copy.CopyDirectoryAsync(libraryRunScore, activeRunScore, progress, cancellationToken);
        Directory.CreateDirectory(activePath);
        File.Copy(libraryEventFile, activeEventFile, overwrite: true);

        // Mid-race failover: apply newer OneDrive RUNSCORE\_autosave on top of Active.
        // (Primary laptop mirrors Backup → _autosave while racing; 2nd laptop must pick that up.)
        var autosaveMerged = 0;
        if (Directory.Exists(autosavePath)
            && Directory.EnumerateFiles(autosavePath, "*", SearchOption.AllDirectories).Any())
        {
            statusProgress?.Report("Applying live autosave from OneDrive…");
            var mergeProgress = new Progress<CopyProgress>(p =>
            {
                autosaveMerged = (int)p.FilesCopied;
                progress?.Report(p);
            });
            await _copy.MirrorNewerFilesAsync(autosavePath, activeRunScore, mergeProgress, cancellationToken);
            // Seed local Backup so RunScore alternate folder also has the recovered files.
            await _copy.MirrorNewerFilesAsync(activeRunScore, backupPath, cancellationToken: cancellationToken);
        }

        var scoped = new List<string> { PathService.RunScoreFolderName, primary };
        var emergency = eval.Outcome == LockAcquireOutcome.NeedsEmergencyConfirm;
        _locks.Acquire(
            request.Pack.Path,
            request.Pack.Office,
            request.Pack.ParentEvent,
            request.Pack.SessionName,
            request.HolderName,
            scoped,
            primaryEventFile: primary,
            reclaimReason: request.EmergencyReason,
            emergency: emergency);

        var active = new ActiveCheckout
        {
            Office = request.Pack.Office,
            ParentEvent = request.Pack.ParentEvent,
            SessionName = request.Pack.SessionName,
            RelativePath = request.Pack.RelativePath,
            LibrarySessionPath = request.Pack.Path,
            ActivePath = activePath,
            BackupPath = backupPath,
            AutosavePath = autosavePath,
            DbsPath = request.Pack.DbsPath,
            PrimaryEventFile = primary,
            ScopedPaths = scoped,
            CheckedOutAt = DateTimeOffset.UtcNow
        };
        _activeStore.Save(active);

        // Point RunScore alternate-save folders at this checkout's Backup before the timer opens RS.
        // Re-applied again on Open RunScore; doing it here covers Explorer / manual launches too.
        var prefResult = _runScorePrefs.ApplySop(backupPath);
        var prefNote = prefResult.Success
            ? (prefResult.UpdatedCount > 0
                ? $" RunScore preferences updated to SOP ({prefResult.UpdatedCount} change(s))."
                : " RunScore preferences already match SOP.")
            : $" RunScore preference SOP warning: {prefResult.Message}";

        var failoverNote = autosaveMerged > 0
            ? $" Applied {autosaveMerged} newer autosave file(s) from OneDrive."
            : Directory.Exists(autosavePath)
                ? " No newer autosave files were found on OneDrive (recovery may be limited to last Finish)."
                : "";

        return new CheckoutResult
        {
            Success = true,
            Outcome = eval.Outcome,
            Active = active,
            Message = emergency
                ? "Emergency reclaim succeeded. RUNSCORE + primary .event are local under Active."
                  + failoverNote
                  + prefNote
                  + " DB's / Pre-Race / other .events stay on OneDrive."
                : "Checkout complete. Launch RunScore/T&S from Active only."
                  + (autosaveMerged > 0 ? failoverNote : "")
                  + prefNote
        };
    }

    public async Task<CheckinResult> CheckinAsync(
        AppSettings settings,
        string holderName,
        bool removeActive = true,
        bool adminOverrideProcesses = false,
        bool waitForOneDrive = true,
        IProgress<CopyProgress>? progress = null,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        var active = _activeStore.Load();
        if (active is null)
            return new CheckinResult { Success = false, Message = "No Active checkout on this machine." };

        var blocking = _processes.GetBlockingProcessNames();
        if (blocking.Count > 0 && !adminOverrideProcesses)
        {
            return new CheckinResult
            {
                Success = false,
                BlockingProcesses = blocking,
                Message = "Close RunScore / Timing & Scoring before Finish: " + string.Join(", ", blocking)
            };
        }

        var activeRunScore = Path.Combine(active.ActivePath, PathService.RunScoreFolderName);
        var activeEvent = Path.Combine(active.ActivePath, active.PrimaryEventFile);
        if (!Directory.Exists(activeRunScore))
            return new CheckinResult { Success = false, Message = $"Active RUNSCORE missing: {activeRunScore}" };
        if (!File.Exists(activeEvent))
            return new CheckinResult { Success = false, Message = $"Active primary .event missing: {activeEvent}" };

        statusProgress?.Report("Backing up OneDrive scoped items…");
        var backupDest = _paths.GetCheckinBackupPath(active.LibrarySessionPath, DateTimeOffset.Now);
        var libraryRunScore = Path.Combine(active.LibrarySessionPath, PathService.RunScoreFolderName);
        var libraryEvent = Path.Combine(active.LibrarySessionPath, active.PrimaryEventFile);
        Directory.CreateDirectory(backupDest);
        if (Directory.Exists(libraryRunScore))
            await _copy.CopyDirectoryAsync(libraryRunScore, Path.Combine(backupDest, PathService.RunScoreFolderName), progress, cancellationToken);
        if (File.Exists(libraryEvent))
            File.Copy(libraryEvent, Path.Combine(backupDest, active.PrimaryEventFile), overwrite: true);

        statusProgress?.Report("Copying Active → OneDrive (RUNSCORE + primary .event)…");
        await _copy.CopyDirectoryAsync(activeRunScore, libraryRunScore, progress, cancellationToken);
        File.Copy(activeEvent, libraryEvent, overwrite: true);

        // Race is finished — clear mid-race autosave so the next checkout doesn't merge leftovers.
        TryClearAutosave(active.AutosavePath);

        _locks.Release(active.LibrarySessionPath, holderName);

        var syncOk = true;
        var syncMessage = string.Empty;
        if (waitForOneDrive)
        {
            statusProgress?.Report("Waiting for OneDrive to sync…");
            var sync = await _syncMonitor.WaitUntilSettledAsync(
                active.LibrarySessionPath,
                TimeSpan.FromSeconds(Math.Max(15, settings.OneDriveSyncWaitSeconds)),
                statusProgress,
                cancellationToken);
            syncOk = sync.Success;
            syncMessage = sync.Message;
        }

        if (removeActive)
        {
            try
            {
                if (Directory.Exists(active.ActivePath))
                    Directory.Delete(active.ActivePath, recursive: true);
            }
            catch (Exception ex)
            {
                _activeStore.Save(null);
                return new CheckinResult
                {
                    Success = true,
                    AwaitingOneDriveConfirm = !syncOk,
                    Message = syncOk
                        ? $"Checked in; could not remove Active folder: {ex.Message}. Safe to shut down after confirming OneDrive."
                        : $"Checked in; Active folder leftover ({ex.Message}). {syncMessage}"
                };
            }
        }

        _activeStore.Save(null);

        if (syncOk)
        {
            return new CheckinResult
            {
                Success = true,
                Message =
                    "Finish complete. Scoped files are on OneDrive and sync looks settled. You can shut down this laptop."
            };
        }

        return new CheckinResult
        {
            Success = true,
            AwaitingOneDriveConfirm = true,
            Message =
                "Finish copied files and released the lock, but OneDrive sync was not confirmed yet. " + syncMessage
                + " Check the OneDrive cloud icon, then shut down only when it shows synced."
        };
    }

    private static void TryClearAutosave(string autosavePath)
    {
        try
        {
            if (Directory.Exists(autosavePath))
                Directory.Delete(autosavePath, recursive: true);
        }
        catch
        {
            // OneDrive may lock briefly; leftover autosave is still OK (newer-only merge).
        }
    }
}
