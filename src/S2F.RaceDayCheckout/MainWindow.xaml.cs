using System.ComponentModel;
using System.Windows;
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

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
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

    private void NewEvent_Click(object sender, RoutedEventArgs e) => _vm.OpenNewEventBuilder();

    private void EditRunScoreSop_Click(object sender, RoutedEventArgs e) => _vm.EditRunScoreSop();

    private void Logo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Triple-click logo to open admin (timers won't stumble into settings).
        if (e.ClickCount >= 3)
            _vm.TryOpenAdmin();
    }

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
