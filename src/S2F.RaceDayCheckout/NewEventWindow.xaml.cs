using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout;

public partial class NewEventWindow : Window
{
    public sealed record SourceItem(string Display, string Path);

    private readonly string _libraryRoot;
    private readonly EventBuilderService _builder = new();
    private readonly ObservableCollection<SourceItem> _sources = new();

    public EventBuildResult? Result { get; private set; }

    public NewEventWindow(string libraryRoot, IEnumerable<string> offices)
    {
        InitializeComponent();
        _libraryRoot = libraryRoot;

        // New events land under live offices — not under Past Races / Templates.
        foreach (var office in EventBuilderService.DestinationOffices)
        {
            if (offices.Any(o => o.Equals(office, StringComparison.OrdinalIgnoreCase))
                || Directory.Exists(Path.Combine(libraryRoot, office)))
                OfficeBox.Items.Add(office);
        }

        if (OfficeBox.Items.Count == 0)
        {
            foreach (var office in EventBuilderService.DestinationOffices)
                OfficeBox.Items.Add(office);
        }

        if (OfficeBox.Items.Count > 0)
            OfficeBox.SelectedIndex = 0;

        SourceBox.ItemsSource = _sources;
        RaceNameBox.TextChanged += (_, _) => UpdatePreview();
        RaceDateBox.TextChanged += (_, _) => UpdatePreview();
        OfficeBox.SelectionChanged += (_, _) =>
        {
            ReloadSources();
            UpdatePreview();
        };

        ReloadSources();
        UpdatePreview();
    }

    private void SourceKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        ReloadSources();
    }

    private void ReloadSources()
    {
        _sources.Clear();
        var useTemplates = SourceKindBox.SelectedIndex <= 0;
        if (useTemplates)
        {
            foreach (var path in _builder.ListTemplateSessions(_libraryRoot))
                _sources.Add(new SourceItem(Path.GetFileName(path), path));
        }
        else
        {
            var office = OfficeBox.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(office))
            {
                // Past Races\{Office}\{Year}\{Race}\…
                foreach (var (display, path) in _builder.ListPastRaceSessions(_libraryRoot, office))
                    _sources.Add(new SourceItem(display, path));
            }
        }

        SourceBox.SelectedIndex = _sources.Count > 0 ? 0 : -1;
    }

    private void UpdatePreview()
    {
        try
        {
            var office = OfficeBox.SelectedItem as string ?? "(office)";
            var name = RaceNameBox.Text?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                PreviewBlock.Text = "Folder preview: enter a race name…";
                return;
            }

            var folder = EntriesIniService.BuildSessionFolderName(name, RaceDateBox.Text);
            PreviewBlock.Text =
                "Will create (current office, not Past Races):\n"
                + Path.Combine(_libraryRoot, office, folder);
        }
        catch (Exception ex)
        {
            PreviewBlock.Text = ex.Message;
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var office = OfficeBox.SelectedItem as string;
        var source = SourceBox.SelectedItem as SourceItem;
        if (string.IsNullOrWhiteSpace(office))
        {
            MessageBox.Show(this, "Select an office.", "New event", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (source is null || string.IsNullOrWhiteSpace(source.Path))
        {
            var hint = SourceKindBox.SelectedIndex <= 0
                ? Path.Combine(_libraryRoot, EventBuilderService.TemplatesFolderName)
                : Path.Combine(_libraryRoot, EventBuilderService.PastRacesFolderName, office);
            MessageBox.Show(
                this,
                "Select a Template or past race to copy.\n\nLooking under:\n" + hint,
                "New event",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        IsEnabled = false;
        try
        {
            Result = await _builder.BuildAsync(new EventBuildRequest
            {
                LibraryRoot = _libraryRoot,
                Office = office,
                SourceSessionPath = source.Path,
                RaceName = RaceNameBox.Text ?? string.Empty,
                RaceDate = RaceDateBox.Text ?? string.Empty,
                ResultsId = ResultsIdBox.Text ?? string.Empty
            });

            if (Result.Success)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show(this, Result.Message, "New event", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "New event", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
