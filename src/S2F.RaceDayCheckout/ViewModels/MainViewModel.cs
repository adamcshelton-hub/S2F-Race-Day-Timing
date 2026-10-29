using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

public enum AdminSection
{
    SetupNewEvent,
    EditCurrentEvent,
    Settings
}

/// <summary>Lazy Admin → Edit Current Event flow (office → event → assign). Cellular: one tree at a time.</summary>
public enum AdminEventsSubStep
{
    Office,
    Session,
    Assign
}

/// <summary>Lazy Admin → Setup New Event (layout → office → [parent] → copy-from → source → details).</summary>
public enum AdminNewEventSubStep
{
    Layout,
    Office,
    Parent,
    SourceKind,
    Source,
    Details,
    /// <summary>After creating a leaf in a Race Group — choose checkout vs add another.</summary>
    GroupCreated
}

public sealed record NewEventSourceItem(string Display, string Path, bool IsFolder = false);

public enum NewEventCopyFrom
{
    Templates = 0,
    PastRaces = 1,
    CurrentEvents = 2
}

public sealed record NewEventSourceKindOption(string Display, NewEventCopyFrom Kind);

/// <summary>One Race (flat) vs Race Group (top-level folder with one leaf per event).</summary>
public sealed record NewEventLayoutOption(string Display, bool IsRaceGroup, string? Detail = null);

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
    private readonly EventBuilderService _eventBuilder = new();
    private readonly Services.ManualBackupCoordinator _manualBackup = new();
    private ManualBackupFloatWindow? _manualBackupFloat;
    /// <summary>Coalesce rapid bib submits into one Graph upload (avoids file-lock storms).</summary>
    private CancellationTokenSource? _mbPublishDebounceCts;
    /// <summary>One-shot SoftPull after Backup Laptop open (canceled on Done).</summary>
    private CancellationTokenSource? _mbSoftPullCts;
    /// <summary>Live SoftPull loop on Main Timing while Manual Backup is Active (Play + idle).</summary>
    private CancellationTokenSource? _mbLiveSoftPullCts;
    private readonly SemaphoreSlim _mbPublishGate = new(1, 1);
    private readonly SemaphoreSlim _mbLiveSoftPullGate = new(1, 1);
    private volatile bool _mbPublishAgain;
    private readonly DispatcherTimer _heartbeatTimer;
    private readonly DispatcherTimer _mirrorTimer;
    private readonly DispatcherTimer _sessionLockPollTimer;
    private readonly DispatcherTimer _clockTimer;
    /// <summary>Last whole second painted on the header / live clocks.</summary>
    private DateTime _lastPaintedClockSecond = DateTime.MinValue;
    private readonly SemaphoreSlim _heartbeatUploadGate = new(1, 1);
    /// <summary>Canceled when Finish is about to release the lock, so an in-flight heartbeat cannot rewrite it.</summary>
    private CancellationTokenSource _heartbeatCts = new();
    private int _suppressLockPublish;
    private bool _heartbeatGateHeldForFinish;
    private readonly SemaphoreSlim _sessionLockPollGate = new(1, 1);
    /// <summary>Offices / Past Races paths already Graph-indexed this run (cellular: don't re-walk).</summary>
    private readonly HashSet<string> _graphIndexedOffices = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Merged local+remote *.event names per session path (Announcer T&S secondary probe).</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _probedEventNames =
        new(StringComparer.OrdinalIgnoreCase);

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
    private CheckoutRole _checkoutRole = CheckoutRole.Timing;
    private AdminSection _adminSection = AdminSection.SetupNewEvent;
    private AdminEventsSubStep _adminEventsSubStep = AdminEventsSubStep.Office;
    private AdminNewEventSubStep _adminNewEventSubStep = AdminNewEventSubStep.Layout;
    /// <summary>PIN unlock expiry — setup staff stay in Admin across Finish without re-entering PIN.</summary>
    private DateTimeOffset? _adminUnlockedUntil;
    private static readonly TimeSpan AdminUnlockDuration = TimeSpan.FromHours(8);
    /// <summary>
    /// Where Finish should return when Active was opened from Admin (Setup vs Edit Current Event).
    /// Not set for normal race-day checkout — Finish goes to office / shutdown guidance.
    /// </summary>
    private enum FinishAdminReturn
    {
        None,
        SetupNewEvent,
        EditCurrentEvent
    }

    private FinishAdminReturn _finishAdminReturn = FinishAdminReturn.None;
    /// <summary>
    /// Keep the Active-page Save As path visible after Create a New Timing &amp; Scoring
    /// (even when a primary .event already exists — staff are Save As'ing announcer/video).
    /// </summary>
    private bool _pinActiveSaveAsGuide;
    /// <summary>Prevent stacking takeover dialogs from heartbeat while one is open.</summary>
    private bool _lockTakenOverPromptOpen;
    /// <summary>After we detect a lost lock, skip cloud writes until user Close Local / Force.</summary>
    private bool _cloudWritesBlockedUntilResolved;
    /// <summary>
    /// After "I'll Close RunScore First", do not re-open the modal until this time —
    /// timer needs the UI free to close vendors, then uses the Active banner.
    /// </summary>
    private DateTimeOffset? _lockTakenOverSnoozeUntil;
    /// <summary>Takeover confirms from SelectSession (before pack download) — reused by CheckoutAsync for the same path only.</summary>
    private bool _checkoutConfirmForce;
    private bool _checkoutConfirmEmergency;
    private string? _checkoutReclaimReason;
    private string? _checkoutConfirmSessionPath;
    private string? _adminOffice;
    /// <summary>Under Admin → Edit Current Event → Past Races only (bucket / year). Separate from race-day browse.</summary>
    private string _adminPastRacesRelative = string.Empty;
    private EventPackInfo? _adminSession;
    private string? _selectedTimingFile;
    private string _adminListFilter = string.Empty;
    private readonly List<string> _adminOfficeSource = new();
    private readonly List<EventPackInfo> _adminSessionSource = new();
    private string _raceDayListFilter = string.Empty;
    private bool _newEventIsRaceGroup;
    private string? _newEventOffice;
    private string _newEventParentName = string.Empty;
    private int _newEventParentYear = DateTime.Today.Year;
    /// <summary>Resolved parent folder under the office (e.g. 2026 - LRM Weekend).</summary>
    private string? _newEventParentFolder;
    /// <summary>Leaf just created under the Race Group (for success UI / checkout label).</summary>
    private string _lastCreatedRaceName = string.Empty;
    private NewEventCopyFrom _newEventCopyFrom = NewEventCopyFrom.Templates;
    private NewEventSourceItem? _newEventSource;
    private string _newEventPastRelative = string.Empty;
    private string _newEventRaceName = string.Empty;
    private DateTime? _newEventRaceDateValue = DateTime.Today;
    private string _newEventResultsId = string.Empty;
    private string _newEventPreview = string.Empty;
    private readonly List<string> _newEventOfficeSource = new();
    private readonly List<NewEventSourceItem> _newEventSourceSource = new();
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
        _mirror = new AutosaveMirrorService(
            _copy,
            _activeStore,
            _cloud,
            () => _settings,
            stillOwnsLockAsync: async ct =>
                await CheckLockOwnershipAsync(ct, "mirror").ConfigureAwait(false) == LockCloudVerdict.Held);

        Offices = new ObservableCollection<string>();
        VisibleOffices = new ObservableCollection<string>();
        Sessions = new ObservableCollection<EventPackInfo>();
        VisibleSessions = new ObservableCollection<EventPackInfo>();
        SessionListView = CollectionViewSource.GetDefaultView(VisibleSessions);
        EventFiles = new ObservableCollection<string>();
        AdminOffices = new ObservableCollection<string>();
        AdminSessions = new ObservableCollection<EventPackInfo>();
        AdminEventFiles = new ObservableCollection<string>();
        NewEventOffices = new ObservableCollection<string>();
        NewEventSources = new ObservableCollection<NewEventSourceItem>();
        NewEventSourceKinds = new ObservableCollection<NewEventSourceKindOption>
        {
            new("Templates Folder", NewEventCopyFrom.Templates),
            new("Past Races", NewEventCopyFrom.PastRaces),
            new("Current Events (This Office)", NewEventCopyFrom.CurrentEvents)
        };
        NewEventLayouts = new ObservableCollection<NewEventLayoutOption>
        {
            new("One Race", IsRaceGroup: false,
                Detail: "A single race folder on disk."),
            new("Race Group — Multiple Events in Series or Multi-Day Event", IsRaceGroup: true,
                Detail: "Sets up a top-level folder with individual event folders for each race in the group.")
        };
        RaceStarts = new ObservableCollection<RaceStartRowViewModel>();
        RaceStartBoard = new ObservableCollection<RaceStartRowViewModel>();
        RaceStarts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRaceStarts));
            RebuildRaceStartBoard();
        };
        AdminRaceStarts = new ObservableCollection<RaceStartRowViewModel>();

        // Poll ~20Hz and paint only when the wall-clock second flips. A 1s DispatcherTimer
        // drifts under UI load and can lag the true second — bad for participant-facing race clocks.
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _clockTimer.Tick += (_, _) => TickLaptopClock();

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(30, _settings.HeartbeatSeconds)) };
        _heartbeatTimer.Tick += async (_, _) =>
        {
            try { await HeartbeatAsync().ConfigureAwait(true); }
            catch { /* non-fatal */ }
        };
        _mirrorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(30, _settings.AutosaveMirrorSeconds)) };
        _mirrorTimer.Tick += async (_, _) =>
        {
            try { await _mirror.MirrorOnceAsync(); }
            catch { /* non-fatal */ }
        };
        _sessionLockPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(60, AppSettings.DefaultSessionLockPollSeconds))
        };
        _sessionLockPollTimer.Tick += async (_, _) =>
        {
            try { await PollSessionLocksOnceAsync().ConfigureAwait(true); }
            catch { /* non-fatal — list stays usable */ }
        };

        EnsureLocalRoots(); // office name folders under C:\S2F\Library — no Graph
        LoadAdminUnlock();
        ReloadFromDisk();
        RefreshLaptopClockSyncStatus();
        TickLaptopClock();
        _clockTimer.Start();
        _heartbeatTimer.Start();
        _mirrorTimer.Start();
        UpdateSessionLockPolling();

        _manualBackup.CatalogChanged += (_, _) => _ = PublishManualBackupCatalogAsync();
    }

    private bool IsAdminUnlocked =>
        _adminUnlockedUntil is { } until && until > DateTimeOffset.UtcNow;

    private static string AdminUnlockStorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S2FRaceDayCheckout",
            "admin-unlock.json");

    /// <summary>
    /// Admin PIN once per <see cref="AdminUnlockDuration"/> (persisted). Returns false if cancelled / wrong.
    /// </summary>
    private bool EnsureAdminUnlocked(string title, string message)
    {
        if (IsAdminUnlocked)
            return true;

        var pin = TimerDialog.PromptAdminPin(
            OwnerWindow,
            title,
            message,
            verifyCompanyPin: true);
        if (pin is null)
            return false;

        GrantAdminUnlock();
        return true;
    }

    private void GrantAdminUnlock()
    {
        _adminUnlockedUntil = DateTimeOffset.UtcNow.Add(AdminUnlockDuration);
        try
        {
            var dir = Path.GetDirectoryName(AdminUnlockStorePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(
                AdminUnlockStorePath,
                System.Text.Json.JsonSerializer.Serialize(new AdminUnlockState
                {
                    UntilUtc = _adminUnlockedUntil.Value
                }));
        }
        catch
        {
            // In-memory unlock still works this run.
        }
    }

    private void LoadAdminUnlock()
    {
        try
        {
            if (!File.Exists(AdminUnlockStorePath))
                return;
            var state = System.Text.Json.JsonSerializer.Deserialize<AdminUnlockState>(
                File.ReadAllText(AdminUnlockStorePath));
            if (state?.UntilUtc is { } until && until > DateTimeOffset.UtcNow)
                _adminUnlockedUntil = until;
            else
                TryDeleteAdminUnlockFile();
        }
        catch
        {
            _adminUnlockedUntil = null;
        }
    }

    private static void TryDeleteAdminUnlockFile()
    {
        try
        {
            if (File.Exists(AdminUnlockStorePath))
                File.Delete(AdminUnlockStorePath);
        }
        catch
        {
            // ignore
        }
    }

    private sealed class AdminUnlockState
    {
        public DateTimeOffset UntilUtc { get; set; }
    }

    private void EnterAdminSetup(string? statusMessage = null)
    {
        Step = WizardStep.Admin;
        _adminSection = AdminSection.SetupNewEvent;
        _adminEventsSubStep = AdminEventsSubStep.Office;
        InitNewEventForm();
        NotifyAdminSection();
        NotifyGraphStatus();
        if (!string.IsNullOrWhiteSpace(statusMessage))
            Status = statusMessage;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Offices { get; }
    /// <summary>Office list after race-day search filter (UI).</summary>
    public ObservableCollection<string> VisibleOffices { get; }
    public ObservableCollection<EventPackInfo> Sessions { get; }
    /// <summary>Session / Past Races list after race-day search filter (UI).</summary>
    public ObservableCollection<EventPackInfo> VisibleSessions { get; }
    /// <summary>Grouped view of <see cref="VisibleSessions"/> (Checked Out / Available when Timing).</summary>
    public ICollectionView SessionListView { get; }
    public ObservableCollection<string> EventFiles { get; }
    public ObservableCollection<string> AdminOffices { get; }
    public ObservableCollection<EventPackInfo> AdminSessions { get; }
    public ObservableCollection<string> AdminEventFiles { get; }
    public ObservableCollection<string> NewEventOffices { get; }
    public ObservableCollection<NewEventSourceItem> NewEventSources { get; }
    public ObservableCollection<NewEventSourceKindOption> NewEventSourceKinds { get; }
    public ObservableCollection<NewEventLayoutOption> NewEventLayouts { get; }

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
                OnPropertyChanged(nameof(ShowExitAdmin));
                OnPropertyChanged(nameof(ShowRolePicker));
                OnPropertyChanged(nameof(ShowCheckForUpdates));
                OnPropertyChanged(nameof(IsTimingActive));
                OnPropertyChanged(nameof(IsAnnouncerActive));
                OnPropertyChanged(nameof(IsManualBackupActive));
                NotifyActiveSaveAsGuide();
                OnPropertyChanged(nameof(IsAdminSetupNewEvent));
                OnPropertyChanged(nameof(IsAdminEditCurrentEvent));
                OnPropertyChanged(nameof(IsAdminSettings));
                OnPropertyChanged(nameof(IsAdminNewEventLayout));
                OnPropertyChanged(nameof(IsAdminNewEventOffice));
                OnPropertyChanged(nameof(IsAdminNewEventParent));
                OnPropertyChanged(nameof(IsAdminNewEventSourceKind));
                OnPropertyChanged(nameof(IsAdminNewEventSource));
                OnPropertyChanged(nameof(IsAdminNewEventDetails));
                OnPropertyChanged(nameof(IsAdminNewEventGroupCreated));
                OnPropertyChanged(nameof(IsNewEventRaceGroup));
                OnPropertyChanged(nameof(IsAdminEventsOffice));
                OnPropertyChanged(nameof(IsAdminEventsSession));
                OnPropertyChanged(nameof(IsAdminEventsAssign));
                OnPropertyChanged(nameof(ShowAdminListFilter));
                OnPropertyChanged(nameof(ShowAddRaceToGroup));
                OnPropertyChanged(nameof(ShowMoveToPastRaces));
                OnPropertyChanged(nameof(ShowAdminHeaderLink));
                OnPropertyChanged(nameof(ShowGraphReadyInline));
                OnPropertyChanged(nameof(ShowGraphSignInBanner));
                OnPropertyChanged(nameof(ShowRaceDayOfficeList));
                OnPropertyChanged(nameof(EmptySessionsTitle));
                OnPropertyChanged(nameof(EmptySessionsBody));
                OnPropertyChanged(nameof(ShowSessionLock));
                OnPropertyChanged(nameof(ShowRaceDayListFilter));
                OnPropertyChanged(nameof(RaceDayListFilterHint));
                UpdateSessionLockPolling();
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
        WizardStep.Office => "Choose Your Office",
        WizardStep.Session => IsPastRacesBrowse ? "Browse Past Races" : "Choose Your Event",
        WizardStep.EventFile => "Which Timing File?",
        // Event name is the hero — don't repeat it in the card below.
        WizardStep.Active => Active?.DisplayName ?? "Ready for Race Day",
        WizardStep.Admin => _adminSection switch
        {
            AdminSection.SetupNewEvent => "Setup New Event",
            AdminSection.EditCurrentEvent => "Edit Current Event",
            _ => "Admin Settings"
        },
        _ => "S2F Race Day"
    };

    public string StepSubtitle => Step switch
    {
        WizardStep.Office => IsAnnouncerRole
            ? "Announcer T&S Laptop — double-click your office. Races with extra T&S .event files are listed."
            : IsManualBackupRole
                ? "Manual Backup Laptop — double-click your office to continue."
                : "Main Timing Laptop — double-click your office to continue.",
        WizardStep.Session => IsPastRacesBrowse
            ? $"{PastRacesBreadcrumb} — open a folder, or double-click a race."
            : IsAnnouncerRole
                ? SelectedOffice is null
                    ? "Races that have a non-primary T&S .event (announcer, video, …)."
                    : $"Office: {SelectedOffice} — races with a non-primary T&S .event."
                : IsManualBackupRole
                    ? SelectedOffice is null
                        ? "Double-click today's race for Manual Backup."
                        : $"Office: {SelectedOffice} — double-click today's race for Manual Backup."
                    : SelectedOffice is null
                        ? "Double-click today's race."
                        : $"Office: {SelectedOffice} — double-click today's race.",
        WizardStep.EventFile => SelectedSession is null
            ? (IsAnnouncerRole ? "Double-click the T&S file for this laptop." : "Double-click the main timing file.")
            : IsAnnouncerRole
                ? $"{SelectedSession.DisplayName} — pick which T&S .event this laptop uses."
                : $"{SelectedSession.DisplayName} — double-click the main timing file.",
        WizardStep.Active => Active is null
            ? ""
            : Active.Role == CheckoutRole.Announcer
                ? $"{Active.Office} · Announcer T&S"
                : Active.Role == CheckoutRole.ManualBackup
                    ? $"{Active.Office} · Manual Backup"
                    : Active.Office,
        WizardStep.Admin => _adminSection switch
        {
            AdminSection.SetupNewEvent => _adminNewEventSubStep switch
            {
                AdminNewEventSubStep.Layout =>
                    "One Race is a single folder. A Race Group is for series or multi-day events — one top-level folder with a folder per race.",
                AdminNewEventSubStep.Office => _newEventIsRaceGroup
                    ? "Choose which office to build this Race Group in."
                    : "Choose which office to build this Event in.",
                AdminNewEventSubStep.Parent => "Name the Race Group and set its year (Year - Name on disk).",
                AdminNewEventSubStep.SourceKind =>
                    "Copy from a Template, Past Races, or a current event in this office.",
                AdminNewEventSubStep.Source => _newEventCopyFrom switch
                {
                    NewEventCopyFrom.Templates => "Double-click the Template to copy.",
                    NewEventCopyFrom.PastRaces =>
                        "Open an office Past Races folder, then a year, or double-click a race to copy.",
                    _ => "Open a Race Group if needed, or double-click a current event to duplicate."
                },
                AdminNewEventSubStep.Details => _newEventIsRaceGroup
                    ? "Enter this race’s name and date, then Create. You can add another race under the same Race Group after."
                    : "Enter race name and date, then Create event.",
                AdminNewEventSubStep.GroupCreated =>
                    "Use Checkout to setup RunScore / T&S for this event, or add another race under the same Race Group.",
                _ => "Set up a new race pack."
            },
            AdminSection.EditCurrentEvent => _adminEventsSubStep switch
            {
                AdminEventsSubStep.Office => "Pick an office to mark the main timing .event.",
                AdminEventsSubStep.Session => _adminOffice is null
                    ? "Pick the event to configure."
                    : $"Office: {_adminOffice} — pick the event to configure.",
                AdminEventsSubStep.Assign => _adminSession is null
                    ? "Mark the main timing .event, then Save."
                    : AdminEventFiles.Count == 0
                        ? $"{_adminSession.DisplayName} — Create a New Timing & Scoring, Save As, then Refresh Event Files."
                        : IsAdminSessionRaceGroupLeaf
                            ? $"{_adminSession.DisplayName} — save main timing, open current T&S, or create a new T&S for announcer / video."
                            : $"{_adminSession.DisplayName} — save main timing, open current T&S, or create a new T&S for announcer / video.",
                _ => "Mark the main timing .event for a race."
            },
            _ => "For setup staff only — not needed on race morning."
        },
        _ => ""
    };

    public string EventFileStepHint =>
        IsAnnouncerRole
            ? "This race has more than one non-primary T&S file. Double-click the one this Announcer T&S laptop should open (announcer, video display, special callout, …)."
            : "More than one Timing & Scoring file was found. Double-click the main timing file for this race.";

    /// <summary>
    /// Race-day Session/EventFile, or Admin only when Back still walks an inner wizard step
    /// (not the Admin top screens — use Exit Admin for those).
    /// </summary>
    public bool ShowBack =>
        Step is WizardStep.Session or WizardStep.EventFile
        || (Step == WizardStep.Admin && AdminBackStaysInAdmin);

    /// <summary>
    /// True when Admin Back peels a Setup / Edit sub-step instead of leaving Admin entirely.
    /// </summary>
    private bool AdminBackStaysInAdmin =>
        (_adminSection == AdminSection.SetupNewEvent
         && _adminNewEventSubStep != AdminNewEventSubStep.Layout)
        || (_adminSection == AdminSection.EditCurrentEvent
            && _adminEventsSubStep != AdminEventsSubStep.Office);

    public bool ShowAdminHeaderLink => Step != WizardStep.Admin;

    public bool ShowExitAdmin => Step == WizardStep.Admin;

    public bool ShowRolePicker => !HasActive && Step is WizardStep.Office or WizardStep.Session;

    public bool IsTimingRole => _checkoutRole == CheckoutRole.Timing;
    public bool IsAnnouncerRole => _checkoutRole == CheckoutRole.Announcer;
    public bool IsManualBackupRole => _checkoutRole == CheckoutRole.ManualBackup;
    public bool ShowSessionLock => IsTimingRole;
    public bool IsTimingActive => IsActiveStep && Active is { Role: CheckoutRole.Timing };
    public bool IsAnnouncerActive => IsActiveStep && Active?.Role == CheckoutRole.Announcer;
    public bool IsManualBackupActive => IsActiveStep && Active is { Role: CheckoutRole.ManualBackup };

    /// <summary>
    /// Another laptop holds the lock — show Close Local Copy on Active instead of a blocking modal loop.
    /// </summary>
    public bool ShowLockTakenOverBanner =>
        IsTimingActive && _cloudWritesBlockedUntilResolved;

    public string LockTakenOverBannerText =>
        "Another laptop took this race. Cloud upload is paused so you cannot overwrite their work.\n\n"
        + "Close RunScore and Timing & Scoring, then tap Close Local Copy. "
        + "To take the race back later, choose it from the event list and use Force Take Over there.";
    public bool IsAdminSetupNewEvent => IsAdminStep && _adminSection == AdminSection.SetupNewEvent;
    public bool IsAdminEditCurrentEvent => IsAdminStep && _adminSection == AdminSection.EditCurrentEvent;
    public bool IsAdminSettings => IsAdminStep && _adminSection == AdminSection.Settings;
    public bool IsAdminNewEventLayout => IsAdminSetupNewEvent && _adminNewEventSubStep == AdminNewEventSubStep.Layout;
    public bool IsAdminNewEventOffice => IsAdminSetupNewEvent && _adminNewEventSubStep == AdminNewEventSubStep.Office;
    public bool IsAdminNewEventParent => IsAdminSetupNewEvent && _adminNewEventSubStep == AdminNewEventSubStep.Parent;
    public bool IsAdminNewEventSourceKind => IsAdminSetupNewEvent && _adminNewEventSubStep == AdminNewEventSubStep.SourceKind;
    public bool IsAdminNewEventSource => IsAdminSetupNewEvent && _adminNewEventSubStep == AdminNewEventSubStep.Source;
    public bool IsAdminNewEventDetails => IsAdminSetupNewEvent && _adminNewEventSubStep == AdminNewEventSubStep.Details;
    public bool IsAdminNewEventGroupCreated =>
        IsAdminSetupNewEvent && _adminNewEventSubStep == AdminNewEventSubStep.GroupCreated;
    public bool IsNewEventRaceGroup => _newEventIsRaceGroup;

    /// <summary>Success step after creating a Race Group leaf — checkout or add another.</summary>
    public bool ShowNewEventGroupCheckout => IsAdminNewEventGroupCreated && SelectedSession is not null;

    public string NewEventGroupCreatedHeadline =>
        string.IsNullOrWhiteSpace(_lastCreatedRaceName)
            ? "Race Created"
            : $"Created {_lastCreatedRaceName}";

    public string NewEventGroupCreatedDetail =>
        string.IsNullOrWhiteSpace(_newEventParentFolder)
            ? "This race is in Library under the Race Group."
            : string.IsNullOrWhiteSpace(_newEventOffice)
                ? $"Under Race Group {_newEventParentFolder}."
                : $"Under {_newEventOffice} / {_newEventParentFolder}.";

    public string NewEventCheckoutLastLabel =>
        string.IsNullOrWhiteSpace(_lastCreatedRaceName)
            ? "Checkout This Race"
            : "Checkout " + _lastCreatedRaceName;

    /// <summary>Edit Current Event — selected session is a leaf under a Race Group folder.</summary>
    public bool IsAdminSessionRaceGroupLeaf =>
        _adminSession is not null
        && !string.Equals(_adminSession.ParentEvent, _adminSession.SessionName, StringComparison.OrdinalIgnoreCase);

    public bool ShowAddRaceToGroup => IsAdminEventsAssign && IsAdminSessionRaceGroupLeaf;

    /// <summary>
    /// Edit Current Event on a live office pack — archive whole race / Race Group into Past Races via Graph.
    /// </summary>
    public bool ShowMoveToPastRaces =>
        IsAdminEventsAssign
        && _adminSession is not null
        && !IsAdminPastRacesBrowse
        && EventBuilderService.DestinationOffices.Contains(
            _adminSession.Office, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Edit Current Event assign — Launch T&amp;S + Save As map (create first .event or add announcer/video).
    /// </summary>
    public bool ShowAdminTsSetup => IsAdminEventsAssign;

    /// <summary>No .event yet — first Save As guidance.</summary>
    public bool ShowAdminTsEmptyHint => IsAdminEventsAssign && AdminEventFiles.Count == 0;

    /// <summary>Already has .event(s) — guidance to Save As extra announcer/video files into Active.</summary>
    public bool ShowAdminTsAddSecondaryHint => IsAdminEventsAssign && AdminEventFiles.Count > 0;

    public bool ShowAdminSaveAssignment => IsAdminEventsAssign && AdminEventFiles.Count > 0;

    public string AdminSaveAsFolderGuide
    {
        get
        {
            if (_adminSession is null)
                return string.Empty;

            var activePath = AdminSessionIsActive()
                ? Active!.ActivePath
                : _paths.GetActivePath(_settings, _adminSession.Office, _adminSession.RelativePath);
            var suggested = VendorLauncher.SuggestEventFileNameFromRaceName(_adminSession.SessionName);
            return VendorLauncher.FormatSaveAsPathInline(activePath, suggested);
        }
    }

    public bool IsAdminEventsOffice => IsAdminEditCurrentEvent && _adminEventsSubStep == AdminEventsSubStep.Office;
    public bool IsAdminEventsSession => IsAdminEditCurrentEvent && _adminEventsSubStep == AdminEventsSubStep.Session;
    public bool IsAdminEventsAssign => IsAdminEditCurrentEvent && _adminEventsSubStep == AdminEventsSubStep.Assign;
    /// <summary>
    /// Past Races path under the list (live office lists rely on StepSubtitle — same density as race-day).
    /// </summary>
    public bool ShowAdminEventsPathBreadcrumb =>
        IsAdminEventsSession && IsAdminPastRacesBrowse;
    public bool ShowAdminListFilter =>
        IsAdminEventsOffice || IsAdminEventsSession
        || IsAdminNewEventOffice || IsAdminNewEventSource;

    public string AdminListFilter
    {
        get => _adminListFilter;
        set
        {
            if (!Set(ref _adminListFilter, value ?? string.Empty))
                return;
            ApplyAdminListFilter();
        }
    }

    public string AdminListFilterHint =>
        IsAdminNewEventOffice || IsAdminEventsOffice
            ? "Search Offices…"
            : "Search This Folder…";

    public bool ShowRaceDayListFilter =>
        (IsOfficeStep && IsGraphSignedIn) || IsSessionStep;

    public string RaceDayListFilter
    {
        get => _raceDayListFilter;
        set
        {
            if (!Set(ref _raceDayListFilter, value ?? string.Empty))
                return;
            ApplyRaceDayListFilter();
        }
    }

    public string RaceDayListFilterHint =>
        IsOfficeStep ? "Search Offices…" : "Search This Folder…";

    private bool IsAdminPastRacesBrowse =>
        string.Equals(_adminOffice, EventBuilderService.PastRacesFolderName, StringComparison.OrdinalIgnoreCase);

    private string AdminPastRacesBreadcrumb
    {
        get
        {
            if (!IsAdminPastRacesBrowse)
                return string.Empty;
            if (string.IsNullOrEmpty(_adminPastRacesRelative))
                return EventBuilderService.PastRacesFolderName;
            return EventBuilderService.PastRacesFolderName + " / "
                   + _adminPastRacesRelative.Replace("/", " / ", StringComparison.Ordinal)
                       .Replace("\\", " / ", StringComparison.Ordinal);
        }
    }

    public string AdminEventsBreadcrumb =>
        _adminEventsSubStep switch
        {
            AdminEventsSubStep.Office => "Step 1 — Choose Office",
            AdminEventsSubStep.Session => IsAdminPastRacesBrowse
                ? $"Step 2 — {AdminPastRacesBreadcrumb} (Open a Folder, or Pick a Race)"
                : string.IsNullOrEmpty(_adminOffice)
                    ? "Step 2 — Choose Event"
                    : $"Step 2 — {_adminOffice}",
            AdminEventsSubStep.Assign => _adminSession is null
                ? "Step 3 — Mark Main Timing .event"
                : $"Step 3 — {_adminOffice} / {_adminSession.DisplayName} — Save, or Choose Another Event to Start Over",
            _ => string.Empty
        };

    private static string FormatBrowseRelative(string relative) =>
        relative.Replace("/", " / ", StringComparison.Ordinal)
            .Replace("\\", " / ", StringComparison.Ordinal);

    private string NewEventSourceBreadcrumb =>
        _newEventCopyFrom switch
        {
            NewEventCopyFrom.Templates => "Choose Template",
            NewEventCopyFrom.PastRaces => string.IsNullOrEmpty(_newEventPastRelative)
                ? EventBuilderService.PastRacesFolderName
                : EventBuilderService.PastRacesFolderName + " / " + FormatBrowseRelative(_newEventPastRelative),
            _ => string.IsNullOrEmpty(_newEventOffice)
                ? "Current Events"
                : string.IsNullOrEmpty(_newEventPastRelative)
                    ? $"{_newEventOffice} — Current Events"
                    : $"{_newEventOffice} / {FormatBrowseRelative(_newEventPastRelative)}"
        };

    public string AdminNewEventBreadcrumb =>
        _adminNewEventSubStep switch
        {
            AdminNewEventSubStep.Layout => "Step 1 — One Race or Race Group",
            AdminNewEventSubStep.Office => _newEventIsRaceGroup
                ? "Step 2 — Choose Office for This Race Group"
                : "Step 2 — Choose Office for This Event",
            AdminNewEventSubStep.Parent => "Step 3 — Race Group Name",
            AdminNewEventSubStep.SourceKind => string.IsNullOrEmpty(_newEventOffice)
                ? "Copy From"
                : _newEventIsRaceGroup && !string.IsNullOrEmpty(_newEventParentFolder)
                    ? $"{_newEventOffice} / {_newEventParentFolder} — Copy From"
                    : $"{_newEventOffice} — Copy From",
            AdminNewEventSubStep.Source => NewEventSourceBreadcrumb,
            AdminNewEventSubStep.Details => _newEventSource is null
                ? "Race Details"
                : _newEventIsRaceGroup && !string.IsNullOrEmpty(_newEventParentFolder)
                    ? $"Copy {_newEventSource.Display} → {_newEventOffice} / {_newEventParentFolder}"
                    : $"Copy {_newEventSource.Display} → {_newEventOffice}",
            AdminNewEventSubStep.GroupCreated => string.IsNullOrEmpty(_newEventParentFolder)
                ? "Race Created — What’s Next?"
                : $"{_newEventOffice} / {_newEventParentFolder} — Race Created",
            _ => string.Empty
        };

    public string EmptySessionsTitle =>
        IsAnnouncerRole && !IsPastRacesBrowse
            ? "No Announcer T&S races here"
            : "No events loaded for this office";

    public string EmptySessionsBody =>
        IsAnnouncerRole && !IsPastRacesBrowse
            ? "No races with a non-primary T&S .event yet. Setup can Save As extra .event files into the race folder, and mark the main timing file under Admin → Edit Current Event."
            : "Nothing listed for this folder yet (empty on OneDrive, or the list timed out). Go Back, or Refresh events.";

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
                OnPropertyChanged(nameof(ShowRolePicker));
                OnPropertyChanged(nameof(IsTimingActive));
                OnPropertyChanged(nameof(IsAnnouncerActive));
                OnPropertyChanged(nameof(IsManualBackupActive));
                OnPropertyChanged(nameof(ShowLockTakenOverBanner));
                NotifyActiveSaveAsGuide();
                UpdateSessionLockPolling();
                ReloadRaceStartsFromActive();
                SyncManualBackupAttachment();
            }
        }
    }

    public Services.ManualBackupCoordinator ManualBackup => _manualBackup;

    public ObservableCollection<RaceStartRowViewModel> RaceStarts { get; }
    /// <summary>Clock cards plus trailing <c>+</c> tile for Active timing board.</summary>
    public ObservableCollection<RaceStartRowViewModel> RaceStartBoard { get; }
    public ObservableCollection<RaceStartRowViewModel> AdminRaceStarts { get; }

    public bool HasRaceStarts => RaceStarts.Count > 0;

    private string _newAdminRaceStartCategory = string.Empty;
    private string _newAdminRaceStartTime = string.Empty;
    private RaceStartEntry? _adminRaceStartEditOriginal;
    private string _laptopClockText = string.Empty;
    private string _laptopClockSyncLabel = "Clock";
    private LaptopClockSyncKind _laptopClockSyncKind = LaptopClockSyncKind.Unknown;
    private bool _raceStartBoardRebuildSuspended;

    public string NewAdminRaceStartCategory
    {
        get => _newAdminRaceStartCategory;
        set => Set(ref _newAdminRaceStartCategory, value ?? string.Empty);
    }

    public string NewAdminRaceStartTime
    {
        get => _newAdminRaceStartTime;
        set => Set(ref _newAdminRaceStartTime, value ?? string.Empty);
    }

    public bool IsEditingAdminRaceStart => _adminRaceStartEditOriginal is not null;

    public string AdminRaceStartCommitLabel => IsEditingAdminRaceStart ? "Save Edit" : "Add Start";

    public string LaptopClockText
    {
        get => _laptopClockText;
        private set => Set(ref _laptopClockText, value);
    }

    public string LaptopClockSyncLabel
    {
        get => _laptopClockSyncLabel;
        private set => Set(ref _laptopClockSyncLabel, value);
    }

    public LaptopClockSyncKind LaptopClockSyncKind
    {
        get => _laptopClockSyncKind;
        private set
        {
            if (!Set(ref _laptopClockSyncKind, value))
                return;
            OnPropertyChanged(nameof(LaptopClockSyncBrush));
            OnPropertyChanged(nameof(ShowLaptopClockWarning));
        }
    }

    /// <summary>True only when staff should verify the laptop clock (no green Synced badge).</summary>
    public bool ShowLaptopClockWarning => LaptopClockSyncKind == LaptopClockSyncKind.CheckClock;

    public System.Windows.Media.Brush LaptopClockSyncBrush =>
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xC3, 0x5C));

    public void RefreshLaptopClockSyncStatus()
    {
        _settings = _settingsStore.Load();
        var kind = LaptopClockSyncStatus.Evaluate(_settings, DateTimeOffset.UtcNow, DateTimeOffset.Now);
        LaptopClockSyncKind = kind;
        LaptopClockSyncLabel = LaptopClockSyncStatus.WarningLabel(kind);
    }

    private void TickLaptopClock()
    {
        var now = DateTime.Now;
        var second = RaceStartClock.TruncateToSecond(now);
        if (second == _lastPaintedClockSecond)
            return;
        _lastPaintedClockSecond = second;

        LaptopClockText = second.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var row in RaceStarts)
            row.Tick(second);
    }

    private void ReloadRaceStartsFromActive()
    {
        _raceStartBoardRebuildSuspended = true;
        try
        {
            RaceStarts.Clear();
            if (Active is null || Active.Role == CheckoutRole.Announcer)
            {
                RebuildRaceStartBoard();
                return;
            }

            foreach (var entry in Active.RaceStarts)
                RaceStarts.Add(CreateClockRow(entry));

            // Default seed: one blank clock + trailing + (same as first Add).
            if (RaceStarts.Count == 0)
                RaceStarts.Add(RaceStartRowViewModel.CreateDraft(CommitRaceStartClock, RemoveRaceStart));
        }
        finally
        {
            _raceStartBoardRebuildSuspended = false;
            RebuildRaceStartBoard();
        }
    }

    private void RebuildRaceStartBoard()
    {
        if (_raceStartBoardRebuildSuspended)
            return;
        RaceStartBoard.Clear();
        foreach (var row in RaceStarts)
            RaceStartBoard.Add(row);
    }

    private RaceStartRowViewModel CreateClockRow(RaceStartEntry entry) =>
        new(entry, onCommit: CommitRaceStartClock, onRemove: RemoveRaceStart);

    /// <summary>Trailing <c>+</c> tile — appends a new clock to the right (draft until labeled + timed).</summary>
    public void AddRaceStartClock()
    {
        if (Active is null || Active.Role == CheckoutRole.Announcer)
            return;

        // One empty clock at a time (no start time and no gun yet).
        if (RaceStarts.Any(IsIncompleteDraft))
        {
            Status = "Enter a start time or Capture Gun Start on the blank clock before adding another.";
            return;
        }

        // Keep on the board only until start/gun makes it persistable.
        RaceStarts.Add(RaceStartRowViewModel.CreateDraft(CommitRaceStartClock, RemoveRaceStart));
    }

    /// <summary>Blank clock: no scheduled start and no gun yet (category label optional).</summary>
    private static bool IsIncompleteDraft(RaceStartRowViewModel row) =>
        !row.IsAddTile && !row.HasStartTime && !row.HasGunStart;

    /// <summary>
    /// Persistable clocks only — incomplete drafts keep Entry at 00:00, which must never
    /// be written or they resurrect as a top-left ghost after every save/delete.
    /// </summary>
    private List<RaceStartEntry> SnapshotPersistableEntries(
        RaceStartRowViewModel? replaceRow = null,
        RaceStartEntry? replacement = null)
    {
        var list = new List<RaceStartEntry>();
        foreach (var r in RaceStarts)
        {
            if (IsIncompleteDraft(r))
                continue;
            if (replaceRow is not null && replacement is not null && ReferenceEquals(r, replaceRow))
            {
                list.Add(replacement);
                continue;
            }

            if (r.TryBuildEntry(out var entry, out _))
                list.Add(entry);
            else
                list.Add(r.Entry);
        }

        return list;
    }

    public void CommitRaceStartClock(RaceStartRowViewModel? row)
    {
        if (row is null || row.IsAddTile || Active is null || Active.Role == CheckoutRole.Announcer)
            return;
        if (!row.TryBuildEntry(out var updated, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Status = error;
            return;
        }

        PersistRaceStarts(RaceStartsStore.Normalize(SnapshotPersistableEntries(row, updated)));
    }

    /// <summary>
    /// Stamp this card’s gun from the laptop clock (<c>HH:MM:SS.ff</c>). Live race clock
    /// counts from gun; scheduled Start Time stays as planned.
    /// </summary>
    public void CaptureGunStart(RaceStartRowViewModel? row)
    {
        if (row is null || row.IsAddTile || Active is null || Active.Role == CheckoutRole.Announcer)
            return;

        // Category label optional — gun (and derived HH:MM) is enough.
        var gun = RaceStartClock.CaptureGunTimeOfDay(DateTime.Now);
        row.ApplyGunStart(gun);
        if (!row.TryBuildEntry(out var updated, out var buildError))
        {
            if (!string.IsNullOrWhiteSpace(buildError))
                Status = buildError;
            return;
        }

        PersistRaceStarts(RaceStartsStore.Normalize(SnapshotPersistableEntries(row, updated)));
        var gunText = RaceStartClock.FormatGunHhMmSs(gun);
        TryCopyGunTimeToClipboard(gunText);
        var label = string.IsNullOrWhiteSpace(row.CategoryText) ? "Clock" : row.CategoryText.Trim();
        Status = label + " gun " + gunText + " (copied)";
    }

    /// <summary>Copy this card’s gun stamp (<c>HH:MM:SS.ff</c>) to the clipboard.</summary>
    public void CopyGunStart(RaceStartRowViewModel? row)
    {
        if (row is null || row.IsAddTile || !row.HasGunStart || row.Entry.GunStartTimeOfDay is not { } gun)
            return;
        var gunText = RaceStartClock.FormatGunHhMmSs(gun);
        if (!TryCopyGunTimeToClipboard(gunText))
        {
            Status = "Couldn't copy gun time.";
            return;
        }

        Status = "Copied " + gunText;
    }

    private static bool TryCopyGunTimeToClipboard(string gunText)
    {
        try
        {
            System.Windows.Clipboard.SetText(gunText);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Remove gun stamp; live clock uses scheduled Start Time again.</summary>
    public void ClearGunStart(RaceStartRowViewModel? row)
    {
        if (row is null || row.IsAddTile || Active is null || Active.Role == CheckoutRole.Announcer)
            return;
        if (!row.HasGunStart)
            return;

        row.ClearGunStart();
        if (!row.TryBuildEntry(out var updated, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Status = error;
            return;
        }

        PersistRaceStarts(RaceStartsStore.Normalize(SnapshotPersistableEntries(row, updated)));
        var label = string.IsNullOrWhiteSpace(row.CategoryText) ? "Clock" : row.CategoryText.Trim();
        Status = label + " gun cleared — using scheduled start.";
    }

    public void RemoveRaceStart(RaceStartRowViewModel? row)
    {
        if (row is null || row.IsAddTile || Active is null)
            return;

        // Drop from the live list first. PersistRaceStarts re-attaches incomplete drafts still
        // in RaceStarts — leaving this row there made × look like a no-op on blank clocks.
        if (!RaceStarts.Remove(row))
            return;

        // Explicit delete may leave zero clocks — do not immediately re-seed a blank card.
        PersistRaceStarts(
            RaceStartsStore.Normalize(SnapshotPersistableEntries()),
            seedBlankIfEmpty: false);
        Status = "Start clock removed.";
    }

    private void PersistRaceStarts(IReadOnlyList<RaceStartEntry> starts, bool seedBlankIfEmpty = true)
    {
        if (Active is null)
            return;

        // Keep in-progress blank clocks; do not go through Active setter (that reloads and
        // used to race LostFocus / Capture Gun Start mid-click).
        var drafts = RaceStarts.Where(IsIncompleteDraft).ToList();
        var normalized = RaceStartsStore.Normalize(starts);

        _active = Active.WithRaceStarts(normalized);
        _activeStore.Save(_active);
        // Mirror into the pack locally; Finish uploads to OneDrive for next checkout / next year.
        try { RaceStartsStore.Save(_active.LibrarySessionPath, normalized); }
        catch { /* Finish will retry write-back */ }

        _raceStartBoardRebuildSuspended = true;
        try
        {
            RaceStarts.Clear();
            foreach (var entry in normalized)
                RaceStarts.Add(CreateClockRow(entry));
            foreach (var draft in drafts)
            {
                if (!RaceStarts.Contains(draft))
                    RaceStarts.Add(draft);
            }

            if (seedBlankIfEmpty && RaceStarts.Count == 0)
                RaceStarts.Add(RaceStartRowViewModel.CreateDraft(CommitRaceStartClock, RemoveRaceStart));
        }
        finally
        {
            _raceStartBoardRebuildSuspended = false;
            RebuildRaceStartBoard();
        }
    }

    public bool HasActive => Active is not null;

    /// <summary>Disk checkout, including a restart resume that the in-memory Active has not caught up to.</summary>
    public bool HasOpenCheckout => _checkout.GetActive() is not null;

    /// <summary>
    /// Timing Active Save As path: always when no .event yet, or after Create a New Timing &amp; Scoring
    /// (announcer / video Save As while a primary already exists).
    /// </summary>
    public bool ShowActiveSaveAsGuide =>
        IsTimingActive
        && Active is not null
        && (_pinActiveSaveAsGuide
            || EventFileSelector.ListEventFiles(Active.ActivePath).Count == 0);

    public string ActiveSaveAsFolderGuide =>
        Active is null
            ? string.Empty
            : VendorLauncher.FormatSaveAsPathInline(
                Active.ActivePath,
                VendorLauncher.SuggestEventFileName(Active));

    private void PinActiveSaveAsGuide()
    {
        _pinActiveSaveAsGuide = true;
        NotifyActiveSaveAsGuide();
    }

    private void ClearActiveSaveAsGuidePin()
    {
        _pinActiveSaveAsGuide = false;
        NotifyActiveSaveAsGuide();
    }

    private void NotifyActiveSaveAsGuide()
    {
        OnPropertyChanged(nameof(ShowActiveSaveAsGuide));
        OnPropertyChanged(nameof(ActiveSaveAsFolderGuide));
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (Set(ref _busy, value))
            {
                OnPropertyChanged(nameof(BusyKeepOpenVisible));
                OnPropertyChanged(nameof(CanTapPrimaryActions));
            }
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

    /// <summary>False while the busy overlay is up — Finish disables so a laggy click is not “ignored.”</summary>
    public bool CanTapPrimaryActions => !Busy;

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

    /// <summary>Let WPF paint the busy overlay before a Graph/lock call that can take seconds.</summary>
    private static async Task YieldUiAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            await Task.Yield();
            return;
        }

        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
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
            || technical.Contains("announcer", StringComparison.OrdinalIgnoreCase)
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

        switch (status)
        {
            case "finish:backup":
                SetFinishProgress(
                    "2 · Saving your race",
                    "Backing up the current library race files…");
                return;
            case "finish:copy":
                SetFinishProgress(
                    "2 · Saving your race",
                    "Saving this laptop's race files to the local library…");
                return;
            case "finish:prefs":
                SetFinishProgress(
                    "2 · Saving your race",
                    "Restoring office RunScore preferences…");
                return;
            case "finish:lock":
                SetFinishProgress(
                    "2 · Saving your race",
                    "Releasing the checkout lock on OneDrive…");
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
            case "finish:upload":
                SetFinishProgress(
                    "3 · Uploading to OneDrive",
                    "Uploading changed race files to OneDrive.\nKeep this window open.");
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

    public string RaceRosterLoginOverride
    {
        get => _settings.RaceRosterLoginOverride;
        set { _settings.RaceRosterLoginOverride = value; OnPropertyChanged(); }
    }

    public string RaceRosterPasswordOverride
    {
        get => _settings.RaceRosterPasswordOverride;
        set { _settings.RaceRosterPasswordOverride = value; OnPropertyChanged(); }
    }

    public string RunSignUpLoginOverride
    {
        get => _settings.RunSignUpLoginOverride;
        set { _settings.RunSignUpLoginOverride = value; OnPropertyChanged(); }
    }

    public string RunSignUpPasswordOverride
    {
        get => _settings.RunSignUpPasswordOverride;
        set { _settings.RunSignUpPasswordOverride = value; OnPropertyChanged(); }
    }

    /// <summary>Read-only — from OneDrive <c>._s2f/runscore-vendor-secrets.json</c> (local AppData cache).</summary>
    public string RunScoreLicenseName =>
        new RunScoreVendorSecretsStore().Load()?.LicenseName?.Trim() ?? string.Empty;

    /// <summary>Read-only — from OneDrive <c>._s2f/runscore-vendor-secrets.json</c> (local AppData cache).</summary>
    public string RunScoreLicenseKey =>
        new RunScoreVendorSecretsStore().Load()?.LicenseKey?.Trim() ?? string.Empty;

    public bool IsGraphSignedIn => _graphAuth.IsSignedIn;

    public string GraphAccountStatus =>
        _graphAuth.IsSignedIn
            ? $"Signed in as {_graphAuth.AccountEmail ?? "OneDrive"}"
            : "Not signed in — race files come from OneDrive.";

    /// <summary>Landing-page OneDrive check — plain language for timers.</summary>
    public string GraphReadyLabel => IsGraphSignedIn ? "OneDrive: ready" : "OneDrive: not signed in";

    public string GraphLandingStatus =>
        IsGraphSignedIn
            ? $"Signed in as {_graphAuth.AccountEmail ?? "OneDrive"}. This laptop can download and sync race files."
            : "Sign in to OneDrive to begin. This laptop does not use the OneDrive desktop app — "
              + "you cannot choose an office or load a race until you are signed in.";

    /// <summary>Compact account line for the header-row ready chip (office step, signed in).</summary>
    public string GraphReadyInlineDetail =>
        $"Signed in as {_graphAuth.AccountEmail ?? "OneDrive"}";

    /// <summary>Signed-in chip on the page-title row, top-right — does not push the office list down.</summary>
    public bool ShowGraphReadyInline => IsOfficeStep && IsGraphSignedIn;

    /// <summary>Full-width sign-in banner — only when signed out, so staff cannot miss it.</summary>
    public bool ShowGraphSignInBanner => IsOfficeStep && !IsGraphSignedIn;

    /// <summary>Office list only after Graph sign-in (Graph-only race day).</summary>
    public bool ShowRaceDayOfficeList => IsOfficeStep && IsGraphSignedIn;

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
            // Restart / same-machine reclaim — treat as race-day, not Admin setup checkout.
            _finishAdminReturn = FinishAdminReturn.None;
            SetCloudWritesBlocked(false);
            ClearLockTakenOverSnooze();
            Step = WizardStep.Active;
            Status = Active.Role == CheckoutRole.Announcer
                ? "Announcer T&S files loaded — tap Done when you're finished."
                : "Checked out — finish before starting another event.";
            // Timing: Backup → _autosave → Graph. Announcer: Graph DB's → Active for T&S CSV auto-load.
            KickAutosaveMirror();
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

        // Graph-only: stay on office step until signed in (do not auto-skip into sessions).
        if (!IsGraphSignedIn)
        {
            Step = WizardStep.Office;
            return;
        }

        // One office → show session step; Graph fill runs via SelectOfficeAsync when needed.
        if (Offices.Count == 1)
        {
            SelectedOffice = Offices[0];
            LoadSessions(Offices[0]);
            Step = WizardStep.Session;
        }
        else
            Step = WizardStep.Office;
    }

    /// <summary>Race-day browse/checkout requires Graph. Active mid-race stays on screen if already checked out.</summary>
    private bool EnsureGraphSignedInToBegin(string action)
    {
        if (IsGraphSignedIn)
            return true;

        Alert(
            "Sign In to OneDrive",
            $"Sign in to OneDrive before you {action}.\n\n"
            + "This laptop does not use the OneDrive desktop app — race files download and upload through this app only.");
        return false;
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
        {
            _pastRacesRelative = string.Empty;
            ClearRaceDayListFilter();
        }

        if (Step == WizardStep.Admin && _adminSection == AdminSection.SetupNewEvent)
        {
            if (_adminNewEventSubStep == AdminNewEventSubStep.GroupCreated)
            {
                // Stay on success — Back returns to layout to start a different pack.
                InitNewEventForm();
                NotifyAdminSection();
                return;
            }

            if (_adminNewEventSubStep == AdminNewEventSubStep.Details)
            {
                _adminNewEventSubStep = AdminNewEventSubStep.Source;
                ClearAdminListFilter();
                ApplyAdminListFilter();
                NotifyAdminSection();
                return;
            }

            if (_adminNewEventSubStep == AdminNewEventSubStep.Source)
            {
                if (_newEventCopyFrom != NewEventCopyFrom.Templates
                    && !string.IsNullOrEmpty(_newEventPastRelative))
                {
                    _ = PopNewEventPastFolderAsync();
                    return;
                }

                _newEventSource = null;
                _newEventPastRelative = string.Empty;
                NewEventSources.Clear();
                _newEventSourceSource.Clear();
                _adminNewEventSubStep = AdminNewEventSubStep.SourceKind;
                ClearAdminListFilter();
                NotifyAdminSection();
                return;
            }

            if (_adminNewEventSubStep == AdminNewEventSubStep.SourceKind)
            {
                if (_newEventIsRaceGroup
                    && !string.IsNullOrEmpty(_lastCreatedRaceName)
                    && SelectedSession is not null)
                {
                    _adminNewEventSubStep = AdminNewEventSubStep.GroupCreated;
                    ClearAdminListFilter();
                    NotifyAdminSection();
                    return;
                }

                _adminNewEventSubStep = _newEventIsRaceGroup
                    ? AdminNewEventSubStep.Parent
                    : AdminNewEventSubStep.Office;
                ClearAdminListFilter();
                ApplyAdminListFilter();
                NotifyAdminSection();
                return;
            }

            if (_adminNewEventSubStep == AdminNewEventSubStep.Parent)
            {
                _adminNewEventSubStep = AdminNewEventSubStep.Office;
                ClearAdminListFilter();
                ApplyAdminListFilter();
                NotifyAdminSection();
                return;
            }

            if (_adminNewEventSubStep == AdminNewEventSubStep.Office)
            {
                _adminNewEventSubStep = AdminNewEventSubStep.Layout;
                ClearAdminListFilter();
                NotifyAdminSection();
                return;
            }
        }

        if (Step == WizardStep.Admin && _adminSection == AdminSection.EditCurrentEvent)
        {
            if (_adminEventsSubStep == AdminEventsSubStep.Assign)
            {
                ResetAdminAssignUi();
                _adminEventsSubStep = AdminEventsSubStep.Session;
                NotifyAdminSection();
                return;
            }

            if (_adminEventsSubStep == AdminEventsSubStep.Session)
            {
                if (IsAdminPastRacesBrowse && !string.IsNullOrEmpty(_adminPastRacesRelative))
                {
                    _ = PopAdminPastRacesFolderAsync();
                    return;
                }

                AdminSessions.Clear();
                _adminSessionSource.Clear();
                _adminOffice = null;
                _adminPastRacesRelative = string.Empty;
                _adminListFilter = string.Empty;
                OnPropertyChanged(nameof(AdminListFilter));
                _adminEventsSubStep = AdminEventsSubStep.Office;
                ApplyAdminListFilter();
                NotifyAdminSection();
                return;
            }
        }

        Step = Step switch
        {
            WizardStep.Session => WizardStep.Office,
            WizardStep.EventFile => WizardStep.Session,
            WizardStep.Admin => HasActive ? WizardStep.Active : WizardStep.Office,
            _ => Step
        };
    }

    /// <summary>
    /// Leave Admin in one click (office list, or Active when a checkout is open).
    /// Does not walk Setup / Edit wizard steps — use Back for that.
    /// </summary>
    public void ExitAdmin()
    {
        if (Step != WizardStep.Admin)
            return;

        Step = HasActive ? WizardStep.Active : WizardStep.Office;
    }

    /// <summary>
    /// Office pick: live offices Graph-index session leaves once; Past Races lists
    /// <b>one folder level</b> only (cellular).
    /// Switches to the event list immediately so a second double-click cannot land on an event
    /// that appears under the cursor after a Graph / lock delay.
    /// </summary>
    public async Task SelectOfficeAsync(string? office)
    {
        if (HasActive || Busy || string.IsNullOrWhiteSpace(office))
            return;

        if (!EnsureGraphSignedInToBegin("choose an office"))
            return;

        SelectedOffice = office;
        _pastRacesRelative = string.Empty;
        ClearRaceDayListFilter();
        // Clear before Step change so the session list is empty while we load — not stale rows.
        Sessions.Clear();
        ApplyRaceDayListFilter();
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowEmptySessions));
        OnPropertyChanged(nameof(EmptySessionsTitle));
        OnPropertyChanged(nameof(EmptySessionsBody));
        OnPropertyChanged(nameof(IsPastRacesBrowse));
        OnPropertyChanged(nameof(PastRacesBreadcrumb));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepSubtitle));

        // Leave the office list before any await — paint first so lag cannot look like "nothing happened."
        Step = WizardStep.Session;
        UpdateSessionLockPolling();
        SetBusy($"Loading events for {office}…");
        await YieldUiAsync().ConfigureAwait(true);

        try
        {
            if (IsPastRacesBrowse)
            {
                // force:false — reuse this-run Graph cache (shared with Admin Past Races).
                await RefreshPastRacesLevelAsync(force: false).ConfigureAwait(true);
                await PreparePastRacesListAsync().ConfigureAwait(true);
            }
            else
            {
                await EnsureOfficeSessionsFromGraphIfNeededAsync(office, manageBusy: false)
                    .ConfigureAwait(true);
                await LoadLiveOfficeSessionsAsync(office).ConfigureAwait(true);
            }
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
            UpdateSessionLockPolling();
        }
    }

    public void SelectSession(EventPackInfo? session) =>
        _ = SelectSessionAsync(session);

    /// <summary>
    /// When Graph is signed in: download the full session pack first, then resolve .event / checkout.
    /// </summary>
    public async Task SelectSessionAsync(EventPackInfo? session)
    {
        // Busy covers office→events load — ignore laggy double-clicks that land on a row.
        if (HasActive || Busy || session is null)
            return;

        if (!EnsureGraphSignedInToBegin("load a race"))
            return;

        if (session.IsNavigationFolder)
        {
            await EnterPastRacesFolderAsync(session).ConfigureAwait(true);
            return;
        }

        if (IsAnnouncerRole)
        {
            await SelectAnnouncerSessionAsync(session).ConfigureAwait(true);
            return;
        }

        if (IsManualBackupRole)
        {
            await StartManualBackupSessionAsync(session).ConfigureAwait(true);
            return;
        }

        // Always refresh Graph lock + Force/soft Take Over BEFORE the full pack download.
        // Do not gate on session.Lock from the list — Laptop 2 often has no local _checkout.json
        // yet, so the row looks Available and we used to burn a pack download first.
        ClearPendingCheckoutTakeoverConfirm();
        if (!await ConfirmTakeoverBeforeSessionDownloadAsync(session).ConfigureAwait(true))
            return;

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

        _finishAdminReturn = FinishAdminReturn.None;

        var assigned = SessionRoleStore.TryLoad(SelectedSession.Path)?.PrimaryEventFile;
        if (!string.IsNullOrWhiteSpace(assigned))
        {
            SelectedEventFile = assigned;
            await CheckoutAsync().ConfigureAwait(true);
            return;
        }

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
            Alert(
                "Choose a file",
                IsAnnouncerRole
                    ? "Select the T&S .event file for this Announcer T&S laptop."
                    : "Select the timing file for this race.");
            return;
        }

        if (IsAnnouncerRole)
        {
            _finishAdminReturn = FinishAdminReturn.None;
            _ = DownloadAndCheckoutAnnouncerAsync(SelectedEventFile!);
            return;
        }

        _finishAdminReturn = FinishAdminReturn.None;
        _ = CheckoutAsync();
    }

    /// <summary>
    /// Refresh <c>_checkout.json</c> only, then Force / soft Take Over before the session pack download.
    /// </summary>
    private async Task<bool> ConfirmTakeoverBeforeSessionDownloadAsync(EventPackInfo session)
    {
        SetBusy("Checking event", "Seeing who has this race…");
        try
        {
            if (_cloud.IsAvailable)
            {
                try
                {
                    using var cts = new CancellationTokenSource(GraphSyncPolicy.HttpTimeout);
                    await _cloud.DownloadCheckoutLockAsync(
                        session.Path,
                        _settings.LibraryRoot,
                        _settings.GraphDriveRoot,
                        cts.Token).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    RaceDayTrace.Write(
                        $"pre-checkout lock refresh failed path={session.Path} error={ex.GetType().Name}: {ex.Message}");
                }
            }

            var eval = _locks.EvaluateAcquire(session.Path, _settings);
            if (eval.Outcome is not (LockAcquireOutcome.BlockedAlive or LockAcquireOutcome.NeedsEmergencyConfirm))
                return true;

            ClearBusy();
            if (eval.Outcome == LockAcquireOutcome.BlockedAlive)
            {
                if (!TryConfirmForceTakeover(eval, out var reason))
                    return false;
                _checkoutConfirmForce = true;
                _checkoutReclaimReason = reason;
                _checkoutConfirmSessionPath = session.Path;
                return true;
            }

            if (!TryConfirmSoftTakeover(eval, out var softReason))
                return false;
            _checkoutConfirmEmergency = true;
            _checkoutReclaimReason = softReason;
            _checkoutConfirmSessionPath = session.Path;
            return true;
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    private void ClearPendingCheckoutTakeoverConfirm()
    {
        _checkoutConfirmForce = false;
        _checkoutConfirmEmergency = false;
        _checkoutReclaimReason = null;
        _checkoutConfirmSessionPath = null;
    }

    private bool TryConfirmForceTakeover(LockAcquireResult eval, out string? reason)
    {
        reason = null;
        var who = eval.Lock is null
            ? "another laptop"
            : EventPackInfo.FormatLockWho(eval.Lock);
        var lastSeen = eval.Lock?.LastSeenAt.ToLocalTime().ToString("g") ?? "unknown";
        // Warning + Admin PIN stay in one branded overlay (no bounce to a separate Admin window).
        if (!TimerDialog.ConfirmForceTakeoverWithAdminPin(OwnerWindow, who, lastSeen))
            return false;

        reason = "Force take-over (Admin PIN) — other laptop still looked alive";
        return true;
    }

    private bool TryConfirmSoftTakeover(LockAcquireResult eval, out string? reason)
    {
        reason = null;
        var who = eval.Lock is null
            ? "another laptop"
            : EventPackInfo.FormatLockWho(eval.Lock);
        var lastSeen = eval.Lock?.LastSeenAt.ToLocalTime().ToString("g") ?? "unknown";
        if (!TimerDialog.Confirm(
                OwnerWindow,
                "Take Over Event?",
                $"This event was checked out by {who}.\n"
                + $"Last seen {lastSeen} — that laptop looks offline (crashed or shut down).\n\n"
                + "Take over on this laptop?\n\n"
                + "We'll load the race from OneDrive and apply any newer live autosave files from the other machine.\n\n"
                + "Close RunScore on this laptop first so Entries and other live files can be written.",
                yesLabel: "Take Over",
                noLabel: "Cancel"))
            return false;

        reason = "Emergency reclaim — other laptop unavailable";
        return true;
    }

    public async Task CheckoutAsync()
    {
        if (SelectedSession is null)
            return;

        if (!EnsureGraphSignedInToBegin("load a race"))
            return;

        if (IsAnnouncerRole)
        {
            await CheckoutAnnouncerFromUiAsync().ConfigureAwait(true);
            return;
        }

        // SelectedEventFile may be empty when checking out a Template with RUNSCORE only.

        SyncDisplayNameFromMachine();
        SetBusy("Loading event", "Getting your race ready…");
        try
        {
            var eval = _locks.EvaluateAcquire(SelectedSession.Path, _settings);
            var samePendingSession = !string.IsNullOrWhiteSpace(_checkoutConfirmSessionPath)
                && string.Equals(
                    _checkoutConfirmSessionPath,
                    SelectedSession.Path,
                    StringComparison.OrdinalIgnoreCase);
            var confirmForce = samePendingSession && _checkoutConfirmForce;
            var confirmEmergency = samePendingSession && _checkoutConfirmEmergency;
            var reason = samePendingSession ? _checkoutReclaimReason : null;
            ClearPendingCheckoutTakeoverConfirm();

            // Force confirm also satisfies a later soft/stale outcome after the pack download.
            if (confirmForce && eval.Outcome == LockAcquireOutcome.NeedsEmergencyConfirm)
            {
                confirmEmergency = true;
                reason ??= "Force take-over (Admin PIN) — other laptop still looked alive";
            }

            if (eval.Outcome == LockAcquireOutcome.BlockedAlive && !confirmForce)
            {
                ClearBusy();
                if (!TryConfirmForceTakeover(eval, out reason))
                    return;
                confirmForce = true;
                SetBusy("Loading event", "Force take-over — recovering race…");
            }

            if (eval.Outcome == LockAcquireOutcome.NeedsEmergencyConfirm && !confirmEmergency)
            {
                ClearBusy();
                if (!TryConfirmSoftTakeover(eval, out reason))
                    return;
                confirmEmergency = true;
                SetBusy("Loading event", "Recovering race…");
            }

            // Live _autosave overlay cannot overwrite Entries.DTA while RSServer holds it.
            var needsLiveOverlay = confirmForce || confirmEmergency
                || eval.Outcome == LockAcquireOutcome.SameMachineReclaim;
            if (needsLiveOverlay
                && !await EnsureVendorsClosedForLiveOverlayAsync(
                        confirmForce ? "Force Take Over" : "Recovering Race")
                    .ConfigureAwait(true))
                return;

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
                    string.IsNullOrWhiteSpace(result.Message)
                        ? "We couldn't load this event. Try again or ask setup staff for help."
                        : result.Message);
                return;
            }

            Active = result.Active;
            Step = WizardStep.Active;
            Status = string.Empty;
            KickAutosaveMirror();

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

        // Ignore double-taps while the busy overlay is already up (except app-close Finish).
        if (Busy && !closingApp)
            return;

        if (Active.Role == CheckoutRole.Announcer)
        {
            await DoneAnnouncerAsync().ConfigureAwait(true);
            return;
        }

        // Primary confirm may open a modal — do it before busy so the overlay does not flash off/on.
        // Missing .event does not hard-block Finish.
        if (!await ConfirmPrimaryEventBeforeFinishAsync().ConfigureAwait(true))
            return;

        // One continuous busy overlay for lock check → wait for RS → upload (no ClearBusy mid-flight).
        SetBusy(
            closingApp ? "Closing — saving your race" : "Saving your race",
            "Confirming this laptop still holds the race…");
        await YieldUiAsync().ConfigureAwait(true);

        // Never Finish-upload over another laptop. A failed OneDrive check is not a takeover.
        switch (await CheckLockOwnershipAsync(caller: "finish").ConfigureAwait(true))
        {
            case LockCloudVerdict.TakenOver:
                RaceDayTrace.Write("lock takeover prompt from finish");
                ClearBusy();
                await HandleLockTakenOverAsync().ConfigureAwait(true);
                return;
            case LockCloudVerdict.Unconfirmed:
                await ShowBusyResultAsync(
                    "Couldn't Confirm Checkout",
                    "OneDrive didn't confirm this laptop still holds the race. Nothing was uploaded. Try Finish again.");
                return;
        }

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

        var pausedHeartbeat = false;
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
                if (!await OfferEndOrphanedRunScoreAfterWaitAsync(wait.ProcessNames, purpose: "Finish")
                        .ConfigureAwait(true))
                    return;

                // Confirm clear after optional End RSServer, then continue Finish.
                wait = await _processGuard.WaitForBlockingClearedAsync(
                    TimeSpan.FromSeconds(8),
                    new Progress<string>(s => SetFinishProgress("1 · Waiting for RunScore", s)));
                if (wait.Kind != BlockingWaitKind.Cleared)
                {
                    var names = string.Join(", ", wait.ProcessNames);
                    await ShowBusyResultAsync(
                        "Still open",
                        "A timing process is still running"
                        + $" ({names}).\n\nClose it (or End RSServer if offered), then tap Finish again.");
                    return;
                }
            }

            SetFinishProgress("2 · Saving your race", "Confirming the checkout lock…");
            // Drain any in-flight lock heartbeat before Check-in releases the lock.
            // A late Active PUT was landing after the Released upload and leaving
            // the other laptop showing Checked Out.
            pausedHeartbeat = true;
            await PauseHeartbeatForFinishAsync().ConfigureAwait(true);
            // The RunScore wait can outlast the check at the start of Finish.
            // Re-read the lock once heartbeats are quiet, before we release it.
            switch (await CheckLockOwnershipAsync(caller: "finish-release").ConfigureAwait(true))
            {
                case LockCloudVerdict.TakenOver:
                    RaceDayTrace.Write("lock takeover prompt from finish-release");
                    ResumeHeartbeatAfterFinish();
                    pausedHeartbeat = false;
                    ClearBusy();
                    await HandleLockTakenOverAsync().ConfigureAwait(true);
                    return;
                case LockCloudVerdict.Unconfirmed:
                    await ShowBusyResultAsync(
                        "Couldn't Confirm Checkout",
                        "OneDrive didn't confirm this laptop still holds the race. Nothing was uploaded. Try Finish again.");
                    return;
            }

            SetFinishProgress("2 · Saving your race", "Starting local save, then OneDrive upload…");
            var copyProgress = new Progress<CopyProgress>(p =>
                SetFinishProgress(
                    "2 · Saving your race",
                    p.FilesCopied > 0
                        ? $"Saving race files to the local library… ({p.FilesCopied} files)"
                        : "Saving race files to the local library…"));
            var statusProgress = new Progress<string>(ApplyFinishStatus);

            var result = await _checkout.CheckinAsync(
                _settings,
                DisplayName.Trim(),
                removeActive: true,
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
                    string.IsNullOrWhiteSpace(result.Message)
                        ? "We couldn't finish saving. Try again or ask setup staff for help."
                        : result.Message);
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
            ClearActiveSaveAsGuidePin();
            LoadOffices();

            var adminReturn = _finishAdminReturn;
            _finishAdminReturn = FinishAdminReturn.None;

            var doneBody = "Your race is saved.";
            if (adminReturn == FinishAdminReturn.None)
                doneBody += "\n\nSafe to shut down this laptop.";
            else if (adminReturn == FinishAdminReturn.EditCurrentEvent)
                doneBody += "\n\nNext: Edit Current Event for this race — refresh files or save main timing if you added a .event.";
            else if (adminReturn == FinishAdminReturn.SetupNewEvent)
                doneBody += "\n\nNext: Setup New Event.";
            if (!result.AutosaveCleared)
            {
                doneBody +=
                    "\n\nWarning: some live autosave files (RUNSCORE\\_autosave) could not be removed. "
                    + "Ask setup to delete that folder before the next race so post-event edits are not overwritten.";
            }

            // Same branded overlay — OK to dismiss. Navigate after OK so Edit Current load doesn't flash Busy.
            await ShowBusyResultAsync("Done", doneBody);

            if (adminReturn == FinishAdminReturn.EditCurrentEvent)
            {
                await ReturnToEditCurrentEventAfterFinishAsync().ConfigureAwait(true);
            }
            else if (adminReturn == FinishAdminReturn.SetupNewEvent)
            {
                EnterAdminSetup("Finish complete — ready for the next setup.");
            }
            else if (Offices.Count == 1)
            {
                // One office — open its list (cheap when already cached). Multi-office: stay on picker
                // so Finish does not re-index events over cellular.
                await SelectOfficeAsync(Offices[0]);
                Status = string.Empty;
            }
            else
            {
                Step = WizardStep.Office;
                Status = string.Empty;
            }
        }
        catch (Exception ex)
        {
            await ShowBusyResultAsync("Couldn't finish", "We couldn't finish saving.\n\n" + ex.Message);
        }
        finally
        {
            if (pausedHeartbeat)
                ResumeHeartbeatAfterFinish();
            _mirror.Resume();
            _mirrorTimer.Start();
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    public async Task<bool> ConfirmCloseAsync(Window? owner = null, bool sessionEnding = false)
    {
        if (_checkout.GetActive() is not { } active)
            return true;

        if (active.Role == CheckoutRole.Announcer)
        {
            var announcerChoice = TimerDialog.AskCloseWhileAnnouncerActive(owner, sessionEnding);
            return announcerChoice switch
            {
                TimerCloseChoice.KeepWorking => false,
                TimerCloseChoice.CloseWithoutSaving => true,
                TimerCloseChoice.DoneLocalOnly => await DoneAndAllowCloseAsync().ConfigureAwait(true),
                _ => false
            };
        }

        var vendorsStillOpen = _processGuard.GetBlockingProcessNames();
        var choice = TimerDialog.AskCloseWhileActive(owner, sessionEnding, vendorsStillOpen);
        return choice switch
        {
            TimerCloseChoice.KeepWorking => false,
            // Leave Active + lock on disk. Reopen resumes. Do not upload or release.
            TimerCloseChoice.CloseWithoutSaving => true,
            TimerCloseChoice.SaveAndClose => await SaveAndAllowCloseAsync(),
            _ => false
        };
    }

    private async Task<bool> SaveAndAllowCloseAsync()
    {
        await FinishAsync(closingApp: true);
        // FinishAsync already shows the branded "Done" overlay.
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

    private void SyncManualBackupAttachment()
    {
        if (Active is { Role: CheckoutRole.Timing or CheckoutRole.ManualBackup })
        {
            var leaf = string.IsNullOrWhiteSpace(Active.SessionName)
                ? Active.ParentEvent
                : Active.SessionName;
            _manualBackup.AttachSession(
                Active.ActivePath,
                _settings.LibraryRoot,
                Active.Office,
                Active.RelativePath,
                leaf ?? Active.RelativePath);
            SyncManualBackupLiveSoftPull();
        }
        else
        {
            StopManualBackupLiveSoftPull();
            _manualBackup.DetachSession();
            try { _manualBackupFloat?.Close(); } catch { /* ignore */ }
            _manualBackupFloat = null;
        }
    }

    /// <summary>
    /// Main Timing only: SoftPull MB tree every ~5s while Manual Backup is enabled (Play + idle)
    /// so phone / Backup Laptop Currents reach Active and file cards stay fresh.
    /// </summary>
    private void SyncManualBackupLiveSoftPull()
    {
        var timing = Active is { Role: CheckoutRole.Timing };
        if (!ManualBackupLiveSoftPullPolicy.ShouldRun(timing, _manualBackup.IsEnabled, _cloud.IsAvailable))
        {
            StopManualBackupLiveSoftPull();
            return;
        }

        if (_mbLiveSoftPullCts is { IsCancellationRequested: false })
            return;

        var cts = new CancellationTokenSource();
        _mbLiveSoftPullCts = cts;
        _ = RunManualBackupLiveSoftPullLoopAsync(cts.Token);
    }

    private void StopManualBackupLiveSoftPull()
    {
        _mbLiveSoftPullCts?.Cancel();
        _mbLiveSoftPullCts?.Dispose();
        _mbLiveSoftPullCts = null;
    }

    private async Task RunManualBackupLiveSoftPullLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(ManualBackupLiveSoftPullPolicy.Interval, token).ConfigureAwait(true);
                if (Active is not { Role: CheckoutRole.Timing } || !_manualBackup.IsEnabled)
                    break;
                await SoftPullManualBackupLiveOnceAsync(token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch { /* soft — loop ends; Sync may restart */ }
        finally
        {
            // Clear so SyncManualBackupLiveSoftPull can restart after unexpected exit.
            if (_mbLiveSoftPullCts is { } live && live.Token == token)
            {
                try { live.Dispose(); } catch { /* ignore */ }
                if (ReferenceEquals(_mbLiveSoftPullCts, live))
                    _mbLiveSoftPullCts = null;
            }
        }
    }

    /// <summary>
    /// Download-only SoftPull (no publish) — local taps keep debounced publish; avoid write storms.
    /// </summary>
    private async Task SoftPullManualBackupLiveOnceAsync(CancellationToken outerToken)
    {
        if (!_cloud.IsAvailable || Active is not { Role: CheckoutRole.Timing } active)
            return;
        if (!await _mbLiveSoftPullGate.WaitAsync(0, outerToken).ConfigureAwait(false))
            return; // single-flight — prior pull still running
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            linked.CancelAfter(ManualBackupLiveSoftPullPolicy.PullBudget);
            await _cloud.DownloadSessionAsync(
                active.LibrarySessionPath,
                _settings.LibraryRoot,
                _settings.GraphDriveRoot,
                status: null,
                linked.Token,
                SessionDownloadOptions.ForManualBackupOnly()).ConfigureAwait(false);

            if (Active is not { Role: CheckoutRole.Timing, LibrarySessionPath: var lib }
                || !string.Equals(lib, active.LibrarySessionPath, StringComparison.OrdinalIgnoreCase))
                return;

            ManualBackupStore.MergeTreePreferLongerJsonl(
                active.LibrarySessionPath,
                active.ActivePath);

            // Refresh file cards (idle + Play). Play TCP loop reads disk independently.
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (Active is { Role: CheckoutRole.Timing })
                    _manualBackup.ReloadFromDisk();
            });
        }
        catch (OperationCanceledException) { /* budget / stop */ }
        catch { /* soft — next tick */ }
        finally
        {
            _mbLiveSoftPullGate.Release();
        }
    }

    public void OpenManualBackupFloat()
    {
        if ((!IsTimingActive && !IsManualBackupActive) || !_manualBackup.ShowPanel)
        {
            Alert(
                "Manual Backup",
                "Manual Backup locations are set on Main Timing (Admin PIN) or during setup. Enable them there first.");
            return;
        }

        if (_manualBackupFloat is { IsVisible: true })
        {
            _manualBackupFloat.Activate();
            return;
        }

        // Unowned Topmost float — never Owner-link Main Timing (that raises it over RSServer).
        _manualBackupFloat = new ManualBackupFloatWindow();
        _manualBackupFloat.Bind(_manualBackup, SubmitManualBackupBib);
        _manualBackupFloat.Closed += (_, _) => _manualBackupFloat = null;
        _manualBackupFloat.Show();
        _manualBackupFloat.Activate();
    }

    /// <summary>
    /// Timing Active only (Admin PIN). Picks which RunScore Results locations are available
    /// for Manual Backup Laptop / staffing — those roles never enable locations themselves.
    /// </summary>
    public void ConfigureManualBackupWithPin()
    {
        if (Active is not { Role: CheckoutRole.Timing })
            return;
        if (!EnsureAdminUnlocked(
                "Manual Backup",
                "Enter the Admin PIN to change Manual Backup locations for this race."))
            return;

        // Only restore prior picks when Manual Backup is already on — never default-map Finish/etc.
        var current = _manualBackup.IsEnabled ? _manualBackup.LocationsText : "";
        // Same names as RunScore Master Menu → Results (Events.xml). Prefer Active; fall back to Library.
        IReadOnlyList<string> LoadRunScoreResults() =>
            Active is null
                ? Array.Empty<string>()
                : RunScoreEventsReader.ListEventNamesFromSession(
                    Active.ActivePath,
                    Active.LibrarySessionPath);

        var dlg = new ManualBackupConfigDialog(
            current,
            _manualBackup.IsEnabled,
            LoadRunScoreResults(),
            reloadSuggestions: LoadRunScoreResults)
        {
            Owner = OwnerWindow
        };
        if (dlg.ShowDialog() != true)
            return;
        _manualBackup.SaveConfig(dlg.Enabled, dlg.Locations);
        Status = _manualBackup.Status;
        SyncManualBackupLiveSoftPull();
        _ = PublishManualBackupTreeAsync();
    }

    public async Task PlayManualBackupAllAsync(string location)
    {
        // Play Active local immediately — do not stall race day on Graph pull/push.
        Status = "Playing Manual Backup…";
        var result = await _manualBackup.PlayAllAsync(location).ConfigureAwait(true);
        ApplyManualBackupPlayResult(result);
        _ = SyncManualBackupAfterPlayAsync();
    }

    public void PauseManualBackupAll(string location)
    {
        _manualBackup.PauseAll(location);
        Status = _manualBackup.Status;
    }

    public async Task PlayManualBackupSegmentAsync(
        string location, string deviceId, string? segment, bool replay)
    {
        Status = replay ? "Replaying Manual Backup…" : "Playing Manual Backup…";
        var result = await _manualBackup.PlaySegmentAsync(location, deviceId, segment, replay)
            .ConfigureAwait(true);
        ApplyManualBackupPlayResult(result);
        _ = SyncManualBackupAfterPlayAsync();
    }

    private void ApplyManualBackupPlayResult(ManualBackupPlayResult result)
    {
        if (ShouldAlertManualBackupPlay(result))
        {
            Alert("Manual Backup", result.Message);
            // Badge already shows Open connected/not — don't keep the long error in the header.
            _manualBackup.ClearStatus();
            Status = string.Empty;
            return;
        }

        Status = _manualBackup.Status;
    }

    private static bool ShouldAlertManualBackupPlay(ManualBackupPlayResult result)
    {
        if (result.Success)
            return false;
        // Pause / busy are expected UI states — status line is enough.
        if (result.Message.Contains("paused", StringComparison.OrdinalIgnoreCase))
            return false;
        if (result.Message.Contains("Already playing", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>
    /// After Play: publish local taps, then soft-pull once as catch-up.
    /// Live SoftPull loop already pulls during Play; this covers the Pause gap.
    /// </summary>
    private async Task SyncManualBackupAfterPlayAsync()
    {
        try
        {
            await PublishManualBackupTreeAsync().ConfigureAwait(true);
            // Prefer live pull path when Timing loop is running (download-only + merge).
            if (_mbLiveSoftPullCts is { IsCancellationRequested: false })
                await SoftPullManualBackupLiveOnceAsync(CancellationToken.None).ConfigureAwait(true);
            else
                await RefreshManualBackupFromCloudAsync(budget: TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        }
        catch { /* soft */ }
    }

    public void CreateManualBackupFilesAll(string location)
    {
        _manualBackup.CreateNewFileAll(location);
        _ = PublishManualBackupTreeAsync();
    }

    public void DeleteManualBackupArchive(string location, string deviceId, string fileName)
    {
        if (Active is null || string.IsNullOrWhiteSpace(fileName))
            return;

        // Capture Library path before delete so Graph can remove the orphan.
        var store = new ManualBackupStore();
        var libFile = Path.Combine(
            store.GetDeviceDir(Active.LibrarySessionPath, location, deviceId),
            Path.GetFileName(fileName));

        if (!_manualBackup.TryDeleteArchive(location, deviceId, fileName))
            return;

        _ = DeleteManualBackupArchiveOnGraphAsync(libFile);
    }

    private async Task DeleteManualBackupArchiveOnGraphAsync(string libraryFilePath)
    {
        if (!_cloud.IsAvailable || string.IsNullOrWhiteSpace(libraryFilePath))
            return;
        try
        {
            await _cloud.DeleteLibraryFileAsync(
                libraryFilePath,
                _settings.LibraryRoot,
                _settings.GraphDriveRoot,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* soft — local delete already done */ }
    }

    public ManualBackupTap? SubmitManualBackupBib(string location, string bib)
    {
        var tap = _manualBackup.SubmitBib(location, bib);
        if (tap is not null)
            SchedulePublishManualBackupTree();
        return tap;
    }

    /// <summary>
    /// Debounce Graph publish after bib entry — rapid Submit was overlapping uploads and
    /// locking <c>current.jsonl</c> against the next append.
    /// </summary>
    private void SchedulePublishManualBackupTree()
    {
        _mbPublishAgain = true;
        _mbPublishDebounceCts?.Cancel();
        _mbPublishDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _mbPublishDebounceCts = cts;
        _ = DebouncedPublishManualBackupTreeAsync(cts.Token);
    }

    private async Task DebouncedPublishManualBackupTreeAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(450, token).ConfigureAwait(false);
            await PublishManualBackupTreeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* newer submit rescheduled */ }
    }

    private async Task PublishManualBackupCatalogAsync()
    {
        if (!_cloud.IsAvailable || Active is null)
            return;
        try
        {
            var catalogPath = ManualBackupStore.GetOfficeCatalogPath(_settings.LibraryRoot, Active.Office);
            if (!File.Exists(catalogPath))
                return;
            await _cloud.UploadLocalFilesAsync(
                new[] { catalogPath },
                _settings.LibraryRoot,
                _settings.GraphDriveRoot,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* soft — catalog retries on next save */ }
    }

    private async Task PublishManualBackupTreeAsync(CancellationToken cancellationToken = default)
    {
        if (!_cloud.IsAvailable || Active is null)
            return;
        await _mbPublishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Loop once more if taps landed while this upload ran.
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                _mbPublishAgain = false;
                var libRoot = ManualBackupStore.GetRoot(Active.LibrarySessionPath);
                if (!Directory.Exists(libRoot))
                    return;
                // MB-only tree (._checkin/manual_backup) — never RUNSCORE / .event / DB's.
                await _cloud.UploadFolderAsync(
                    libRoot,
                    _settings.LibraryRoot,
                    _settings.GraphDriveRoot,
                    status: null,
                    cancellationToken).ConfigureAwait(false);
                await PublishManualBackupCatalogAsync().ConfigureAwait(false);
            } while (_mbPublishAgain);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* soft */ }
        finally
        {
            _mbPublishGate.Release();
        }
    }

    private async Task RefreshManualBackupFromCloudAsync(TimeSpan? budget = null)
    {
        if (!_cloud.IsAvailable || Active is null)
            return;
        try
        {
            // Publish local laptop taps first so Graph merge does not clobber them.
            await PublishManualBackupTreeAsync().ConfigureAwait(true);

            using var cts = new CancellationTokenSource(budget ?? TimeSpan.FromSeconds(45));
            await _cloud.DownloadSessionAsync(
                Active.LibrarySessionPath,
                _settings.LibraryRoot,
                _settings.GraphDriveRoot,
                status: null,
                cts.Token,
                SessionDownloadOptions.ForManualBackupOnly()).ConfigureAwait(true);

            // Soft-merge Library → Active: phone Past / Current in, never shrink jsonl or reset PlayCursor.
            ManualBackupStore.MergeTreePreferLongerJsonl(
                Active.LibrarySessionPath,
                Active.ActivePath);

            _manualBackup.ReloadFromDisk();
        }
        catch { /* soft — keep local */ }
    }

    public async Task StartManualBackupSessionAsync(EventPackInfo session)
    {
        if (HasActive)
        {
            Alert("Manual Backup", "Finish or Done the current session first.");
            return;
        }

        var store = new ManualBackupStore();
        var manifest = store.LoadManifest(session.Path);

        // Fast path: locations already on disk — open Active immediately; soft-pull taps in background.
        if (manifest.Enabled && manifest.Locations.Count > 0)
        {
            EnterManualBackupActive(session);
            _ = SoftPullManualBackupMetaAsync(session.Path);
            return;
        }

        // Need Graph for manifest — busy overlay so double-click does not look dead on cellular.
        if (_cloud.IsAvailable)
        {
            SetBusy("Opening Manual Backup", "Checking locations for this race…");
            await YieldUiAsync().ConfigureAwait(true);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await _cloud.DownloadSessionAsync(
                    session.Path,
                    _settings.LibraryRoot,
                    _settings.GraphDriveRoot,
                    status: new Progress<string>(s =>
                    {
                        if (!string.IsNullOrWhiteSpace(s))
                            ProgressText = s;
                    }),
                    cancellationToken: cts.Token,
                    options: SessionDownloadOptions.ForManualBackupOnly()).ConfigureAwait(true);
            }
            catch { /* soft — may still be local after partial */ }
            finally
            {
                ClearBusy();
            }

            manifest = store.LoadManifest(session.Path);
        }

        if (!manifest.Enabled || manifest.Locations.Count == 0)
        {
            Alert(
                "Manual Backup",
                "No Manual Backup locations are enabled for this race yet.\n\n"
                + "On Main Timing, use Enable Manual Backup… / Settings (Admin PIN) and check the RunScore Results locations. "
                + "Staffing Manual Backup will use that same list.");
            return;
        }

        EnterManualBackupActive(session);
    }

    private void EnterManualBackupActive(EventPackInfo session)
    {
        var active = new ActiveCheckout
        {
            Office = session.Office,
            ParentEvent = session.ParentEvent,
            SessionName = session.SessionName,
            RelativePath = session.RelativePath,
            LibrarySessionPath = session.Path,
            ActivePath = session.Path, // write straight to Library / Graph pack
            BackupPath = session.Path,
            AutosavePath = Path.Combine(session.Path, PathService.RunScoreFolderName, PathService.AutosaveFolderName),
            DbsPath = session.DbsPath,
            PrimaryEventFile = session.PrimaryEventFile ?? "",
            Role = CheckoutRole.ManualBackup,
            ScopedPaths = Array.Empty<string>(),
            CheckedOutAt = DateTimeOffset.UtcNow
        };
        _activeStore.Save(active);
        Active = active;
        Step = WizardStep.Active;
        Status = "Manual Backup ready — enter bibs in the Entry window.";
        // Same Topmost Entry popup as Main Timing (not an in-page form).
        OpenManualBackupFloat();
    }

    /// <summary>
    /// Background MB-only Graph pull after Active is already open (other devices' Past / Current).
    /// Download gate keeps longer local current.jsonl. Canceled on Done so SoftPull cannot
    /// re-inflate Current after seal.
    /// </summary>
    private async Task SoftPullManualBackupMetaAsync(string librarySessionPath)
    {
        if (!_cloud.IsAvailable)
            return;

        _mbSoftPullCts?.Cancel();
        _mbSoftPullCts?.Dispose();
        var softCts = new CancellationTokenSource();
        _mbSoftPullCts = softCts;
        var token = softCts.Token;

        try
        {
            // Brief delay so first bibs land before SoftPull lists Graph.
            await Task.Delay(1500, token).ConfigureAwait(true);
            if (Active is not { Role: CheckoutRole.ManualBackup, LibrarySessionPath: var path }
                || !string.Equals(path, librarySessionPath, StringComparison.OrdinalIgnoreCase))
                return;

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(25));
            await _cloud.DownloadSessionAsync(
                librarySessionPath,
                _settings.LibraryRoot,
                _settings.GraphDriveRoot,
                status: null,
                cancellationToken: budget.Token,
                options: SessionDownloadOptions.ForManualBackupOnly()).ConfigureAwait(true);
            if (Active is { Role: CheckoutRole.ManualBackup, LibrarySessionPath: var still }
                && string.Equals(still, librarySessionPath, StringComparison.OrdinalIgnoreCase))
                _manualBackup.ReloadFromDisk();
        }
        catch (OperationCanceledException) { /* Done / newer SoftPull */ }
        catch { /* soft */ }
    }

    public async Task DoneManualBackupAsync()
    {
        if (Active is not { Role: CheckoutRole.ManualBackup } mb)
            return;
        var sessionPath = mb.LibrarySessionPath;

        // Stop SoftPull before seal — otherwise a late pull can restore Current after we archive it.
        _mbSoftPullCts?.Cancel();
        _mbSoftPullCts?.Dispose();
        _mbSoftPullCts = null;
        _mbPublishDebounceCts?.Cancel();
        _mbPublishDebounceCts?.Dispose();
        _mbPublishDebounceCts = null;

        // Seal this laptop's Current → Past so Timing can Play without needing a Timing checkout rotate.
        try
        {
            var sealedCount = new ManualBackupStore().CreateNewFileForDevice(
                sessionPath,
                _manualBackup.DeviceId);
            if (sealedCount > 0)
                _manualBackup.ReloadFromDisk();
        }
        catch { /* soft */ }

        // Flush Graph publish (MB tree only) — cellular-bounded; local Past already sealed.
        var uploadTimedOut = false;
        try
        {
            _mbPublishAgain = true;
            SetBusy("Saving Manual Backup", "Uploading taps to OneDrive…");
            await YieldUiAsync().ConfigureAwait(true);
            using var uploadBudget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await PublishManualBackupTreeAsync(uploadBudget.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            uploadTimedOut = true;
        }
        catch { /* soft — local Past still sealed */ }
        finally
        {
            ClearBusy();
        }

        _activeStore.Save(null);
        Active = null;
        Step = WizardStep.Office;
        Status = uploadTimedOut
            ? "Manual Backup saved on this laptop — OneDrive upload timed out; Timing can retry pull later."
            : "Manual Backup closed — taps saved for Timing Play (Past).";
        // Only undo MB heartbeat reclaim — never release a real Timing checkout left on this PC.
        _ = ReleaseAccidentalManualBackupLockAsync(sessionPath);
    }

    /// <summary>
    /// Manual Backup never owns Timing checkout. Only release locks this machine reclaimed
    /// via the old MB heartbeat bug — leave a legitimate Timing Active lock alone.
    /// </summary>
    private async Task ReleaseAccidentalManualBackupLockAsync(string sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath) || !_locks.ThisMachineHoldsActiveLock(sessionPath))
            return;
        var existing = _locks.TryRead(sessionPath);
        if (existing is null
            || !string.Equals(
                existing.ReclaimReason,
                "orphan-active-after-released-lock",
                StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            var holder = string.IsNullOrWhiteSpace(DisplayName) ? _machine.MachineName : DisplayName.Trim();
            _locks.Release(sessionPath, holder, reason: "manual-backup-done-not-timing-lock");
            if (_cloud.IsAvailable)
            {
                await _cloud.UploadCheckoutLockAsync(
                    sessionPath,
                    _settings.LibraryRoot,
                    _settings.GraphDriveRoot,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { /* soft */ }
    }


    /// <summary>
    /// Before Finish: warn if no .event (Continue Edit vs Finish); if files exist and primary
    /// unset, offer to mark main timing. Missing .event does not hard-block Finish.
    /// </summary>
    private async Task<bool> ConfirmPrimaryEventBeforeFinishAsync()
    {
        if (Active is null)
            return false;

        var files = EventFileSelector.ListEventFiles(Active.ActivePath);
        var assignedOnDisk = !string.IsNullOrWhiteSpace(Active.PrimaryEventFile)
            && File.Exists(Path.Combine(Active.ActivePath, Active.PrimaryEventFile));
        var sidecar = SessionRoleStore.TryLoad(Active.LibrarySessionPath)?.PrimaryEventFile;
        var sidecarOnDisk = !string.IsNullOrWhiteSpace(sidecar)
            && File.Exists(Path.Combine(Active.ActivePath, sidecar!));

        // Normal race day — primary already known.
        if (assignedOnDisk || sidecarOnDisk)
        {
            var primary = assignedOnDisk ? Active.PrimaryEventFile! : sidecar!;
            if (!assignedOnDisk)
                ApplyActivePrimaryEvent(primary);
            await PersistPrimaryAssignmentAsync(primary).ConfigureAwait(true);
            return true;
        }

        // No .event yet — show guidance; staff may Finish anyway or stay and Save As.
        if (files.Count == 0)
            return TimerDialog.ConfirmFinishWithoutTimingFile(OwnerWindow, Active.ActivePath);

        var suggested = !string.IsNullOrWhiteSpace(Active.PrimaryEventFile)
            ? Active.PrimaryEventFile
            : EventFileSelector.ChoosePrimary(files);
        var picked = TimerDialog.PickPrimaryEventFile(
            OwnerWindow,
            Active.ActivePath,
            files,
            suggested);
        // Cancel skips assignment but still allows Finish.
        if (!string.IsNullOrWhiteSpace(picked))
        {
            ApplyActivePrimaryEvent(picked);
            await PersistPrimaryAssignmentAsync(picked).ConfigureAwait(true);
            NotifyActiveSaveAsGuide();
        }

        return true;
    }

    private async Task PersistPrimaryAssignmentAsync(string primaryEventFile)
    {
        EnsureSessionPrimarySaved(primaryEventFile);
        if (Active is null || !_cloud.IsAvailable)
            return;

        try
        {
            await _cloud.UploadSessionRolesAsync(
                Active.LibrarySessionPath,
                _settings.LibraryRoot,
                _settings.GraphDriveRoot).ConfigureAwait(true);
        }
        catch
        {
            // Local assignment is enough for this Finish; Admin can re-save later.
        }
    }

    private void ApplyActivePrimaryEvent(string primaryEventFile)
    {
        if (Active is null)
            return;

        var name = Path.GetFileName(primaryEventFile.Trim());
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
            PrimaryEventFile = name,
            Role = Active.Role,
            ScopedPaths = Active.ScopedPaths.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? Active.ScopedPaths
                : Active.ScopedPaths.Append(name).ToList(),
            CheckedOutAt = Active.CheckedOutAt,
            PendingFinishUploadRelativePaths = Active.PendingFinishUploadRelativePaths,
            RaceStarts = Active.RaceStarts
        };
        _activeStore.Save(Active);
    }

    private void EnsureSessionPrimarySaved(string primaryEventFile)
    {
        if (Active is null || string.IsNullOrWhiteSpace(Active.LibrarySessionPath))
            return;

        var name = Path.GetFileName(primaryEventFile.Trim());
        try
        {
            SessionRoleStore.Save(
                Active.LibrarySessionPath,
                new SessionRoles { PrimaryEventFile = name });
        }
        catch (Exception ex)
        {
            Status = "Could not save main timing assignment: " + ex.Message;
        }
    }

    public void LaunchTimingAndScoring()
    {
        if (Active is null) return;

        // Timing may pick up a Save As. Announcer stays on the assigned file.
        if (Active.Role != CheckoutRole.Announcer)
        {
            var discovered = VendorLauncher.ResolvePrimaryEventFile(Active);
            if (!string.IsNullOrWhiteSpace(discovered)
                && !string.Equals(Active.PrimaryEventFile, discovered, StringComparison.OrdinalIgnoreCase))
            {
                ApplyActivePrimaryEvent(discovered);
            }
        }

        var result = _launcher.LaunchTimingAndScoring(Active, _settings);
        if (!result.Success)
        {
            Alert("Timing & Scoring", result.Message);
            return;
        }

        // Folder map stays under Finish — don't cover the T&S splash with another dialog.
        NotifyActiveSaveAsGuide();
        Status = ShowActiveSaveAsGuide
            ? "Timing & Scoring opening… Save As path is below Finish."
            : "Timing & Scoring opening…";
    }

    /// <summary>Race-day / Active: open empty T&amp;S for Save As (announcer or first .event).</summary>
    public void LaunchEmptyTimingAndScoring()
    {
        if (Active is null) return;

        var result = _launcher.LaunchTimingAndScoring(Active, _settings, bare: true);
        if (!result.Success)
        {
            Alert("Timing & Scoring", result.Message);
            return;
        }

        PinActiveSaveAsGuide();
        Status = "Create a New Timing & Scoring opening… Save As path is on this page (above Finish).";
    }

    public void OpenWorkingFolder()
    {
        if (Active is null || !Directory.Exists(Active.ActivePath))
            return;
        Process.Start(new ProcessStartInfo { FileName = Active.ActivePath, UseShellExecute = true });
        NotifyActiveSaveAsGuide();
    }

    public void TryOpenAdmin()
    {
        if (!EnsureAdminUnlocked("Admin", "Enter the Admin PIN to open settings."))
            return;

        EnterAdminSetup();
    }

    public void SaveAdminSettings()
    {
        _settingsStore.Save(_settings);
        try
        {
            var pin = new RunScorePreferencesService().EnsureVendorCredentials(
                overrides: VendorCredentialOverrides.From(_settings));
            Status = pin.UpdatedCount > 0
                ? "Settings saved. " + pin.Message
                : "Settings saved.";
        }
        catch
        {
            Status = "Settings saved.";
        }

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
            await SyncVendorSecretsFromGraphAsync().ConfigureAwait(true);
            await RefreshLibraryFromGraphAsync();
            // After sign-in, allow single-office auto-advance into the session list.
            if (!HasActive)
                ReloadFromDisk();
            UpdateSessionLockPolling();
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
            UpdateSessionLockPolling();
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
            // Race day cannot continue without Graph — back to office + sign-in banner.
            if (!HasActive && Step is WizardStep.Session or WizardStep.EventFile)
            {
                _pastRacesRelative = string.Empty;
                SelectedSession = null;
                SelectedEventFile = null;
                Step = WizardStep.Office;
            }
            UpdateSessionLockPolling();
        }
        catch (Exception)
        {
            Alert("OneDrive sign-out", "Could not sign out. Try again, or restart the app.");
        }
    }

    /// <summary>
    /// Pull <c>._s2f/runscore-vendor-secrets.json</c> into AppData cache, refresh Admin license
    /// fields when present, and pin vendor logins to HKCU.
    /// Soft-fail; never blocks checkout. Tiny known-path GET — always refresh when signed in so
    /// rotated OneDrive credentials are not stuck behind a stale AppData cache.
    /// </summary>
    private async Task SyncVendorSecretsFromGraphAsync()
    {
        if (!_cloud.IsAvailable)
            return;

        try
        {
            var result = await _cloud.SyncRunScoreVendorSecretsAsync(_settings.GraphDriveRoot)
                .ConfigureAwait(true);
            if (!result.Success)
                return;

            NotifyRunScoreLicenseFields();

            // Pin HKCU as soon as the cache is fresh — do not wait for checkout / Open RunScore.
            // Admin RaceRoster / RunSignUp overrides win over the OneDrive file.
            // RunScore still must be restarted to reload prefs if it is already open.
            try
            {
                var pin = new RunScorePreferencesService().EnsureVendorCredentials(
                    overrides: VendorCredentialOverrides.From(_settings));
                if (pin.UpdatedCount > 0 && !string.IsNullOrWhiteSpace(pin.Message))
                    Status = pin.Message;
                else if (!string.IsNullOrWhiteSpace(result.Message))
                    Status = result.Message;
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(result.Message))
                    Status = result.Message;
            }
        }
        catch
        {
            // Non-fatal — race day uses existing cache / leaves registry alone.
        }
    }

    /// <summary>Refresh Admin Settings license display from the vendor-secrets AppData cache.</summary>
    private void NotifyRunScoreLicenseFields()
    {
        OnPropertyChanged(nameof(RunScoreLicenseName));
        OnPropertyChanged(nameof(RunScoreLicenseKey));
    }

    /// <summary>
    /// Already signed in (MSAL cache): refresh vendor secrets from OneDrive (tiny GET).
    /// Soft — does not block landing / race day. Call from window load.
    /// </summary>
    public Task EnsureVendorSecretsIfSignedInAsync() =>
        SyncVendorSecretsFromGraphAsync();

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

            // Intentional Graph moment — also pick up rotated OneDrive vendor secrets.
            await SyncVendorSecretsFromGraphAsync().ConfigureAwait(true);

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
    /// <param name="manageBusy">
    /// False when the caller already owns the busy overlay (office→events transition).
    /// </param>
    private async Task EnsureOfficeSessionsFromGraphIfNeededAsync(string office, bool manageBusy = true)
    {
        if (!_cloud.IsAvailable || HasActive)
            return;
        if (_graphIndexedOffices.Contains(office))
            return;

        try
        {
            if (manageBusy)
            {
                Busy = true;
                BusyTitle = $"Loading events for {office}…";
            }
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
            if (manageBusy)
                Busy = false;
        }
    }

    /// <summary>
    /// Shared Timing / Admin Past Races Graph cache key for one relative path under Past Races.
    /// </summary>
    private static string PastRacesCacheKey(string relativeUnderPastRaces) =>
        EventBuilderService.PastRacesFolderName + ":" + relativeUnderPastRaces.Replace('\\', '/');

    private string PastRacesIndexKey => PastRacesCacheKey(_pastRacesRelative);

    private async Task EnterPastRacesFolderAsync(EventPackInfo folder)
    {
        if (HasActive || folder is null || !folder.IsNavigationFolder)
            return;

        _pastRacesRelative = string.IsNullOrEmpty(_pastRacesRelative)
            ? folder.SessionName
            : _pastRacesRelative.TrimEnd('/', '\\') + "/" + folder.SessionName;
        ClearRaceDayListFilter();
        OnPropertyChanged(nameof(PastRacesBreadcrumb));
        OnPropertyChanged(nameof(StepSubtitle));

        await RefreshPastRacesLevelAsync(force: false).ConfigureAwait(true);
        await PreparePastRacesListAsync().ConfigureAwait(true);
    }

    private async Task PopPastRacesFolderAsync()
    {
        var rel = _pastRacesRelative.Replace('\\', '/').Trim('/');
        var slash = rel.LastIndexOf('/');
        _pastRacesRelative = slash < 0 ? string.Empty : rel[..slash];
        ClearRaceDayListFilter();
        OnPropertyChanged(nameof(PastRacesBreadcrumb));
        OnPropertyChanged(nameof(StepSubtitle));

        await RefreshPastRacesLevelAsync(force: false).ConfigureAwait(true);
        await PreparePastRacesListAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// One children list under the current Past Races path. Probe RUNSCORE only at year depth
    /// (Past Races / office bucket / year → race leaves).
    /// </summary>
    private Task RefreshPastRacesLevelAsync(bool force) =>
        RefreshPastRacesLevelAtAsync(_pastRacesRelative, PastRacesIndexKey, force);

    /// <param name="relativeUnderPastRaces">e.g. empty, <c>Memphis Past Races</c>, or <c>Memphis Past Races/2026</c>.</param>
    private async Task RefreshPastRacesLevelAtAsync(string relativeUnderPastRaces, string cacheKey, bool force)
    {
        if (!_cloud.IsAvailable)
            return;
        if (!force && _graphIndexedOffices.Contains(cacheKey))
            return;

        var relative = string.IsNullOrEmpty(relativeUnderPastRaces)
            ? EventBuilderService.PastRacesFolderName
            : EventBuilderService.PastRacesFolderName + "/" + relativeUnderPastRaces.Replace('\\', '/');
        var depth = string.IsNullOrEmpty(relativeUnderPastRaces)
            ? 0
            : relativeUnderPastRaces.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
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
                _graphIndexedOffices.Add(cacheKey);
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
        {
            if (entry.IsNavigationFolder)
            {
                Sessions.Add(entry);
                continue;
            }

            EnrichWithSecondaries(entry);
            if (IsAnnouncerRole && entry.SecondaryEventFiles.Count == 0)
                continue;
            Sessions.Add(entry);
        }

        ApplyRaceDayListFilter();
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowEmptySessions));
        OnPropertyChanged(nameof(EmptySessionsTitle));
        OnPropertyChanged(nameof(EmptySessionsBody));
        OnPropertyChanged(nameof(PastRacesBreadcrumb));
        OnPropertyChanged(nameof(StepSubtitle));
    }

    private static string EventNamesCacheKey(string scope) => "events:" + scope;

    private async Task LoadLiveOfficeSessionsAsync(string office)
    {
        var sessions = _scanner.ListSessions(_settings.LibraryRoot, office);
        if (IsAnnouncerRole)
            await ProbeSessionEventNamesAsync(sessions.Select(s => s.Path).ToList(), EventNamesCacheKey(office))
                .ConfigureAwait(true);

        // Timing: pull each session's tiny _checkout.json so Checked Out is accurate even when
        // this laptop never held the race (local Library had no lock file → looked Available).
        if (IsTimingRole)
            await RefreshSessionLocksFromGraphAsync(sessions).ConfigureAwait(true);

        LoadSessions(office);
    }

    /// <summary>
    /// One office only — parallel GETs of <c>._checkin/_checkout.json</c> (not the race pack).
    /// </summary>
    private async Task RefreshSessionLocksFromGraphAsync(IReadOnlyList<EventPackInfo> sessions)
    {
        if (!_cloud.IsAvailable || sessions.Count == 0)
            return;

        var paths = sessions
            .Where(s => !s.IsNavigationFolder)
            .Select(s => s.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
            return;

        try
        {
            using var cts = new CancellationTokenSource(GraphSyncPolicy.OfficeSessionsBudget);
            var libraryRoot = _settings.LibraryRoot;
            var driveRoot = _settings.GraphDriveRoot;
            await Task.WhenAll(paths.Select(async path =>
            {
                try
                {
                    await _cloud.DownloadCheckoutLockAsync(path, libraryRoot, driveRoot, cts.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Weak cell — leave local lock as-is; SelectSession still re-checks before download.
                }
            })).ConfigureAwait(false);
        }
        catch
        {
            // Budget / cancel — list still usable; SelectSession re-checks before pack download.
        }
    }

    /// <summary>
    /// Cellular-safe: while sitting on a live office event list (Timing, Graph signed in, no Active),
    /// re-pull tiny <c>_checkout.json</c> files on a slow timer so Checked Out and Available update
    /// without Refresh Events. Not Past Races; not when Busy / selecting a pack.
    /// </summary>
    private bool WantsSessionLockPolling =>
        Step == WizardStep.Session
        && IsTimingRole
        && !IsPastRacesBrowse
        && !HasActive
        && _cloud.IsAvailable
        && !string.IsNullOrWhiteSpace(SelectedOffice);

    private bool CanPollSessionLocksNow =>
        WantsSessionLockPolling && !Busy;

    private void UpdateSessionLockPolling()
    {
        if (WantsSessionLockPolling)
        {
            if (!_sessionLockPollTimer.IsEnabled)
                _sessionLockPollTimer.Start();
        }
        else if (_sessionLockPollTimer.IsEnabled)
        {
            _sessionLockPollTimer.Stop();
        }
    }

    private async Task PollSessionLocksOnceAsync()
    {
        if (!CanPollSessionLocksNow)
            return;
        if (!_sessionLockPollGate.Wait(0))
            return;

        try
        {
            var office = SelectedOffice!;
            var sessions = _scanner.ListSessions(_settings.LibraryRoot, office);
            await RefreshSessionLocksFromGraphAsync(sessions).ConfigureAwait(true);

            // Left the list / signed out / started checkout while GETs were in flight.
            if (!CanPollSessionLocksNow
                || !string.Equals(SelectedOffice, office, StringComparison.OrdinalIgnoreCase))
                return;

            LoadSessions(office);
        }
        finally
        {
            _sessionLockPollGate.Release();
        }
    }

    private async Task PreparePastRacesListAsync()
    {
        if (IsAnnouncerRole)
        {
            var folder = Path.Combine(_settings.LibraryRoot, EventBuilderService.PastRacesFolderName);
            if (!string.IsNullOrEmpty(_pastRacesRelative))
                folder = Path.Combine(folder, _pastRacesRelative.Replace('/', Path.DirectorySeparatorChar));
            var paths = _scanner.FindSessionLeavesShallow(folder).ToList();
            await ProbeSessionEventNamesAsync(paths, EventNamesCacheKey(PastRacesIndexKey)).ConfigureAwait(true);
        }

        LoadPastRacesBrowser();
    }

    /// <summary>
    /// Merge local + remote *.event names for listed sessions (Announcer T&S secondary filter).
    /// One office / Past Races level only — parallel Graph children lists, OfficeSessionsBudget.
    /// </summary>
    private async Task ProbeSessionEventNamesAsync(IReadOnlyList<string> sessionPaths, string cacheKey)
    {
        if (!IsAnnouncerRole || sessionPaths.Count == 0)
            return;

        foreach (var path in sessionPaths)
            _probedEventNames[path] = EventFileSelector.ListEventFiles(path);

        if (!_cloud.IsAvailable)
            return;
        if (_graphIndexedOffices.Contains(cacheKey))
            return;

        SetBusy("Checking T&S files", "Looking up which races have extra Timing & Scoring files…");
        try
        {
            using var budget = new CancellationTokenSource(GraphSyncPolicy.OfficeSessionsBudget);
            var gate = new SemaphoreSlim(GraphSyncPolicy.DownloadParallelism);
            var done = 0;
            var total = sessionPaths.Count;
            var tasks = sessionPaths.Select(async path =>
            {
                await gate.WaitAsync(budget.Token).ConfigureAwait(false);
                try
                {
                    budget.Token.ThrowIfCancellationRequested();
                    var remote = await _cloud.ListRemoteEventFileNamesAsync(
                        path, _settings.LibraryRoot, _settings.GraphDriveRoot, budget.Token)
                        .ConfigureAwait(false);
                    var merged = new HashSet<string>(
                        _probedEventNames.TryGetValue(path, out var local) ? local : Array.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var name in remote)
                        merged.Add(name);
                    _probedEventNames[path] = merged
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Keep local names for this session.
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref done);
                    ProgressText = $"Checked {n} of {total}…";
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(true);
            _graphIndexedOffices.Add(cacheKey);
            Status = "Extra T&S .event files checked.";
        }
        catch (OperationCanceledException)
        {
            Status = "T&S file check timed out — showing what we already know on this laptop.";
        }
        catch (Exception ex)
        {
            Status = "T&S file check skipped: " + ex.Message;
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    private void EnrichWithSecondaries(EventPackInfo session)
    {
        var names = _probedEventNames.TryGetValue(session.Path, out var probed)
            ? probed
            : session.EventFiles;
        if (names.Count == 0)
            names = EventFileSelector.ListEventFiles(session.Path);

        var sidecarPrimary = SessionRoleStore.TryLoad(session.Path)?.PrimaryEventFile;
        session.PrimaryEventFile = SecondaryEventSelector.ResolvePrimary(names, sidecarPrimary)
                                   ?? session.PrimaryEventFile;
        session.SecondaryEventFiles = SecondaryEventSelector.ListSecondaries(names, sidecarPrimary);
    }

    public async Task SetCheckoutRoleAsync(CheckoutRole role)
    {
        if (HasActive || _checkoutRole == role)
            return;

        _checkoutRole = role;
        OnPropertyChanged(nameof(IsTimingRole));
        OnPropertyChanged(nameof(IsAnnouncerRole));
        OnPropertyChanged(nameof(IsManualBackupRole));
        OnPropertyChanged(nameof(ShowSessionLock));
        OnPropertyChanged(nameof(StepSubtitle));
        OnPropertyChanged(nameof(EmptySessionsTitle));
        OnPropertyChanged(nameof(EmptySessionsBody));
        UpdateSessionLockPolling();


        if (Step != WizardStep.Session || string.IsNullOrWhiteSpace(SelectedOffice))
            return;

        if (IsPastRacesBrowse)
            await PreparePastRacesListAsync().ConfigureAwait(true);
        else
            await LoadLiveOfficeSessionsAsync(SelectedOffice).ConfigureAwait(true);
    }

    private async Task SelectAnnouncerSessionAsync(EventPackInfo session)
    {
        EnrichWithSecondaries(session);
        var secondaries = session.SecondaryEventFiles;
        if (secondaries.Count == 0)
        {
            Alert(
                "No other T&S file",
                "This race has no non-primary Timing & Scoring .event file. "
                + "Setup can Save As extra files (announcer, video display, …) into the race folder.");
            return;
        }

        SelectedSession = session;
        _sessionPackPulled = false;
        EventFiles.Clear();
        foreach (var name in secondaries)
            EventFiles.Add(name);

        if (secondaries.Count == 1)
        {
            SelectedEventFile = secondaries[0];
            await DownloadAndCheckoutAnnouncerAsync(secondaries[0]).ConfigureAwait(true);
            return;
        }

        SelectedEventFile = secondaries[0];
        Step = WizardStep.EventFile;
        OnPropertyChanged(nameof(StepSubtitle));
        OnPropertyChanged(nameof(EventFileStepHint));
    }

    private async Task DownloadAndCheckoutAnnouncerAsync(string eventFile)
    {
        if (SelectedSession is null || string.IsNullOrWhiteSpace(eventFile))
            return;

        SelectedEventFile = eventFile;
        if (_cloud.IsAvailable)
        {
            SetBusy("Downloading Announcer T&S files", "Getting the .event file and DB's from OneDrive…");
            try
            {
                var pull = await _cloud.DownloadSessionAsync(
                    SelectedSession.Path,
                    _settings.LibraryRoot,
                    _settings.GraphDriveRoot,
                    new Progress<string>(s =>
                        ProgressText = string.IsNullOrWhiteSpace(s) ? "Please wait…" : s),
                    options: SessionDownloadOptions.ForAnnouncer(eventFile)).ConfigureAwait(true);

                if (!pull.Success && !File.Exists(Path.Combine(SelectedSession.Path, eventFile)))
                {
                    await ShowBusyResultAsync(
                        "Couldn't download Announcer T&S files",
                        pull.Message + "\n\nTry again when signal is better, or ask setup staff for help.");
                    return;
                }

                _sessionPackPulled = true;
                Status = pull.Success
                    ? pull.Message
                    : "Download incomplete — using files already on this laptop.";
            }
            catch (OperationCanceledException)
            {
                await ShowBusyResultAsync(
                    "Download timed out",
                    "Announcer T&S download timed out on cellular. Try again when signal is better.");
                return;
            }
            catch (Exception ex)
            {
                await ShowBusyResultAsync("Couldn't download Announcer T&S files", ex.Message);
                return;
            }
            finally
            {
                if (Busy && !BusyNeedsAcknowledge)
                    ClearBusy();
            }
        }

        await CheckoutAnnouncerFromUiAsync().ConfigureAwait(true);
    }

    private async Task CheckoutAnnouncerFromUiAsync()
    {
        if (SelectedSession is null)
            return;

        SyncDisplayNameFromMachine();
        SetBusy("Loading Announcer T&S files", "Getting this laptop's copy ready…");
        try
        {
            var progress = new Progress<CopyProgress>(p =>
                ProgressText = p.FilesCopied > 0
                    ? $"Copying files… ({p.FilesCopied})"
                    : "Getting this laptop's copy ready…");
            var statusProgress = new Progress<string>(s => ProgressText = string.IsNullOrWhiteSpace(s) ? "Please wait…" : s);
            var result = await _checkout.CheckoutAsync(new CheckoutRequest
            {
                Pack = SelectedSession,
                Settings = _settings,
                HolderName = DisplayName.Trim(),
                PrimaryEventFileOverride = SelectedEventFile,
                Role = CheckoutRole.Announcer,
                SkipGraphDownload = _sessionPackPulled
            }, progress, statusProgress).ConfigureAwait(true);

            if (!result.Success)
            {
                await ShowBusyResultAsync("Couldn't load Announcer T&S files", result.Message);
                return;
            }

            Active = result.Active;
            Step = WizardStep.Active;
            Status = string.Empty;
            // First Graph DB's → Active tick now so T&S auto-load is fresh before the 60s timer.
            KickAutosaveMirror();
            ClearBusy();
        }
        catch (Exception ex)
        {
            await ShowBusyResultAsync("Couldn't load Announcer T&S files", ex.Message);
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    public async Task DoneAnnouncerAsync()
    {
        if (Active?.Role != CheckoutRole.Announcer)
            return;

        if (Busy)
            return;

        SetBusy("Saving Announcer .event", "Uploading to OneDrive, then clearing this laptop…");
        await YieldUiAsync().ConfigureAwait(true);

        // Pause DB's pull so it cannot recreate Active files while Done clears them.
        _mirrorTimer.Stop();
        _mirror.Pause();
        try
        {
            try { await _mirror.DrainAsync().ConfigureAwait(true); }
            catch { /* continue Done */ }

            var statusProgress = new Progress<string>(s =>
            {
                if (string.IsNullOrWhiteSpace(s))
                    return;
                if (s.StartsWith("done:copy", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("finish:copy", StringComparison.OrdinalIgnoreCase))
                    SetBusy("Saving Announcer .event", "Copying the .event into the local library…");
                else if (s.StartsWith("done:sync", StringComparison.OrdinalIgnoreCase)
                         || s.StartsWith("finish:sync", StringComparison.OrdinalIgnoreCase)
                         || s.StartsWith("finish:upload", StringComparison.OrdinalIgnoreCase))
                    SetBusy("Saving Announcer .event", "Uploading the .event to OneDrive…");
                else
                    SetBusy("Saving Announcer .event", FriendlyProgress(s));
            });

            var result = await _checkout.DoneAnnouncerAsync(
                _settings,
                statusProgress).ConfigureAwait(true);
            if (!result.Success)
            {
                await ShowBusyResultAsync(
                    result.BlockingProcesses.Count > 0 ? "Still Open" : "Couldn't Finish Announcer",
                    result.Message);
                return;
            }

            if (result.RetainActiveForCloudRetry)
            {
                await ShowBusyResultAsync(
                    "Saved on This Laptop — Cloud Still Needed",
                    result.Message
                    + "\n\nDo not shut down yet. Keep this app open and tap Done again when cellular signal is better.");
                return;
            }

            Active = null;
            SelectedSession = null;
            SelectedEventFile = null;
            ClearActiveSaveAsGuidePin();
            LoadOffices();
            if (Offices.Count == 1)
                await SelectOfficeAsync(Offices[0]).ConfigureAwait(true);
            else
                Step = WizardStep.Office;
            Status = string.Empty;
            await ShowBusyResultAsync("Done", result.Message).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await ShowBusyResultAsync("Couldn't Finish Announcer", ex.Message);
        }
        finally
        {
            _mirror.Resume();
            _mirrorTimer.Start();
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    private async Task<bool> DoneAndAllowCloseAsync()
    {
        await DoneAnnouncerAsync().ConfigureAwait(true);
        return _checkout.GetActive() is null;
    }

    public void ShowAdminSetupNewEvent()
    {
        _adminSection = AdminSection.SetupNewEvent;
        InitNewEventForm();
        NotifyAdminSection();
    }

    public string? NewEventOffice => _newEventOffice;
    public NewEventSourceItem? NewEventSource => _newEventSource;

    public string NewEventRaceName
    {
        get => _newEventRaceName;
        set
        {
            if (!Set(ref _newEventRaceName, value ?? string.Empty))
                return;
            // Preview updates on LostFocus/Enter — not per keystroke (keeps typing snappy).
            UpdateNewEventPreview();
        }
    }

    /// <summary>Calendar selection → Entries.ini / folder year as MM/DD/YYYY.</summary>
    public DateTime? NewEventRaceDateValue
    {
        get => _newEventRaceDateValue;
        set
        {
            if (!Set(ref _newEventRaceDateValue, value))
                return;
            UpdateNewEventPreview();
        }
    }

    /// <summary>Normalized date string for Entries.ini and folder naming.</summary>
    public string NewEventRaceDate =>
        _newEventRaceDateValue is { } dt
            ? dt.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

    public string NewEventResultsId
    {
        get => _newEventResultsId;
        set => Set(ref _newEventResultsId, value ?? string.Empty);
    }

    public string NewEventPreview
    {
        get => _newEventPreview;
        private set => Set(ref _newEventPreview, value);
    }

    private void InitNewEventForm()
    {
        EnsureLocalRoots();
        LoadOffices();

        _adminNewEventSubStep = AdminNewEventSubStep.Layout;
        _newEventIsRaceGroup = false;
        _newEventOffice = null;
        _newEventParentName = string.Empty;
        _newEventParentFolder = null;
        _lastCreatedRaceName = string.Empty;
        if (_newEventParentYear is < 2000 or > 2100)
            _newEventParentYear = DateTime.Today.Year;
        _newEventSource = null;
        _newEventCopyFrom = NewEventCopyFrom.Templates;
        _newEventPastRelative = string.Empty;
        _newEventRaceName = string.Empty;
        _newEventResultsId = string.Empty;
        _newEventRaceDateValue ??= DateTime.Today;
        OnPropertyChanged(nameof(NewEventParentName));
        OnPropertyChanged(nameof(NewEventParentYear));
        OnPropertyChanged(nameof(NewEventParentFolderPreview));
        OnPropertyChanged(nameof(NewEventRaceName));
        OnPropertyChanged(nameof(NewEventRaceDateValue));
        OnPropertyChanged(nameof(NewEventRaceDate));
        OnPropertyChanged(nameof(NewEventResultsId));
        OnPropertyChanged(nameof(IsNewEventRaceGroup));
        NewEventSources.Clear();
        _newEventSourceSource.Clear();
        ClearAdminListFilter();

        _newEventOfficeSource.Clear();
        foreach (var office in EventBuilderService.DestinationOffices)
        {
            if (Offices.Any(o => o.Equals(office, StringComparison.OrdinalIgnoreCase))
                || Directory.Exists(Path.Combine(_settings.LibraryRoot, office)))
                _newEventOfficeSource.Add(office);
        }

        if (_newEventOfficeSource.Count == 0)
        {
            foreach (var office in EventBuilderService.DestinationOffices)
                _newEventOfficeSource.Add(office);
        }

        ApplyAdminListFilter();
        UpdateNewEventPreview();
    }

    public async Task SelectNewEventLayoutAsync(NewEventLayoutOption? layout)
    {
        if (layout is null || !IsAdminSetupNewEvent)
            return;

        _newEventIsRaceGroup = layout.IsRaceGroup;
        _newEventParentFolder = null;
        OnPropertyChanged(nameof(IsNewEventRaceGroup));
        ClearAdminListFilter();
        _adminNewEventSubStep = AdminNewEventSubStep.Office;
        ApplyAdminListFilter();
        NotifyAdminSection();
        UpdateNewEventPreview();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    public async Task SelectNewEventOfficeAsync(string? office)
    {
        if (string.IsNullOrWhiteSpace(office) || !IsAdminSetupNewEvent)
            return;

        _newEventOffice = office;
        _newEventSource = null;
        _newEventPastRelative = string.Empty;
        NewEventSources.Clear();
        _newEventSourceSource.Clear();
        ClearAdminListFilter();
        _adminNewEventSubStep = _newEventIsRaceGroup
            ? AdminNewEventSubStep.Parent
            : AdminNewEventSubStep.SourceKind;
        OnPropertyChanged(nameof(NewEventOffice));
        NotifyAdminSection();
        UpdateNewEventPreview();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    public string NewEventParentName
    {
        get => _newEventParentName;
        set
        {
            if (!Set(ref _newEventParentName, value ?? string.Empty))
                return;
            OnPropertyChanged(nameof(NewEventParentFolderPreview));
            UpdateNewEventPreview();
        }
    }

    /// <summary>Year prefix for the race-group parent folder (e.g. 2026 in "2026 - LRM Weekend").</summary>
    public int NewEventParentYear
    {
        get => _newEventParentYear;
        set
        {
            if (!Set(ref _newEventParentYear, value))
                return;
            OnPropertyChanged(nameof(NewEventParentFolderPreview));
            UpdateNewEventPreview();
        }
    }

    public string NewEventParentFolderPreview
    {
        get
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_newEventParentName))
                    return "Race Group folder: enter a name…";
                if (_newEventParentYear is < 2000 or > 2100)
                    return "Race Group folder: enter a 4-digit year.";
                return "Race Group folder: "
                       + EntriesIniService.BuildSessionFolderName(
                           _newEventParentName.Trim(),
                           "01/01/" + _newEventParentYear.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    public void ConfirmNewEventParent()
    {
        if (!IsAdminSetupNewEvent || !_newEventIsRaceGroup)
            return;

        if (string.IsNullOrWhiteSpace(_newEventParentName))
        {
            Alert("Race Group", "Enter a Race Group name (e.g. LRM Weekend).");
            return;
        }

        if (_newEventParentYear is < 2000 or > 2100)
        {
            Alert("Race Group", "Enter a 4-digit year for the Race Group folder (e.g. 2026).");
            return;
        }

        try
        {
            _newEventParentFolder = EntriesIniService.BuildSessionFolderName(
                _newEventParentName.Trim(),
                "01/01/" + _newEventParentYear.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Alert("Race Group", ex.Message);
            return;
        }

        ClearAdminListFilter();
        _adminNewEventSubStep = AdminNewEventSubStep.SourceKind;
        NotifyAdminSection();
        UpdateNewEventPreview();
    }

    public async Task SelectNewEventSourceKindAsync(NewEventCopyFrom kind)
    {
        if (!IsAdminSetupNewEvent || string.IsNullOrWhiteSpace(_newEventOffice))
            return;

        _newEventCopyFrom = kind;
        _newEventSource = null;
        _newEventPastRelative = string.Empty;
        ClearAdminListFilter();
        _adminNewEventSubStep = AdminNewEventSubStep.Source;
        NotifyAdminSection();

        if (kind == NewEventCopyFrom.Templates)
        {
            await EnsureTemplatesFromGraphIfNeededAsync().ConfigureAwait(true);
            LoadNewEventTemplates();
        }
        else
        {
            await EnsureNewEventSourceLevelFromGraphIfNeededAsync(force: false).ConfigureAwait(true);
            LoadNewEventSourceBrowser();
        }

        ApplyAdminListFilter();
    }

    public async Task SelectNewEventSourceAsync(NewEventSourceItem? item)
    {
        if (item is null || !IsAdminSetupNewEvent)
            return;

        if (item.IsFolder)
        {
            _newEventPastRelative = string.IsNullOrEmpty(_newEventPastRelative)
                ? item.Display
                : _newEventPastRelative.TrimEnd('/', '\\') + "/" + item.Display;
            ClearAdminListFilter();
            await EnsureNewEventSourceLevelFromGraphIfNeededAsync(force: false).ConfigureAwait(true);
            LoadNewEventSourceBrowser();
            ApplyAdminListFilter();
            NotifyAdminSection();
            return;
        }

        _newEventSource = item;
        OnPropertyChanged(nameof(NewEventSource));
        ClearAdminListFilter();
        _adminNewEventSubStep = AdminNewEventSubStep.Details;
        UpdateNewEventPreview();
        NotifyAdminSection();
    }

    private async Task PopNewEventPastFolderAsync()
    {
        var rel = _newEventPastRelative.Replace('\\', '/').Trim('/');
        var slash = rel.LastIndexOf('/');
        _newEventPastRelative = slash < 0 ? string.Empty : rel[..slash];
        ClearAdminListFilter();
        await EnsureNewEventSourceLevelFromGraphIfNeededAsync(force: false).ConfigureAwait(true);
        LoadNewEventSourceBrowser();
        ApplyAdminListFilter();
        NotifyAdminSection();
    }

    private const string TemplatesGraphCacheKey = "Templates";

    private async Task EnsureTemplatesFromGraphIfNeededAsync()
    {
        if (!_cloud.IsAvailable)
            return;
        if (_graphIndexedOffices.Contains(TemplatesGraphCacheKey))
            return;

        try
        {
            Busy = true;
            BusyTitle = "Loading Templates from OneDrive…";
            var result = await _cloud.RefreshFolderChildrenAsync(
                _settings.LibraryRoot,
                _settings.GraphDriveRoot,
                EventBuilderService.TemplatesFolderName,
                probeSessionLeaves: true,
                new Progress<string>(s => Status = s)).ConfigureAwait(true);
            Status = result.Message;
            if (result.Success)
                _graphIndexedOffices.Add(TemplatesGraphCacheKey);
            else if (!Directory.Exists(Path.Combine(_settings.LibraryRoot, EventBuilderService.TemplatesFolderName)))
                Alert("OneDrive Templates", result.Message);
        }
        catch (Exception ex)
        {
            Alert("OneDrive Templates", ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Graph for Setup New Event copy-from: Past Races uses the same one-level cache as race-day;
    /// Current Events indexes the destination office once.
    /// </summary>
    private async Task EnsureNewEventSourceLevelFromGraphIfNeededAsync(bool force)
    {
        if (_newEventCopyFrom == NewEventCopyFrom.Templates)
            return;

        if (_newEventCopyFrom == NewEventCopyFrom.PastRaces)
        {
            await RefreshPastRacesLevelAtAsync(
                    _newEventPastRelative,
                    PastRacesCacheKey(_newEventPastRelative),
                    force)
                .ConfigureAwait(true);
            return;
        }

        if (string.IsNullOrWhiteSpace(_newEventOffice))
            return;

        // Live office: one office index (session leaves). Nested Race Group folders then list locally.
        await EnsureOfficeSessionsFromGraphIfNeededAsync(_newEventOffice, manageBusy: true)
            .ConfigureAwait(true);
    }

    private void LoadNewEventTemplates()
    {
        _newEventSourceSource.Clear();
        foreach (var path in _eventBuilder.ListTemplateSessions(_settings.LibraryRoot))
            _newEventSourceSource.Add(new NewEventSourceItem(Path.GetFileName(path), path));
    }

    /// <summary>
    /// One folder level under Past Races root, or under the destination office (Current Events).
    /// </summary>
    private void LoadNewEventSourceBrowser()
    {
        _newEventSourceSource.Clear();

        string folder;
        if (_newEventCopyFrom == NewEventCopyFrom.PastRaces)
        {
            folder = Path.Combine(_settings.LibraryRoot, EventBuilderService.PastRacesFolderName);
        }
        else if (_newEventCopyFrom == NewEventCopyFrom.CurrentEvents)
        {
            if (string.IsNullOrWhiteSpace(_newEventOffice))
                return;
            folder = Path.Combine(_settings.LibraryRoot, _newEventOffice);
        }
        else
        {
            return;
        }

        if (!string.IsNullOrEmpty(_newEventPastRelative))
            folder = Path.Combine(folder, _newEventPastRelative.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(folder))
            return;

        // Match race-day Past Races ordering (year folders newest first).
        var entries = LibraryScanner.ListImmediateChildFolders(folder)
            .Select(dir =>
            {
                var name = Path.GetFileName(dir);
                var isSession = _paths.IsSessionLeaf(dir)
                                || Directory.Exists(Path.Combine(dir, PathService.RunScoreFolderName));
                return new NewEventSourceItem(name, dir, IsFolder: !isSession);
            })
            .OrderBy(e => LooksLikeYearFolderName(e.Display) ? 0 : 1)
            .ThenByDescending(e => LooksLikeYearFolderName(e.Display) ? e.Display : "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Display, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
            _newEventSourceSource.Add(entry);
    }

    private static bool LooksLikeYearFolderName(string? name) =>
        name is { Length: 4 }
        && name[0] is >= '1' and <= '9'
        && char.IsDigit(name[1]) && char.IsDigit(name[2]) && char.IsDigit(name[3]);

    private void UpdateNewEventPreview()
    {
        try
        {
            var office = _newEventOffice ?? "(office)";
            var name = _newEventRaceName.Trim();
            if (name.Length == 0)
            {
                NewEventPreview = _newEventIsRaceGroup && !string.IsNullOrEmpty(_newEventParentFolder)
                    ? $"Will create under Race Group {_newEventParentFolder}: enter this race’s name…"
                    : "Folder preview: enter a race name…";
                return;
            }

            if (_newEventIsRaceGroup && !string.IsNullOrEmpty(_newEventParentFolder))
            {
                NewEventPreview =
                    "Will create (Race Group race):\n"
                    + Path.Combine(_settings.LibraryRoot, office, _newEventParentFolder, name);
                return;
            }

            var folder = EntriesIniService.BuildSessionFolderName(name, NewEventRaceDate);
            NewEventPreview =
                "Will create (one race — not Past Races):\n"
                + Path.Combine(_settings.LibraryRoot, office, folder);
        }
        catch (Exception ex)
        {
            NewEventPreview = ex.Message;
        }
    }

    public void ShowAdminEditCurrentEvent()
    {
        _adminSection = AdminSection.EditCurrentEvent;
        _adminEventsSubStep = AdminEventsSubStep.Office;
        _adminOffice = null;
        _adminPastRacesRelative = string.Empty;
        ResetAdminAssignUi();
        AdminSessions.Clear();
        _adminSessionSource.Clear();
        // Same office list as Timing / Announcer (AllowedOfficeFolders — no Other).
        _adminOfficeSource.Clear();
        foreach (var name in Offices)
            _adminOfficeSource.Add(name);
        _adminListFilter = string.Empty;
        OnPropertyChanged(nameof(AdminListFilter));
        ApplyAdminListFilter();

        NotifyAdminSection();
    }

    public void ShowAdminSettings()
    {
        _adminSection = AdminSection.Settings;
        // Offline / already-cached company file: surface license without waiting on Graph.
        NotifyRunScoreLicenseFields();
        NotifyAdminSection();
    }

    public string? SelectedTimingFile
    {
        get => _selectedTimingFile;
        set => Set(ref _selectedTimingFile, value);
    }

    public EventPackInfo? SelectedAdminSession => _adminSession;

    public async Task SelectAdminOfficeAsync(string? office)
    {
        if (Busy || string.IsNullOrWhiteSpace(office) || !IsAdminEditCurrentEvent)
            return;

        ResetAdminAssignUi();
        AdminSessions.Clear();
        _adminSessionSource.Clear();
        _adminOffice = office;
        _adminPastRacesRelative = string.Empty;
        _adminListFilter = string.Empty;
        OnPropertyChanged(nameof(AdminListFilter));
        // Sub-step before any await so the office list is gone — paint before Graph / disk work.
        _adminEventsSubStep = AdminEventsSubStep.Session;
        NotifyAdminSection();
        SetBusy($"Loading events for {office}…");
        await YieldUiAsync().ConfigureAwait(true);

        try
        {
            if (IsAdminPastRacesBrowse)
            {
                // Same nested browse + same this-run Graph cache as race-day Past Races (no admin: key).
                await RefreshPastRacesLevelAtAsync(
                    _adminPastRacesRelative,
                    PastRacesCacheKey(_adminPastRacesRelative),
                    force: false).ConfigureAwait(true);
                LoadAdminPastRacesBrowser();
            }
            else
            {
                // Same EnsureOfficeSessions cache as Timing — skip Graph if that office was already indexed.
                await EnsureOfficeSessionsFromGraphIfNeededAsync(office, manageBusy: false)
                    .ConfigureAwait(true);
                foreach (var session in _scanner.ListSessions(_settings.LibraryRoot, office))
                    _adminSessionSource.Add(session);
                ApplyAdminListFilter();
            }
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    public async Task SelectAdminSessionAsync(EventPackInfo? session)
    {
        // Busy covers office→events load — ignore laggy double-clicks that land on a row.
        if (Busy || session is null || !IsAdminEditCurrentEvent)
            return;

        if (session.IsNavigationFolder)
        {
            await EnterAdminPastRacesFolderAsync(session).ConfigureAwait(true);
            return;
        }

        // Graph name list can take a few seconds — show busy so double-click feels accepted.
        SetBusy(
            "Loading event files",
            $"Looking up Timing & Scoring files for {session.DisplayName}…");
        try
        {
            await LoadAdminEventFilesAsync(session).ConfigureAwait(true);
            if (!ReferenceEquals(_adminSession, session))
                return;

            _adminEventsSubStep = AdminEventsSubStep.Assign;
            NotifyAdminSection();
            Status = $"Ready to mark the main timing file for {session.DisplayName}.";
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    private async Task EnterAdminPastRacesFolderAsync(EventPackInfo folder)
    {
        if (folder is null || !folder.IsNavigationFolder || !IsAdminPastRacesBrowse)
            return;

        _adminPastRacesRelative = string.IsNullOrEmpty(_adminPastRacesRelative)
            ? folder.SessionName
            : _adminPastRacesRelative.TrimEnd('/', '\\') + "/" + folder.SessionName;
        ClearAdminListFilter();

        await RefreshPastRacesLevelAtAsync(
            _adminPastRacesRelative,
            PastRacesCacheKey(_adminPastRacesRelative),
            force: false).ConfigureAwait(true);
        LoadAdminPastRacesBrowser();
        NotifyAdminSection();
    }

    private async Task PopAdminPastRacesFolderAsync()
    {
        var rel = _adminPastRacesRelative.Replace('\\', '/').Trim('/');
        var slash = rel.LastIndexOf('/');
        _adminPastRacesRelative = slash < 0 ? string.Empty : rel[..slash];
        ClearAdminListFilter();

        await RefreshPastRacesLevelAtAsync(
            _adminPastRacesRelative,
            PastRacesCacheKey(_adminPastRacesRelative),
            force: false).ConfigureAwait(true);
        LoadAdminPastRacesBrowser();
        NotifyAdminSection();
    }

    private void LoadAdminPastRacesBrowser()
    {
        _adminSessionSource.Clear();
        var folder = Path.Combine(_settings.LibraryRoot, EventBuilderService.PastRacesFolderName);
        if (!string.IsNullOrEmpty(_adminPastRacesRelative))
            folder = Path.Combine(folder, _adminPastRacesRelative.Replace('/', Path.DirectorySeparatorChar));

        foreach (var entry in _scanner.ListBrowserEntries(
                     _settings.LibraryRoot, EventBuilderService.PastRacesFolderName, folder))
            _adminSessionSource.Add(entry);
        ApplyAdminListFilter();
    }

    private void ApplyAdminListFilter()
    {
        var q = _adminListFilter.Trim();

        if (IsAdminNewEventOffice)
        {
            NewEventOffices.Clear();
            foreach (var name in _newEventOfficeSource)
            {
                if (q.Length == 0 || name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    NewEventOffices.Add(name);
            }
            return;
        }

        if (IsAdminNewEventSource)
        {
            NewEventSources.Clear();
            foreach (var entry in _newEventSourceSource)
            {
                if (q.Length == 0 || entry.Display.Contains(q, StringComparison.OrdinalIgnoreCase))
                    NewEventSources.Add(entry);
            }
            return;
        }

        if (!IsAdminEditCurrentEvent)
            return;

        if (_adminEventsSubStep == AdminEventsSubStep.Office)
        {
            AdminOffices.Clear();
            foreach (var name in _adminOfficeSource)
            {
                if (q.Length == 0 || name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    AdminOffices.Add(name);
            }
            return;
        }

        if (_adminEventsSubStep != AdminEventsSubStep.Session)
            return;

        AdminSessions.Clear();
        foreach (var entry in _adminSessionSource)
        {
            if (q.Length == 0
                || entry.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || entry.SessionName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (entry.ParentEvent?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                AdminSessions.Add(entry);
        }
    }

    private void ClearAdminListFilter()
    {
        if (_adminListFilter.Length != 0)
        {
            _adminListFilter = string.Empty;
            OnPropertyChanged(nameof(AdminListFilter));
        }

        ApplyAdminListFilter();
    }

    private async Task LoadAdminEventFilesAsync(EventPackInfo session)
    {
        _adminSession = session;
        OnPropertyChanged(nameof(SelectedAdminSession));
        AdminEventFiles.Clear();
        SelectedTimingFile = null;

        // Prefer Timing / Announcer disk + this-run probe; Graph only if we have not listed this session yet.
        var names = new HashSet<string>(
            EventFileSelector.ListEventFiles(session.Path),
            StringComparer.OrdinalIgnoreCase);
        // Mid-checkout Save As lands in Active first — include those so assign / Finish stay in sync.
        if (Active is not null
            && string.Equals(Active.LibrarySessionPath, session.Path, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in EventFileSelector.ListEventFiles(Active.ActivePath))
                names.Add(name);
        }

        var alreadyProbed = _probedEventNames.TryGetValue(session.Path, out var probed);
        if (alreadyProbed)
        {
            foreach (var name in probed!)
                names.Add(name);
        }

        var roles = SessionRoleStore.TryLoad(session.Path);
        var needRemoteNames = !alreadyProbed;
        var needRemoteRoles = roles is null;

        if (_cloud.IsAvailable && (needRemoteNames || needRemoteRoles))
        {
            try
            {
                if (needRemoteNames)
                {
                    var remote = await _cloud.ListRemoteEventFileNamesAsync(
                        session.Path, _settings.LibraryRoot, _settings.GraphDriveRoot).ConfigureAwait(true);
                    foreach (var name in remote)
                        names.Add(name);
                    _probedEventNames[session.Path] = names
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                // One session only — never prefetch sibling races for Admin assign.
                if (needRemoteRoles)
                {
                    await _cloud.DownloadSessionRolesAsync(
                        new[] { session.Path },
                        _settings.LibraryRoot,
                        _settings.GraphDriveRoot).ConfigureAwait(true);
                    roles = SessionRoleStore.TryLoad(session.Path);
                }
            }
            catch
            {
                // Local names and any sidecar already on disk are enough to assign.
            }
        }

        if (!ReferenceEquals(_adminSession, session))
            return;

        if (!string.IsNullOrWhiteSpace(roles?.PrimaryEventFile))
            names.Add(roles.PrimaryEventFile);

        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var name in sorted)
            AdminEventFiles.Add(name);

        SelectedTimingFile = roles?.PrimaryEventFile
                             ?? (sorted.Count == 1 ? sorted[0] : null);
        ReloadAdminRaceStarts();
        NotifyAdminTsSetup();
    }

    private void ResetAdminAssignUi()
    {
        _adminSession = null;
        OnPropertyChanged(nameof(SelectedAdminSession));
        AdminEventFiles.Clear();
        SelectedTimingFile = null;
        AdminRaceStarts.Clear();
        _adminRaceStartEditOriginal = null;
        NewAdminRaceStartCategory = string.Empty;
        NewAdminRaceStartTime = string.Empty;
        OnPropertyChanged(nameof(IsEditingAdminRaceStart));
        OnPropertyChanged(nameof(AdminRaceStartCommitLabel));
        NotifyAdminTsSetup();
    }

    private void ReloadAdminRaceStarts()
    {
        AdminRaceStarts.Clear();
        _adminRaceStartEditOriginal = null;
        NewAdminRaceStartCategory = string.Empty;
        NewAdminRaceStartTime = string.Empty;
        OnPropertyChanged(nameof(IsEditingAdminRaceStart));
        OnPropertyChanged(nameof(AdminRaceStartCommitLabel));
        if (_adminSession is null)
            return;
        foreach (var entry in RaceStartsStore.Load(_adminSession.Path))
            AdminRaceStarts.Add(new RaceStartRowViewModel(entry));
    }

    public void BeginEditAdminRaceStart(RaceStartRowViewModel? row)
    {
        if (row is null)
            return;
        _adminRaceStartEditOriginal = row.Entry;
        NewAdminRaceStartCategory = row.Category;
        NewAdminRaceStartTime = row.StartDisplay;
        OnPropertyChanged(nameof(IsEditingAdminRaceStart));
        OnPropertyChanged(nameof(AdminRaceStartCommitLabel));
    }

    public void AddAdminRaceStart()
    {
        if (_adminSession is null)
            return;

        var category = NewAdminRaceStartCategory.Trim();
        if (string.IsNullOrWhiteSpace(category))
        {
            Status = "Enter a category name (e.g. 5K).";
            return;
        }

        if (!RaceStartClock.TryParse24h(NewAdminRaceStartTime, out var tod))
        {
            Status = "Enter start time in 24-hour format (e.g. 08:00 or 830).";
            return;
        }

        var next = AdminRaceStarts.Select(r => r.Entry).ToList();
        TimeSpan? keepGun = null;
        if (_adminRaceStartEditOriginal is { } orig)
        {
            keepGun = orig.GunStartTimeOfDay;
            var idx = next.FindIndex(e =>
                string.Equals(e.Category, orig.Category, StringComparison.Ordinal)
                && e.StartTimeOfDay == orig.StartTimeOfDay);
            if (idx >= 0)
                next.RemoveAt(idx);
            _adminRaceStartEditOriginal = null;
        }

        next.Add(new RaceStartEntry
        {
            Category = category,
            StartTimeOfDay = tod,
            GunStartTimeOfDay = keepGun
        });
        ApplyAdminRaceStartsLocal(next);
        NewAdminRaceStartCategory = string.Empty;
        NewAdminRaceStartTime = string.Empty;
        OnPropertyChanged(nameof(IsEditingAdminRaceStart));
        OnPropertyChanged(nameof(AdminRaceStartCommitLabel));
        Status = $"Race start listed for {_adminSession.DisplayName}. Tap Save Race Starts to sync OneDrive.";
    }

    public void RemoveAdminRaceStart(RaceStartRowViewModel? row)
    {
        if (row is null || _adminSession is null)
            return;
        var next = AdminRaceStarts.Select(r => r.Entry).ToList();
        var idx = next.FindIndex(e =>
            string.Equals(e.Category, row.Entry.Category, StringComparison.Ordinal)
            && e.StartTimeOfDay == row.Entry.StartTimeOfDay);
        if (idx < 0)
            return;
        next.RemoveAt(idx);
        if (_adminRaceStartEditOriginal is { } orig
            && string.Equals(orig.Category, row.Entry.Category, StringComparison.Ordinal)
            && orig.StartTimeOfDay == row.Entry.StartTimeOfDay)
        {
            _adminRaceStartEditOriginal = null;
            NewAdminRaceStartCategory = string.Empty;
            NewAdminRaceStartTime = string.Empty;
            OnPropertyChanged(nameof(IsEditingAdminRaceStart));
            OnPropertyChanged(nameof(AdminRaceStartCommitLabel));
        }

        ApplyAdminRaceStartsLocal(next);
    }

    private void ApplyAdminRaceStartsLocal(IReadOnlyList<RaceStartEntry> starts)
    {
        if (_adminSession is null)
            return;
        var normalized = RaceStartsStore.Normalize(starts);
        try
        {
            RaceStartsStore.Save(_adminSession.Path, normalized);
        }
        catch (Exception ex)
        {
            Status = "Could not save race starts locally: " + ex.Message;
            return;
        }

        AdminRaceStarts.Clear();
        foreach (var entry in normalized)
            AdminRaceStarts.Add(new RaceStartRowViewModel(entry));

        // If this race is Active on this laptop, keep Active in sync with Admin edits.
        if (Active is { Role: not CheckoutRole.Announcer }
            && string.Equals(Active.LibrarySessionPath, _adminSession.Path, StringComparison.OrdinalIgnoreCase))
        {
            Active = Active.WithRaceStarts(normalized);
            _activeStore.Save(Active);
        }
    }

    public async Task SaveAdminRaceStartsAsync()
    {
        if (_adminSession is null)
        {
            Alert("Choose an event", "Pick an office and an event, then set race starts.");
            return;
        }

        var starts = AdminRaceStarts.Select(r => r.Entry).ToList();
        try
        {
            RaceStartsStore.Save(_adminSession.Path, starts);
        }
        catch (Exception ex)
        {
            Alert("Race Starts", ex.Message);
            return;
        }

        if (Active is { Role: not CheckoutRole.Announcer }
            && string.Equals(Active.LibrarySessionPath, _adminSession.Path, StringComparison.OrdinalIgnoreCase))
        {
            Active = Active.WithRaceStarts(RaceStartsStore.Normalize(starts));
            _activeStore.Save(Active);
        }

        const string nextHint = " Tap Choose Another Event for a different race, or Back to leave Admin.";

        if (!_cloud.IsAvailable)
        {
            Status = "Race starts saved on this laptop. Sign in to OneDrive to sync them." + nextHint;
            return;
        }

        SetBusy("Saving Race Starts", "Uploading race starts…");
        try
        {
            var upload = await _cloud.UploadRaceStartsAsync(
                _adminSession.Path,
                _settings.LibraryRoot,
                _settings.GraphDriveRoot).ConfigureAwait(true);
            if (!upload.Success)
            {
                await ShowBusyResultAsync(
                    "Saved on this laptop",
                    upload.Message + "\n\nUse Choose Another Event for a different race.").ConfigureAwait(true);
                return;
            }

            Status = $"Saved race starts for {_adminSession.DisplayName}. {upload.Message}" + nextHint;
        }
        catch (Exception ex)
        {
            await ShowBusyResultAsync(
                "Saved on this laptop",
                "Race starts are on this laptop, but OneDrive upload failed.\n\n" + ex.Message
                + "\n\nUse Choose Another Event for a different race.").ConfigureAwait(true);
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    /// <summary>
    /// Edit Current Event — open the selected (or main) .event already in this race.
    /// </summary>
    public async Task LaunchAdminOpenCurrentTsAsync()
    {
        if (!await EnsureAdminTimingCheckoutAsync().ConfigureAwait(true))
            return;

        EnsureAdminEventFileInActive(SelectedTimingFile);
        var result = _launcher.LaunchTimingAndScoring(
            Active!,
            _settings,
            bare: false,
            eventFileOverride: SelectedTimingFile);
        if (!result.Success)
        {
            Alert("Open Current Timing & Scoring", result.Message);
            return;
        }

        await LoadAdminEventFilesAsync(_adminSession!).ConfigureAwait(true);
        Status = $"Opened {SelectedTimingFile ?? Active!.PrimaryEventFile} — edit and save in Timing & Scoring.";
    }

    /// <summary>
    /// Edit Current Event — empty T&amp;S so staff can build a new .event (first timing or announcer/video)
    /// and Save As into Active.
    /// </summary>
    public async Task LaunchAdminNewEmptyTsAsync()
    {
        if (!await EnsureAdminTimingCheckoutAsync().ConfigureAwait(true))
            return;

        var result = _launcher.LaunchTimingAndScoring(Active!, _settings, bare: true);
        if (!result.Success)
        {
            Alert("Create a New Timing & Scoring", result.Message);
            return;
        }

        PinActiveSaveAsGuide();
        NotifyAdminTsSetup();
        Status = "Create a New Timing & Scoring opening… Save As path is on this page (above Finish).";
    }

    /// <summary>Checkout this Admin race as Timing when needed so Active + Backup match Save As / Finish.</summary>
    private async Task<bool> EnsureAdminTimingCheckoutAsync()
    {
        if (_adminSession is null || !IsAdminEditCurrentEvent)
            return false;

        if (HasActive && !AdminSessionIsActive())
        {
            Alert(
                "Already Checked Out",
                "Finish the current race before launching Timing & Scoring for another event.");
            return false;
        }

        // Even if already Active, Finish should return to Edit Current Event (not race-day office).
        _finishAdminReturn = FinishAdminReturn.EditCurrentEvent;

        if (HasActive)
            return true;

        SelectedSession = _adminSession;
        SelectedEventFile = string.Empty;
        EventFiles.Clear();
        _sessionPackPulled = SessionPackLooksUsable(_adminSession.Path);
        _checkoutRole = CheckoutRole.Timing;
        OnPropertyChanged(nameof(IsTimingRole));
        OnPropertyChanged(nameof(IsAnnouncerRole));
        await CheckoutAsync().ConfigureAwait(true);
        return Active is not null;
    }

    /// <summary>After Finish from Edit Current Event Launch/Create T&amp;S — restore assign step for that race.</summary>
    private async Task ReturnToEditCurrentEventAfterFinishAsync()
    {
        Step = WizardStep.Admin;
        _adminSection = AdminSection.EditCurrentEvent;
        GrantAdminUnlock();

        if (_adminSession is not null)
        {
            _adminEventsSubStep = AdminEventsSubStep.Assign;
            await LoadAdminEventFilesAsync(_adminSession).ConfigureAwait(true);
            Status = $"Finish complete — {_adminSession.DisplayName}. Refresh Event Files or Save Main Timing File if you added a .event.";
        }
        else
        {
            _adminEventsSubStep = AdminEventsSubStep.Office;
            Status = "Finish complete — pick the event again under Edit Current Event.";
        }

        NotifyAdminSection();
        NotifyAdminTsSetup();
        NotifyGraphStatus();
    }

    /// <summary>Copy Library .event into Active when Open Current needs a file not staged yet.</summary>
    private void EnsureAdminEventFileInActive(string? eventFileName)
    {
        if (Active is null || string.IsNullOrWhiteSpace(eventFileName))
            return;

        var name = Path.GetFileName(eventFileName.Trim());
        var dest = Path.Combine(Active.ActivePath, name);
        if (File.Exists(dest))
            return;

        var src = Path.Combine(Active.LibrarySessionPath, name);
        if (!File.Exists(src))
            return;

        try
        {
            Directory.CreateDirectory(Active.ActivePath);
            File.Copy(src, dest, overwrite: true);
        }
        catch
        {
            // Launch will report missing file.
        }
    }

    public async Task RefreshAdminEventFilesAsync()
    {
        if (_adminSession is null || !IsAdminEventsAssign)
            return;

        SetBusy(
            "Refreshing Event Files",
            $"Looking up Timing & Scoring files for {_adminSession.DisplayName}…");
        try
        {
            // Force a fresh Graph name probe when signed in (clear this-run cache for this session).
            _probedEventNames.Remove(_adminSession.Path);
            await LoadAdminEventFilesAsync(_adminSession).ConfigureAwait(true);
            if (!IsAdminEventsAssign || _adminSession is null)
                return;

            Status = AdminEventFiles.Count == 0
                ? "No .event found yet — use Create a New Timing & Scoring, Save As into Active, then Refresh Event Files."
                : $"Found {AdminEventFiles.Count} .event file(s). Save Main Timing File when the primary is correct.";
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    /// <summary>Assign step → office list so staff can configure another race without Back×N.</summary>
    public void ResetAdminEventsToStart()
    {
        if (!IsAdminEditCurrentEvent)
            return;

        ResetAdminAssignUi();
        AdminSessions.Clear();
        _adminSessionSource.Clear();
        _adminOffice = null;
        _adminPastRacesRelative = string.Empty;
        ClearAdminListFilter();
        _adminEventsSubStep = AdminEventsSubStep.Office;
        ApplyAdminListFilter();
        NotifyAdminSection();
        Status = "Pick an office to configure another event. Back leaves Admin.";
    }

    public async Task SaveAdminEventRolesAsync()
    {
        if (_adminSession is null)
        {
            Alert("Choose an event", "Pick an office and an event, then mark the main timing .event.");
            return;
        }

        var primary = string.IsNullOrWhiteSpace(SelectedTimingFile) ? null : SelectedTimingFile;
        if (primary is null)
        {
            Alert("Assignment", "Choose the main timing .event file.");
            return;
        }

        var roles = new SessionRoles { PrimaryEventFile = primary };
        var error = SessionRoleStore.Validate(roles);
        if (error is not null)
        {
            Alert("Assignment", error);
            return;
        }

        var savedName = _adminSession.DisplayName;
        try
        {
            SessionRoleStore.Save(_adminSession.Path, roles);
        }
        catch (Exception ex)
        {
            Alert("Assignment", ex.Message);
            return;
        }

        const string nextHint = " Tap Choose another event to configure a different race, or Back to leave Admin.";

        if (!_cloud.IsAvailable)
        {
            Status = "Assignment saved on this laptop. Sign in to OneDrive to sync it." + nextHint;
            return;
        }

        SetBusy("Saving assignment", "Uploading the event assignment…");
        try
        {
            var upload = await _cloud.UploadSessionRolesAsync(
                _adminSession.Path,
                _settings.LibraryRoot,
                _settings.GraphDriveRoot).ConfigureAwait(true);
            if (!upload.Success)
            {
                await ShowBusyResultAsync(
                    "Saved on this laptop",
                    upload.Message + "\n\nUse Choose another event to configure a different race.").ConfigureAwait(true);
                return;
            }

            Status = $"Saved main timing for {savedName}. {upload.Message}" + nextHint;
        }
        catch (Exception ex)
        {
            await ShowBusyResultAsync(
                "Saved on this laptop",
                "The assignment is on this laptop, but OneDrive upload failed.\n\n" + ex.Message
                + "\n\nUse Choose another event to configure a different race.").ConfigureAwait(true);
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    private void NotifyAdminSection()
    {
        OnPropertyChanged(nameof(IsAdminSetupNewEvent));
        OnPropertyChanged(nameof(IsAdminEditCurrentEvent));
        OnPropertyChanged(nameof(IsAdminSettings));
        OnPropertyChanged(nameof(IsAdminNewEventLayout));
        OnPropertyChanged(nameof(IsAdminNewEventOffice));
        OnPropertyChanged(nameof(IsAdminNewEventParent));
        OnPropertyChanged(nameof(IsAdminNewEventSourceKind));
        OnPropertyChanged(nameof(IsAdminNewEventSource));
        OnPropertyChanged(nameof(IsAdminNewEventDetails));
        OnPropertyChanged(nameof(IsAdminNewEventGroupCreated));
        OnPropertyChanged(nameof(IsNewEventRaceGroup));
        OnPropertyChanged(nameof(ShowNewEventGroupCheckout));
        OnPropertyChanged(nameof(NewEventGroupCreatedHeadline));
        OnPropertyChanged(nameof(NewEventGroupCreatedDetail));
        OnPropertyChanged(nameof(NewEventCheckoutLastLabel));
        OnPropertyChanged(nameof(IsAdminEventsOffice));
        OnPropertyChanged(nameof(IsAdminEventsSession));
        OnPropertyChanged(nameof(IsAdminEventsAssign));
        OnPropertyChanged(nameof(ShowAdminEventsPathBreadcrumb));
        OnPropertyChanged(nameof(IsAdminSessionRaceGroupLeaf));
        OnPropertyChanged(nameof(ShowAddRaceToGroup));
        OnPropertyChanged(nameof(ShowMoveToPastRaces));
        OnPropertyChanged(nameof(ShowAdminTsSetup));
        OnPropertyChanged(nameof(ShowAdminTsEmptyHint));
        OnPropertyChanged(nameof(ShowAdminTsAddSecondaryHint));
        OnPropertyChanged(nameof(ShowAdminSaveAssignment));
        OnPropertyChanged(nameof(AdminSaveAsFolderGuide));
        OnPropertyChanged(nameof(ShowAdminListFilter));
        OnPropertyChanged(nameof(AdminListFilterHint));
        OnPropertyChanged(nameof(AdminEventsBreadcrumb));
        OnPropertyChanged(nameof(AdminNewEventBreadcrumb));
        OnPropertyChanged(nameof(NewEventParentFolderPreview));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepSubtitle));
        OnPropertyChanged(nameof(ShowAdminHeaderLink));
        OnPropertyChanged(nameof(ShowExitAdmin));
        OnPropertyChanged(nameof(ShowBack));
    }

    private void NotifyAdminTsSetup()
    {
        OnPropertyChanged(nameof(ShowAdminTsSetup));
        OnPropertyChanged(nameof(ShowAdminTsEmptyHint));
        OnPropertyChanged(nameof(ShowAdminTsAddSecondaryHint));
        OnPropertyChanged(nameof(ShowAdminSaveAssignment));
        OnPropertyChanged(nameof(AdminSaveAsFolderGuide));
        OnPropertyChanged(nameof(StepSubtitle));
    }

    private bool AdminSessionIsActive() =>
        Active is not null
        && _adminSession is not null
        && string.Equals(Active.LibrarySessionPath, _adminSession.Path, StringComparison.OrdinalIgnoreCase);

    private void NotifyGraphStatus()
    {
        OnPropertyChanged(nameof(IsGraphSignedIn));
        OnPropertyChanged(nameof(GraphAccountStatus));
        OnPropertyChanged(nameof(GraphReadyLabel));
        OnPropertyChanged(nameof(GraphLandingStatus));
        OnPropertyChanged(nameof(GraphReadyInlineDetail));
        OnPropertyChanged(nameof(ShowGraphReadyInline));
        OnPropertyChanged(nameof(ShowGraphSignInBanner));
        OnPropertyChanged(nameof(ShowRaceDayOfficeList));
        OnPropertyChanged(nameof(ShowRaceDayListFilter));
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

    /// <summary>Open a vendor download page in the default browser (Admin → Settings → Software Installs).</summary>
    public void OpenSoftwareInstallUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Alert("Software Installs", "That download link is missing or invalid.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
            Status = "Opened download page in your browser.";
        }
        catch (Exception ex)
        {
            Alert("Software Installs", "Could not open the download page:\n" + ex.Message);
        }
    }

    public void CopyRunScoreLicenseName() =>
        CopyRunScoreLicenseField(RunScoreLicenseName, "License name", "License Name");

    public void CopyRunScoreLicenseKey() =>
        CopyRunScoreLicenseField(RunScoreLicenseKey, "License key", "License Key");

    private void CopyRunScoreLicenseField(string value, string emptyLabel, string copiedLabel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Alert(
                "RunScore License",
                $"No {emptyLabel} on this laptop yet.\n\n"
                + "Add licenseName and licenseKey to OneDrive ._s2f/runscore-vendor-secrets.json, "
                + "then Sign In (or Refresh Offices) so this app can pull them.");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(value.Trim());
            Status = $"Copied RunScore {copiedLabel} — paste into Help → Register RunScore.";
        }
        catch (Exception ex)
        {
            Alert("RunScore License", "Could not copy to the clipboard:\n" + ex.Message);
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

    public async Task CreateNewEventAsync()
    {
        if (HasActive)
        {
            Alert("Already checked out", "Finish the current race before setting up a new event.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_newEventOffice))
        {
            Alert("New event", "Select an office.");
            return;
        }

        if (_newEventSource is null || string.IsNullOrWhiteSpace(_newEventSource.Path))
        {
            var hint = _newEventCopyFrom switch
            {
                NewEventCopyFrom.Templates =>
                    Path.Combine(_settings.LibraryRoot, EventBuilderService.TemplatesFolderName),
                NewEventCopyFrom.PastRaces =>
                    Path.Combine(_settings.LibraryRoot, EventBuilderService.PastRacesFolderName),
                _ => Path.Combine(_settings.LibraryRoot, _newEventOffice ?? "")
            };
            Alert(
                "New event",
                "Select a Template, past race, or current event to copy.\n\nLooking under:\n" + hint);
            return;
        }

        SetBusy("Creating event", "Downloading the source pack if needed, then copying…");
        EventBuildResult result;
        try
        {
            if (_cloud.IsAvailable)
            {
                ProgressText = "Downloading source from OneDrive…";
                var pull = await _cloud.DownloadSessionAsync(
                    _newEventSource.Path,
                    _settings.LibraryRoot,
                    _settings.GraphDriveRoot,
                    new Progress<string>(s => ProgressText = s)).ConfigureAwait(true);
                if (!pull.Success)
                    Status = pull.Message;
            }

            if (!SessionPackLooksUsable(_newEventSource.Path))
            {
                if (Busy && !BusyNeedsAcknowledge)
                    ClearBusy();
                Alert(
                    "New event",
                    "The source pack has no race files yet (often an empty Template placeholder).\n\n"
                    + "Sign in to OneDrive, wait for Templates to finish loading, then try again.\n\n"
                    + _newEventSource.Path);
                return;
            }

            ProgressText = "Building the new event folder in Library…";
            if (_newEventIsRaceGroup && string.IsNullOrWhiteSpace(_newEventParentFolder))
            {
                if (Busy && !BusyNeedsAcknowledge)
                    ClearBusy();
                Alert("Race Group", "Set the Race Group name before creating a race.");
                return;
            }

            result = await _eventBuilder.BuildAsync(new EventBuildRequest
            {
                LibraryRoot = _settings.LibraryRoot,
                Office = _newEventOffice,
                SourceSessionPath = _newEventSource.Path,
                RaceName = _newEventRaceName,
                RaceDate = NewEventRaceDate,
                ResultsId = _newEventResultsId,
                ParentFolderName = _newEventIsRaceGroup ? _newEventParentFolder : null
            }).ConfigureAwait(true);

            if (result.Success
                && _cloud.IsAvailable
                && !string.IsNullOrWhiteSpace(result.SessionPath))
            {
                // Publish RUNSCORE + DB's (seeded) so OneDrive gets the pack before checkout/Finish.
                ProgressText = "Publishing new event to OneDrive…";
                var publish = await _cloud.UploadSessionAsync(
                    result.SessionPath,
                    _settings.LibraryRoot,
                    _settings.GraphDriveRoot,
                    new Progress<string>(s => ProgressText = s)).ConfigureAwait(true);
                if (!publish.Success)
                    Status = "Event created locally; OneDrive publish incomplete: " + publish.Message;
            }
        }
        catch (Exception ex)
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
            Alert("New event", ex.Message);
            return;
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }

        if (!result.Success)
        {
            Alert("New event", result.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(result.SessionPath) || string.IsNullOrWhiteSpace(result.Office))
        {
            Alert("Event created", result.Message);
            ReloadFromDisk();
            return;
        }

        if (!SessionPackLooksUsable(result.SessionPath))
        {
            Alert(
                "New event",
                "Library folder was created but looks empty:\n"
                + result.SessionPath
                + "\n\nSource copy did not bring race files. Fix the Template / Past Race pack, then try again.");
            ReloadFromDisk();
            return;
        }

        SelectedOffice = result.Office;
        LoadSessions(result.Office);
        var relative = result.RelativePath
                       ?? result.SessionFolderName
                       ?? Path.GetFileName(result.SessionPath);
        var pack = Sessions.FirstOrDefault(s =>
            string.Equals(s.Path, result.SessionPath, StringComparison.OrdinalIgnoreCase));
        if (pack is null)
        {
            var eventFiles = EventFileSelector.ListEventFiles(result.SessionPath);
            var leaf = result.SessionFolderName ?? Path.GetFileName(result.SessionPath);
            var parent = result.ParentFolderName ?? leaf;
            pack = new EventPackInfo
            {
                Office = result.Office,
                ParentEvent = parent,
                SessionName = leaf,
                RelativePath = relative,
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

        // Race Group: land on an explicit success step (checkout vs add another).
        if (_newEventIsRaceGroup)
        {
            _lastCreatedRaceName = pack.SessionName;
            Status = result.MissingEventFile
                ? result.Message
                : $"Created {relative}.";
            _adminNewEventSubStep = AdminNewEventSubStep.GroupCreated;
            UpdateNewEventPreview();
            NotifyAdminSection();
            OnPropertyChanged(nameof(NewEventGroupCreatedHeadline));
            OnPropertyChanged(nameof(NewEventGroupCreatedDetail));
            OnPropertyChanged(nameof(NewEventCheckoutLastLabel));
            return;
        }

        Status = result.MissingEventFile
            ? result.Message
            : "New event checked out: " + relative;

        // Local Library pack is the source of truth — do not Graph-pull a session that isn't on OneDrive yet.
        _sessionPackPulled = true;
        await CheckoutAfterNewEventAsync(result).ConfigureAwait(true);
    }

    /// <summary>From the Race Group success step — copy another leaf under the same group.</summary>
    public void AddAnotherRaceInGroup()
    {
        if (!IsAdminNewEventGroupCreated || string.IsNullOrWhiteSpace(_newEventParentFolder))
            return;

        PrepareAddAnotherRaceInGroup();
    }

    /// <summary>Clear leaf fields and open Copy From for the next race under the same Race Group.</summary>
    private void PrepareAddAnotherRaceInGroup()
    {
        _newEventSource = null;
        _newEventPastRelative = string.Empty;
        _newEventRaceName = string.Empty;
        _newEventResultsId = string.Empty;
        _newEventRaceDateValue = DateTime.Today;
        NewEventSources.Clear();
        _newEventSourceSource.Clear();
        OnPropertyChanged(nameof(NewEventSource));
        OnPropertyChanged(nameof(NewEventRaceName));
        OnPropertyChanged(nameof(NewEventRaceDateValue));
        OnPropertyChanged(nameof(NewEventRaceDate));
        OnPropertyChanged(nameof(NewEventResultsId));
        ClearAdminListFilter();
        _adminNewEventSubStep = AdminNewEventSubStep.SourceKind;
        Status = string.IsNullOrWhiteSpace(_newEventParentFolder)
            ? "Add another race under this Race Group."
            : $"Add another race under {_newEventParentFolder}.";
        UpdateNewEventPreview();
        NotifyAdminSection();
    }

    /// <summary>
    /// Admin → Edit Current Event: Graph-move the flat race or whole Race Group into Past Races.
    /// </summary>
    public async Task MoveAdminSessionToPastRacesAsync()
    {
        if (!ShowMoveToPastRaces || _adminSession is null)
            return;

        if (!_cloud.IsAvailable)
        {
            Alert("Move to Past Races", "Sign in to OneDrive first (Admin → Settings).");
            return;
        }

        var office = _adminSession.Office;
        var itemName = PastRacesArchivePaths.ResolveArchiveItemName(_adminSession);
        var year = PastRacesArchivePaths.ResolveArchiveYear(itemName);
        var destRel = PastRacesArchivePaths.DestinationItemRelative(office, year, itemName)
            .Replace('/', Path.DirectorySeparatorChar);

        if (PastRacesArchivePaths.IsLibraryPathUnderArchiveItem(
                _settings.LibraryRoot, office, itemName, Active?.LibrarySessionPath))
        {
            Alert(
                "Move to Past Races",
                "This race (or a race in this Race Group) is checked out on this laptop.\n\n"
                + "Finish first, then move it to Past Races.");
            return;
        }

        var what = IsAdminSessionRaceGroupLeaf
            ? $"the whole Race Group “{itemName}” (all races in the group)"
            : $"“{itemName}”";

        if (!TimerDialog.Confirm(
                OwnerWindow,
                "Move to Past Races",
                $"Move {what} out of {office} into:\n\n{destRel}\n\n"
                + "This updates OneDrive. Staff will no longer see it under the live office list.",
                yesLabel: "Move to Past Races",
                noLabel: "Cancel"))
            return;

        SetBusy("Moving to Past Races", $"Moving {itemName}…");
        try
        {
            var result = await _cloud.MoveOfficeItemToPastRacesAsync(
                    _settings.LibraryRoot,
                    _settings.GraphDriveRoot,
                    office,
                    itemName,
                    new Progress<string>(s => Status = s))
                .ConfigureAwait(true);

            Status = result.Message;
            if (!result.Success)
            {
                Alert("Move to Past Races", result.Message);
                return;
            }

            // Move already refreshed the live office on Graph. Invalidate Past Races browse cache only.
            _graphIndexedOffices.Add(office);
            _graphIndexedOffices.RemoveWhere(k =>
                k.StartsWith(EventBuilderService.PastRacesFolderName + ":", StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, EventBuilderService.PastRacesFolderName, StringComparison.OrdinalIgnoreCase));

            try
            {
                var mb = new ManualBackupStore();
                mb.RemoveOfficeCatalogSessionsUnder(
                    _settings.LibraryRoot,
                    office,
                    _adminSession.RelativePath);
                var catalogPath = ManualBackupStore.GetOfficeCatalogPath(_settings.LibraryRoot, office);
                if (File.Exists(catalogPath) && _cloud.IsAvailable)
                {
                    await _cloud.UploadLocalFilesAsync(
                        new[] { catalogPath },
                        _settings.LibraryRoot,
                        _settings.GraphDriveRoot,
                        CancellationToken.None).ConfigureAwait(true);
                }
            }
            catch { /* soft — Past Races move already succeeded */ }

            Alert("Move to Past Races", result.Message);

            ResetAdminAssignUi();
            _adminEventsSubStep = AdminEventsSubStep.Session;
            _adminSessionSource.Clear();
            foreach (var session in _scanner.ListSessions(_settings.LibraryRoot, office))
                _adminSessionSource.Add(session);
            ApplyAdminListFilter();
            NotifyAdminSection();
        }
        catch (Exception ex)
        {
            Alert("Move to Past Races", ex.Message);
        }
        finally
        {
            if (Busy && !BusyNeedsAcknowledge)
                ClearBusy();
        }
    }

    /// <summary>
    /// Edit Current Event → add a new leaf under the selected Race Group (skips layout / naming).
    /// </summary>
    public void BeginAddRaceToExistingGroup()
    {
        if (!ShowAddRaceToGroup || _adminSession is null)
            return;

        var office = _adminSession.Office;
        var parent = _adminSession.ParentEvent;

        _adminSection = AdminSection.SetupNewEvent;
        EnsureLocalRoots();
        LoadOffices();

        _newEventIsRaceGroup = true;
        _newEventOffice = office;
        _newEventParentFolder = parent;
        _newEventParentName = parent;
        _lastCreatedRaceName = string.Empty;
        SelectedSession = null;
        OnPropertyChanged(nameof(IsNewEventRaceGroup));
        OnPropertyChanged(nameof(NewEventOffice));

        _newEventOfficeSource.Clear();
        foreach (var o in EventBuilderService.DestinationOffices)
            _newEventOfficeSource.Add(o);

        PrepareAddAnotherRaceInGroup();
        Status = $"Adding a race under {_newEventOffice} / {_newEventParentFolder}.";
    }

    /// <summary>Checkout the race just created under a Race Group.</summary>
    public async Task CheckoutSelectedNewEventAsync()
    {
        if (SelectedSession is null || string.IsNullOrWhiteSpace(SelectedSession.Path))
        {
            Alert("Checkout", "Create a race in this group first.");
            return;
        }

        if (HasActive)
        {
            Alert("Already checked out", "Finish the current race before checking out another.");
            return;
        }

        _sessionPackPulled = true;
        _checkoutRole = CheckoutRole.Timing;
        _finishAdminReturn = FinishAdminReturn.SetupNewEvent;
        OnPropertyChanged(nameof(IsTimingRole));
        OnPropertyChanged(nameof(IsAnnouncerRole));

        var missingEvent = EventFileSelector.ListEventFiles(SelectedSession.Path).Count == 0;
        if (missingEvent)
        {
            var activePath = _paths.GetActivePath(
                _settings, SelectedSession.Office, SelectedSession.RelativePath);
            var suggested = VendorLauncher.SuggestEventFileNameFromRaceName(SelectedSession.SessionName);
            if (!TimerDialog.ConfirmSaveAsGuide(
                    OwnerWindow,
                    "No T&S .event yet",
                    VendorLauncher.FormatSaveAsFolderGuide(activePath, suggested)))
            {
                _finishAdminReturn = FinishAdminReturn.None;
                Status = "Checkout cancelled — you can still add another race under the Race Group.";
                return;
            }
        }

        await CheckoutAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// True when the session has more than an empty RUNSCORE placeholder (Graph probe-only).
    /// Blank Entries.ini alone does not count — Build can invent that after copying nothing.
    /// </summary>
    private static bool SessionPackLooksUsable(string sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath) || !Directory.Exists(sessionPath))
            return false;

        if (EventFileSelector.ListEventFiles(sessionPath).Count > 0)
            return true;

        var runScore = Path.Combine(sessionPath, PathService.RunScoreFolderName);
        if (!Directory.Exists(runScore))
            return false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(runScore, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("Entries.ini", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (new FileInfo(file).Length > 32)
                            return true;
                    }
                    catch
                    {
                        // treat as empty
                    }

                    continue;
                }

                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
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

        _checkoutRole = CheckoutRole.Timing;
        _finishAdminReturn = FinishAdminReturn.SetupNewEvent;
        OnPropertyChanged(nameof(IsTimingRole));
        OnPropertyChanged(nameof(IsAnnouncerRole));
        await CheckoutAsync();
    }

    private void LoadOffices()
    {
        Offices.Clear();
        foreach (var office in _scanner.ListOffices(_settings.LibraryRoot))
            Offices.Add(office.Name);
        ApplyRaceDayListFilter();
    }

    private void LoadSessions(string office)
    {
        Sessions.Clear();
        foreach (var session in _scanner.ListSessions(_settings.LibraryRoot, office))
        {
            EnrichWithSecondaries(session);
            if (IsAnnouncerRole && session.SecondaryEventFiles.Count == 0)
                continue;
            Sessions.Add(session);
        }
        ApplyRaceDayListFilter();
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowEmptySessions));
        OnPropertyChanged(nameof(EmptySessionsTitle));
        OnPropertyChanged(nameof(EmptySessionsBody));
    }

    private void ApplyRaceDayListFilter()
    {
        var q = _raceDayListFilter.Trim();

        VisibleOffices.Clear();
        foreach (var name in Offices)
        {
            if (q.Length == 0 || name.Contains(q, StringComparison.OrdinalIgnoreCase))
                VisibleOffices.Add(name);
        }

        var matched = new List<EventPackInfo>();
        foreach (var entry in Sessions)
        {
            if (q.Length == 0
                || entry.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || entry.SessionName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (entry.ParentEvent?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (entry.PrimaryEventFile?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (entry.SecondaryEventSummary?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                matched.Add(entry);
        }

        // Timing: Folders → Available → Checked Out (in-use races stay out of the way).
        if (ShowSessionLock)
            matched.Sort(CompareSessionListOrder);

        VisibleSessions.Clear();
        foreach (var entry in matched)
            VisibleSessions.Add(entry);

        ConfigureSessionListGrouping();
    }

    private static int CompareSessionListOrder(EventPackInfo a, EventPackInfo b)
    {
        var rank = SessionSectionRank(a).CompareTo(SessionSectionRank(b));
        if (rank != 0)
            return rank;
        return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static int SessionSectionRank(EventPackInfo entry) =>
        entry.IsNavigationFolder ? 0 : entry.IsActivelyCheckedOut ? 2 : 1;

    private void ConfigureSessionListGrouping()
    {
        SessionListView.GroupDescriptions.Clear();
        if (ShowSessionLock)
        {
            SessionListView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(EventPackInfo.ListSection)));
        }

        SessionListView.Refresh();
    }

    private void ClearRaceDayListFilter()
    {
        if (_raceDayListFilter.Length == 0)
        {
            ApplyRaceDayListFilter();
            return;
        }

        _raceDayListFilter = string.Empty;
        OnPropertyChanged(nameof(RaceDayListFilter));
        ApplyRaceDayListFilter();
    }

    /// <summary>Clear this-run Graph cache for the current office / Past Races folder and reload.</summary>
    public async Task RetryOfficeSessionsAsync()
    {
        if (HasActive || string.IsNullOrWhiteSpace(SelectedOffice))
            return;

        if (IsPastRacesBrowse)
        {
            _graphIndexedOffices.Remove(PastRacesIndexKey);
            _graphIndexedOffices.Remove(EventNamesCacheKey(PastRacesIndexKey));
            _probedEventNames.Clear();
            await RefreshPastRacesLevelAsync(force: true).ConfigureAwait(true);
            await PreparePastRacesListAsync().ConfigureAwait(true);
            return;
        }

        _graphIndexedOffices.Remove(SelectedOffice);
        _graphIndexedOffices.Remove(EventNamesCacheKey(SelectedOffice));
        _probedEventNames.Clear();
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

    /// <summary>
    /// Refresh the cloud lock when signed in.
    /// Upload only when this machine still holds Active.
    /// Show a takeover only when the downloaded lock is Active for another machine.
    /// A failed download does not upload and does not claim another laptop took the race.
    /// </summary>
    private async Task<LockCloudVerdict> CheckLockOwnershipAsync(
        CancellationToken cancellationToken = default,
        string caller = "check")
    {
        if (_cloudWritesBlockedUntilResolved)
        {
            RaceDayTrace.Write($"lock own-check caller={caller} result=taken-over reason=writes-blocked");
            return LockCloudVerdict.TakenOver;
        }

        var active = _activeStore.Load();
        // Timing lock is Main Timing only — Announcer / Manual Backup never own or reclaim it.
        if (active is null || active.Role is CheckoutRole.Announcer or CheckoutRole.ManualBackup)
            return LockCloudVerdict.Held;

        var sessionPath = active.LibrarySessionPath;
        var lockPath = _paths.GetLockPath(sessionPath);
        var heldBefore = _locks.ThisMachineHoldsActiveLock(sessionPath);
        var before = RaceDayTrace.FormatLock(_locks.TryRead(sessionPath));
        byte[]? snapshot = null;
        if (heldBefore && File.Exists(lockPath))
        {
            try { snapshot = File.ReadAllBytes(lockPath); }
            catch (Exception ex)
            {
                RaceDayTrace.Write($"lock snapshot failed path={lockPath} error={ex.GetType().Name}: {ex.Message}");
            }
        }

        var downloadConfirmed = !_cloud.IsAvailable;
        if (_cloud.IsAvailable)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(GraphSyncPolicy.HttpTimeout);
                var pull = await _cloud.DownloadCheckoutLockAsync(
                    sessionPath,
                    _settings.LibraryRoot,
                    _settings.GraphDriveRoot,
                    cts.Token).ConfigureAwait(false);
                downloadConfirmed = pull.Success;
                if (!pull.Success)
                    LogOwnCheck(caller, LockCloudVerdict.Unconfirmed, "graph-download-failed", sessionPath, before, pull.Message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                LogOwnCheck(caller, LockCloudVerdict.Unconfirmed, "graph-download-timeout", sessionPath, before, "HttpTimeout");
                return LockCloudVerdict.Unconfirmed;
            }
            catch (Exception ex)
            {
                LogOwnCheck(caller, LockCloudVerdict.Unconfirmed, "graph-download-exception", sessionPath, before, ex.GetType().Name + ": " + ex.Message);
                return LockCloudVerdict.Unconfirmed;
            }

            if (!downloadConfirmed)
                return LockCloudVerdict.Unconfirmed;
        }

        var afterLock = _locks.TryRead(sessionPath);
        var hasLocalTimingActive = active.Role == CheckoutRole.Timing
            && !string.IsNullOrWhiteSpace(active.LibrarySessionPath);
        var decision = LockOwnershipPolicy.Decide(
            downloadConfirmed,
            afterLock,
            _machine.MachineId,
            heldBefore && snapshot is not null,
            hasLocalTimingActive: hasLocalTimingActive);

        if (decision.RestoreLocalActive)
        {
            if (snapshot is null || !_locks.TryReplaceLockFile(sessionPath, snapshot))
            {
                LogOwnCheck(caller, LockCloudVerdict.Unconfirmed, "restore-failed", sessionPath, before, "cloud copy was not another laptop");
                return LockCloudVerdict.Unconfirmed;
            }

            LogOwnCheck(caller, LockCloudVerdict.Held, "restored-local", sessionPath, before, "cloud copy was not another laptop");
            return LockCloudVerdict.Held;
        }

        if (decision.ReclaimFromActiveCheckout)
        {
            try
            {
                _locks.Acquire(
                    active.LibrarySessionPath,
                    active.Office,
                    active.ParentEvent,
                    active.SessionName,
                    string.IsNullOrWhiteSpace(DisplayName) ? _machine.MachineName : DisplayName.Trim(),
                    active.ScopedPaths,
                    string.IsNullOrWhiteSpace(active.PrimaryEventFile) ? null : active.PrimaryEventFile,
                    reclaimReason: "orphan-active-after-released-lock",
                    emergency: false);

                // Publish Active again — cloud still has Released after a kill mid-Finish.
                if (_cloud.IsAvailable)
                {
                    try
                    {
                        using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        uploadCts.CancelAfter(GraphSyncPolicy.HttpTimeout);
                        await _cloud.UploadCheckoutLockAsync(
                            sessionPath,
                            _settings.LibraryRoot,
                            _settings.GraphDriveRoot,
                            uploadCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Local reclaim is enough for Finish on this laptop; heartbeat can retry publish.
                    }
                }

                LogOwnCheck(caller, LockCloudVerdict.Held, "reclaimed-orphan-active", sessionPath, before, "active.json present; lock was Released");
                return LockCloudVerdict.Held;
            }
            catch (Exception ex)
            {
                LogOwnCheck(
                    caller,
                    LockCloudVerdict.Unconfirmed,
                    "reclaim-failed",
                    sessionPath,
                    before,
                    ex.GetType().Name + ": " + ex.Message);
                return LockCloudVerdict.Unconfirmed;
            }
        }

        var reason = decision.Verdict switch
        {
            LockCloudVerdict.Held => "held",
            LockCloudVerdict.TakenOver => "taken-over",
            _ => "not-active"
        };
        LogOwnCheck(
            caller,
            decision.Verdict,
            reason,
            sessionPath,
            before,
            _cloud.IsAvailable ? "downloaded" : "graph-unavailable");
        return decision.Verdict;
    }

    private void LogOwnCheck(
        string caller,
        LockCloudVerdict verdict,
        string reason,
        string sessionPath,
        string before,
        string detail)
    {
        if (verdict == LockCloudVerdict.Held && reason == "held")
            return;

        var after = RaceDayTrace.FormatLock(_locks.TryRead(sessionPath));
        RaceDayTrace.Write(
            $"lock own-check caller={caller} result={verdict} reason={reason} " +
            $"ours={_machine.MachineId} oursMachine={_machine.MachineName} oursUser={_machine.WindowsUser} " +
            $"before=[{before}] after=[{after}] detail={detail}");
    }

    private async Task HeartbeatAsync()
    {
        if (Volatile.Read(ref _suppressLockPublish) > 0)
            return;

        // One heartbeat at a time, and Finish waits on this same gate so a lock
        // download/PUT cannot overlap the Released upload.
        if (!await _heartbeatUploadGate.WaitAsync(0).ConfigureAwait(true))
            return;

        var promptTakenOver = false;
        try
        {
            if (Volatile.Read(ref _suppressLockPublish) > 0)
                return;

            var active = _activeStore.Load();
            // Manual Backup must not heartbeat / reclaim Timing locks (that greys the race as Checked Out).
            if (active is null || active.Role is CheckoutRole.Announcer or CheckoutRole.ManualBackup)
                return;

            var ct = _heartbeatCts.Token;
            switch (await CheckLockOwnershipAsync(ct, caller: "heartbeat").ConfigureAwait(true))
            {
                case LockCloudVerdict.TakenOver:
                    if (IsLockTakenOverSnoozed)
                    {
                        RaceDayTrace.Write("lock takeover prompt snoozed — banner on Active");
                        return;
                    }

                    // Prompt after the gate is released — the dialog pumps the UI,
                    // and Finish must be able to wait out this tick. Do not return
                    // here: return would skip the prompt that follows this try.
                    promptTakenOver = true;
                    break;
                case LockCloudVerdict.Unconfirmed:
                    RaceDayTrace.Write("lock heartbeat upload skipped reason=unconfirmed");
                    return;
            }

            if (!promptTakenOver
                && Volatile.Read(ref _suppressLockPublish) == 0
                && !ct.IsCancellationRequested)
            {
                try { _locks.Heartbeat(active.LibrarySessionPath); }
                catch (Exception ex)
                {
                    RaceDayTrace.Write($"lock heartbeat exception error={ex.GetType().Name}: {ex.Message}");
                }

                if (Volatile.Read(ref _suppressLockPublish) > 0 || ct.IsCancellationRequested)
                    return;

                if (_cloud.IsAvailable)
                {
                    // Tiny lock JSON only — never UploadFolderAsync(._checkin) (history + backup trees).
                    var lockPath = _paths.GetLockPath(active.LibrarySessionPath);
                    if (!File.Exists(lockPath))
                    {
                        RaceDayTrace.Write($"lock heartbeat upload skipped reason=missing-file path={lockPath}");
                    }
                    else if (!_locks.ThisMachineHoldsActiveLock(active.LibrarySessionPath))
                    {
                        // Re-check after local heartbeat write — never upload if we no longer own.
                        RaceDayTrace.Write("lock heartbeat upload skipped reason=lost-ownership-after-write");
                    }
                    else
                    {
                        var sessionPath = active.LibrarySessionPath;
                        var libraryRoot = _settings.LibraryRoot;
                        var driveRoot = _settings.GraphDriveRoot;
                        using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        uploadCts.CancelAfter(GraphSyncPolicy.HttpTimeout);
                        await _cloud.UploadCheckoutLockAsync(sessionPath, libraryRoot, driveRoot, uploadCts.Token)
                            .ConfigureAwait(true);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            RaceDayTrace.Write("lock heartbeat canceled");
        }
        catch (Exception ex)
        {
            RaceDayTrace.Write($"lock heartbeat exception error={ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _heartbeatUploadGate.Release();
        }

        if (promptTakenOver && Volatile.Read(ref _suppressLockPublish) == 0)
        {
            RaceDayTrace.Write("lock takeover prompt from heartbeat");
            await HandleLockTakenOverAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Stop lock heartbeats and wait out any in-flight download/PUT before Finish
    /// writes Released. Holds the gate until <see cref="ResumeHeartbeatAfterFinish"/>.
    /// </summary>
    private async Task PauseHeartbeatForFinishAsync()
    {
        _heartbeatTimer.Stop();
        Interlocked.Increment(ref _suppressLockPublish);
        try { _heartbeatCts.Cancel(); }
        catch (ObjectDisposedException) { }

        _heartbeatGateHeldForFinish = await _heartbeatUploadGate
            .WaitAsync(TimeSpan.FromSeconds(40))
            .ConfigureAwait(true);
        if (!_heartbeatGateHeldForFinish)
            RaceDayTrace.Write("lock heartbeat drain timed out before finish release");
    }

    private void ResumeHeartbeatAfterFinish()
    {
        var held = _heartbeatGateHeldForFinish;
        _heartbeatGateHeldForFinish = false;
        if (held)
        {
            try { _heartbeatUploadGate.Release(); }
            catch (SemaphoreFullException) { }
        }

        if (Volatile.Read(ref _suppressLockPublish) > 0)
            Interlocked.Decrement(ref _suppressLockPublish);

        // Fresh token for the next tick. Dispose the canceled one only when no
        // heartbeat is still inside the gate (timeout means it might be).
        var previous = _heartbeatCts;
        _heartbeatCts = new CancellationTokenSource();
        if (held)
        {
            try { previous.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        if (!_heartbeatTimer.IsEnabled)
            _heartbeatTimer.Start();
    }

    private async Task HandleLockTakenOverAsync()
    {
        if (_lockTakenOverPromptOpen)
        {
            RaceDayTrace.Write("lock takeover prompt skipped reason=already-open");
            return;
        }

        var active = Active ?? _activeStore.Load();
        if (active is null || active.Role is CheckoutRole.Announcer or CheckoutRole.ManualBackup)
        {
            RaceDayTrace.Write("lock takeover prompt skipped reason=no-timing-active");
            return;
        }

        SetCloudWritesBlocked(true);
        _mirror.Pause();

        var other = _locks.TryGetOtherMachineActiveLock(active.LibrarySessionPath);
        RaceDayTrace.Write(
            $"lock takeover dialog ours={_machine.MachineId} oursMachine={_machine.MachineName} oursUser={_machine.WindowsUser} " +
            $"local={RaceDayTrace.FormatLock(_locks.TryRead(active.LibrarySessionPath))} " +
            $"other={(other is null ? "none" : RaceDayTrace.FormatLock(other))}");
        _lockTakenOverPromptOpen = true;
        try
        {
            var who = other is null
                ? "Another timer"
                : EventPackInfo.FormatLockWho(other);
            var blocking = _processGuard.GetBlockingProcessNames();
            var choice = TimerDialog.AskLockTakenOver(
                OwnerWindow,
                who,
                closeRunScore: blocking.Any(ProcessGuard.IsRunScoreProcess)
                    ? () => _processGuard.TryCloseRunScoreForAbandonedCheckout().Message
                    : null,
                closeTimingAndScoring: blocking.Any(ProcessGuard.IsTimingAndScoringProcess)
                    ? () => _processGuard.TryCloseTimingAndScoringForAbandonedCheckout().Message
                    : null);
            RaceDayTrace.Write($"lock takeover choice={choice}");

            if (choice == TimerLockTakenOverChoice.CloseLocalCopy)
            {
                ClearLockTakenOverSnooze();
                await CloseLocalCopyAfterLockTakenAsync().ConfigureAwait(true);
                return;
            }

            // Not Now / Dismissed — free the UI; banner keeps Close Local Copy (no Force here).
            SnoozeLockTakenOverPrompt();
            Status = "Close RunScore and Timing & Scoring, then tap Close Local Copy.";
        }
        finally
        {
            _lockTakenOverPromptOpen = false;
        }
    }

    private bool IsLockTakenOverSnoozed =>
        _lockTakenOverSnoozeUntil is { } until && until > DateTimeOffset.UtcNow;

    private void SnoozeLockTakenOverPrompt()
    {
        // Long enough to close vendors and use the banner; heartbeat will not steal focus.
        _lockTakenOverSnoozeUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        OnPropertyChanged(nameof(ShowLockTakenOverBanner));
    }

    private void ClearLockTakenOverSnooze()
    {
        _lockTakenOverSnoozeUntil = null;
        OnPropertyChanged(nameof(ShowLockTakenOverBanner));
    }

    private void SetCloudWritesBlocked(bool blocked)
    {
        _cloudWritesBlockedUntilResolved = blocked;
        OnPropertyChanged(nameof(ShowLockTakenOverBanner));
    }

    public void CloseRunScoreFromBanner()
    {
        if (!_cloudWritesBlockedUntilResolved)
            return;
        Status = _processGuard.TryCloseRunScoreForAbandonedCheckout().Message;
    }

    public void CloseTimingAndScoringFromBanner()
    {
        if (!_cloudWritesBlockedUntilResolved)
            return;
        Status = _processGuard.TryCloseTimingAndScoringForAbandonedCheckout().Message;
    }

    /// <summary>Active banner — Close Local Copy after the timer closed RunScore / T&amp;S.</summary>
    public async Task CloseLocalCopyFromBannerAsync()
    {
        if (!_cloudWritesBlockedUntilResolved || Active is null)
            return;
        ClearLockTakenOverSnooze();
        await CloseLocalCopyAfterLockTakenAsync().ConfigureAwait(true);
    }

    private async Task CloseLocalCopyAfterLockTakenAsync()
    {
        // Mirror already paused on takeover — drain any in-flight copy so Active unlocks.
        _mirror.Pause();
        try { await _mirror.DrainAsync().ConfigureAwait(true); }
        catch { /* best-effort */ }

        var result = _checkout.AbandonLocalActive(requireVendorsClosed: true);
        if (!result.Success && result.BlockingProcesses.Count > 0)
        {
            // RSServer often lingers briefly after the window closes — wait before prompting.
            ProgressText = "Waiting for RunScore / Timing & Scoring to exit…";
            var wait = await _processGuard.WaitForBlockingClearedAsync(
                TimeSpan.FromSeconds(12),
                new Progress<string>(s => ProgressText = s)).ConfigureAwait(true);
            if (wait.Kind == BlockingWaitKind.Cleared)
            {
                result = _checkout.AbandonLocalActive(requireVendorsClosed: true);
            }
            else if (wait.Kind == BlockingWaitKind.StillRunning
                     && await OfferEndOrphanedRunScoreAfterWaitAsync(wait.ProcessNames, purpose: "Close Local Copy")
                         .ConfigureAwait(true))
            {
                result = _checkout.AbandonLocalActive(requireVendorsClosed: true);
            }
            else if (!TimerDialog.Confirm(
                    OwnerWindow,
                    "Vendors Still Open",
                    result.Message + "\n\nClose them, then Close Local Copy again — or clear Active anyway (you must close RunScore / Timing & Scoring yourself).",
                    yesLabel: "Clear Active Anyway",
                    noLabel: "Cancel"))
            {
                // Stay blocked — next heartbeat re-prompts.
                return;
            }
            else
            {
                result = _checkout.AbandonLocalActive(requireVendorsClosed: false);
            }
        }

        if (!result.Success)
        {
            Alert("Close Local Copy", result.Message);
            return;
        }

        Active = null;
        SelectedSession = null;
        SelectedEventFile = null;
        _finishAdminReturn = FinishAdminReturn.None;
        ClearActiveSaveAsGuidePin();
        SetCloudWritesBlocked(false);
        ClearLockTakenOverSnooze();
        _mirror.Resume();
        LoadOffices();
        if (Offices.Count == 1)
            await SelectOfficeAsync(Offices[0]).ConfigureAwait(true);
        else
            Step = WizardStep.Office;
        Status = result.Message;
    }

    /// <summary>
    /// After a normal wait with no RunScore window: offer to end headless RSServer only.
    /// Never auto-kills — timer must confirm. Refuses if Timing &amp; Scoring is in the list.
    /// </summary>
    /// <param name="purpose">Title Case action label: Finish, Close Local Copy, or Take Over.</param>
    /// <returns>True when leftovers were cleared (or ended successfully).</returns>
    private async Task<bool> OfferEndOrphanedRunScoreAfterWaitAsync(
        IReadOnlyList<string> processNames,
        string purpose)
    {
        var names = string.Join(", ", processNames);
        if (!ProcessGuard.AreOnlyRunScoreProcesses(processNames))
        {
            await ShowBusyResultAsync(
                "Still open",
                "The app window is closed, but a background process is still running.\n\n"
                + $"Still detected: {names}\n\n"
                + "Close Timing & Scoring (or other timing apps), wait a few seconds, then try again.")
                .ConfigureAwait(true);
            return false;
        }

        ClearBusy();
        var end = TimerDialog.Confirm(
            OwnerWindow,
            "Leftover RSServer",
            "RunScore's window is closed, but a background process is still running ("
            + names + ").\n\n"
            + "We already waited for a normal shutdown. You can end the leftover RSServer so "
            + purpose
            + " can continue — only if you are sure RunScore is closed.\n\n"
            + "Ending RSServer unlocks race files on this laptop.",
            yesLabel: "End RSServer",
            noLabel: "Cancel " + purpose,
            yesIsDanger: true);

        if (!end)
        {
            if (string.Equals(purpose, "Finish", StringComparison.Ordinal))
            {
                await ShowBusyResultAsync(
                    "Finish Paused",
                    "Finish was cancelled while RSServer was still running in the background.\n\n"
                    + "Tap Finish again when RunScore has fully exited, or choose End RSServer when offered.")
                    .ConfigureAwait(true);
            }

            return false;
        }

        SetBusy(
            purpose == "Finish" ? "Saving your race" : purpose,
            "Ending leftover RSServer…");
        var kill = await Task.Run(() => _processGuard.TryEndHeadlessRunScoreProcesses())
            .ConfigureAwait(true);
        if (!kill.Success)
        {
            await ShowBusyResultAsync("Could Not End RSServer", kill.Message).ConfigureAwait(true);
            return false;
        }

        Status = kill.Message;
        return true;
    }

    /// <summary>
    /// Soft/force/same-machine reclaim overlays Graph <c>_autosave</c> onto Active RUNSCORE.
    /// RSServer often locks Entries.DTA — gate like Finish so the overlay can write.
    /// </summary>
    /// <returns>False when the timer cancelled or vendors are still open.</returns>
    private async Task<bool> EnsureVendorsClosedForLiveOverlayAsync(string busyTitle)
    {
        var blocking = _processGuard.GetBlockingProcessNames();
        if (blocking.Count == 0)
            return true;

        ClearBusy();
        // Full-screen dim blocks Alt-Tab to RS — close vendors from this overlay (same as lost-lock).
        if (!TimerDialog.ConfirmCloseVendorsForTakeover(
                OwnerWindow,
                blocking,
                closeRunScore: () => _processGuard.TryCloseRunScoreForAbandonedCheckout().Message,
                closeTimingAndScoring: () => _processGuard.TryCloseTimingAndScoringForAbandonedCheckout().Message,
                refreshStillOpen: () => _processGuard.GetBlockingProcessNames()))
            return false;

        SetBusy(busyTitle, "Waiting for RunScore to exit so live files can be applied…");
        var wait = await _processGuard.WaitForBlockingClearedAsync(
                TimeSpan.FromSeconds(25),
                new Progress<string>(s => ProgressText = s))
            .ConfigureAwait(true);

        if (wait.Kind == BlockingWaitKind.WindowStillOpen)
        {
            var still = string.Join(", ", wait.ProcessNames);
            await ShowBusyResultAsync(
                    "Still open",
                    "Close RunScore (and Timing & Scoring) first, then try Take Over again.\n\n"
                    + $"Still open: {still}")
                .ConfigureAwait(true);
            return false;
        }

        if (wait.Kind == BlockingWaitKind.StillRunning)
        {
            if (!await OfferEndOrphanedRunScoreAfterWaitAsync(wait.ProcessNames, purpose: "Take Over")
                    .ConfigureAwait(true))
                return false;

            wait = await _processGuard.WaitForBlockingClearedAsync(
                    TimeSpan.FromSeconds(8),
                    new Progress<string>(s => ProgressText = s))
                .ConfigureAwait(true);
            if (wait.Kind != BlockingWaitKind.Cleared)
            {
                var still = string.Join(", ", wait.ProcessNames);
                await ShowBusyResultAsync(
                        "Still open",
                        "A timing process is still running"
                        + $" ({still}).\n\nClose it (or End RSServer if offered), then try Take Over again.")
                    .ConfigureAwait(true);
                return false;
            }
        }

        SetBusy(busyTitle, "Getting your race ready…");
        return true;
    }

    /// <summary>
    /// Run one mid-race mirror tick immediately (do not wait for the timer).
    /// Timing: Backup → Library \_autosave → Graph (failover). Announcer: Graph DB's → Active (T&amp;S CSV).
    /// </summary>
    private void KickAutosaveMirror()
    {
        if (Active is null)
            return;
        if (Active.Role == CheckoutRole.Timing && _cloudWritesBlockedUntilResolved)
            return;

        _mirror.Resume();
        _ = Task.Run(async () =>
        {
            try { await _mirror.MirrorOnceAsync().ConfigureAwait(false); }
            catch { /* next timer tick retries */ }
        });
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
