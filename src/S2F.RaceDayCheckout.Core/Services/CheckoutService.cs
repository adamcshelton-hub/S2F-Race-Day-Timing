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
    public CheckoutRole Role { get; init; } = CheckoutRole.Timing;

    /// <summary>
    /// UI already downloaded this session pack from Graph before checkout — skip a second pull (cellular).
    /// </summary>
    public bool SkipGraphDownload { get; init; }
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
    /// <summary>False when Finish could not delete OneDrive RUNSCORE\_autosave (retry / ask setup).</summary>
    public bool AutosaveCleared { get; init; } = true;
    /// <summary>
    /// Local library save succeeded but cloud upload did not confirm — keep Active + lock
    /// so staff can tap Finish again without another machine taking a stale cloud pack.
    /// </summary>
    public bool RetainActiveForCloudRetry { get; init; }
}

public sealed class CheckoutService
{
    private readonly PathService _paths;
    private readonly CheckoutLockService _locks;
    private readonly IFileCopyService _copy;
    private readonly IProcessGuard _processes;
    private readonly ActiveCheckoutStore _activeStore;
    private readonly RunScorePreferencesService _runScorePrefs;
    private readonly SharedFolderLinkService _links;
    private readonly ICloudLibrarySync _cloud;

    public CheckoutService(
        PathService paths,
        CheckoutLockService locks,
        IFileCopyService copy,
        IProcessGuard processes,
        ActiveCheckoutStore activeStore,
        RunScorePreferencesService? runScorePrefs = null,
        SharedFolderLinkService? links = null,
        ICloudLibrarySync? cloud = null)
    {
        _paths = paths;
        _locks = locks;
        _copy = copy;
        _processes = processes;
        _activeStore = activeStore;
        _runScorePrefs = runScorePrefs ?? new RunScorePreferencesService();
        _links = links ?? new SharedFolderLinkService();
        _cloud = cloud ?? new NullCloudLibrarySync();
    }

    public ActiveCheckout? GetActive() => _activeStore.Load();

    public async Task<CheckoutResult> CheckoutAsync(
        CheckoutRequest request,
        IProgress<CopyProgress>? progress = null,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        var existingActive = _activeStore.Load();
        if (existingActive is not null)
        {
            var sameSession = string.Equals(
                existingActive.LibrarySessionPath, request.Pack.Path, StringComparison.OrdinalIgnoreCase);
            if (!sameSession || existingActive.Role != request.Role)
            {
                var leave = existingActive.Role == CheckoutRole.Announcer ? "Tap Done" : "Finish & check in";
                return new CheckoutResult
                {
                    Success = false,
                    Message = $"This machine already has an Active checkout: {existingActive.DisplayName}. {leave} first."
                };
            }
        }

        if (string.IsNullOrWhiteSpace(request.HolderName))
        {
            return new CheckoutResult
            {
                Success = false,
                Message = "Display name is required before checkout."
            };
        }

        if (request.Role == CheckoutRole.Announcer)
            return await CheckoutAnnouncerAsync(request, progress, statusProgress, cancellationToken).ConfigureAwait(false);

        // .event is optional at checkout (Templates often have RUNSCORE only).
        // Staff can Open Timing & Scoring bare, Save As into Active, then Finish picks it up.
        var hintedPrimary = !string.IsNullOrWhiteSpace(request.PrimaryEventFileOverride)
            ? request.PrimaryEventFileOverride!
            : request.Pack.PrimaryEventFile
              ?? EventFileSelector.ChoosePrimary(request.Pack.EventFiles)
              ?? string.Empty;
        // Sidecar primary wins once that file is on disk (after Graph download or a local pack).

        var useGraph = _cloud.IsAvailable;
        if (!useGraph)
        {
            return new CheckoutResult
            {
                Success = false,
                Message =
                    "Sign in to OneDrive before loading a race. "
                    + "This laptop does not use the OneDrive desktop app — checkout downloads through this app only."
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

        if (useGraph && !request.SkipGraphDownload)
        {
            statusProgress?.Report("Downloading race pack from OneDrive…");
            CloudSyncResult pull;
            try
            {
                using var pullCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Must match DownloadSessionAsync's own budget — FinishUploadBudget (90s) was truncating packs.
                pullCts.CancelAfter(GraphSyncPolicy.SessionDownloadBudget);
                pull = await _cloud.DownloadSessionAsync(
                    request.Pack.Path,
                    request.Settings.LibraryRoot,
                    request.Settings.GraphDriveRoot,
                    statusProgress,
                    pullCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                pull = new CloudSyncResult
                {
                    Success = false,
                    Message = "Download timed out on cellular. Using local library files if present."
                };
            }

            // Prefer local pack when cloud pull fails/times out — do not block race morning.
            if (!pull.Success && !Directory.Exists(Path.Combine(request.Pack.Path, PathService.RunScoreFolderName)))
            {
                return new CheckoutResult
                {
                    Success = false,
                    Message = pull.Message
                };
            }
        }

        var primary = ResolveTimingPrimary(request.Pack.Path, hintedPrimary);

        var libraryRunScore = Path.Combine(request.Pack.Path, PathService.RunScoreFolderName);
        if (!Directory.Exists(libraryRunScore))
        {
            return new CheckoutResult
            {
                Success = false,
                Message = $"RUNSCORE folder missing: {libraryRunScore}"
            };
        }

        var libraryEventFile = string.IsNullOrWhiteSpace(primary)
            ? null
            : Path.Combine(request.Pack.Path, primary);
        if (libraryEventFile is not null && !File.Exists(libraryEventFile))
        {
            // Graph download may not include .event yet (bare T&S Save As flow).
            if (!useGraph)
            {
                return new CheckoutResult
                {
                    Success = false,
                    Message = $"Primary .event not found: {libraryEventFile}"
                };
            }

            libraryEventFile = null;
            primary = string.Empty;
        }

        var activePath = _paths.GetActivePath(request.Settings, request.Pack.Office, request.Pack.RelativePath);
        var backupPath = _paths.GetBackupPath(request.Settings, request.Pack.Office, request.Pack.RelativePath);
        var autosavePath = _paths.GetAutosavePath(request.Pack.Path);
        var activeRunScore = Path.Combine(activePath, PathService.RunScoreFolderName);

        Directory.CreateDirectory(activePath);
        Directory.CreateDirectory(backupPath);
        Directory.CreateDirectory(autosavePath);
        PathService.TryMarkHidden(autosavePath);
        TryRemoveAutosaveTombstone(autosavePath);

        // Graph-only: Library under C:\S2F\Library is real files from Graph download (or a
        // prior pull). Library bytes come from Graph download only.
        statusProgress?.Report("Copying race files to this laptop…");
        await _copy.CopyDirectoryAsync(libraryRunScore, activeRunScore, progress, cancellationToken);
        Directory.CreateDirectory(activePath);
        if (libraryEventFile is not null)
            File.Copy(libraryEventFile, Path.Combine(activePath, primary), overwrite: true);

        statusProgress?.Report("Preparing local DB's folder…");
        var dbsLink = _links.EnsureLocalDbsFolder(activePath, request.Pack.Path);
        var dbsLinkNote = dbsLink.Success
            ? (dbsLink.Created ? " Active DB's is local; app publishes to OneDrive via Graph." : "")
            : $" DB's warning: {dbsLink.Message}";

        // Mid-race failover: overlay OneDrive RUNSCORE\_autosave only on reclaim/force/
        // same-machine reclaim. Normal checkout must NOT merge leftovers — Finish clears
        // _autosave so post-race OneDrive edits are not overwritten by stale mid-race mirrors.
        // Graph pack download skips _autosave on cellular — reclaim must pull that folder
        // even when SkipGraphDownload left the base pack on disk.
        var reclaim = IsLiveAutosaveReclaim(eval.Outcome, request);
        var autosaveMerged = reclaim
            ? await ApplyLiveAutosaveOverlayAsync(
                    request,
                    autosavePath,
                    activeRunScore,
                    backupPath,
                    useGraph,
                    progress,
                    statusProgress,
                    cancellationToken)
            : 0;

        var scoped = new List<string> { PathService.RunScoreFolderName };
        if (!string.IsNullOrWhiteSpace(primary))
            scoped.Add(primary);
        var emergency =
            eval.Outcome == LockAcquireOutcome.NeedsEmergencyConfirm
            || request.ConfirmEmergencyReclaim
            || request.ConfirmForceTakeover;
        _locks.Acquire(
            request.Pack.Path,
            request.Pack.Office,
            request.Pack.ParentEvent,
            request.Pack.SessionName,
            request.HolderName,
            scoped,
            primaryEventFile: string.IsNullOrWhiteSpace(primary) ? null : primary,
            reclaimReason: request.EmergencyReason,
            emergency: emergency);

        if (useGraph)
        {
            // Lock JSON only — never UploadFolderAsync(._checkin) (re-PUTs history + walks backups).
            try
            {
                await _cloud.UploadCheckoutLockAsync(
                    request.Pack.Path,
                    request.Settings.LibraryRoot,
                    request.Settings.GraphDriveRoot,
                    cancellationToken);
            }
            catch
            {
                // Non-fatal — heartbeat retries.
            }
        }

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
            Role = CheckoutRole.Timing,
            ScopedPaths = scoped,
            CheckedOutAt = DateTimeOffset.UtcNow
        };
        _activeStore.Save(active);

        // Soft: always re-pull vendor secrets (tiny GET) before SOP pin so rotated OneDrive
        // credentials apply. Admin RaceRoster/RunSignUp overrides still win at pin time.
        // Never blocks checkout on failure — ApplySop uses prior cache / leaves registry alone.
        await TrySyncVendorSecretsAsync(request.Settings.GraphDriveRoot, cancellationToken)
            .ConfigureAwait(false);

        // Point RunScore alternate-save folders at this checkout's Backup before the timer opens RS.
        // Re-applied on Open RunScore only when RS is not already running (prefs load at process start).
        var prefResult = _runScorePrefs.ApplySop(backupPath, request.Settings.RaceDayRoot);

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
            ? $" Applied {autosaveMerged} live autosave file(s) from OneDrive into Active + Backup."
            : reclaim
                ? " No live autosave files were found on OneDrive (recovery may be limited to last Finish)."
                : "";

        var reclaimLead = request.ConfirmForceTakeover
            ? "Force take-over succeeded. RUNSCORE + primary .event are local under Active."
            : emergency
                ? "Emergency reclaim succeeded. RUNSCORE + primary .event are local under Active."
                : eval.Outcome == LockAcquireOutcome.SameMachineReclaim && autosaveMerged > 0
                    ? "Same-machine reclaim succeeded. Live autosave was re-applied under Active."
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
                  + (useGraph
                      ? " Pre-Race / other .events stay in the cloud library until needed."
                      : " DB's stay on OneDrive (linked from Active); Pre-Race / other .events stay on OneDrive.")
                : "Checkout complete. Launch RunScore/T&S from Active only."
                  + prefNote
                  + dbsLinkNote
        };
    }

    /// <summary>
    /// Announcer T&amp;S checkout: no lock, no RunScore, no heartbeat upload.
    /// Copies the chosen non-primary .event and the full DB's tree into Active.
    /// </summary>
    private async Task<CheckoutResult> CheckoutAnnouncerAsync(
        CheckoutRequest request,
        IProgress<CopyProgress>? progress,
        IProgress<string>? statusProgress,
        CancellationToken cancellationToken)
    {
        var roles = SessionRoleStore.TryLoad(request.Pack.Path);
        var eventNames = EventFileSelector.ListEventFiles(request.Pack.Path);
        var secondaries = SecondaryEventSelector.ListSecondaries(eventNames, roles?.PrimaryEventFile);
        // UI pick first; else sole secondary; else legacy sidecar announcer field.
        var picked = SessionRoleStore.NormalizeFileName(request.PrimaryEventFileOverride);
        string? announcer = null;
        if (picked is not null
            && (secondaries.Contains(picked, StringComparer.OrdinalIgnoreCase)
                || File.Exists(Path.Combine(request.Pack.Path, picked))))
            announcer = picked;
        else if (secondaries.Count == 1)
            announcer = secondaries[0];
        else
            announcer = SessionRoleStore.NormalizeFileName(roles?.AnnouncerEventFile)
                        ?? SessionRoleStore.NormalizeFileName(request.Pack.AnnouncerEventFile);

        if (string.IsNullOrWhiteSpace(announcer))
        {
            return new CheckoutResult
            {
                Success = false,
                Message = "No non-primary Timing & Scoring .event file for this race. "
                          + "Setup can Save As extra files into the race folder."
            };
        }

        var useGraph = _cloud.IsAvailable;
        if (!useGraph)
        {
            return new CheckoutResult
            {
                Success = false,
                Message =
                    "Sign in to OneDrive before loading Announcer files. "
                    + "This laptop does not use the OneDrive desktop app."
            };
        }

        if (!request.SkipGraphDownload)
        {
            statusProgress?.Report("Downloading Announcer T&S files…");
            CloudSyncResult pull;
            try
            {
                using var pullCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pullCts.CancelAfter(GraphSyncPolicy.SessionDownloadBudget);
                pull = await _cloud.DownloadSessionAsync(
                    request.Pack.Path,
                    request.Settings.LibraryRoot,
                    request.Settings.GraphDriveRoot,
                    statusProgress,
                    pullCts.Token,
                    SessionDownloadOptions.ForAnnouncer(announcer)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                pull = new CloudSyncResult
                {
                    Success = false,
                    Message = "Download timed out on cellular. Using Announcer T&S files already on this laptop."
                };
            }

            if (!pull.Success && !File.Exists(Path.Combine(request.Pack.Path, announcer)))
            {
                return new CheckoutResult
                {
                    Success = false,
                    Message = pull.Message
                };
            }
        }

        var libraryEvent = Path.Combine(request.Pack.Path, announcer);
        if (!File.Exists(libraryEvent))
        {
            return new CheckoutResult
            {
                Success = false,
                Message = $"T&S file not found: {announcer}."
            };
        }

        var activePath = _paths.GetActivePath(request.Settings, request.Pack.Office, request.Pack.RelativePath);
        var backupPath = _paths.GetBackupPath(request.Settings, request.Pack.Office, request.Pack.RelativePath);
        var autosavePath = _paths.GetAutosavePath(request.Pack.Path);
        Directory.CreateDirectory(activePath);
        File.Copy(libraryEvent, Path.Combine(activePath, announcer), overwrite: true);

        statusProgress?.Report("Preparing local DB's folder…");
        var dbsLink = _links.EnsureLocalDbsFolder(activePath, request.Pack.Path);
        var libraryDbs = Path.Combine(request.Pack.Path, SharedFolderLinkService.DbsFolderName);
        if (Directory.Exists(libraryDbs))
        {
            try
            {
                await _copy.CopyDirectoryAsync(
                    libraryDbs,
                    Path.Combine(activePath, SharedFolderLinkService.DbsFolderName),
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                dbsLink = new LinkResult { Success = false, Message = ex.Message };
            }
        }

        var dbsNote = dbsLink.Success
            ? " DB's exports are in the local race folder."
            : $" DB's warning: {dbsLink.Message}";

        var scoped = new List<string> { announcer, SharedFolderLinkService.DbsFolderName };
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
            PrimaryEventFile = announcer,
            Role = CheckoutRole.Announcer,
            ScopedPaths = scoped,
            CheckedOutAt = DateTimeOffset.UtcNow
        };
        _activeStore.Save(active);

        return new CheckoutResult
        {
            Success = true,
            Active = active,
            Message = "Announcer files are on this laptop. Open Timing & Scoring when you're ready. "
                      + "Keep this app open — about every minute it pulls DB's (announcer.csv) from OneDrive for T&S auto-load. "
                      + "Done uploads this laptop's .event to OneDrive, then clears the local copy. "
                      + "The timing laptop's checkout lock is not affected."
                      + dbsNote
        };
    }

    /// <summary>
    /// Announcer leave: copy Active *.event into Library, Graph-upload those diffs, then clear Active.
    /// Does not upload RUNSCORE/DB's and does not release a timing lock.
    /// </summary>
    public async Task<CheckinResult> DoneAnnouncerAsync(
        AppSettings settings,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        var active = _activeStore.Load();
        if (active is null)
            return new CheckinResult { Success = false, Message = "No Active checkout on this machine." };

        if (active.Role != CheckoutRole.Announcer)
        {
            return new CheckinResult
            {
                Success = false,
                Message = "This checkout is Timing. Use Finish so the race uploads."
            };
        }

        var blocking = _processes.GetBlockingProcessNames();
        if (blocking.Count > 0)
        {
            return new CheckinResult
            {
                Success = false,
                BlockingProcesses = blocking,
                Message = "Close Timing & Scoring before Done: " + string.Join(", ", blocking)
            };
        }

        // Announcer Done = .event only (cellular). Never push DB's / RUNSCORE from this laptop.
        var uploadRelativePaths = FinishUploadPlanner.PlanUploadRelativePaths(active)
            .Where(p => p.EndsWith(".event", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var mustRetainCloudPending = uploadRelativePaths.Count > 0;

        if (!_cloud.IsAvailable)
        {
            if (active.PendingFinishUploadRelativePaths is { Count: > 0 })
            {
                return new CheckinResult
                {
                    Success = true,
                    RetainActiveForCloudRetry = true,
                    Message =
                        "Announcer .event from the last Done is still waiting to upload. "
                        + "Sign in to OneDrive, then tap Done again."
                };
            }

            if (mustRetainCloudPending)
            {
                return new CheckinResult
                {
                    Success = false,
                    Message =
                        "Sign in to OneDrive to Done. This laptop does not use the OneDrive desktop app — "
                        + "the announcer .event uploads through this app only. Your Active copy is unchanged."
                };
            }

            // No .event changes — clear local Active without Graph.
        }

        try
        {
            if (uploadRelativePaths.Count > 0)
            {
                statusProgress?.Report("done:copy");
                foreach (var rel in uploadRelativePaths)
                {
                    var activeEvent = Path.Combine(active.ActivePath, rel);
                    if (!File.Exists(activeEvent))
                        continue;
                    var libraryEvent = Path.Combine(active.LibrarySessionPath, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(libraryEvent)!);
                    File.Copy(activeEvent, libraryEvent, overwrite: true);
                }

                statusProgress?.Report("done:sync");
                IReadOnlyList<string>? stillPending = null;
                bool syncOk;
                string syncMessage;
                try
                {
                    using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    uploadCts.CancelAfter(GraphSyncPolicy.FinishUploadBudget);
                    var upload = await _cloud.UploadSessionAsync(
                        active.LibrarySessionPath,
                        settings.LibraryRoot,
                        settings.GraphDriveRoot,
                        statusProgress,
                        uploadCts.Token,
                        onlyRelativePaths: uploadRelativePaths).ConfigureAwait(false);
                    syncOk = upload.Success;
                    syncMessage = upload.Message;
                    if (!syncOk)
                    {
                        stillPending = upload.FailedRelativePaths.Count > 0
                            ? upload.FailedRelativePaths
                                .Where(p => p.EndsWith(".event", StringComparison.OrdinalIgnoreCase))
                                .ToList()
                            : uploadRelativePaths;
                    }
                }
                catch (OperationCanceledException)
                {
                    syncOk = false;
                    stillPending = uploadRelativePaths;
                    syncMessage = cancellationToken.IsCancellationRequested
                        ? "Done was interrupted before cloud upload finished. The .event is saved on this laptop — tap Done again."
                        : "Cloud upload timed out on cellular. The .event is saved on this laptop — tap Done again when signal improves.";
                }

                if (!syncOk)
                    return RetainAnnouncerForCloudRetry(active, stillPending, syncMessage);

                if (active.PendingFinishUploadRelativePaths is { Count: > 0 })
                    _activeStore.Save(active.WithPendingFinishUpload(null));
            }
        }
        catch (OperationCanceledException) when (mustRetainCloudPending)
        {
            return RetainAnnouncerForCloudRetry(
                active,
                uploadRelativePaths,
                cancellationToken.IsCancellationRequested
                    ? "Done was interrupted while saving the .event. It is on this laptop — tap Done again."
                    : "Done timed out while saving the .event. It is on this laptop — tap Done again when signal improves.");
        }
        catch (Exception ex) when (mustRetainCloudPending)
        {
            return RetainAnnouncerForCloudRetry(
                active,
                uploadRelativePaths,
                "Done could not finish uploading the .event to OneDrive. It is saved on this laptop — tap Done again.\n\n"
                + ex.Message);
        }

        var filesCleared = true;
        if (Directory.Exists(active.ActivePath))
            filesCleared = _links.DeleteActiveTreeSafe(active.ActivePath);

        _activeStore.Save(null);
        var uploadedNote = uploadRelativePaths.Count > 0
            ? "Announcer .event was uploaded to OneDrive. "
            : "No .event changes to upload. ";
        return new CheckinResult
        {
            Success = true,
            AutosaveCleared = true,
            Message = filesCleared
                ? "Done. " + uploadedNote + "Local announcer files were removed. The timing laptop was not affected."
                : "Done. " + uploadedNote
                  + "Checkout cleared, but some Active files are still locked — close Timing & Scoring, then delete the leftover folder under RaceDay\\Active if it remains."
        };
    }

    /// <summary>
    /// Another laptop took the cloud lock (soft/force takeover). Clear local Active only —
    /// no Finish upload and no lock release (the other machine owns the lock).
    /// </summary>
    /// <param name="requireVendorsClosed">
    /// When true, refuse if RunScore / T&amp;S still running. When false (user already warned),
    /// clear Active anyway — staff must close vendors themselves.
    /// </param>
    public CheckinResult AbandonLocalActive(bool requireVendorsClosed = true)
    {
        var active = _activeStore.Load();
        if (active is null)
            return new CheckinResult { Success = false, Message = "No Active checkout on this machine." };

        if (requireVendorsClosed)
        {
            var blocking = _processes.GetBlockingProcessNames();
            if (blocking.Count > 0)
            {
                return new CheckinResult
                {
                    Success = false,
                    BlockingProcesses = blocking,
                    Message = "Close RunScore and Timing & Scoring first: " + string.Join(", ", blocking)
                };
            }
        }

        var filesCleared = true;
        try
        {
            if (Directory.Exists(active.ActivePath))
                filesCleared = _links.DeleteActiveTreeSafe(active.ActivePath);
        }
        catch
        {
            filesCleared = false;
        }

        // Always drop local checkout state — do not leave the timer stuck on takeover UI
        // because RSServer still holds a handle on Active\RUNSCORE.
        _activeStore.Save(null);

        if (active.Role == CheckoutRole.Timing)
        {
            try { _runScorePrefs.RestoreNativePrefs(); }
            catch { /* non-fatal */ }
        }

        return new CheckinResult
        {
            Success = true,
            AutosaveCleared = true,
            Message = filesCleared
                ? "Local copy closed. Nothing was uploaded and the other laptop's checkout lock was left alone."
                : "Local copy closed (checkout cleared). Some Active files are still locked — close RunScore, then delete the leftover folder under RaceDay\\Active if it remains."
        };
    }

    /// <summary>
    /// Sidecar primary when that file is on disk. Otherwise the hinted file, then ChoosePrimary.
    /// </summary>
    private static string ResolveTimingPrimary(string sessionPath, string? hinted)
    {
        var assigned = SessionRoleStore.TryLoad(sessionPath)?.PrimaryEventFile;
        if (!string.IsNullOrWhiteSpace(assigned)
            && File.Exists(Path.Combine(sessionPath, assigned)))
            return assigned;

        if (!string.IsNullOrWhiteSpace(hinted))
        {
            var name = Path.GetFileName(hinted.Trim());
            if (!string.IsNullOrWhiteSpace(name) && File.Exists(Path.Combine(sessionPath, name)))
                return name;
        }

        return EventFileSelector.ChoosePrimary(EventFileSelector.ListEventFiles(sessionPath))
               ?? string.Empty;
    }

    public async Task<CheckinResult> CheckinAsync(
        AppSettings settings,
        string holderName,
        bool removeActive = true,
        bool adminOverrideProcesses = false,
        IProgress<CopyProgress>? progress = null,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        var active = _activeStore.Load();
        if (active is null)
            return new CheckinResult { Success = false, Message = "No Active checkout on this machine." };

        if (active.Role == CheckoutRole.Announcer)
        {
            return new CheckinResult
            {
                Success = false,
                Message = "Announcer checkout uses Done (uploads the .event to OneDrive). Close Timing & Scoring, then tap Done."
            };
        }

        // Graph-only: no OneDrive desktop client on race-day laptops. Finish must upload via Graph.
        if (!_cloud.IsAvailable)
        {
            if (active.PendingFinishUploadRelativePaths is { Count: > 0 })
            {
                return new CheckinResult
                {
                    Success = true,
                    RetainActiveForCloudRetry = true,
                    Message =
                        "Race files from the last Finish are still waiting to upload. "
                        + "Sign in to OneDrive, then tap Finish again."
                };
            }

            return new CheckinResult
            {
                Success = false,
                Message =
                    "Sign in to OneDrive to Finish. This laptop does not use the OneDrive desktop app — "
                    + "race files upload through this app only. Your Active checkout is unchanged."
            };
        }

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
        if (!Directory.Exists(activeRunScore))
            return new CheckinResult { Success = false, Message = $"Active RUNSCORE missing: {activeRunScore}" };

        // Pick up .event files Save As'd mid-checkout (Templates → bare T&S → Save As into Active).
        // Copy every Active *.event to Library (primary + announcer/video secondaries).
        var activeEventNames = EventFileSelector.ListEventFiles(active.ActivePath);

        // Diff Active vs Library BEFORE finish:copy — download/checkout timestamps must not
        // drive Graph upload (that was uploading ~100 unchanged RUNSCORE files).
        // Union with any pending paths from a prior Graph fail (Active≈Library on retry).
        var uploadRelativePaths = FinishUploadPlanner.PlanUploadRelativePaths(active);

        // Once we mutate Library toward Active, any failure before Graph success must keep
        // pending paths — otherwise the next Finish sees Active≈Library and skips upload.
        var mustRetainCloudPending = uploadRelativePaths.Count > 0
            || active.PendingFinishUploadRelativePaths is { Count: > 0 };

        var autosavePath = active.AutosavePath;
        var syncOk = true;
        var syncMessage = string.Empty;

        try
        {
            statusProgress?.Report("finish:backup");
            _paths.EnsureAppMetaFolder(active.LibrarySessionPath);
            var backupDest = _paths.GetCheckinBackupPath(active.LibrarySessionPath, DateTimeOffset.Now);
            var libraryRunScore = Path.Combine(active.LibrarySessionPath, PathService.RunScoreFolderName);
            Directory.CreateDirectory(backupDest);
            if (Directory.Exists(libraryRunScore))
                await _copy.CopyDirectoryAsync(libraryRunScore, Path.Combine(backupDest, PathService.RunScoreFolderName), progress, cancellationToken);
            foreach (var eventName in activeEventNames)
            {
                var libraryEventPrior = Path.Combine(active.LibrarySessionPath, eventName);
                if (File.Exists(libraryEventPrior))
                    File.Copy(libraryEventPrior, Path.Combine(backupDest, eventName), overwrite: true);
            }

            statusProgress?.Report("finish:copy");
            // Full replace into Library (local). Graph upload uses uploadRelativePaths from the
            // pre-copy Active-vs-Library diff — not these rewritten timestamps.
            await _copy.CopyDirectoryAsync(activeRunScore, libraryRunScore, progress, cancellationToken);
            foreach (var eventName in activeEventNames)
            {
                var activeEventPath = Path.Combine(active.ActivePath, eventName);
                var libraryEvent = Path.Combine(active.LibrarySessionPath, eventName);
                File.Copy(activeEventPath, libraryEvent, overwrite: true);
            }

            // Clear local mid-race autosave ASAP (before Graph). Do NOT clear remote _autosave
            // yet — that burns cellular before race-file PUTs; run it after upload succeeds.
            statusProgress?.Report("finish:autosave");
            ClearAutosaveWithRetry(autosavePath);

            // Publish Active DB's into the library session before Graph upload.
            var activeDbs = Path.Combine(active.ActivePath, SharedFolderLinkService.DbsFolderName);
            var libraryDbs = Path.Combine(active.LibrarySessionPath, SharedFolderLinkService.DbsFolderName);
            if (Directory.Exists(activeDbs) && !IsReparsePoint(activeDbs))
            {
                Directory.CreateDirectory(libraryDbs);
                await _copy.MirrorNewerFilesAsync(activeDbs, libraryDbs, cancellationToken: cancellationToken);
            }

            // Cloud upload BEFORE releasing the lock / clearing Active.
            statusProgress?.Report("finish:sync");
            IReadOnlyList<string>? stillPending = null;
            try
            {
                using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                uploadCts.CancelAfter(GraphSyncPolicy.FinishUploadBudget);
                var upload = await _cloud.UploadSessionAsync(
                    active.LibrarySessionPath,
                    settings.LibraryRoot,
                    settings.GraphDriveRoot,
                    statusProgress,
                    uploadCts.Token,
                    onlyRelativePaths: uploadRelativePaths);
                syncOk = upload.Success;
                syncMessage = upload.Message;
                if (!syncOk)
                {
                    // Partial fail → only remaining paths; hard fail / empty list → full planned set.
                    stillPending = upload.FailedRelativePaths.Count > 0
                        ? upload.FailedRelativePaths
                        : uploadRelativePaths;
                }
            }
            catch (OperationCanceledException)
            {
                syncOk = false;
                stillPending = uploadRelativePaths;
                syncMessage = cancellationToken.IsCancellationRequested
                    ? "Finish was interrupted before cloud upload finished. Race files are saved on this laptop — tap Finish again."
                    : "Cloud upload timed out on cellular. Race files are saved on this laptop — tap Finish again when signal improves.";
            }

            if (!syncOk)
                return RetainActiveForCloudRetry(active, autosavePath, stillPending, syncMessage);

            // Cloud OK — drop any prior pending list before releasing Active.
            if (active.PendingFinishUploadRelativePaths is { Count: > 0 })
                _activeStore.Save(active.WithPendingFinishUpload(null));
        }
        catch (OperationCanceledException) when (mustRetainCloudPending)
        {
            return RetainActiveForCloudRetry(
                active,
                autosavePath,
                uploadRelativePaths,
                cancellationToken.IsCancellationRequested
                    ? "Finish was interrupted while saving race files. Files are on this laptop — tap Finish again."
                    : "Finish timed out while saving race files. Files are on this laptop — tap Finish again when signal improves.");
        }
        catch (Exception ex) when (mustRetainCloudPending)
        {
            return RetainActiveForCloudRetry(
                active,
                autosavePath,
                uploadRelativePaths,
                "Finish could not finish uploading to OneDrive. Race files are saved on this laptop — tap Finish again.\n\n"
                + ex.Message);
        }

        _locks.Release(active.LibrarySessionPath, holderName);

        // Publish Released lock AFTER local release. UploadSessionAsync (Finish) skips lock
        // metadata so we do not PUT Active then Released (wastes cellular) and so OneDrive
        // does not keep a stale Active lock after a successful Finish.
        var lockPublished = false;
        if (syncOk)
        {
            statusProgress?.Report("finish:lock");
            // Retry — a silent miss leaves every other laptop showing Checked Out until soft take-over.
            for (var attempt = 1; attempt <= 3 && !lockPublished; attempt++)
            {
                try
                {
                    using var lockCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    lockCts.CancelAfter(GraphSyncPolicy.HttpTimeout);
                    var lockPut = await _cloud.UploadCheckoutLockAsync(
                        active.LibrarySessionPath,
                        settings.LibraryRoot,
                        settings.GraphDriveRoot,
                        lockCts.Token);
                    lockPublished = lockPut.Success && lockPut.FilesTransferred > 0;
                    if (!lockPublished && attempt < 3)
                        await Task.Delay(400 * attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Per-attempt timeout — retry.
                }
                catch
                {
                    // Next attempt.
                }
            }

            // Remote _autosave clear after race PUTs (bounded budget inside ClearRemoteFolderAsync).
            statusProgress?.Report("finish:autosave-cloud");
            try
            {
                await _cloud.ClearRemoteFolderAsync(
                    autosavePath,
                    settings.LibraryRoot,
                    settings.GraphDriveRoot,
                    cancellationToken);
            }
            catch
            {
                // Local autosave already cleared; leftover cloud overlay is non-fatal.
            }
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

        // Put office/native RSServer prefs back (race Backup paths must not stick in HKCU).
        statusProgress?.Report("finish:prefs");
        string prefsNote;
        try
        {
            var prefsRestore = _runScorePrefs.RestoreNativePrefs();
            prefsNote = prefsRestore.Success && prefsRestore.UpdatedCount > 0
                ? " " + prefsRestore.Message
                : string.Empty;
        }
        catch (Exception ex)
        {
            prefsNote = " Office RunScore prefs restore warning: " + ex.Message;
        }

        statusProgress?.Report("finish:autosave");
        var autosaveCleared = ClearAutosaveWithRetry(autosavePath);
        var autosaveNote = autosaveCleared
            ? string.Empty
            : " Live autosave files could not be fully removed — ask setup to delete RUNSCORE\\_autosave before the next race.";
        autosaveNote += prefsNote;
        var lockNote = syncOk && !lockPublished
            ? " Checkout lock may still look Active on other laptops until they Refresh Events or Soft Take Over — ask setup if it sticks."
            : string.Empty;

        if (activeRemoveError is not null)
        {
            return new CheckinResult
            {
                Success = true,
                AutosaveCleared = autosaveCleared,
                Message =
                    $"Checked in; could not remove Active folder: {activeRemoveError}. "
                    + "Race files were uploaded — safe to shut down after closing leftover folders."
                    + autosaveNote
                    + lockNote
            };
        }

        return new CheckinResult
        {
            Success = true,
            AutosaveCleared = autosaveCleared,
            Message =
                "Finish complete. Race files were uploaded to OneDrive. You can shut down this laptop."
                + autosaveNote
                + lockNote
        };
    }

    /// <summary>
    /// Persist pending announcer .event Graph paths and keep Active.
    /// Does not touch RUNSCORE\_autosave (timing laptop owns that) or the checkout lock.
    /// </summary>
    private CheckinResult RetainAnnouncerForCloudRetry(
        ActiveCheckout active,
        IReadOnlyList<string>? stillPending,
        string message)
    {
        var candidates = (stillPending ?? Array.Empty<string>())
            .Concat(active.PendingFinishUploadRelativePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p)
                        && p.EndsWith(".event", StringComparison.OrdinalIgnoreCase))
            .Select(FinishUploadPlanner.NormalizeRelative)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existing = FinishUploadPlanner.ExistingLibraryRelativePaths(
            active.LibrarySessionPath, candidates);
        var toSave = existing.Count > 0 ? existing : candidates;

        _activeStore.Save(active.WithPendingFinishUpload(toSave.Count > 0 ? toSave : stillPending));
        return new CheckinResult
        {
            Success = true,
            RetainActiveForCloudRetry = true,
            AutosaveCleared = true,
            Message = message
        };
    }

    /// <summary>
    /// Persist pending Graph Finish paths and keep Active + lock. Prefer paths that still
    /// exist under Library; if none do, keep the planned set so retry cannot no-op.
    /// </summary>
    private CheckinResult RetainActiveForCloudRetry(
        ActiveCheckout active,
        string autosavePath,
        IReadOnlyList<string>? stillPending,
        string message)
    {
        var candidates = (stillPending ?? Array.Empty<string>())
            .Concat(active.PendingFinishUploadRelativePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(FinishUploadPlanner.NormalizeRelative)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existing = FinishUploadPlanner.ExistingLibraryRelativePaths(
            active.LibrarySessionPath, candidates);
        // Prefer on-disk Library paths; fall back to the full candidate set so we never
        // wipe pending after a Library mutation (Active≈Library trap).
        var toSave = existing.Count > 0 ? existing : candidates;

        _activeStore.Save(active.WithPendingFinishUpload(toSave.Count > 0 ? toSave : stillPending));
        var autosaveStill = ClearAutosaveWithRetry(autosavePath);
        return new CheckinResult
        {
            Success = true,
            RetainActiveForCloudRetry = true,
            AutosaveCleared = autosaveStill,
            Message = message
        };
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
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

    /// <summary>
    /// Soft/force/same-machine reclaim must pull and apply Graph <c>RUNSCORE\_autosave</c>.
    /// </summary>
    public static bool IsLiveAutosaveReclaim(LockAcquireOutcome outcome, CheckoutRequest request) =>
        outcome is LockAcquireOutcome.NeedsEmergencyConfirm or LockAcquireOutcome.SameMachineReclaim
        || request.ConfirmForceTakeover
        || request.ConfirmEmergencyReclaim;

    /// <summary>
    /// Download OneDrive <c>_autosave</c> (when Graph is up), then force-overlay onto Active RUNSCORE
    /// and Backup so RunScore alternate-save sees the recovered entries/results.
    /// </summary>
    private async Task<int> ApplyLiveAutosaveOverlayAsync(
        CheckoutRequest request,
        string autosavePath,
        string activeRunScore,
        string backupPath,
        bool useGraph,
        IProgress<CopyProgress>? progress,
        IProgress<string>? statusProgress,
        CancellationToken cancellationToken)
    {
        if (useGraph)
        {
            statusProgress?.Report("Downloading live autosave from OneDrive…");
            try
            {
                using var overlayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                overlayCts.CancelAfter(GraphSyncPolicy.MidRacePublishBudget);
                // AutosaveOverlayOnly always re-PUTs every file (no size-skip) so same-length
                // Entries.DTA edits from the other laptop replace a stale local overlay.
                await _cloud.DownloadSessionAsync(
                    request.Pack.Path,
                    request.Settings.LibraryRoot,
                    request.Settings.GraphDriveRoot,
                    statusProgress,
                    overlayCts.Token,
                    SessionDownloadOptions.ForReclaimAutosaveOverlay()).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Weak cell — merge whatever is already local under Library \_autosave.
            }
            catch
            {
                // Non-fatal — merge local overlay if present.
            }
        }

        if (!Directory.Exists(autosavePath)
            || !Directory.EnumerateFiles(autosavePath, "*", SearchOption.AllDirectories).Any())
            return 0;

        statusProgress?.Report("Applying live autosave from OneDrive…");
        var merged = 0;
        var mergeProgress = new Progress<CopyProgress>(p =>
        {
            merged = (int)p.FilesCopied;
            progress?.Report(p);
        });
        // Force overlay (not newer-only): Graph downloads stamp mtime=now on the base pack,
        // so MirrorNewerFiles would skip the live mid-race files.
        // Per-file retries: RunScore often locks Entries.DTA on this laptop during reclaim.
        await _copy.OverlayAllFilesAsync(autosavePath, activeRunScore, mergeProgress, cancellationToken)
            .ConfigureAwait(false);
        // Seed Backup from the same overlay — RunScore alternate-save must see recovered files
        // even when this laptop's prior Backup is newer by mtime.
        Directory.CreateDirectory(backupPath);
        await _copy.OverlayAllFilesAsync(autosavePath, backupPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return merged;
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

    /// <summary>
    /// Always re-pull vendor secrets on checkout (tiny known-path GET). Soft-fail — rotated
    /// OneDrive credentials must not stay stuck behind a stale AppData cache.
    /// </summary>
    private async Task TrySyncVendorSecretsAsync(
        string graphDriveRoot,
        CancellationToken cancellationToken)
    {
        if (!_cloud.IsAvailable)
            return;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(GraphSyncPolicy.VendorSecretsBudget);
            await _cloud.SyncRunScoreVendorSecretsAsync(graphDriveRoot, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Soft — ApplySop uses cache when present; otherwise leaves vendor registry alone.
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
