using System.Collections.ObjectModel;
using System.Windows;

namespace S2F.RaceDayCheckout;

public partial class ManualBackupConfigDialog : Window
{
    private readonly ObservableCollection<LocationChoice> _choices = new();
    private readonly Func<IReadOnlyList<string>>? _reloadSuggestions;
    private readonly HashSet<string> _initialSelected;

    public ManualBackupConfigDialog(
        string locationsCsv,
        bool enabled,
        IEnumerable<string>? suggestedLocations = null,
        Func<IReadOnlyList<string>>? reloadSuggestions = null)
    {
        InitializeComponent();
        _ = enabled; // enable follows selected locations on Save
        _reloadSuggestions = reloadSuggestions;
        _initialSelected = new HashSet<string>(ParseCsv(locationsCsv), StringComparer.OrdinalIgnoreCase);
        ApplySuggestions(suggestedLocations ?? Array.Empty<string>(), keepCurrentChecks: false);
    }

    public bool Enabled { get; private set; }
    public IReadOnlyList<string> Locations { get; private set; } = Array.Empty<string>();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_reloadSuggestions is null)
            return;
        ApplySuggestions(_reloadSuggestions(), keepCurrentChecks: true);
    }

    private void ApplySuggestions(IEnumerable<string> suggestedLocations, bool keepCurrentChecks)
    {
        var checkedNow = keepCurrentChecks
            ? new HashSet<string>(_choices.Where(c => c.IsSelected).Select(c => c.Name), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(_initialSelected, StringComparer.OrdinalIgnoreCase);

        var fromRunScore = suggestedLocations
            .Select(s => (s ?? "").Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _choices.Clear();
        foreach (var name in fromRunScore)
        {
            _choices.Add(new LocationChoice
            {
                Name = name,
                // Only check names the user already saved — never auto-map new Results rows.
                IsSelected = checkedNow.Contains(name)
            });
        }

        // Keep previously saved picks visible if Events.xml briefly omits them.
        foreach (var name in checkedNow)
        {
            if (_choices.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            _choices.Add(new LocationChoice { Name = name, IsSelected = true });
        }

        SuggestList.ItemsSource = null;
        SuggestList.ItemsSource = _choices;
        EmptySuggestText.Visibility = fromRunScore.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Locations = _choices
            .Where(c => c.IsSelected)
            .Select(c => c.Name.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Picking locations turns Manual Backup on; none checked turns it off.
        Enabled = Locations.Count > 0;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static List<string> ParseCsv(string? csv) =>
        (csv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();

    private sealed class LocationChoice
    {
        public string Name { get; set; } = "";
        public bool IsSelected { get; set; }
    }
}
