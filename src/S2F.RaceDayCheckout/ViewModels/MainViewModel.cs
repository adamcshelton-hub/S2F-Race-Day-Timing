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
    private readonly DispatcherTimer _heartbeatTimer;
    private readonly DispatcherTimer _mirrorTimer;

    private AppSettings _settings;
    private WizardStep _step = WizardStep.Office;
    private string _status = string.Empty;
    private string _progressText = string.Empty;
    private string? _selectedOffice;
    private EventPackInfo? _selectedSession;
    private string? _selectedEventFile;
    private ActiveCheckout? _active;
    private bool _busy;
    private bool _busyNeedsAcknowledge;
    private string _busyTitle = string.Empty;
    private TaskCompletionSource? _busyAck;

    public MainViewModel()
    {
        _locks = new CheckoutLockService(_paths, _machine);
        _scanner = new LibraryScanner(_paths, _locks);
        _checkout = new CheckoutService(_paths, _locks, _copy, _processGuard, _activeStore);
        _mirror = new AutosaveMirrorService(_copy, _activeStore);
        _settings = _settingsStore.Load();

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
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(StepSubtitle));
                OnPropertyChanged(nameof(ShowBack));
            }
        }
    }

    public bool IsOfficeStep => Step == WizardStep.Office;
    public bool IsSessionStep => Step == WizardStep.Session;
    public bool IsEventFileStep => Step == WizardStep.EventFile;
    public bool IsActiveStep => Step == WizardStep.Active;
    public bool IsAdminStep => Step == WizardStep.Admin;

    public string StepTitle => Step switch
    {
        WizardStep.Office => "Choose your office",
        WizardStep.Session => "Choose your event",
        WizardStep.EventFile => "Which timing file?",
        // Event name is the hero — don't repeat it in the card below.
        WizardStep.Active => Active?.DisplayName ?? "Ready for race day",
        WizardStep.Admin => "Admin settings",
        _ => "S2F Race Day"
    };

    public string StepSubtitle => Step switch
    {
        WizardStep.Office => "Select the office for today's race.",
        WizardStep.Session => SelectedOffice is null ? "" : $"Office: {SelectedOffice}",
        WizardStep.EventFile => SelectedSession is null ? "" : SelectedSession.DisplayName,
        WizardStep.Active => Active is null
            ? ""
            : $"{Active.Office} · Open RunScore and Timing & Scoring to start",
        WizardStep.Admin => "For setup staff only — not needed on race morning.",
        _ => ""
    };

    public bool ShowBack => Step is WizardStep.Session or WizardStep.EventFile or WizardStep.Admin;

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
        if (technical.Contains("Downloading from OneDrive", StringComparison.OrdinalIgnoreCase)
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

    public string AdminPin
    {
        get => _settings.AdminPin;
        set { _settings.AdminPin = value; OnPropertyChanged(); }
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

    public void ReloadFromDisk()
    {
        Active = _checkout.GetActive();
        if (Active is not null)
        {
            Step = WizardStep.Active;
            Status = "Checked out — finish before starting another event.";
            return;
        }

        LoadOffices();
        if (string.IsNullOrWhiteSpace(_settings.DisplayName))
            EnsureDisplayName();
        Status = Directory.Exists(_settings.LibraryRoot)
            ? string.Empty
            : "Library folder not found. Ask setup staff to check Admin settings.";

        // One office → skip straight to events (no pointless single-choice screen).
        if (Offices.Count == 1)
            SelectOffice(Offices[0]);
        else
            Step = WizardStep.Office;
    }

    public void GoBack()
    {
        if (HasActive && Step != WizardStep.Admin)
            return;

        Step = Step switch
        {
            WizardStep.Session => WizardStep.Office,
            WizardStep.EventFile => WizardStep.Session,
            WizardStep.Admin => HasActive ? WizardStep.Active : WizardStep.Office,
            _ => Step
        };
    }

    public void SelectOffice(string? office)
    {
        if (HasActive || string.IsNullOrWhiteSpace(office))
            return;
        SelectedOffice = office;
        LoadSessions(office);
        Step = WizardStep.Session;
    }

    public void SelectSession(EventPackInfo? session)
    {
        if (HasActive || session is null)
            return;
        if (session.Lock is { Status: LockLifecycleStatus.Active }
            && !string.Equals(session.Lock.MachineId, _machine.MachineId, StringComparison.OrdinalIgnoreCase))
        {
            Alert(
                "Already checked out",
                $"This event is already checked out by {session.Lock.HolderName} on {session.Lock.MachineName}.");
            return;
        }

        SelectedSession = session;
        EventFiles.Clear();
        foreach (var f in session.EventFiles)
            EventFiles.Add(f);

        if (EventFiles.Count == 0)
        {
            Alert("Can't continue", "No Timing & Scoring .event file found for this event.");
            return;
        }

        if (EventFiles.Count == 1)
        {
            SelectedEventFile = EventFiles[0];
            _ = CheckoutAsync();
            return;
        }

        SelectedEventFile = session.PrimaryEventFile ?? EventFiles[0];
        Step = WizardStep.EventFile;
    }

    public void ConfirmEventFileAndCheckout()
    {
        if (string.IsNullOrWhiteSpace(SelectedEventFile))
        {
            Alert("Choose a file", "Select the timing file for this race.");
            return;
        }

        _ = CheckoutAsync();
    }

    public async Task CheckoutAsync()
    {
        if (SelectedSession is null || string.IsNullOrWhiteSpace(SelectedEventFile))
            return;

        EnsureDisplayName();
        SetBusy("Loading event", "Getting your race ready…");
        try
        {
            var eval = _locks.EvaluateAcquire(SelectedSession.Path, _settings);
            string? reason = null;
            var confirmEmergency = false;

            if (eval.Outcome == LockAcquireOutcome.BlockedAlive)
            {
                ClearBusy();
                Alert(
                    "Already in use",
                    $"Someone else is already using this event.\n\n{eval.Lock?.HolderName} on {eval.Lock?.MachineName}");
                return;
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
                EmergencyReason = reason
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

            // After crash take-over (or any checkout that applied autosave), make recovery obvious.
            if (confirmEmergency || result.Message.Contains("autosave", StringComparison.OrdinalIgnoreCase))
            {
                await ShowBusyResultAsync(
                    confirmEmergency ? "Race recovered" : "Race loaded",
                    result.Message);
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
            closingApp ? "Closing — saving your race" : "Finishing up",
            "Please wait. Do not shut down yet…");
        try
        {
            var copyProgress = new Progress<CopyProgress>(p =>
                ProgressText = p.FilesCopied > 0 ? $"Saving race files… ({p.FilesCopied})" : "Saving race files…");
            var statusProgress = new Progress<string>(s => ProgressText = FriendlyProgress(s));

            var result = await _checkout.CheckinAsync(
                _settings,
                DisplayName.Trim(),
                removeActive: true,
                waitForOneDrive: true,
                progress: copyProgress,
                statusProgress: statusProgress);

            if (!result.Success && result.BlockingProcesses.Count > 0)
            {
                await ShowBusyResultAsync(
                    "Still open",
                    "Close RunScore and Timing & Scoring first, then tap Finish again.");
                return;
            }

            if (!result.Success)
            {
                await ShowBusyResultAsync(
                    "Couldn't finish",
                    "We couldn't finish saving. Try again or ask setup staff for help.");
                return;
            }

            Active = null;
            SelectedSession = null;
            SelectedEventFile = null;
            LoadOffices();
            if (Offices.Count == 1)
                SelectOffice(Offices[0]);
            else
                Step = WizardStep.Office;
            Status = string.Empty;

            // Same branded overlay — OK to dismiss. No second Windows dialog.
            await ShowBusyResultAsync(
                "All done",
                result.AwaitingOneDriveConfirm
                    ? "Your race is saved.\n\nCheck the OneDrive cloud icon in the taskbar — when it looks finished, you can shut down this laptop."
                    : "Your race is saved.\n\nSafe to shut down this laptop.");
        }
        catch (Exception ex)
        {
            await ShowBusyResultAsync("Couldn't finish", "We couldn't finish saving.\n\n" + ex.Message);
        }
        finally
        {
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
        var result = _launcher.LaunchRunScore(Active, _settings);
        if (!result.Success)
            Alert("RunScore", result.Message);
        else
            Status = result.Message.Contains("Updated", StringComparison.OrdinalIgnoreCase)
                ? "RunScore opening… (preferences set to SOP)"
                : "RunScore opening…";
    }

    public void LaunchTimingAndScoring()
    {
        if (Active is null) return;
        var result = _launcher.LaunchTimingAndScoring(Active, _settings);
        if (!result.Success)
            Alert("Timing & Scoring", result.Message);
        else
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
        if (!string.Equals(pin, _settings.AdminPin, StringComparison.Ordinal))
        {
            Alert("Admin", "Incorrect PIN.");
            return;
        }

        Step = WizardStep.Admin;
    }

    public void SaveAdminSettings()
    {
        _settingsStore.Save(_settings);
        Status = "Settings saved.";
        ReloadFromDisk();
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
    }

    private void EnsureDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(_settings.DisplayName))
            return;
        _settings.DisplayName = Environment.UserName;
        _settingsStore.Save(_settings);
        OnPropertyChanged(nameof(DisplayName));
    }

    private void Heartbeat()
    {
        var active = _activeStore.Load();
        if (active is null) return;
        try { _locks.Heartbeat(active.LibrarySessionPath); }
        catch { /* ignore */ }
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
