using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Services;

namespace S2F.RaceDayCheckout;

public partial class ManualBackupFloatWindow : Window
{
    private ManualBackupCoordinator? _coord;
    private Func<string, string, ManualBackupTap?>? _submit;
    private readonly ObservableCollection<ManualBackupTap> _recent = new();

    public ManualBackupFloatWindow()
    {
        InitializeComponent();
        RecentList.ItemsSource = _recent;
    }

    public void Bind(
        ManualBackupCoordinator coordinator,
        Func<string, string, ManualBackupTap?>? submit = null)
    {
        _coord = coordinator;
        _submit = submit;
        DataContext = coordinator;
        LocationBox.ItemsSource = coordinator.Locations;
        if (coordinator.Locations.Count > 0)
            LocationBox.SelectedIndex = 0;
        coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ManualBackupCoordinator.Locations)
                or nameof(ManualBackupCoordinator.ShowPanel))
            {
                Dispatcher.Invoke(() =>
                {
                    LocationBox.ItemsSource = null;
                    LocationBox.ItemsSource = coordinator.Locations;
                    if (LocationBox.SelectedIndex < 0 && coordinator.Locations.Count > 0)
                        LocationBox.SelectedIndex = 0;
                });
            }
            if (e.PropertyName == nameof(ManualBackupCoordinator.Status))
                Dispatcher.Invoke(() => StatusText.Text = coordinator.Status);
        };
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_coord is null)
            return;
        var loc = LocationBox.SelectedValue as string
                  ?? (LocationBox.SelectedItem as ManualBackupLocationRow)?.Location;
        if (string.IsNullOrWhiteSpace(loc))
        {
            StatusText.Text = "Pick a location.";
            return;
        }
        var bib = BibBox.Text?.Trim() ?? "";
        if (bib.Length == 0)
            return;
        var tap = _submit?.Invoke(loc, bib) ?? _coord.SubmitBib(loc, bib);
        if (tap is not null)
        {
            _recent.Insert(0, tap);
            while (_recent.Count > 5)
                _recent.RemoveAt(_recent.Count - 1);
            BibBox.Clear();
            BibBox.Focus();
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_coord is null || sender is not Button { Tag: ManualBackupTap tap })
            return;
        if (_coord.TryVoid(tap.Location, tap.Id))
            _recent.Remove(tap);
        else
            StatusText.Text = "Already sent — ask the timer (cannot undo Played times).";
    }
}

