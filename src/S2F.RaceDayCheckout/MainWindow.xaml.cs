using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;
using S2F.RaceDayCheckout.Services;
using S2F.RaceDayCheckout.ViewModels;

namespace S2F.RaceDayCheckout;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _forceClose;
    private bool _closePromptOpen;
    private bool _sessionEnding;
    private bool _adminFocusQueued;
    private string? _lastAdminFocusKey;
    /// <summary>When true, hour GotFocus keeps caret at end (Backspace from empty MM) instead of SelectAll.</summary>
    private bool _raceStartHourCaretAtEnd;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.PropertyChanged += ViewModel_PropertyChanged;
        if (Application.Current is not null)
            Application.Current.SessionEnding += App_SessionEnding;
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

        // Updates before office Graph / setup prompts — if a version is available the timer must
        // see Download, Restart & Install before they can click into an event.
        await UpdateService.CheckForUpdatesQuietAsync(
            _vm.HasActive,
            this,
            msg => _vm.Status = msg);

        // Already signed in (token cache): pull vendor secrets if AppData cache is empty.
        // Soft + short Graph budget — does not wait on office indexing below.
        try
        {
            await _vm.EnsureVendorSecretsIfSignedInAsync()
                .WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            // Non-fatal — checkout / Open RunScore can retry when cache still missing.
        }
        catch
        {
            // Non-fatal
        }

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

        // Once per machine: official Notepad++ installer → C:\Program Files\Notepad++.
        // Skipped when that exe is already there, and when this build has no bundled installer.
        await NotepadPlusPlusSetup.EnsureAsync(this, _vm);

        // New laptops only: silent RemotePC host deploy (skips if already installed or redist missing).
        await RemotePcSetup.EnsureAsync(this, _vm);

        // Company External Timers → HKCU (same hive RunScore Tools → External Timers uses).
        // Soft — registry write only; does not require RunScore to be installed yet.
        try
        {
            var timers = new RunScorePreferencesService().EnsureExternalTimers();
            if (timers.UpdatedCount > 0 && !string.IsNullOrWhiteSpace(timers.Message))
                _vm.Status = timers.Message;
        }
        catch
        {
            // Non-fatal
        }

        // First launch after Setup: taskbar pin before Defender UAC so we keep foreground
        // (RequestPinCurrentAppAsync silently fails when the app is not foreground).
        await TaskbarPinPrompt.TryOfferAsync(this);

        // First install only (Velopack OnFirstRun): create RaceDay Active/Backup + Defender opt-in.
        // Not on checkout; not again on routine updates once DefenderExclusionsConfigured is set.
        DefenderExclusionPrompt.TryOffer(this);

        // After first-install UAC (Defender), sync Windows clock when HTTPS skew is material.
        // Soft budget — never blocks checkout if UAC/network stalls.
        try
        {
            await SystemTimeSyncPrompt.TryRunAsync(this, msg => _vm.Status = msg)
                .WaitAsync(TimeSpan.FromSeconds(100));
        }
        catch (TimeoutException)
        {
            RaceDayTrace.Write("time-sync launch budget exceeded");
        }
        catch
        {
            // Non-fatal
        }
        finally
        {
            _vm.RefreshLaptopClockSyncStatus();
        }
    }

    private void AddRaceStartClock_Click(object sender, RoutedEventArgs e)
    {
        var before = _vm.RaceStarts.Count;
        _vm.AddRaceStartClock();
        if (_vm.RaceStarts.Count <= before)
            return;
        var row = _vm.RaceStarts[^1];
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => FocusRaceStartCategory(row));
    }

    private void FocusRaceStartCategory(RaceStartRowViewModel row)
    {
        var box = FindCategoryBoxForRow(RaceStartBoardList, row);
        if (box is null)
        {
            // Container may not be generated yet — one more pass after layout.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var retry = FindCategoryBoxForRow(RaceStartBoardList, row);
                if (retry is null)
                    return;
                retry.Focus();
                Keyboard.Focus(retry);
            });
            return;
        }

        box.Focus();
        Keyboard.Focus(box);
    }

    private static TextBox? FindCategoryBoxForRow(DependencyObject root, RaceStartRowViewModel row)
    {
        if (root is TextBox { Tag: "Category" } box
            && ReferenceEquals(box.DataContext, row))
            return box;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindCategoryBoxForRow(System.Windows.Media.VisualTreeHelper.GetChild(root, i), row);
            if (found is not null)
                return found;
        }

        return null;
    }

    private void RemoveRaceStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RaceStartRowViewModel row })
            _vm.RemoveRaceStart(row);
    }

    private void CaptureGunStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RaceStartRowViewModel row })
            _vm.CaptureGunStart(row);
    }

    private void ClearGunStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RaceStartRowViewModel row })
            _vm.ClearGunStart(row);
    }

    private void CopyGunStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RaceStartRowViewModel row })
            _vm.CopyGunStart(row);
    }

    private void RaceStartClockField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RaceStartRowViewModel row })
            return;
        // Defer so HH → MM focus hop does not commit mid-edit.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Keyboard.FocusedElement is FrameworkElement fe
                && ReferenceEquals(fe.DataContext, row)
                && fe.Tag is "StartHour" or "StartMinute")
                return;
            _vm.CommitRaceStartClock(row);
        });
    }

    private void RaceStartClockField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: RaceStartRowViewModel row })
            _vm.CommitRaceStartClock(row);
        if (sender is UIElement el)
            el.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void RaceStartTimeDigit_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (var ch in e.Text)
        {
            if (!char.IsDigit(ch))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void RaceStartTimeArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Unset time: any click in the time chrome starts at HH so typing replaces helper 00:00.
        if (sender is not FrameworkElement { DataContext: RaceStartRowViewModel row } root)
            return;
        if (!row.ShowStartHourPlaceholder || !row.ShowStartMinutePlaceholder)
            return;
        if (FindTaggedTextBoxNear(root, "StartHour") is not { } hour)
            return;
        hour.Focus();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            hour.CaretIndex = 0;
            hour.SelectionLength = 0;
        });
        e.Handled = true;
    }

    private void RaceStartHour_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox hour)
            return;
        var caretAtEnd = _raceStartHourCaretAtEnd;
        _raceStartHourCaretAtEnd = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (caretAtEnd)
            {
                // Backspace from empty MM — keep deleting HH digits.
                hour.CaretIndex = hour.Text.Length;
                hour.SelectionLength = 0;
                return;
            }

            if (hour.Text.Length == 0)
            {
                hour.CaretIndex = 0;
                hour.SelectionLength = 0;
                return;
            }

            // Forward typing replaces the current HH.
            hour.SelectAll();
        });
    }

    private void RaceStartMinute_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox minute)
            return;
        // Still unset — bounce to HH so entry always starts at hours.
        if (minute.DataContext is RaceStartRowViewModel { ShowStartHourPlaceholder: true }
            && FindTaggedTextBoxNear(minute, "StartHour") is { } hour)
        {
            hour.Focus();
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (minute.Text.Length == 0)
            {
                minute.CaretIndex = 0;
                minute.SelectionLength = 0;
                return;
            }

            // Forward typing replaces the current MM.
            minute.SelectAll();
        });
    }

    private void RaceStartHour_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { IsKeyboardFocusWithin: true } hour || hour.Text.Length < 2)
            return;
        hour.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void RaceStartHour_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Colon / period advances to minutes without typing into HH.
        if (e.Key is Key.OemPeriod or Key.OemSemicolon or Key.Decimal)
        {
            e.Handled = true;
            if (sender is UIElement el)
                el.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }

        // Tab after HH — land in MM (not Capture / ×), especially when MM is still empty.
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) == 0
            && sender is TextBox hour
            && FindTaggedTextBoxNear(hour, "StartMinute") is { } minute)
        {
            e.Handled = true;
            minute.Focus();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (minute.Text.Length == 0)
                {
                    minute.CaretIndex = 0;
                    minute.SelectionLength = 0;
                }
                else
                    minute.SelectAll();
            });
        }
    }

    private void RaceStartMinute_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox minute)
            return;

        // Habitual Tab after auto-advance from HH: stay in empty MM until digits are entered.
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) == 0
            && minute.Text.Length == 0)
        {
            e.Handled = true;
            minute.Focus();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                minute.CaretIndex = 0;
                minute.SelectionLength = 0;
            });
            return;
        }

        if (e.Key != Key.Back || minute.CaretIndex != 0 || minute.SelectionLength != 0)
            return;
        if (minute.Text.Length > 0)
            return;
        e.Handled = true;
        var hour = FindTaggedTextBoxNear(minute, "StartHour");
        if (hour is null)
        {
            minute.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            return;
        }

        _raceStartHourCaretAtEnd = true;
        hour.Focus();
    }

    private static TextBox? FindTaggedTextBoxNear(DependencyObject start, string tag)
    {
        for (var node = start; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            var hit = FindTaggedTextBoxDown(node, tag);
            if (hit is not null)
                return hit;
        }

        return null;
    }

    private static TextBox? FindTaggedTextBoxDown(DependencyObject start, string tag)
    {
        if (start is TextBox { Tag: string t } self && t == tag)
            return self;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(start);
        for (var i = 0; i < count; i++)
        {
            var found = FindTaggedTextBoxDown(System.Windows.Media.VisualTreeHelper.GetChild(start, i), tag);
            if (found is not null)
                return found;
        }

        return null;
    }

    private void AddAdminRaceStart_Click(object sender, RoutedEventArgs e) =>
        _vm.AddAdminRaceStart();

    private void EditAdminRaceStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RaceStartRowViewModel row })
            _vm.BeginEditAdminRaceStart(row);
    }

    private void RemoveAdminRaceStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RaceStartRowViewModel row })
            _vm.RemoveAdminRaceStart(row);
    }

    private void NewAdminRaceStartField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        _vm.AddAdminRaceStart();
    }

    private async void SaveAdminRaceStarts_Click(object sender, RoutedEventArgs e) =>
        await _vm.SaveAdminRaceStartsAsync();

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

    private void CloseRunScoreAfterTakeover_Click(object sender, RoutedEventArgs e) =>
        _vm.CloseRunScoreFromBanner();

    private void CloseTsAfterTakeover_Click(object sender, RoutedEventArgs e) =>
        _vm.CloseTimingAndScoringFromBanner();

    private async void CloseLocalCopyAfterTakeover_Click(object sender, RoutedEventArgs e) =>
        await _vm.CloseLocalCopyFromBannerAsync();

    private async void Finish_Click(object sender, RoutedEventArgs e) => await _vm.FinishAsync();

    private async void Done_Click(object sender, RoutedEventArgs e) => await _vm.DoneAnnouncerAsync();

    private async void RoleTiming_Click(object sender, RoutedEventArgs e) =>
        await _vm.SetCheckoutRoleAsync(CheckoutRole.Timing);

    private async void RoleAnnouncer_Click(object sender, RoutedEventArgs e) =>
        await _vm.SetCheckoutRoleAsync(CheckoutRole.Announcer);

    private void Admin_Click(object sender, RoutedEventArgs e) => _vm.TryOpenAdmin();

    private void ExitAdmin_Click(object sender, RoutedEventArgs e) => _vm.ExitAdmin();

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

    private async void AdminMoveToPastRaces_Click(object sender, RoutedEventArgs e) =>
        await _vm.MoveAdminSessionToPastRacesAsync();

    private async void NewEventSourceKindContinue_Click(object sender, RoutedEventArgs e) =>
        await SelectNewEventSourceKindFromListAsync();

    private async void NewEventSourceKind_DoubleClick(object sender, MouseButtonEventArgs e) =>
        await SelectNewEventSourceKindFromListAsync();

    private async Task SelectNewEventSourceKindFromListAsync()
    {
        if (NewEventSourceKindList.SelectedItem is not NewEventSourceKindOption kind)
            return;
        await _vm.SelectNewEventSourceKindAsync(kind.Kind);
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

    private async void AdminOpenCurrentTs_Click(object sender, RoutedEventArgs e) =>
        await _vm.LaunchAdminOpenCurrentTsAsync();

    private async void AdminNewEmptyTs_Click(object sender, RoutedEventArgs e) =>
        await _vm.LaunchAdminNewEmptyTsAsync();

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

    private void OpenSoftwareInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
            _vm.OpenSoftwareInstallUrl(url);
    }

    private void CopyRunScoreLicenseName_Click(object sender, RoutedEventArgs e) =>
        _vm.CopyRunScoreLicenseName();

    private void CopyRunScoreLicenseKey_Click(object sender, RoutedEventArgs e) =>
        _vm.CopyRunScoreLicenseKey();

    /// <summary>
    /// Windows shutdown/logoff. Cancel immediately when a race is checked out so the
    /// session-end timeout does not kill the app before Finish. The close prompt runs
    /// after this handler returns.
    /// </summary>
    private void App_SessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (_forceClose || !_vm.HasOpenCheckout)
            return;

        e.Cancel = true;
        _sessionEnding = true;
        ScheduleClosePrompt();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_forceClose)
            return;

        // No checkout — let the X button and Windows shutdown close the app.
        if (!_vm.HasOpenCheckout)
            return;

        // Cancel before any dialog. A blocking ShowDialog inside Closing makes Windows
        // treat the app as hung and force-kill it during shutdown.
        e.Cancel = true;
        ScheduleClosePrompt();
    }

    private void ScheduleClosePrompt()
    {
        if (_closePromptOpen)
            return;

        _closePromptOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = RunClosePromptAsync()));
    }

    private async Task RunClosePromptAsync()
    {
        try
        {
            if (_vm.Busy)
            {
                TimerDialog.Alert(
                    this,
                    "Still Working",
                    "Wait until this step finishes, then close the app.");
                return;
            }

            var sessionEnding = _sessionEnding;
            var ok = await _vm.ConfirmCloseAsync(this, sessionEnding);
            if (!ok)
                return;

            _forceClose = true;
            Close();
        }
        catch (Exception ex)
        {
            TimerDialog.Alert(this, "Couldn't Close", ex.Message);
        }
        finally
        {
            // Shutdown during the prompt is already cancelled. Drop the flag so the next X is a normal close.
            _sessionEnding = false;
            _closePromptOpen = false;
        }
    }
}
