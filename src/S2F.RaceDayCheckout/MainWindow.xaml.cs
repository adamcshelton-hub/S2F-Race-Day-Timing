using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
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
        // Updates first so a new version isn't hidden behind the pin dialog / silent failures.
        await UpdateService.CheckForUpdatesQuietAsync(
            _vm.HasActive,
            this,
            msg => _vm.Status = msg);

        // First launch after Setup: offer taskbar pin (Windows still shows its own confirm).
        await TaskbarPinPrompt.TryOfferAsync(this);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await UpdateService.CheckForUpdatesInteractiveAsync(_vm.HasActive, this);

    private void Back_Click(object sender, RoutedEventArgs e) => _vm.GoBack();

    private void OfficeContinue_Click(object sender, RoutedEventArgs e) =>
        _vm.SelectOffice(OfficeList.SelectedItem as string);

    private void Office_DoubleClick(object sender, MouseButtonEventArgs e) =>
        _vm.SelectOffice(OfficeList.SelectedItem as string);

    private void SessionContinue_Click(object sender, RoutedEventArgs e) =>
        _vm.SelectSession(SessionList.SelectedItem as EventPackInfo);

    private void Session_DoubleClick(object sender, MouseButtonEventArgs e) =>
        _vm.SelectSession(SessionList.SelectedItem as EventPackInfo);

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

    private void EditRunScoreSop_Click(object sender, RoutedEventArgs e) => _vm.EditRunScoreSop();

    private void BrowseLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (TryPickFolder("Choose Library root (OneDrive)", _vm.LibraryRoot) is { } path)
            _vm.LibraryRoot = path;
    }

    private void BrowseRaceDay_Click(object sender, RoutedEventArgs e)
    {
        if (TryPickFolder("Choose local Race Day folder", _vm.RaceDayRoot) is { } path)
            _vm.RaceDayRoot = path;
    }

    private void BrowseRunScoreExe_Click(object sender, RoutedEventArgs e)
    {
        if (TryPickExe("Locate RunScore (rsserver.exe)", _vm.RunScoreExePath) is { } path)
            _vm.RunScoreExePath = path;
    }

    private void BrowseTsExe_Click(object sender, RoutedEventArgs e)
    {
        if (TryPickExe("Locate Timing & Scoring", _vm.TimingAndScoringExePath) is { } path)
            _vm.TimingAndScoringExePath = path;
    }

    private static string? TryPickFolder(string title, string? current)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string? TryPickExe(string title, string? current)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                var dir = Path.GetDirectoryName(current);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
                if (File.Exists(current))
                    dialog.FileName = Path.GetFileName(current);
            }
            catch
            {
                // ignore bad current path
            }
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

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
