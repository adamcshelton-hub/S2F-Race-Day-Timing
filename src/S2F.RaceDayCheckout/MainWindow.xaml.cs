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
        // Quiet update check; never restarts while Active.
        await UpdateService.CheckForUpdatesQuietAsync(_vm.HasActive, this);
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
