using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class CheckoutRequest
{
    public required EventPackInfo Pack { get; init; }
    public required AppSettings Settings { get; init; }
    public required string HolderName { get; init; }
    public string? PrimaryEventFileOverride { get; init; }
    public bool ConfirmEmergencyReclaim { get; init; }
    /// <summary>Admin PIN confirmed force take-over while the other lock still looks alive.</summary>
    public bool ConfirmForceTakeover { get; init; }
    public string? EmergencyReason { get; init; }
}

public sealed class CheckoutResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public ActiveCheckout? Active { get; init; }
    public LockAcquireOutcome? Outcome { get; init; }
    public CheckoutLock? BlockingLock { get; init; }
    /// <summary>
    /// Registry SOP was written while RunScore was already running — timer must close RS
    /// and reopen via this app so the new alternate-save folder is loaded.
    /// </summary>
    public bool RunScoreNeedsRestart { get; init; }
}

public sealed class CheckinResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockingProcesses { get; init; } = Array.Empty<string>();
    public bool AwaitingOneDriveConfirm { get; init; }
    /// <summary>False when Finish could not delete OneDrive RUNSCORE\_autosave (retry / ask setup).</summary>
    public bool AutosaveCleared { get; init; } = true;
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
    private readonly SharedFolderLinkService _links;

    public CheckoutService(
        PathService paths,
        CheckoutLockService locks,
        IFileCopyService copy,
        IProcessGuard processes,
        ActiveCheckoutStore activeStore,
        IOneDriveSyncMonitor? syncMonitor = null,
        IOneDriveHydrationService? hydrate = null,
        RunScorePreferencesService? runScorePrefs = null,
        SharedFolderLinkService? links = null)
    {
        _paths = paths;
        _locks = locks;
        _copy = copy;
        _processes = processes;
        _activeStore = activeStore;
        _syncMonitor = syncMonitor ?? new SettlingOneDriveSyncMonitor();
        _hydrate = hydrate ?? new OneDriveHydrationService();
        _runScorePrefs = runScorePrefs ?? new RunScorePreferencesService();
        _links = links ?? new SharedFolderLinkService();
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
                if (!request.ConfirmForceTakeover || string.IsNullOrWhiteSpace(request.EmergencyReason))
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
        PathService.TryMarkHidden(autosavePath);
        TryRemoveAutosaveTombstone(autosavePath);

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

        // Keep RunScore relative exports (Backupsave / announcersave → ~..\DB's\…) on OneDrive.
        statusProgress?.Report("Linking Active DB's to OneDrive…");
        var dbsLink = _links.EnsureDbsLink(activePath, request.Pack.Path);
        var dbsLinkNote = dbsLink.Success
            ? (dbsLink.Created ? " Active DB's linked to OneDrive for announcer/backup exports." : "")
            : $" DB's link warning: {dbsLink.Message}";

        // Mid-race failover: apply newer OneDrive RUNSCORE\_autosave only on reclaim/force.
        // Normal checkout must NOT merge leftovers — Finish clears _autosave so post-race
        // OneDrive edits are not overwritten by stale mid-race mirrors.
        var reclaim =
            eval.Outcome is LockAcquireOutcome.NeedsEmergencyConfirm or LockAcquireOutcome.SameMachineReclaim
            || request.ConfirmForceTakeover;
        var autosaveMerged = 0;
        if (reclaim
            && Directory.Exists(autosavePath)
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
        var emergency =
            eval.Outcome == LockAcquireOutcome.NeedsEmergencyConfirm || request.ConfirmForceTakeover;
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
        // Re-applied on Open RunScore only when RS is not already running (prefs load at process start).
        var prefResult = _runScorePrefs.ApplySop(backupPath);

        // Heal any mangled siblings left by a prior missing-trailing-slash registry path.
        try
        {
            await AutosaveMirrorService.RecoverMisplacedBackupSiblingsAsync(
                backupPath, autosavePath, cancellationToken);
        }
        catch
        {
            // Non-fatal — mirror timer will retry while checkout is held.
        }
        var rsRunning = _processes.GetRunScoreProcessNames();
        var runScoreNeedsRestart = rsRunning.Count > 0;
        var prefNote = prefResult.Success
            ? (prefResult.UpdatedCount > 0
                ? $" RunScore preferences updated to SOP ({prefResult.UpdatedCount} change(s))."
                : " RunScore preferences already match SOP.")
            : $" RunScore preference SOP warning: {prefResult.Message}";
        if (runScoreNeedsRestart)
        {
            prefNote +=
                " RunScore was already open (" + string.Join(", ", rsRunning)
                + ") — close it, then tap Open RunScore in this app so the new autosave folder takes effect.";
        }

        var failoverNote = autosaveMerged > 0
            ? $" Applied {autosaveMerged} newer autosave file(s) from OneDrive."
            : reclaim && Directory.Exists(autosavePath)
                ? " No newer autosave files were found on OneDrive (recovery may be limited to last Finish)."
                : "";

        var reclaimLead = request.ConfirmForceTakeover
            ? "Force take-over succeeded. RUNSCORE + primary .event are local under Active."
            : emergency
                ? "Emergency reclaim succeeded. RUNSCORE + primary .event are local under Active."
                : null;

        return new CheckoutResult
        {
            Success = true,
            Outcome = eval.Outcome,
            Active = active,
            RunScoreNeedsRestart = runScoreNeedsRestart,
            Message = reclaimLead is not null
                ? reclaimLead
                  + failoverNote
                  + prefNote
                  + dbsLinkNote
                  + " DB's stay on OneDrive (linked from Active); Pre-Race / other .events stay on OneDrive."
                : "Checkout complete. Launch RunScore/T&S from Active only."
                  + prefNote
                  + dbsLinkNote
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

        statusProgress?.Report("finish:backup");
        _paths.EnsureAppMetaFolder(active.LibrarySessionPath);
        var backupDest = _paths.GetCheckinBackupPath(active.LibrarySessionPath, DateTimeOffset.Now);
        var libraryRunScore = Path.Combine(active.LibrarySessionPath, PathService.RunScoreFolderName);
        var libraryEvent = Path.Combine(active.LibrarySessionPath, active.PrimaryEventFile);
        Directory.CreateDirectory(backupDest);
        if (Directory.Exists(libraryRunScore))
            await _copy.CopyDirectoryAsync(libraryRunScore, Path.Combine(backupDest, PathService.RunScoreFolderName), progress, cancellationToken);
        if (File.Exists(libraryEvent))
            File.Copy(libraryEvent, Path.Combine(backupDest, active.PrimaryEventFile), overwrite: true);

        statusProgress?.Report("finish:copy");
        await _copy.CopyDirectoryAsync(activeRunScore, libraryRunScore, progress, cancellationToken);
        File.Copy(activeEvent, libraryEvent, overwrite: true);

        // Clear mid-race autosave ASAP (before the OneDrive settle wait). Waiting first kept
        // OneDrive locks on _autosave and made Finish both slow and unable to delete it.
        var autosavePath = active.AutosavePath;
        statusProgress?.Report("finish:autosave");
        ClearAutosaveWithRetry(autosavePath);

        _locks.Release(active.LibrarySessionPath, holderName);

        var syncOk = true;
        var syncMessage = string.Empty;
        if (waitForOneDrive)
        {
            statusProgress?.Report("finish:sync");
            var sync = await _syncMonitor.WaitUntilSettledAsync(
                active.LibrarySessionPath,
                TimeSpan.FromSeconds(Math.Max(15, settings.OneDriveSyncWaitSeconds)),
                statusProgress,
                cancellationToken);
            syncOk = sync.Success;
            syncMessage = sync.Message;
        }

        string? activeRemoveError = null;
        if (removeActive)
        {
            statusProgress?.Report("finish:cleanup");
            try
            {
                // Drop DB's junction/symlink first — recursive delete must not wipe OneDrive DB's.
                if (Directory.Exists(active.ActivePath))
                    _links.DeleteActiveTreeSafe(active.ActivePath);
            }
            catch (Exception ex)
            {
                activeRemoveError = ex.Message;
            }
        }

        // Drop local active record, then one last clear (mirror is paused by the UI).
        _activeStore.Save(null);
        statusProgress?.Report("finish:autosave");
        var autosaveCleared = ClearAutosaveWithRetry(autosavePath);
        var autosaveNote = autosaveCleared
            ? string.Empty
            : " Live autosave files could not be fully removed — ask setup to delete RUNSCORE\\_autosave before the next race.";

        if (activeRemoveError is not null)
        {
            return new CheckinResult
            {
                Success = true,
                AutosaveCleared = autosaveCleared,
                AwaitingOneDriveConfirm = !syncOk,
                Message = syncOk
                    ? $"Checked in; could not remove Active folder: {activeRemoveError}. Safe to shut down after confirming OneDrive."
                      + autosaveNote
                    : $"Checked in; Active folder leftover ({activeRemoveError}). {syncMessage}" + autosaveNote
            };
        }

        if (syncOk)
        {
            return new CheckinResult
            {
                Success = true,
                AutosaveCleared = autosaveCleared,
                Message =
                    "Finish complete. Scoped files are on OneDrive and sync looks settled. You can shut down this laptop."
                    + autosaveNote
            };
        }

        return new CheckinResult
        {
            Success = true,
            AutosaveCleared = autosaveCleared,
            AwaitingOneDriveConfirm = true,
            Message =
                "Finish copied files and released the lock, but OneDrive sync was not confirmed yet. " + syncMessage
                + " Check the OneDrive cloud icon, then shut down only when it shows synced."
                + autosaveNote
        };
    }

    /// <summary>
    /// Removes OneDrive RUNSCORE\_autosave contents (and the folder when possible).
    /// Success = no files left. An empty leftover folder is OK — reclaim only merges when files exist.
    /// </summary>
    public static bool ClearAutosaveWithRetry(string autosavePath)
    {
        if (string.IsNullOrWhiteSpace(autosavePath) || !Directory.Exists(autosavePath))
            return true;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            TryDeleteAutosaveTree(autosavePath);

            if (IsAutosaveCleared(autosavePath))
            {
                if (Directory.Exists(autosavePath))
                    PathService.TryMarkHidden(autosavePath);
                return true;
            }

            Thread.Sleep(120 * (attempt + 1));
        }

        var cleared = IsAutosaveCleared(autosavePath);
        if (Directory.Exists(autosavePath))
            PathService.TryMarkHidden(autosavePath);
        TryRemoveAutosaveTombstone(autosavePath);
        return cleared;
    }

    /// <summary>True when folder is gone or contains no files (empty dir is safe for reclaim).</summary>
    public static bool IsAutosaveCleared(string autosavePath)
    {
        if (string.IsNullOrWhiteSpace(autosavePath) || !Directory.Exists(autosavePath))
            return true;

        try
        {
            return !Directory.EnumerateFiles(autosavePath, "*", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteAutosaveTree(string autosavePath)
    {
        if (!Directory.Exists(autosavePath))
            return;

        ClearReadOnlyRecursive(autosavePath);
        TryDeleteAutosaveContents(autosavePath);

        // OneDrive often blocks Directory.Delete on a hot folder — rename then delete.
        try
        {
            if (Directory.Exists(autosavePath)
                && !Directory.EnumerateFiles(autosavePath, "*", SearchOption.AllDirectories).Any())
            {
                var tombstone = autosavePath.TrimEnd('\\', '/') + PathService.AutosaveTombstoneSuffix;
                if (Directory.Exists(tombstone))
                {
                    try { Directory.Delete(tombstone, recursive: true); }
                    catch { PathService.TryMarkHidden(tombstone); }
                }

                try
                {
                    Directory.Move(autosavePath, tombstone);
                    PathService.TryMarkHidden(tombstone);
                    Directory.Delete(tombstone, recursive: true);
                    return;
                }
                catch
                {
                    PathService.TryMarkHidden(tombstone);
                    try { Directory.Delete(autosavePath, recursive: false); }
                    catch
                    {
                        PathService.TryMarkHidden(autosavePath);
                    }
                }
            }
            else if (Directory.Exists(autosavePath))
            {
                Directory.Delete(autosavePath, recursive: true);
            }
        }
        catch
        {
            // Contents may still be cleared; IsAutosaveCleared decides success.
        }
    }

    private static void TryRemoveAutosaveTombstone(string autosavePath)
    {
        var tombstone = autosavePath.TrimEnd('\\', '/') + PathService.AutosaveTombstoneSuffix;
        if (!Directory.Exists(tombstone))
            return;
        try
        {
            Directory.Delete(tombstone, recursive: true);
        }
        catch
        {
            // Keep it out of the way if OneDrive still holds it.
            PathService.TryMarkHidden(tombstone);
        }
    }

    private static void ClearReadOnlyRecursive(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // continue — delete may still succeed
                }
            }
        }
        catch
        {
            // directory may already be gone / inaccessible
        }
    }

    private static void TryDeleteAutosaveContents(string autosavePath)
    {
        if (!Directory.Exists(autosavePath))
            return;

        foreach (var file in Directory.EnumerateFiles(autosavePath, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch
            {
                // OneDrive / share lock — next attempt
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(autosavePath, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                Directory.Delete(dir, recursive: false);
            }
            catch
            {
                // nested retry on next loop
            }
        }
    }
}
