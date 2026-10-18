using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Services;
using S2F.RaceDayCheckout.ViewModels;

namespace S2F.RaceDayCheckout;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _forceClose;
    private bool _adminFocusQueued;
    private string? _lastAdminFocusKey;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // NotifyAdminSection fires many IsAdmin* flags at once — coalesce to one focus pass.
        if (e.PropertyName is nameof(MainViewModel.IsAdminNewEventLayout)
            or nameof(MainViewModel.IsAdminNewEventOffice)
            or nameof(MainViewModel.IsAdminNewEventParent)
            or nameof(MainViewModel.IsAdminNewEventSourceKind)
            or nameof(MainViewModel.IsAdminNewEventSource)
            or nameof(MainViewModel.IsAdminNewEventDetails)
            or nameof(MainViewModel.IsAdminNewEventGroupCreated)
            or nameof(MainViewModel.IsAdminEventsOffice)
            or nameof(MainViewModel.IsAdminEventsSession)
            or nameof(MainViewModel.IsAdminEventsAssign)
            or nameof(MainViewModel.IsAdminSetupNewEvent)
            or nameof(MainViewModel.IsAdminEditCurrentEvent))
        {
            if (_adminFocusQueued)
                return;
            _adminFocusQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _adminFocusQueued = false;
                FocusAdminPrimaryControl();
            }, DispatcherPriority.Input);
        }
    }

    private string CurrentAdminFocusKey()
    {
        if (_vm.IsAdminNewEventLayout) return "ne-layout";
        if (_vm.IsAdminNewEventOffice) return "ne-office";
        if (_vm.IsAdminNewEventParent) return "ne-parent";
        if (_vm.IsAdminNewEventSourceKind) return "ne-sk";
        if (_vm.IsAdminNewEventSource) return "ne-src";
        if (_vm.IsAdminNewEventDetails) return "ne-details";
        if (_vm.IsAdminNewEventGroupCreated) return "ne-created";
        if (_vm.IsAdminEventsOffice) return "ed-office";
        if (_vm.IsAdminEventsSession) return "ed-session";
        if (_vm.IsAdminEventsAssign) return "ed-assign";
        if (_vm.IsAdminSetupNewEvent) return "ne";
        if (_vm.IsAdminEditCurrentEvent) return "ed";
        return "";
    }

    /// <summary>Put keyboard focus on the active Admin step control so Tab/Enter work immediately.</summary>
    private void FocusAdminPrimaryControl()
    {
        if (!_vm.IsAdminStep)
            return;

        var key = CurrentAdminFocusKey();
        if (key.Length == 0)
            return;

        // Same step already focused — never steal caret while staff are typing.
        if (key == _lastAdminFocusKey)
            return;
        _lastAdminFocusKey = key;

        if (_vm.IsAdminNewEventLayout)
        {
            FocusList(NewEventLayoutList);
            return;
        }

        if (_vm.IsAdminNewEventOffice)
        {
            if (NewEventListFilterBox.IsVisible)
                NewEventListFilterBox.Focus();
            else
                FocusList(NewEventOfficeList);
            return;
        }

        if (_vm.IsAdminNewEventParent)
        {
            NewEventParentNameBox.Focus();
            NewEventParentNameBox.SelectAll();
            return;
        }

        if (_vm.IsAdminNewEventSourceKind)
        {
            FocusList(NewEventSourceKindList);
            return;
        }

        if (_vm.IsAdminNewEventSource)
        {
            if (NewEventListFilterBox.IsVisible)
                NewEventListFilterBox.Focus();
            else
                FocusList(NewEventSourceList);
            return;
        }

        if (_vm.IsAdminNewEventDetails)
        {
            NewEventRaceNameBox.Focus();
            NewEventRaceNameBox.SelectAll();
            return;
        }

        if (_vm.IsAdminNewEventGroupCreated)
            return;

        if (_vm.IsAdminEventsOffice)
        {
            FocusList(AdminOfficeList);
            return;
        }

        if (_vm.IsAdminEventsSession)
        {
            FocusList(AdminSessionList);
        }
    }

    private static void FocusList(ListBox list)
    {
        if (!list.IsVisible)
            return;
        list.Focus();
        if (list.SelectedIndex < 0 && list.Items.Count > 0)
            list.SelectedIndex = 0;
        list.ScrollIntoView(list.SelectedItem);
    }

    private void AdminListFilter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        if (_vm.IsAdminNewEventOffice)
        {
            FocusList(NewEventOfficeList);
            return;
        }

        if (_vm.IsAdminNewEventSource)
            FocusList(NewEventSourceList);
    }

    private async void NewEventLayoutList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await _vm.SelectNewEventLayoutAsync(NewEventLayoutList.SelectedItem as NewEventLayoutOption);
    }

    private async void NewEventOfficeList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await _vm.SelectNewEventOfficeAsync(NewEventOfficeList.SelectedItem as string);
    }

    private void NewEventParentField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        // Commit binding before Continue reads the year/name.
        NewEventParentNameBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        NewEventParentYearBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _vm.ConfirmNewEventParent();
    }

    private async void NewEventSourceKindList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await SelectNewEventSourceKindFromListAsync();
    }

    private async void NewEventSourceList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await _vm.SelectNewEventSourceAsync(NewEventSourceList.SelectedItem as NewEventSourceItem);
    }

    private async void NewEventDetailsField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        if (ReferenceEquals(sender, NewEventRaceNameBox))
        {
            NewEventRaceNameBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            NewEventRaceDatePicker.Focus();
            return;
        }

        if (ReferenceEquals(sender, NewEventRaceDatePicker))
        {
            NewEventResultsIdBox.Focus();
            NewEventResultsIdBox.SelectAll();
            return;
        }

        // Results ID (or Create): build the event.
        NewEventRaceNameBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (sender is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        await _vm.CreateNewEventAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _vm.EnsureLocalRoots();
        _vm.ReloadFromDisk(); // pick up local office placeholders created above
        _vm.RefreshGraphLandingStatus();

        // Optional: confirm which offices exist on OneDrive (does not block showing the list).
        try
        {
            await _vm.EnsureLibraryIndexedFromGraphAsync()
                .WaitAsync(TimeSpan.FromSeconds(25));
        }
        catch (TimeoutException)
        {
            _vm.Status = "OneDrive office check timed out — local offices are ready; events load when you pick one.";
        }
        catch (Exception ex)
        {
            _vm.Status = "OneDrive office check skipped: " + ex.Message;
        }

        // Updates first so a new version isn't hidden behind setup dialogs / silent failures.
        await UpdateService.CheckForUpdatesQuietAsync(
            _vm.HasActive,
            this,
            msg => _vm.Status = msg);

        // Once per machine: official Notepad++ installer → C:\Program Files\Notepad++.
        // Skipped when that exe is already there, and when this build has no bundled installer.
        await NotepadPlusPlusSetup.EnsureAsync(this, _vm);

        // First install only (Velopack OnFirstRun): create RaceDay Active/Backup + Defender opt-in.
        // Not on checkout; not again on routine updates once DefenderExclusionsConfigured is set.
        DefenderExclusionPrompt.TryOffer(this);

        // First launch after Setup: offer taskbar pin (Windows still shows its own confirm).
        await TaskbarPinPrompt.TryOfferAsync(this);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await UpdateService.CheckForUpdatesInteractiveAsync(_vm.HasActive, this);

    private void Back_Click(object sender, RoutedEventArgs e) => _vm.GoBack();

    private async void OfficeContinue_Click(object sender, RoutedEventArgs e) =>
        await _vm.SelectOfficeAsync(OfficeList.SelectedItem as string);

    private async void Office_DoubleClick(object sender, MouseButtonEventArgs e) =>
        await _vm.SelectOfficeAsync(OfficeList.SelectedItem as string);

    private async void RefreshSessions_Click(object sender, RoutedEventArgs e) =>
        await _vm.RetryOfficeSessionsAsync();

    private async void SessionContinue_Click(object sender, RoutedEventArgs e) =>
        await _vm.SelectSessionAsync(SessionList.SelectedItem as EventPackInfo);

    private async void Session_DoubleClick(object sender, MouseButtonEventArgs e) =>
        await _vm.SelectSessionAsync(SessionList.SelectedItem as EventPackInfo);

    private void EventFileContinue_Click(object sender, RoutedEventArgs e) =>
        _vm.ConfirmEventFileAndCheckout();

    private void EventFile_DoubleClick(object sender, MouseButtonEventArgs e) =>
        _vm.ConfirmEventFileAndCheckout();

    private void LaunchRunScore_Click(object sender, RoutedEventArgs e) => _vm.LaunchRunScore();

    private void LaunchTs_Click(object sender, RoutedEventArgs e) => _vm.LaunchTimingAndScoring();

    private void OpenWorkingFolder_Click(object sender, RoutedEventArgs e) => _vm.OpenWorkingFolder();

    private async void Finish_Click(object sender, RoutedEventArgs e) => await _vm.FinishAsync();

    private async void Done_Click(object sender, RoutedEventArgs e) => await _vm.DoneAnnouncerAsync();

    private async void RoleTiming_Click(object sender, RoutedEventArgs e) =>
        await _vm.SetCheckoutRoleAsync(CheckoutRole.Timing);

    private async void RoleAnnouncer_Click(object sender, RoutedEventArgs e) =>
        await _vm.SetCheckoutRoleAsync(CheckoutRole.Announcer);

    private void Admin_Click(object sender, RoutedEventArgs e) => _vm.TryOpenAdmin();

    private void AdminSetupNewEvent_Click(object sender, RoutedEventArgs e) =>
        _vm.ShowAdminSetupNewEvent();

    private void AdminEditCurrentEvent_Click(object sender, RoutedEventArgs e) =>
        _vm.ShowAdminEditCurrentEvent();

    private void AdminSettings_Click(object sender, RoutedEventArgs e) => _vm.ShowAdminSettings();

    private async void NewEventLayoutContinue_Click(object sender, RoutedEventArgs e) =>
        await _vm.SelectNewEventLayoutAsync(NewEventLayoutList.SelectedItem as NewEventLayoutOption);

    private async void NewEventLayout_DoubleClick(object sender, MouseButtonEventArgs e) =>
        await _vm.SelectNewEventLayoutAsync(NewEventLayoutList.SelectedItem as NewEventLayoutOption);

    private async void NewEventOfficeContinue_Click(object sender, RoutedEventArgs e) =>
        await _vm.SelectNewEventOfficeAsync(NewEventOfficeList.SelectedItem as string);

    private async void NewEventOffice_DoubleClick(object sender, MouseButtonEventArgs e) =>
        await _vm.SelectNewEventOfficeAsync(NewEventOfficeList.SelectedItem as string);

    private void NewEventParentContinue_Click(object sender, RoutedEventArgs e) =>
        _vm.ConfirmNewEventParent();

    private async void NewEventCheckoutLast_Click(object sender, RoutedEventArgs e) =>
        await _vm.CheckoutSelectedNewEventAsync();

    private void NewEventAddAnother_Click(object sender, RoutedEventArgs e) =>
        _vm.AddAnotherRaceInGroup();

    private void AdminAddRaceToGroup_Click(object sender, RoutedEventArgs e) =>
        _vm.BeginAddRaceToExistingGroup();

    private async void NewEventSourceKindContinue_Click(object sender, RoutedEventArgs e) =>
        await SelectNewEventSourceKindFromListAsync();

    private async void NewEventSourceKind_DoubleClick(object sender, MouseButtonEventArgs e) =>
        await SelectNewEventSourceKindFromListAsync();

    private async Task SelectNewEventSourceKindFromListAsync()
    {
        if (NewEventSourceKindList.SelectedItem is not NewEventSourceKindOption kind)
            return;
        await _vm.SelectNewEventSourceKindAsync(useTemplates: kind.UseTemplates);
    }

    private async void NewEventSourceContinue_Click(object sender, RoutedEventArgs e) =>
        await _vm.SelectNewEventSourceAsync(NewEventSourceList.SelectedItem as NewEventSourceItem);

    private async void NewEventSource_DoubleClick(object sender, MouseButtonEventArgs e) =>
        await _vm.SelectNewEventSourceAsync(NewEventSourceList.SelectedItem as NewEventSourceItem);

    private async void AdminOfficeContinue_Click(object sender, RoutedEventArgs e) =>
        await _vm.SelectAdminOfficeAsync(AdminOfficeList.SelectedItem as string);

    private async void AdminOffice_DoubleClick(object sender, MouseButtonEventArgs e) =>
        await _vm.SelectAdminOfficeAsync(AdminOfficeList.SelectedItem as string);

    private async void AdminSessionContinue_Click(object sender, RoutedEventArgs e) =>
        await _vm.SelectAdminSessionAsync(AdminSessionList.SelectedItem as EventPackInfo);

    private async void AdminSession_DoubleClick(object sender, MouseButtonEventArgs e) =>
        await _vm.SelectAdminSessionAsync(AdminSessionList.SelectedItem as EventPackInfo);

    private async void SaveEventRoles_Click(object sender, RoutedEventArgs e) =>
        await _vm.SaveAdminEventRolesAsync();

    private async void AdminLaunchTs_Click(object sender, RoutedEventArgs e) =>
        await _vm.LaunchAdminTimingAndScoringAsync();

    private async void AdminRefreshEventFiles_Click(object sender, RoutedEventArgs e) =>
        await _vm.RefreshAdminEventFilesAsync();

    private void AdminEventsStartOver_Click(object sender, RoutedEventArgs e) =>
        _vm.ResetAdminEventsToStart();

    private void BusyOk_Click(object sender, RoutedEventArgs e) => _vm.AcknowledgeBusy();

    private void SaveAdmin_Click(object sender, RoutedEventArgs e) => _vm.SaveAdminSettings();

    private async void GraphSignIn_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        await _vm.SignInGraphAsync(hwnd);
    }

    private async void GraphSignOut_Click(object sender, RoutedEventArgs e) =>
        await _vm.SignOutGraphAsync();

    private async void GraphRefresh_Click(object sender, RoutedEventArgs e) =>
        await _vm.RefreshLibraryFromGraphAsync();

    private async void CreateNewEvent_Click(object sender, RoutedEventArgs e) =>
        await _vm.CreateNewEventAsync();

    private void EditRunScoreSop_Click(object sender, RoutedEventArgs e) => _vm.EditRunScoreSop();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_forceClose)
            return;

        e.Cancel = true;
        var ok = await _vm.ConfirmCloseAsync(this);
        if (!ok)
            return;

        _forceClose = true;
        await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
    }
}
