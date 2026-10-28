using System.Windows;

namespace S2F.RaceDayCheckout;

public partial class ManualBackupConfigDialog : Window
{
    public ManualBackupConfigDialog(string locationsCsv, bool enabled)
    {
        InitializeComponent();
        EnabledBox.IsChecked = enabled;
        LocationsBox.Text = locationsCsv;
    }

    public bool Enabled { get; private set; }
    public IReadOnlyList<string> Locations { get; private set; } = Array.Empty<string>();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Enabled = EnabledBox.IsChecked == true;
        Locations = (LocationsBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

