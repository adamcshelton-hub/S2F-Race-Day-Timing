using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;
using S2F.RaceDayCheckout.Services;

namespace S2F.RaceDayCheckout;

/// <summary>
/// Shared Manual Backup bib entry: location picker, numeric bib + caret sticky focus, Last 5 with TOD.
/// Compact = Timing float; Station = Manual Backup Laptop Active.
/// </summary>
public partial class ManualBackupEntryPanel : UserControl
{
    public static readonly DependencyProperty IsStationLayoutProperty =
        DependencyProperty.Register(
            nameof(IsStationLayout),
            typeof(bool),
            typeof(ManualBackupEntryPanel),
            new PropertyMetadata(false, (d, _) => ((ManualBackupEntryPanel)d).ApplyLayoutMetrics()));

    public static readonly DependencyProperty LabelFontSizeProperty =
        DependencyProperty.Register(nameof(LabelFontSize), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(11d));

    public static readonly DependencyProperty FieldFontSizeProperty =
        DependencyProperty.Register(nameof(FieldFontSize), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(14d));

    public static readonly DependencyProperty FieldMinHeightProperty =
        DependencyProperty.Register(nameof(FieldMinHeight), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(34d));

    public static readonly DependencyProperty BibFontSizeProperty =
        DependencyProperty.Register(nameof(BibFontSize), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(20d));

    public static readonly DependencyProperty BibMinHeightProperty =
        DependencyProperty.Register(nameof(BibMinHeight), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(40d));

    public static readonly DependencyProperty BibPaddingProperty =
        DependencyProperty.Register(nameof(BibPadding), typeof(Thickness), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(new Thickness(10, 8, 10, 8)));

    public static readonly DependencyProperty SubmitFontSizeProperty =
        DependencyProperty.Register(nameof(SubmitFontSize), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(14d));

    public static readonly DependencyProperty SubmitMinWidthProperty =
        DependencyProperty.Register(nameof(SubmitMinWidth), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(88d));

    public static readonly DependencyProperty SubmitPaddingProperty =
        DependencyProperty.Register(nameof(SubmitPadding), typeof(Thickness), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(new Thickness(12, 6, 12, 6)));

    public static readonly DependencyProperty RecentRowHeightProperty =
        DependencyProperty.Register(nameof(RecentRowHeight), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(31d));

    public static readonly DependencyProperty RecentBibFontSizeProperty =
        DependencyProperty.Register(nameof(RecentBibFontSize), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(13d));

    public static readonly DependencyProperty RecentTimeFontSizeProperty =
        DependencyProperty.Register(nameof(RecentTimeFontSize), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(12d));

    public static readonly DependencyProperty RecentFixedHeightProperty =
        DependencyProperty.Register(nameof(RecentFixedHeight), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(155d));

    public static readonly DependencyProperty RecentMinHeightProperty =
        DependencyProperty.Register(nameof(RecentMinHeight), typeof(double), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(0d));

    public static readonly DependencyProperty RecentVerticalAlignmentProperty =
        DependencyProperty.Register(nameof(RecentVerticalAlignment), typeof(VerticalAlignment), typeof(ManualBackupEntryPanel),
            new PropertyMetadata(VerticalAlignment.Top));

    private ManualBackupCoordinator? _coord;
    private Func<string, string, ManualBackupTap?>? _submit;
    private readonly ObservableCollection<RecentTapRow> _recent = new();
    private PropertyChangedEventHandler? _coordPropertyChanged;
    private bool _bound;
    private bool _showOpenStatus = true;

    public ManualBackupEntryPanel()
    {
        InitializeComponent();
        RecentList.ItemsSource = _recent;
        _recent.CollectionChanged += Recent_CollectionChanged;
        UpdateRecentEmptyHint();
        ApplyLayoutMetrics();
        Loaded += (_, _) =>
        {
            ApplyLayoutMetrics();
            FocusBibSoon();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                FocusBibSoon();
        };
        PreviewMouseDown += Panel_PreviewMouseDown;
        PreviewGotKeyboardFocus += Panel_PreviewGotKeyboardFocus;
    }

    public bool IsStationLayout
    {
        get => (bool)GetValue(IsStationLayoutProperty);
        set => SetValue(IsStationLayoutProperty, value);
    }

    public double LabelFontSize
    {
        get => (double)GetValue(LabelFontSizeProperty);
        set => SetValue(LabelFontSizeProperty, value);
    }

    public double FieldFontSize
    {
        get => (double)GetValue(FieldFontSizeProperty);
        set => SetValue(FieldFontSizeProperty, value);
    }

    public double FieldMinHeight
    {
        get => (double)GetValue(FieldMinHeightProperty);
        set => SetValue(FieldMinHeightProperty, value);
    }

    public double BibFontSize
    {
        get => (double)GetValue(BibFontSizeProperty);
        set => SetValue(BibFontSizeProperty, value);
    }

    public double BibMinHeight
    {
        get => (double)GetValue(BibMinHeightProperty);
        set => SetValue(BibMinHeightProperty, value);
    }

    public Thickness BibPadding
    {
        get => (Thickness)GetValue(BibPaddingProperty);
        set => SetValue(BibPaddingProperty, value);
    }

    public double SubmitFontSize
    {
        get => (double)GetValue(SubmitFontSizeProperty);
        set => SetValue(SubmitFontSizeProperty, value);
    }

    public double SubmitMinWidth
    {
        get => (double)GetValue(SubmitMinWidthProperty);
        set => SetValue(SubmitMinWidthProperty, value);
    }

    public Thickness SubmitPadding
    {
        get => (Thickness)GetValue(SubmitPaddingProperty);
        set => SetValue(SubmitPaddingProperty, value);
    }

    public double RecentRowHeight
    {
        get => (double)GetValue(RecentRowHeightProperty);
        set => SetValue(RecentRowHeightProperty, value);
    }

    public double RecentBibFontSize
    {
        get => (double)GetValue(RecentBibFontSizeProperty);
        set => SetValue(RecentBibFontSizeProperty, value);
    }

    public double RecentTimeFontSize
    {
        get => (double)GetValue(RecentTimeFontSizeProperty);
        set => SetValue(RecentTimeFontSizeProperty, value);
    }

    public double RecentFixedHeight
    {
        get => (double)GetValue(RecentFixedHeightProperty);
        set => SetValue(RecentFixedHeightProperty, value);
    }

    public double RecentMinHeight
    {
        get => (double)GetValue(RecentMinHeightProperty);
        set => SetValue(RecentMinHeightProperty, value);
    }

    public VerticalAlignment RecentVerticalAlignment
    {
        get => (VerticalAlignment)GetValue(RecentVerticalAlignmentProperty);
        set => SetValue(RecentVerticalAlignmentProperty, value);
    }

    /// <summary>When false, hides RunScore Open status (Backup Laptop does not Play).</summary>
    public bool ShowOpenStatus
    {
        get => _showOpenStatus;
        set
        {
            _showOpenStatus = value;
            if (OpenStatusBorder is not null)
                OpenStatusBorder.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void Bind(
        ManualBackupCoordinator coordinator,
        Func<string, string, ManualBackupTap?>? submit = null)
    {
        if (_bound && ReferenceEquals(_coord, coordinator))
        {
            _submit = submit;
            RefreshLocations();
            FocusBibSoon();
            return;
        }

        if (_coord is not null)
        {
            _coord.CurrentFileRotated -= OnCurrentFileRotated;
            if (_coordPropertyChanged is not null)
                _coord.PropertyChanged -= _coordPropertyChanged;
        }

        _coord = coordinator;
        _submit = submit;
        DataContext = coordinator;
        OpenStatusBorder.Visibility = _showOpenStatus ? Visibility.Visible : Visibility.Collapsed;
        RefreshLocations();
        _coordPropertyChanged = (_, e) =>
        {
            if (e.PropertyName is nameof(ManualBackupCoordinator.Locations)
                or nameof(ManualBackupCoordinator.ShowPanel))
            {
                Dispatcher.Invoke(RefreshLocations);
            }
            if (e.PropertyName == nameof(ManualBackupCoordinator.Status))
                Dispatcher.Invoke(() => StatusText.Text = coordinator.Status);
        };
        coordinator.PropertyChanged += _coordPropertyChanged;
        coordinator.CurrentFileRotated += OnCurrentFileRotated;
        StatusText.Text = coordinator.Status;
        _bound = true;
        FocusBibSoon();
    }

    public void ClearRecent()
    {
        _recent.Clear();
        UpdateRecentEmptyHint();
    }

    public void FocusBibSoon()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || !IsEnabled || LocationBusy)
                return;
            FocusBib();
        }, DispatcherPriority.Input);
    }

    private void ApplyLayoutMetrics()
    {
        if (IsStationLayout)
        {
            LabelFontSize = 14;
            FieldFontSize = 18;
            FieldMinHeight = 52;
            BibFontSize = 44;
            BibMinHeight = 76;
            BibPadding = new Thickness(18, 14, 18, 14);
            SubmitFontSize = 20;
            SubmitMinWidth = 160;
            SubmitPadding = new Thickness(24, 14, 24, 14);
            RecentRowHeight = 52;
            RecentBibFontSize = 20;
            RecentTimeFontSize = 16;
            RecentFixedHeight = double.NaN;
            RecentMinHeight = 280;
            RecentVerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            LabelFontSize = 11;
            FieldFontSize = 14;
            FieldMinHeight = 34;
            BibFontSize = 20;
            BibMinHeight = 40;
            BibPadding = new Thickness(10, 8, 10, 8);
            SubmitFontSize = 14;
            SubmitMinWidth = 88;
            SubmitPadding = new Thickness(12, 6, 12, 6);
            RecentRowHeight = 31;
            RecentBibFontSize = 13;
            RecentTimeFontSize = 12;
            RecentFixedHeight = 155;
            RecentMinHeight = 0;
            RecentVerticalAlignment = VerticalAlignment.Top;
        }
    }

    private void Recent_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateRecentEmptyHint();

    private void UpdateRecentEmptyHint()
    {
        if (RecentEmptyHint is null)
            return;
        RecentEmptyHint.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCurrentFileRotated(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            _recent.Clear();
            UpdateRecentEmptyHint();
        });

    private void RefreshLocations()
    {
        if (_coord is null)
            return;
        var keep = LocationBox.SelectedValue as string
                   ?? (LocationBox.SelectedItem as ManualBackupLocationRow)?.Location;
        LocationBox.ItemsSource = null;
        LocationBox.ItemsSource = _coord.Locations;
        if (!string.IsNullOrWhiteSpace(keep))
        {
            foreach (var row in _coord.Locations)
            {
                if (string.Equals(row.Location, keep, StringComparison.OrdinalIgnoreCase))
                {
                    LocationBox.SelectedItem = row;
                    return;
                }
            }
        }
        if (LocationBox.SelectedIndex < 0 && _coord.Locations.Count > 0)
            LocationBox.SelectedIndex = 0;
    }

    private void BibBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
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

    private void BibBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            e.CancelCommand();
            return;
        }
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)
            && !e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }
        var raw = (e.DataObject.GetData(DataFormats.UnicodeText) as string)
                  ?? (e.DataObject.GetData(DataFormats.Text) as string)
                  ?? "";
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        e.CancelCommand();
        if (digits.Length == 0)
            return;
        var start = tb.SelectionStart;
        var next = tb.Text.Remove(start, tb.SelectionLength).Insert(start, digits);
        tb.Text = next;
        tb.CaretIndex = start + digits.Length;
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        try
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
            var bib = new string((BibBox.Text ?? "").Where(char.IsDigit).ToArray());
            if (bib.Length == 0)
                return;
            var tap = _submit?.Invoke(loc, bib) ?? _coord.SubmitBib(loc, bib);
            if (tap is not null)
            {
                _recent.Insert(0, RecentTapRow.From(tap));
                while (_recent.Count > 5)
                    _recent.RemoveAt(_recent.Count - 1);
                BibBox.Clear();
                UpdateRecentEmptyHint();
            }
        }
        finally
        {
            FocusBibSoon();
        }
    }

    private void LocationBox_DropDownClosed(object sender, EventArgs e) => FocusBibSoon();

    private void Last5_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        FocusBib();
        e.Handled = true;
    }

    private void Panel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src)
            return;
        if (IsWithin(src, LocationBox) || IsWithin(src, BibBox) || IsWithin(src, SubmitButton))
            return;
        FocusBibSoon();
    }

    private void Panel_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not DependencyObject next)
            return;
        if (IsWithin(next, BibBox) || IsWithin(next, LocationBox) || IsWithin(next, SubmitButton))
            return;
        e.Handled = true;
        FocusBib();
    }

    private bool LocationBusy =>
        LocationBox.IsDropDownOpen || LocationBox.IsKeyboardFocusWithin;

    private void FocusBib()
    {
        if (!IsVisible || !IsEnabled || LocationBusy)
            return;
        var win = Window.GetWindow(this);
        if (win is { IsActive: false })
            return;
        BibBox.Focus();
        Keyboard.Focus(BibBox);
    }

    private static bool IsWithin(DependencyObject? child, DependencyObject? ancestor)
    {
        if (child is null || ancestor is null)
            return false;
        for (var d = child; d is not null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, ancestor))
                return true;
        }
        return false;
    }

    private sealed class RecentTapRow
    {
        public required string Bib { get; init; }
        public required string TimeDisplay { get; init; }

        public static RecentTapRow From(ManualBackupTap tap) => new()
        {
            Bib = tap.Bib,
            TimeDisplay = ManualBackupStore.FormatTodLocal(tap.TimestampUtc)
        };
    }
}
