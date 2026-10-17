using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;
using S2F.RaceDayCheckout.Services;

namespace S2F.RaceDayCheckout.ViewModels;

public enum WizardStep
{
    Office,
    Session,
    EventFile,
    Active,
    Admin
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SettingsStore _settingsStore = new();
    private readonly RunScoreSopStore _sopStore = new();
    private readonly PathService _paths = new();
    private readonly MachineIdentity _machine = new();
    private readonly FileCopyService _copy = new();
    private readonly ProcessGuard _processGuard = new();
    private readonly ActiveCheckoutStore _activeStore = new();
    private readonly VendorLauncher _launcher = new();
    private readonly CheckoutLockService _locks;
    private readonly LibraryScanner _scanner;
    private readonly CheckoutService _checkout;
    private readonly AutosaveMirrorService _mirror;
    private readonly GraphAuthService _graphAuth;
    private readonly ICloudLibrarySync _cloud;
    private readonly DispatcherTimer _heartbeatTimer;
    private readonly DispatcherTimer _mirrorTimer;
    private readonly SemaphoreSlim _heartbeatUploadGate = new(1, 1);
    /// <summary>Offices / Past Races paths already Graph-indexed this run (cellular: don't re-walk).</summary>
    private readonly HashSet<string> _graphIndexedOffices = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings;
    private WizardStep _step = WizardStep.Office;
    private string _status = string.Empty;
    private string _progressText = string.Empty;
    private string? _selectedOffice;
    /// <summary>Under Past Races only — e.g. empty, <c>Memphis Past Races</c>, or <c>Memphis Past Races/2025</c>.</summary>
    private string _pastRacesRelative = string.Empty;
    private EventPackInfo? _selectedSession;
    private string? _selectedEventFile;
    /// <summary>True after SelectSession pulled this pack from Graph (Checkout skips second download).</summary>
    private bool _sessionPackPulled;
    private ActiveCheckout? _active;
    private bool _busy;
    private bool _busyNeedsAcknowledge;
    private string _busyTitle = string.Empty;
    private TaskCompletionSource? _busyAck;

    public MainViewModel()
    {
        _settings = _settingsStore.LoadAndPersistMigrations();
        _graphAuth = new GraphAuthService(_settings.GraphClientId);
        _cloud = new GraphDriveSyncService(_graphAuth);
        _locks = new CheckoutLockService(_paths, _machine);
        _scanner = new LibraryScanner(_paths, _locks);
        _checkout = new CheckoutService(_paths, _locks, _copy, _processGuard, _activeStore, cloud: _cloud);
        _mirror = new AutosaveMirrorService(_copy, _activeStore, _cloud, () => _settings);

        Offices = new ObservableCollection<string>();
        Sessions = new ObservableCollection<EventPackInfo>();
        EventFiles = new ObservableCollection<string>();

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(30, _settings.HeartbeatSeconds)) };
        _heartbeatTimer.Tick += (_, _) => Heartbeat();
        _mirrorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(30, _settings.AutosaveMirrorSeconds)) };
        _mirrorTimer.Tick += async (_, _) =>
        {
            try { await _mirror.MirrorOnceAsync(); }
            catch { /* non-fatal */ }
        };

        EnsureLocalRoots(); // office name folders under C:\S2F\Library — no Graph
        ReloadFromDisk();
        _heartbeatTimer.Start();
        _mirrorTimer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Offices { get; }
    public ObservableCollection<EventPackInfo> Sessions { get; }
    public ObservableCollection<string> EventFiles { get; }

    public WizardStep Step
    {
        get => _step;
        private set
        {
            if (Set(ref _step, value))
            {
                OnPropertyChanged(nameof(IsOfficeStep));
                OnPropertyChanged(nameof(IsSessionStep));
                OnPropertyChanged(nameof(IsEventFileStep));
                OnPropertyChanged(nameof(IsActiveStep));
                OnPropertyChanged(nameof(IsAdminStep));
                OnPropertyChanged(nameof(ShowEmptySessions));
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(StepSubtitle));
                OnPropertyChanged(nameof(ShowBack));
                OnPropertyChanged(nameof(ShowNewEventLink));
                OnPropertyChanged(nameof(ShowCheckForUpdates));
            }
        }
    }

    public bool IsOfficeStep => Step == WizardStep.Office;
    public bool IsSessionStep => Step == WizardStep.Session;
    public bool IsEventFileStep => Step == WizardStep.EventFile;
    public bool IsActiveStep => Step == WizardStep.Active;
    public bool IsAdminStep => Step == WizardStep.Admin;
    public bool HasSessions => Sessions.Count > 0;
    public bool ShowEmptySessions => IsSessionStep && !HasSessions;

    public bool IsPastRacesBrowse =>
        string.Equals(SelectedOffice, EventBuilderService.PastRacesFolderName, StringComparison.OrdinalIgnoreCase);

    public string PastRacesBreadcrumb
    {
        get
        {
            if (!IsPastRacesBrowse)
                return string.Empty;
            if (string.IsNullOrEmpty(_pastRacesRelative))
                return EventBuilderService.PastRacesFolderName;
            return EventBuilderService.PastRacesFolderName + " / "
                   + _pastRacesRelative.Replace("/", " / ", StringComparison.Ordinal)
                       .Replace("\\", " / ", StringComparison.Ordinal);
        }
    }

    public string StepTitle => Step switch
    {
        WizardStep.Office => "Choose your office",
        WizardStep.Session => IsPastRacesBrowse ? "Browse Past Races" : "Choose your event",
        WizardStep.EventFile => "Which timing file?",
        // Event name is the hero — don't repeat it in the card below.
        WizardStep.Active => Active?.DisplayName ?? "Ready for race day",
        WizardStep.Admin => "Admin settings",
        _ => "S2F Race Day"
    };

    public string StepSubtitle => Step switch
    {
        WizardStep.Office => "Double-click your office to continue.",
        WizardStep.Session => IsPastRacesBrowse
            ? $"{PastRacesBreadcrumb} — open a folder, or double-click a race."
            : SelectedOffice is null
                ? "Double-click today's race."
                : $"Office: {SelectedOffice} — double-click today's race.",
        WizardStep.EventFile => SelectedSession is null
            ? "Double-click the main timing file."
            : $"{SelectedSession.DisplayName} — double-click the main timing file.",
        WizardStep.Active => Active is null
            ? ""
            : $"{Active.Office} · Keep this app open while racing (live backup)",
        WizardStep.Admin => "For setup staff only — not needed on race morning.",
        _ => ""
    };

    public bool ShowBack => Step is WizardStep.Session or WizardStep.EventFile or WizardStep.Admin;

    /// <summary>Mid-week builder entry — office page only (hidden once checked out or deeper in the wizard).</summary>
    public bool ShowNewEventLink => !HasActive && Step == WizardStep.Office;

    /// <summary>Timers can check for updates without opening Admin (office or Active).</summary>
    public bool ShowCheckForUpdates => Step is WizardStep.Office or WizardStep.Active;

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => Set(ref _progressText, value);
    }

    /// <summary>Large headline on the full-screen busy overlay (Loading / Saving / Closing).</summary>
    public string BusyTitle
    {
        get => _busyTitle;
        set => Set(ref _busyTitle, value);
    }

    public string? SelectedOffice
    {
        get => _selectedOffice;
        set => Set(ref _selectedOffice, value);
    }

    public EventPackInfo? SelectedSession
    {
        get => _selectedSession;
        set => Set(ref _selectedSession, value);
    }

    public string? SelectedEventFile
    {
        get => _selectedEventFile;
        set => Set(ref _selectedEventFile, value);
    }

    public ActiveCheckout? Active
    {
        get => _active;
        private set
        {
            if (Set(ref _active, value))
            {
                OnPropertyChanged(nameof(HasActive));
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(StepSubtitle));
                OnPropertyChanged(nameof(ShowNewEventLink));
            }
        }
    }

    public bool HasActive => Active is not null;

    public bool Busy
    {
        get => _busy;
        set
        {
            if (Set(ref _busy, value))
                OnPropertyChanged(nameof(BusyKeepOpenVisible));
        }
    }

    /// <summary>When true, overlay shows OK instead of “keep open” (result / error).</summary>
    public bool BusyNeedsAcknowledge
    {
        get => _busyNeedsAcknowledge;
        private set
        {
            if (Set(ref _busyNeedsAcknowledge, value))
                OnPropertyChanged(nameof(BusyKeepOpenVisible));
        }
    }

    public bool BusyKeepOpenVisible => Busy && !BusyNeedsAcknowledge;

    private void SetBusy(string title, string detail = "")
    {
        BusyNeedsAcknowledge = false;
        BusyTitle = title;
        ProgressText = detail;
        Busy = true;
    }

    private async Task ShowBusyResultAsync(string title, string detail)
    {
        BusyTitle = title;
        ProgressText = detail;
        BusyNeedsAcknowledge = true;
        Busy = true;
        _busyAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _busyAck.Task;
        ClearBusy();
    }

    public void AcknowledgeBusy() => _busyAck?.TrySetResult();

    private void ClearBusy()
    {
        BusyNeedsAcknowledge = false;
        _busyAck?.TrySetResult();
        _busyAck = null;
        Busy = false;
        BusyTitle = string.Empty;
        ProgressText = string.Empty;
    }

    private static Window? OwnerWindow =>
        Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    private static void Alert(string title, string message) =>
        TimerDialog.Alert(OwnerWindow, title, message);

    private static string FriendlyProgress(string? technical)
    {
        if (string.IsNullOrWhiteSpace(technical))
            return "Please wait…";
        if (technical.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("Opening", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("Skipping", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("Skipped", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("downloaded", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("autosave", StringComparison.OrdinalIgnoreCase))
            return technical;
        if (technical.Contains("OneDrive", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("settle", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("sync", StringComparison.OrdinalIgnoreCase))
            return "Saving to the cloud — keep this window open…";
        if (technical.Contains("Backup", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("Copying", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("Saving", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("files", StringComparison.OrdinalIgnoreCase))
            return "Saving your race files…";
        return technical;
    }

    private void SetFinishProgress(string title, string detail)
    {
        BusyTitle = title;
        ProgressText = detail;
    }

    /// <summary>Map check-in status codes to timer-facing Finish overlay copy.</summary>
    private void ApplyFinishStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            SetFinishProgress("Saving your race", "Please wait…");
            return;
        }

        if (status.StartsWith("finish:sync-stable:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = status.Split(':');
            var cur = parts.Length > 2 ? parts[2] : "?";
            var need = parts.Length > 3 ? parts[3] : "?";
            SetFinishProgress(
                "3 · Uploading to OneDrive",
                $"Cloud upload looks stable ({cur}/{need}).\nAlmost done — keep this window open.");
            return;
        }

        switch (status)
        {
            case "finish:backup":
                SetFinishProgress(
                    "2 · Saving your race",
                    "Backing up the current OneDrive race files…");
                return;
            case "finish:copy":
                SetFinishProgress(
                    "2 · Saving your race",
                    "Copying this laptop's race files to OneDrive…");
                return;
            case "finish:prefs":
                SetFinishProgress(
                    "2 · Saving your race",
                    "Restoring office RunScore preferences…");
                return;
            case "finish:autosave":
                SetFinishProgress(
                    "2 · Saving your race",
                    "Clearing local mid-race autosave…");
                return;
            case "finish:autosave-cloud":
                SetFinishProgress(
                    "2 · Saving your race",
                    "Clearing mid-race autosave on OneDrive…");
                return;
            case "finish:sync":
            case "finish:sync-wait":
            case "finish:upload":
                SetFinishProgress(
                    "3 · Uploading to OneDrive",
                    _cloud.IsAvailable
                        ? "Uploading changed race files to OneDrive.\nKeep this window open."
                        : "Waiting for OneDrive to finish uploading.\nThis can take a minute — keep this window open.");
                return;
            case "finish:cleanup":
                SetFinishProgress(
                    "4 · Cleaning up",
                    "Removing the local Active copy on this laptop…");
                return;
            default:
                if (status.StartsWith("finish:upload-progress:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = status.Split(':');
                    var label = parts.Length > 2 ? parts[2] : "files";
                    var n = parts.Length > 3 ? parts[3] : "";
                    SetFinishProgress(
                        "3 · Uploading to OneDrive",
                        string.IsNullOrEmpty(n)
                            ? $"Uploading {label}…\nKeep this window open."
                            : $"Uploading {label} ({n})…\nKeep this window open.");
                    return;
                }

                if (status.StartsWith("finish:upload-folder:", StringComparison.OrdinalIgnoreCase)
                    || status.StartsWith("finish:upload-file:", StringComparison.OrdinalIgnoreCase))
                {
                    SetFinishProgress(
                        "3 · Uploading to OneDrive",
                        "Uploading changed race files…\nKeep this window open.");
                    return;
                }

                SetFinishProgress("Saving your race", FriendlyProgress(status));
                return;
        }
    }

    public string LibraryRoot
    {
        get => _settings.LibraryRoot;
        set { _settings.LibraryRoot = value; OnPropertyChanged(); }
    }

    public string RaceDayRoot
    {
        get => _settings.RaceDayRoot;
        set { _settings.RaceDayRoot = value; OnPropertyChanged(); }
    }

    public string DisplayName
    {
        get => _settings.DisplayName;
        set { _settings.DisplayName = value; OnPropertyChanged(); }
    }

    public string RunScoreExePath
    {
        get => _settings.RunScoreExePath;
        set { _settings.RunScoreExePath = value; OnPropertyChanged(); }
    }

    public string TimingAndScoringExePath
    {
        get => _settings.TimingAndScoringExePath;
        set { _settings.TimingAndScoringExePath = value; OnPropertyChanged(); }
    }

    public string GraphDriveRoot
    {
        get => _settings.GraphDriveRoot;
        set { _settings.GraphDriveRoot = value; OnPropertyChanged(); }
    }

    public bool IsGraphSignedIn => _graphAuth.IsSignedIn;

    public string GraphAccountStatus =>
        _graphAuth.IsSignedIn
            ? $"Signed in as {_graphAuth.AccountEmail ?? "OneDrive"}"
            : "Not signed in — checkout uses the local Library folder (OneDrive Desktop if present).";

    /// <summary>Landing-page OneDrive check — plain language for timers.</summary>
    public string GraphReadyLabel => IsGraphSignedIn ? "OneDrive: ready" : "OneDrive: not signed in";

    public string GraphLandingStatus =>
        IsGraphSignedIn
            ? $"Signed in as {_graphAuth.AccountEmail ?? "OneDrive"}. Cloud sync is set up for this laptop."
            : "Ask setup to sign in under Admin (triple-click the logo). Until then, this laptop uses the local library only.";

    public System.Windows.Media.Brush GraphReadyBannerBackground =>
        IsGraphSignedIn
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xFB, 0xF6))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xF8, 0xE8));

    public System.Windows.Media.Brush GraphReadyBannerBorder =>
        IsGraphSignedIn
            ? (System.Windows.Media.Brush)Application.Current.FindResource("SaveBrush")
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xC3, 0x5C));

    public void ReloadFromDisk()
    {
        Active = _checkout.GetActive();
        if (Active is not null)
        {
            Step = WizardStep.Active;
            Status = "Checked out — finish before starting another event.";
            return;
        }

        // No Active — put office RSServer prefs back if a prior race-day SOP left Backup paths in HKCU.
        try { new RunScorePreferencesService().RestoreNativePrefs(); }
        catch { /* non-fatal */ }

        LoadOffices();
        SyncDisplayNameFromMachine();
        Status = Directory.Exists(_settings.LibraryRoot)
            ? string.Empty
            : "Library folder not found. Ask setup staff to check Admin settings.";

        // One office → show session step with local list; Graph fill runs via SelectOfficeAsync when needed.
        if (Offices.Count == 1)
        {
            SelectedOffice = Offices[0];
            LoadSessions(Offices[0]);
            Step = WizardStep.Session;
        }
        else
            Step = WizardStep.Office;
    }

    public void GoBack()
    {
        if (HasActive && Step != WizardStep.Admin)
            return;

        if (Step == WizardStep.Session && IsPastRacesBrowse && !string.IsNullOrEmpty(_pastRacesRelative))
        {
            _ = PopPastRacesFolderAsync();
            return;
        }

        if (Step == WizardStep.Session)
            _pastRacesRelative = string.Empty;

        Step = Step switch
        {
            WizardStep.Session => WizardStep.Office,
            WizardStep.EventFile => WizardStep.Session,
            WizardStep.Admin => HasActive ? WizardStep.Active : WizardStep.Office,
            _ => Step
        };
    }

    /// <summary>
    /// Office pick: live offices Graph-index session leaves once; Past Races lists
    /// <b>one folder level</b> only (cellular).
    /// </summary>
    public async Task SelectOfficeAsync(string? office)
    {
        if (HasActive || string.IsNullOrWhiteSpace(office))
            return;

        SelectedOffice = office;
        _pastRacesRelative = string.Empty;
        OnPropertyChanged(nameof(IsPastRacesBrowse));
        OnPropertyChanged(nameof(PastRacesBreadcrumb));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepSubtitle));

        if (IsPastRacesBrowse)
        {
            await RefreshPastRacesLevelAsync(force: true).ConfigureAwait(true);
            LoadPastRacesBrowser();
        }
        else
        {
            await EnsureOfficeSessionsFromGraphIfNeededAsync(office).ConfigureAwait(true);
            LoadSessions(office);
        }

        Step = WizardStep.Session;
    }

    public void SelectSession(EventPackInfo? session) =>
        _ = SelectSessionAsync(session);

    /// <summary>
    /// When Graph is signed in: download the full session pack first, then resolve .event / checkout.
    /// </summary>
    public async Task SelectSessionAsync(EventPackInfo? session)
    {
        if (HasActive || session is null)
            return;

        if (session.IsNavigationFolder)
        {
            await EnterPastRacesFolderAsync(session).ConfigureAwait(true);
            return;
        }

        if (session.Lock is { Status: LockLifecycleStatus.Active }
            && !string.Equals(session.Lock.MachineId, _machine.MachineId, StringComparison.OrdinalIgnoreCase))
        {
            Alert(
                "Already checked out",
                $"This event is already checked out by {session.Lock.HolderName} on {session.Lock.MachineName}.");
            return;
        }

        SelectedSession = session;
        _sessionPackPulled = false;
        EventFiles.Clear();
        foreach (var f in session.EventFiles)
            EventFiles.Add(f);

        if (_cloud.IsAvailable)
        {
            SetBusy("Downloading event", "Getting race files from OneDrive…");
            try
            {
                // DownloadSessionAsync enforces SessionDownloadBudget; keep UI progress live.
                var pull = await _cloud.DownloadSessionAsync(
                    session.Path,
                    _settings.LibraryRoot,
                    _settings.GraphDriveRoot,
                    new Progress<string>(s =>
                    {
                        ProgressText = string.IsNullOrWhiteSpace(s) ? "Please wait…" : s;
                    })).ConfigureAwait(true);

                if (!pull.Success
                    && !Directory.Exists(Path.Combine(session.Path, PathService.RunScoreFolderName)))
                {
                    await ShowBusyResultAsync(
                        "Couldn't download event",
                        pull.Message
                        + "\n\nTry again when signal is better, or ask setup staff for help.");
                    return;
                }

                _sessionPackPulled = pull.Success || pull.FilesTransferred > 0;
                // Re-read .event files from the downloaded pack (placeholders had none).
                var files = EventFileSelector.ListEventFiles(session.Path);
                EventFiles.Clear();
                foreach (var f in files)
                    EventFiles.Add(f);
                SelectedSession = new EventPackInfo
                {
                    Office = session.Office,
                    ParentEvent = session.ParentEvent,
                    SessionName = session.SessionName,
                    RelativePath = session.RelativePath,
                    Path = session.Path,
                    EventFiles = files,
                    PrimaryEventFile = EventFileSelector.ChoosePrimary(files),
                    Lock = session.Lock
                };
                Status = pull.Success
                    ? pull.Message
                    : "Download incomplete — using what is on this laptop.";
            }
            catch (OperationCanceledException)
            {
                await ShowBusyResultAsync(
                    "Download timed out",
                    "OneDrive download timed out on cellular. Try again when signal is better.");
                return;
            }
            catch (Exception ex)
            {
                await ShowBusyResultAsync("Couldn't download event", ex.Message);
                return;
            }
            finally
            {
                if (Busy && !BusyNeedsAcknowledge)
                    ClearBusy();
            }
        }

        await ContinueCheckoutAfterSessionReadyAsync().ConfigureAwait(true);
    }

    private async Task ContinueCheckoutAfterSessionReadyAsync()
    {
        if (SelectedSession is null)
            return;

        if (EventFiles.Count == 0)
        {
            var activePath = _paths.GetActivePath(
                _settings, SelectedSession.Office, SelectedSession.RelativePath);
            var suggested = VendorLauncher.SuggestEventFileNameFromRaceName(SelectedSession.SessionName);
            if (!TimerDialog.ConfirmSaveAsGuide(
                    OwnerWindow,
                    "No T&S .event yet",
                    VendorLauncher.FormatSaveAsFolderGuide(activePath, suggested)))
                return;

            SelectedEventFile = string.Empty;
            await CheckoutAsync().ConfigureAwait(true);
            return;
        }

        if (EventFiles.Count == 1)
        {
            SelectedEventFile = EventFiles[0];
            await CheckoutAsync().ConfigureAwait(true);
            return;
        }

        SelectedEventFile = SelectedSession.PrimaryEventFile ?? EventFiles[0];
        Step = WizardStep.EventFile;
    }

    public void ConfirmEventFileAndCheckout()
    {
        if (SelectedSession is null)
            return;
        if (EventFiles.Count > 0 && string.IsNullOrWhiteSpace(SelectedEventFile))
        {
            Alert("Choose a file", "Select the timing file for this race.");
            return;
        }

        _ = CheckoutAsync();
    }

    public async Task CheckoutAsync()
    {
        if (SelectedSession is null)
            return;
        // SelectedEventFile may be empty when checking out a Template with RUNSCORE only.

        SyncDisplayNameFromMachine();
        SetBusy("Loading event", "Getting your race ready…");
        try
        {
            var eval = _locks.EvaluateAcquire(SelectedSession.Path, _settings);
            string? reason = null;
            var confirmEmergency = false;

            var confirmForce = false;

            if (eval.Outcome == LockAcquireOutcome.BlockedAlive)
            {
                ClearBusy();
                var holder = eval.Lock?.HolderName ?? "another timer";
                var machine = eval.Lock?.MachineName ?? "another laptop";
                var lastSeen = eval.Lock?.LastSeenAt.ToLocalTime().ToString("g") ?? "unknown";
                if (!TimerDialog.Confirm(
                        OwnerWindow,
                        "Already in use",
                        $"Someone else is already using this event.\n\n"
                        + $"{holder} on {machine}\nLast seen {lastSeen}\n\n"
                        + "Only force take-over if that laptop truly cannot finish (crash, power loss, or you're the only timer left).\n\n"
                        + "Force take-over requires the Admin PIN.",
                        yesLabel: "Force take-over…",
                        noLabel: "Cancel"))
                    return;

                var pin = PromptPin("Enter Admin PIN to force take-over:");
                if (pin is null)
                    return;
                if (!string.Equals(pin, AppSettings.DefaultAdminPin, StringComparison.Ordinal))
                {
                    Alert("Wrong PIN", "Admin PIN did not match. Event was not taken over.");
                    return;
                }

                SetBusy("Loading event", "Force take-over — recovering race…");
                reason = "Force take-over (Admin PIN) — other laptop still looked alive";
                confirmForce = true;
            }

            if (eval.Outcome == LockAcquireOutcome.NeedsEmergencyConfirm)
            {
                ClearBusy();
                var holder = eval.Lock?.HolderName ?? "another timer";
                var machine = eval.Lock?.MachineName ?? "another laptop";
                var lastSeen = eval.Lock?.LastSeenAt.ToLocalTime().ToString("g") ?? "unknown";
                if (!TimerDialog.Confirm(
                        OwnerWindow,
                        "Take over event?",
                        $"This event was checked out by {holder} on {machine}.\n"
                        + $"Last seen {lastSeen} — that laptop looks offline (crashed or shut down).\n\n"
                        + "Take over on this laptop?\n\n"
                        + "We'll load the race from OneDrive and apply any newer live autosave files from the other machine.",
                        yesLabel: "Take over",
                        noLabel: "Cancel"))
                    return;
                SetBusy("Loading event", "Recovering race…");
                reason = "Emergency reclaim — other laptop unavailable";
                confirmEmergency = true;
            }

            var progress = new Progress<CopyProgress>(p =>
                ProgressText = p.FilesCopied > 0 ? $"Copying race files… ({p.FilesCopied})" : "Getting your race ready…");
            var statusProgress = new Progress<string>(s => ProgressText = FriendlyProgress(s));

            var result = await _checkout.CheckoutAsync(new CheckoutRequest
            {
                Pack = SelectedSession,
                Settings = _settings,
                HolderName = DisplayName.Trim(),
                PrimaryEventFileOverride = SelectedEventFile,
                ConfirmEmergencyReclaim = confirmEmergency,
                ConfirmForceTakeover = confirmForce,
                EmergencyReason = reason,
                SkipGraphDownload = _sessionPackPulled
            }, progress, statusProgress);

            if (!result.Success)
            {
                await ShowBusyResultAsync(
                    "Couldn't load event",
                    "We couldn't load this event. Try again or ask setup staff for help.");
                return;
            }

            Active = result.Active;
            Step = WizardStep.Active;
            Status = string.Empty;

            // One-shot warnings: reclaim/autosave recovery, or prefs written while RSServer was already open.
            if (confirmEmergency || confirmForce
                || result.RunScoreNeedsRestart
                || result.Message.Contains("autosave", StringComparison.OrdinalIgnoreCase))
            {
                var title = confirmForce ? "Force take-over complete"
                    : confirmEmergency ? "Race recovered"
                    : result.RunScoreNeedsRestart ? "Close RunScore, then reopen here"
                    : "Race loaded";
                await ShowBusyResultAsync(title, result.Message);
            }
            else
            {
                ClearBusy();
            }
        }
        catch (Exception ex)
        {
            await ShowBusyResultAsync("Couldn't load event", "We couldn't load this event.\n\n" + ex.Message);
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
            if (SelectedOffice is not null)
                LoadSessions(SelectedOffice);
        }
    }

    public async Task FinishAsync(bool closingApp = false)
    {
        if (Active is null)
            return;

        SetBusy(
            closingApp ? "Closing — saving your race" : "Saving your race",
            "Please wait…\nDo not shut down this laptop yet.");

        // Pause live mirror so it cannot recreate RUNSCORE\_autosave while Finish deletes it.
        _mirrorTimer.Stop();
        _mirror.Pause();
        try
        {
            await _mirror.DrainAsync();
        }
        catch
        {
            // continue Finish even if drain fails
        }

        try
        {
            // 1) Fail fast if the RunScore/T&S window is still open.
            // 2) Only then wait for headless RSServer (often lingers after the UI closes).
            SetFinishProgress(
                "1 · Waiting for RunScore",
                "Checking that RunScore is closed…");
            var wait = await _processGuard.WaitForBlockingClearedAsync(
                TimeSpan.FromSeconds(25),
                new Progress<string>(s => SetFinishProgress("1 · Waiting for RunScore", s)));
            if (wait.Kind == BlockingWaitKind.WindowStillOpen)
            {
                var names = string.Join(", ", wait.ProcessNames);
                await ShowBusyResultAsync(
                    "Still open",
                    "Close RunScore (and Timing & Scoring) first, then tap Finish again.\n\n"
                    + $"Still open: {names}");
                return;
            }

            if (wait.Kind == BlockingWaitKind.StillRunning)
            {
                var names = string.Join(", ", wait.ProcessNames);
                await ShowBusyResultAsync(
                    "Still open",
                    "The RunScore window is closed, but a background process is still running"
                    + (wait.ProcessNames.Any(ProcessGuard.IsRunScoreProcess) ? " (RSServer)." : ".")
                    + $"\n\nStill detected: {names}\n\n"
                    + "Wait a few seconds and tap Finish again. If it keeps happening, end it in Task Manager.");
                return;
            }

            SetFinishProgress("2 · Saving your race", "Starting file save to OneDrive…");
            var copyProgress = new Progress<CopyProgress>(p =>
                SetFinishProgress(
                    "2 · Saving your race",
                    p.FilesCopied > 0
                        ? $"Copying race files to OneDrive… ({p.FilesCopied} files)"
                        : "Copying race files to OneDrive…"));
            var statusProgress = new Progress<string>(ApplyFinishStatus);

            var result = await _checkout.CheckinAsync(
                _settings,
                DisplayName.Trim(),
                removeActive: true,
                waitForOneDrive: true,
                progress: copyProgress,
                statusProgress: statusProgress);

            if (!result.Success && result.BlockingProcesses.Count > 0)
            {
                var names = string.Join(", ", result.BlockingProcesses);
                await ShowBusyResultAsync(
                    "Still open",
                    "RunScore or Timing & Scoring reopened (or is still shutting down).\n\n"
                    + $"Still detected: {names}\n\n"
                    + "Close it, wait a moment, then tap Finish again.");
                return;
            }

            if (!result.Success)
            {
                await ShowBusyResultAsync(
                    "Couldn't finish",
                    "We couldn't finish saving. Try again or ask setup staff for help.");
                return;
            }

            // Local save OK but Graph upload incomplete — keep Active + lock; staff retry Finish.
            if (result.RetainActiveForCloudRetry)
            {
                await ShowBusyResultAsync(
                    "Saved on this laptop — cloud still needed",
                    result.Message
                    + "\n\nDo not shut down yet. Keep this app open and tap Finish again when cellular signal is better."
                    + "\n\nAnother laptop must not check out this event until cloud upload succeeds.");
                return;
            }

            Active = null;
            SelectedSession = null;
            SelectedEventFile = null;
            LoadOffices();
            if (Offices.Count == 1)
                await SelectOfficeAsync(Offices[0]);
            else
                Step = WizardStep.Office;
            Status = string.Empty;

            var doneBody = result.AwaitingOneDriveConfirm
                ? "Your race is saved.\n\nCheck the OneDrive cloud icon in the taskbar — when it looks finished, you can shut down this laptop."
                : "Your race is saved.\n\nSafe to shut down this laptop.";
            if (!result.AutosaveCleared)
            {
                doneBody +=
                    "\n\nWarning: some live autosave files (RUNSCORE\\_autosave) could not be removed. "
                    + "Ask setup to delete that folder before the next race so post-event edits are not overwritten.";
            }

            // Same branded overlay — OK to dismiss. No second Windows dialog.
            await ShowBusyResultAsync("All done", doneBody);
        }
        catch (Exception ex)
        {
            await ShowBusyResultAsync("Couldn't finish", "We couldn't finish saving.\n\n" + ex.Message);
        }
        finally
        {
            _mirror.Resume();
            _mirrorTimer.Start();
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    public async Task<bool> ConfirmCloseAsync(Window? owner = null)
    {
        if (_checkout.GetActive() is null)
            return true;

        var choice = TimerDialog.AskCloseWhileActive(owner);
        return choice switch
        {
            TimerCloseChoice.KeepWorking => false,
            TimerCloseChoice.CloseWithoutSaving => true,
            TimerCloseChoice.SaveAndClose => await SaveAndAllowCloseAsync(),
            _ => false
        };
    }

    private async Task<bool> SaveAndAllowCloseAsync()
    {
        await FinishAsync(closingApp: true);
        // FinishAsync already shows the branded "All done" overlay.
        return _checkout.GetActive() is null;
    }

    public void LaunchRunScore()
    {
        if (Active is null) return;

        // Prefs are written at checkout. Only re-apply + launch when RS is not already running
        // (registry is read at RSServer start). No popup nag if it's already open — checkout warned once.
        var running = _processGuard.GetRunScoreProcessNames();
        if (running.Count > 0)
        {
            Status = "RunScore is already open. Close it, then tap Open RunScore here if you need the race-day preferences.";
            return;
        }

        var result = _launcher.LaunchRunScore(Active, _settings);
        if (!result.Success)
            Alert("RunScore", result.Message);
        else
            Status = result.Message.Contains("Updated", StringComparison.OrdinalIgnoreCase)
                     || result.Message.Contains("SOP", StringComparison.OrdinalIgnoreCase)
                     || result.Message.Contains("preference", StringComparison.OrdinalIgnoreCase)
                ? "RunScore opening… (preferences set to SOP)"
                : "RunScore opening…";
    }

    public void LaunchTimingAndScoring()
    {
        if (Active is null) return;

        // If staff Save As'd a .event into Active, remember it for Finish / UI.
        var discovered = VendorLauncher.ResolvePrimaryEventFile(Active);
        if (!string.IsNullOrWhiteSpace(discovered)
            && !string.Equals(Active.PrimaryEventFile, discovered, StringComparison.OrdinalIgnoreCase))
        {
            Active = new ActiveCheckout
            {
                Office = Active.Office,
                ParentEvent = Active.ParentEvent,
                SessionName = Active.SessionName,
                RelativePath = Active.RelativePath,
                LibrarySessionPath = Active.LibrarySessionPath,
                ActivePath = Active.ActivePath,
                BackupPath = Active.BackupPath,
                AutosavePath = Active.AutosavePath,
                DbsPath = Active.DbsPath,
                PrimaryEventFile = discovered,
                ScopedPaths = Active.ScopedPaths.Contains(discovered, StringComparer.OrdinalIgnoreCase)
                    ? Active.ScopedPaths
                    : Active.ScopedPaths.Append(discovered).ToList(),
                CheckedOutAt = Active.CheckedOutAt
            };
            _activeStore.Save(Active);
        }

        var result = _launcher.LaunchTimingAndScoring(Active, _settings);
        if (!result.Success)
        {
            Alert("Timing & Scoring", result.Message);
            return;
        }

        // Folder map was already shown at checkout — don't cover the T&S splash with another dialog.
        Status = "Timing & Scoring opening…";
    }

    public void OpenWorkingFolder()
    {
        if (Active is null || !Directory.Exists(Active.ActivePath))
            return;
        Process.Start(new ProcessStartInfo { FileName = Active.ActivePath, UseShellExecute = true });
    }

    public void TryOpenAdmin()
    {
        var pin = PromptPin("Enter admin PIN to open settings:");
        if (pin is null)
            return;
        if (!string.Equals(pin, AppSettings.DefaultAdminPin, StringComparison.Ordinal))
        {
            Alert("Admin", "Incorrect PIN.");
            return;
        }

        Step = WizardStep.Admin;
        NotifyGraphStatus();
    }

    public void SaveAdminSettings()
    {
        _settingsStore.Save(_settings);
        Status = "Settings saved.";
        ReloadFromDisk();
        NotifyGraphStatus();
    }

    public async Task SignInGraphAsync(IntPtr parentWindow)
    {
        try
        {
            Busy = true;
            BusyTitle = "Signing in to OneDrive…";
            var ok = await _graphAuth.SignInAsync(parentWindow);
            if (!ok)
            {
                Status = "OneDrive sign-in cancelled.";
                return;
            }

            Status = $"Signed in as {_graphAuth.AccountEmail}.";
            NotifyGraphStatus();
            await RefreshLibraryFromGraphAsync();
        }
        catch (Exception)
        {
            Alert(
                "OneDrive sign-in",
                "Could not sign in to OneDrive. Check the internet connection and try again.\n\n"
                + "If this keeps happening, ask setup staff for help.");
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task SignOutGraphAsync()
    {
        try
        {
            await _graphAuth.SignOutAsync();
            _graphIndexedOffices.Clear();
            Status = "Signed out of OneDrive Graph.";
            NotifyGraphStatus();
        }
        catch (Exception)
        {
            Alert("OneDrive sign-out", "Could not sign out. Try again, or restart the app.");
        }
    }

    /// <summary>Admin / sign-in: probe office folders only (no event trees — cellular).</summary>
    public async Task RefreshLibraryFromGraphAsync(bool blockUi = true)
    {
        if (!_cloud.IsAvailable)
        {
            Alert("OneDrive", "Sign in to OneDrive first.");
            return;
        }

        try
        {
            if (blockUi)
            {
                Busy = true;
                BusyTitle = "Loading offices from OneDrive…";
            }
            else
                Status = "Checking office folders on OneDrive…";

            _graphIndexedOffices.Clear();
            var result = await _cloud.RefreshOfficeListAsync(
                _settings.LibraryRoot,
                _settings.GraphDriveRoot,
                new Progress<string>(s => Status = s));
            Status = result.Message;
            if (!result.Success && blockUi)
                Alert("OneDrive library", result.Message);
            ReloadFromDisk();
        }
        catch (Exception ex)
        {
            Status = "OneDrive office check failed.";
            if (blockUi)
                Alert("OneDrive library", ex.Message);
        }
        finally
        {
            if (blockUi)
                Busy = false;
        }
    }

    /// <summary>
    /// Once per office per app run: Graph-index that office only (add + prune moved events).
    /// Skipped when offline / already indexed this run — cellular, one tree at a time.
    /// </summary>
    private async Task EnsureOfficeSessionsFromGraphIfNeededAsync(string office)
    {
        if (!_cloud.IsAvailable || HasActive)
            return;
        if (_graphIndexedOffices.Contains(office))
            return;

        try
        {
            Busy = true;
            BusyTitle = $"Loading events for {office}…";
            var result = await _cloud.RefreshOfficeSessionsAsync(
                _settings.LibraryRoot,
                _settings.GraphDriveRoot,
                office,
                new Progress<string>(s => Status = s),
                protectSessionPath: Active?.LibrarySessionPath);
            Status = result.Message;
            if (!result.Success)
                Alert("OneDrive events", result.Message);
            else
                _graphIndexedOffices.Add(office);
        }
        catch (Exception ex)
        {
            Alert("OneDrive events", ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private string PastRacesIndexKey =>
        EventBuilderService.PastRacesFolderName + ":" + _pastRacesRelative.Replace('\\', '/');

    private async Task EnterPastRacesFolderAsync(EventPackInfo folder)
    {
        if (HasActive || folder is null || !folder.IsNavigationFolder)
            return;

        _pastRacesRelative = string.IsNullOrEmpty(_pastRacesRelative)
            ? folder.SessionName
            : _pastRacesRelative.TrimEnd('/', '\\') + "/" + folder.SessionName;
        OnPropertyChanged(nameof(PastRacesBreadcrumb));
        OnPropertyChanged(nameof(StepSubtitle));

        await RefreshPastRacesLevelAsync(force: true).ConfigureAwait(true);
        LoadPastRacesBrowser();
    }

    private async Task PopPastRacesFolderAsync()
    {
        var rel = _pastRacesRelative.Replace('\\', '/').Trim('/');
        var slash = rel.LastIndexOf('/');
        _pastRacesRelative = slash < 0 ? string.Empty : rel[..slash];
        OnPropertyChanged(nameof(PastRacesBreadcrumb));
        OnPropertyChanged(nameof(StepSubtitle));

        await RefreshPastRacesLevelAsync(force: false).ConfigureAwait(true);
        LoadPastRacesBrowser();
    }

    /// <summary>
    /// One children list under the current Past Races path. Probe RUNSCORE only at year depth
    /// (Past Races / office bucket / year → race leaves).
    /// </summary>
    private async Task RefreshPastRacesLevelAsync(bool force)
    {
        if (!_cloud.IsAvailable || HasActive)
            return;
        if (!force && _graphIndexedOffices.Contains(PastRacesIndexKey))
            return;

        var relative = string.IsNullOrEmpty(_pastRacesRelative)
            ? EventBuilderService.PastRacesFolderName
            : EventBuilderService.PastRacesFolderName + "/" + _pastRacesRelative.Replace('\\', '/');
        var depth = string.IsNullOrEmpty(_pastRacesRelative)
            ? 0
            : _pastRacesRelative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        // depth 0 = buckets, 1 = years, 2+ = race folders (probe session leaves)
        var probeLeaves = depth >= 2;

        try
        {
            Busy = true;
            BusyTitle = probeLeaves ? "Loading races…" : "Loading folders…";
            var result = await _cloud.RefreshFolderChildrenAsync(
                _settings.LibraryRoot,
                _settings.GraphDriveRoot,
                relative,
                probeSessionLeaves: probeLeaves,
                new Progress<string>(s => Status = s));
            Status = result.Message;
            if (!result.Success)
                Alert("OneDrive", result.Message);
            else
                _graphIndexedOffices.Add(PastRacesIndexKey);
        }
        catch (Exception ex)
        {
            Alert("OneDrive", ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private void LoadPastRacesBrowser()
    {
        Sessions.Clear();
        var folder = Path.Combine(_settings.LibraryRoot, EventBuilderService.PastRacesFolderName);
        if (!string.IsNullOrEmpty(_pastRacesRelative))
            folder = Path.Combine(folder, _pastRacesRelative.Replace('/', Path.DirectorySeparatorChar));

        foreach (var entry in _scanner.ListBrowserEntries(
                     _settings.LibraryRoot, EventBuilderService.PastRacesFolderName, folder))
            Sessions.Add(entry);

        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowEmptySessions));
        OnPropertyChanged(nameof(PastRacesBreadcrumb));
        OnPropertyChanged(nameof(StepSubtitle));
    }

    private void NotifyGraphStatus()
    {
        OnPropertyChanged(nameof(IsGraphSignedIn));
        OnPropertyChanged(nameof(GraphAccountStatus));
        OnPropertyChanged(nameof(GraphReadyLabel));
        OnPropertyChanged(nameof(GraphLandingStatus));
        OnPropertyChanged(nameof(GraphReadyBannerBackground));
        OnPropertyChanged(nameof(GraphReadyBannerBorder));
    }

    /// <summary>Refresh OneDrive status shown on the office landing page.</summary>
    public void RefreshGraphLandingStatus() => NotifyGraphStatus();

    /// <summary>
    /// If any allowed office folder is missing locally, probe Graph for those offices only
    /// (no event trees). Partial local caches (e.g. 5 of 8) must not skip the rest.
    /// </summary>
    public async Task EnsureLibraryIndexedFromGraphAsync()
    {
        if (!_cloud.IsAvailable || HasActive)
            return;

        if (!HasMissingOfficeFolders())
            return;

        await RefreshLibraryFromGraphAsync(blockUi: false);
    }

    private bool HasMissingOfficeFolders()
    {
        var root = _settings.LibraryRoot;
        if (string.IsNullOrWhiteSpace(root))
            return true;

        foreach (var name in PathService.AllowedOfficeFolders)
        {
            if (!Directory.Exists(Path.Combine(root, name)))
                return true;
        }

        return false;
    }

    public void EditRunScoreSop()
    {
        try
        {
            var path = _sopStore.EnsureFile();
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            Status = "Opened RunScore SOP — save the file, then checkout / Open RunScore to apply.";
        }
        catch (Exception ex)
        {
            Alert("RunScore SOP", "Could not open runscore-sop.json:\n" + ex.Message);
        }
    }

    /// <summary>Create Library + RaceDay Active/Backup roots if missing (idempotent).</summary>
    public void EnsureLocalRoots()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_settings.LibraryRoot))
            {
                Directory.CreateDirectory(_settings.LibraryRoot);
                // Known office names — show immediately without waiting on Graph (cellular).
                // Events still load from Graph only when an office is picked and local list is empty.
                foreach (var office in PathService.AllowedOfficeFolders)
                    Directory.CreateDirectory(Path.Combine(_settings.LibraryRoot, office));
            }
            DefenderExclusionPolicy.EnsureRaceDayFolders(_settings.RaceDayRoot);
        }
        catch
        {
            // Permissions — Admin can fix paths later.
        }
    }

    public void OpenNewEventBuilder()
    {
        if (HasActive)
        {
            Alert("Already checked out", "Finish the current race before setting up a new event.");
            return;
        }

        EnsureLocalRoots();
        LoadOffices();
        var offices = Offices.Count > 0
            ? Offices.ToList()
            : PathService.AllowedOfficeFolders.ToList();

        var owner = OwnerWindow;
        var dlg = new S2F.RaceDayCheckout.NewEventWindow(_settings.LibraryRoot, offices);
        if (owner is not null)
            dlg.Owner = owner;

        var ok = dlg.ShowDialog() == true;
        if (!ok || dlg.Result is not { Success: true } result)
            return;

        if (string.IsNullOrWhiteSpace(result.SessionPath) || string.IsNullOrWhiteSpace(result.Office))
        {
            Alert("Event created", result.Message);
            ReloadFromDisk();
            return;
        }

        // Auto-checkout so mid-week staff land on Active and can open T&S / RunScore immediately.
        SelectedOffice = result.Office;
        LoadSessions(result.Office);
        var pack = Sessions.FirstOrDefault(s =>
            string.Equals(s.Path, result.SessionPath, StringComparison.OrdinalIgnoreCase));
        if (pack is null)
        {
            // Scanner race / OneDrive delay — fall back to a direct pack from the new folder.
            var eventFiles = EventFileSelector.ListEventFiles(result.SessionPath);
            pack = new EventPackInfo
            {
                Office = result.Office,
                ParentEvent = result.SessionFolderName ?? Path.GetFileName(result.SessionPath),
                SessionName = result.SessionFolderName ?? Path.GetFileName(result.SessionPath),
                RelativePath = result.SessionFolderName ?? Path.GetFileName(result.SessionPath),
                Path = result.SessionPath,
                EventFiles = eventFiles,
                PrimaryEventFile = result.PrimaryEventFile ?? EventFileSelector.ChoosePrimary(eventFiles)
            };
        }

        SelectedSession = pack;
        EventFiles.Clear();
        foreach (var f in pack.EventFiles)
            EventFiles.Add(f);
        SelectedEventFile = result.PrimaryEventFile ?? pack.PrimaryEventFile ?? string.Empty;

        Status = result.MissingEventFile
            ? result.Message
            : "New event checked out: " + result.SessionFolderName;

        _ = CheckoutAfterNewEventAsync(result);
    }

    private async Task CheckoutAfterNewEventAsync(EventBuildResult build)
    {
        // Same Save As map as the "No T&S .event yet" checkout confirm (new-event skips that dialog).
        if (build.MissingEventFile
            && !string.IsNullOrWhiteSpace(build.SessionPath)
            && SelectedSession is not null)
        {
            var activePath = _paths.GetActivePath(
                _settings, SelectedSession.Office, SelectedSession.RelativePath);
            var suggested = build.SuggestedEventFileName
                            ?? VendorLauncher.SuggestEventFileNameFromRaceName(
                                SelectedSession.SessionName);
            if (!TimerDialog.ConfirmSaveAsGuide(
                    OwnerWindow,
                    "No T&S .event yet",
                    VendorLauncher.FormatSaveAsFolderGuide(activePath, suggested)))
            {
                // Staff cancelled — leave the pack on disk but don't checkout.
                SelectedSession = null;
                SelectedEventFile = null;
                EventFiles.Clear();
                Status = "Event created — checkout cancelled.";
                ReloadFromDisk();
                return;
            }
        }

        await CheckoutAsync();
    }

    private void LoadOffices()
    {
        Offices.Clear();
        foreach (var office in _scanner.ListOffices(_settings.LibraryRoot))
            Offices.Add(office.Name);
    }

    private void LoadSessions(string office)
    {
        Sessions.Clear();
        foreach (var session in _scanner.ListSessions(_settings.LibraryRoot, office))
            Sessions.Add(session);
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowEmptySessions));
    }

    /// <summary>Clear this-run Graph cache for the current office / Past Races folder and reload.</summary>
    public async Task RetryOfficeSessionsAsync()
    {
        if (HasActive || string.IsNullOrWhiteSpace(SelectedOffice))
            return;

        if (IsPastRacesBrowse)
        {
            _graphIndexedOffices.Remove(PastRacesIndexKey);
            await RefreshPastRacesLevelAsync(force: true).ConfigureAwait(true);
            LoadPastRacesBrowser();
            return;
        }

        _graphIndexedOffices.Remove(SelectedOffice);
        await SelectOfficeAsync(SelectedOffice);
    }

    /// <summary>Lock holder name is always this PC's computer name (Environment.MachineName).</summary>
    private void SyncDisplayNameFromMachine()
    {
        var name = _machine.MachineName;
        if (string.Equals(_settings.DisplayName, name, StringComparison.Ordinal))
            return;
        _settings.DisplayName = name;
        _settingsStore.Save(_settings);
        OnPropertyChanged(nameof(DisplayName));
    }

    private void Heartbeat()
    {
        var active = _activeStore.Load();
        if (active is null) return;
        try { _locks.Heartbeat(active.LibrarySessionPath); }
        catch { /* ignore */ }

        if (!_cloud.IsAvailable)
            return;

        // Tiny lock JSON only — never UploadFolderAsync(._checkin) (history + backup trees).
        var lockPath = _paths.GetLockPath(active.LibrarySessionPath);
        if (!File.Exists(lockPath))
            return;

        var sessionPath = active.LibrarySessionPath;
        var libraryRoot = _settings.LibraryRoot;
        var driveRoot = _settings.GraphDriveRoot;
        // Skip tick if prior lock upload still running (weak cell must not stack).
        if (!_heartbeatUploadGate.Wait(0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(GraphSyncPolicy.HttpTimeout);
                await _cloud.UploadCheckoutLockAsync(sessionPath, libraryRoot, driveRoot, cts.Token);
            }
            catch
            {
                // Non-fatal — next heartbeat retries.
            }
            finally
            {
                _heartbeatUploadGate.Release();
            }
        });
    }

    private static string? PromptPin(string message)
    {
        var window = new Window
        {
            Title = "Admin",
            Width = 360,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgBrush")
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var box = new System.Windows.Controls.PasswordBox();
        panel.Children.Add(box);
        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        string? result = null;
        var ok = new Button { Content = "OK", Style = (Style)Application.Current.FindResource("PrimaryButton"), Width = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("SecondaryButton"), Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) => { result = box.Password; window.DialogResult = true; };
        cancel.Click += (_, _) => { window.DialogResult = false; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        box.Focus();
        return window.ShowDialog() == true ? result : null;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
