using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.ViewModels;

public sealed class RaceStartRowViewModel : INotifyPropertyChanged
{
    private string _categoryText = string.Empty;
    private string _startHourText = string.Empty;
    private string _startMinuteText = string.Empty;
    private string _liveDisplay = string.Empty;
    private bool _isCountUp;
    private TimeSpan? _gunStartTimeOfDay;
    private RaceStartEntry _entry;
    private readonly Action<RaceStartRowViewModel>? _onCommit;
    private readonly Action<RaceStartRowViewModel>? _onRemove;

    private RaceStartRowViewModel(bool isAddTile)
    {
        IsAddTile = isAddTile;
        _entry = new RaceStartEntry { Category = "+", StartTimeOfDay = TimeSpan.Zero };
    }

    public RaceStartRowViewModel(
        RaceStartEntry entry,
        Action<RaceStartRowViewModel>? onCommit = null,
        Action<RaceStartRowViewModel>? onRemove = null,
        bool draftUnsetTime = false)
    {
        _entry = entry;
        _onCommit = onCommit;
        _onRemove = onRemove;
        _categoryText = entry.Category;
        _gunStartTimeOfDay = entry.GunStartTimeOfDay;
        if (draftUnsetTime)
        {
            _startHourText = string.Empty;
            _startMinuteText = string.Empty;
        }
        else
            ApplyTimeOfDay(entry.StartTimeOfDay);
        Tick(DateTime.Now);
    }

    public static RaceStartRowViewModel CreateAddTile() => new(isAddTile: true);

    /// <summary>Blank category + unset time (UI shows 00:00 helper only).</summary>
    public static RaceStartRowViewModel CreateDraft(
        Action<RaceStartRowViewModel>? onCommit = null,
        Action<RaceStartRowViewModel>? onRemove = null) =>
        new(
            new RaceStartEntry { Category = string.Empty, StartTimeOfDay = TimeSpan.Zero },
            onCommit,
            onRemove,
            draftUnsetTime: true);

    public bool IsAddTile { get; }
    public bool IsClockTile => !IsAddTile;

    public RaceStartEntry Entry => _entry;

    public string CategoryText
    {
        get => _categoryText;
        set
        {
            if (!Set(ref _categoryText, value ?? string.Empty))
                return;
            OnPropertyChanged(nameof(Category));
        }
    }

    /// <summary>Display alias for bindings that expect Category.</summary>
    public string Category => _categoryText;

    /// <summary>Hours digits only (0–23). Empty = helper "00", not a stored time.</summary>
    public string StartHourText
    {
        get => _startHourText;
        set
        {
            var digits = DigitsOnly(value, maxLen: 2);
            if (!Set(ref _startHourText, digits))
                return;
            OnPropertyChanged(nameof(StartTimeText));
            OnPropertyChanged(nameof(StartDisplay));
            OnPropertyChanged(nameof(ShowStartHourPlaceholder));
            OnPropertyChanged(nameof(HasStartTime));
            Tick(DateTime.Now);
        }
    }

    /// <summary>Minutes digits only (0–59). Empty = helper "00", not a stored time.</summary>
    public string StartMinuteText
    {
        get => _startMinuteText;
        set
        {
            var digits = DigitsOnly(value, maxLen: 2);
            if (!Set(ref _startMinuteText, digits))
                return;
            OnPropertyChanged(nameof(StartTimeText));
            OnPropertyChanged(nameof(StartDisplay));
            OnPropertyChanged(nameof(ShowStartMinutePlaceholder));
            OnPropertyChanged(nameof(HasStartTime));
            Tick(DateTime.Now);
        }
    }

    public bool ShowStartHourPlaceholder => string.IsNullOrEmpty(_startHourText);
    public bool ShowStartMinutePlaceholder => string.IsNullOrEmpty(_startMinuteText);
    public bool HasStartTime => TryReadTimeOfDay(out _);

    public bool HasGunStart => _gunStartTimeOfDay is not null;

    public string GunStartDisplay =>
        _gunStartTimeOfDay is { } gun
            ? "Gun " + RaceStartClock.FormatGunHhMmSs(gun)
            : string.Empty;

    /// <summary>Composed <c>HH:MM</c> for lists / admin display.</summary>
    public string StartTimeText =>
        TryReadTimeOfDay(out var tod)
            ? RaceStartClock.FormatStartHhMm(tod)
            : string.Empty;

    public string StartDisplay => StartTimeText;

    public string LiveDisplay
    {
        get => _liveDisplay;
        private set => Set(ref _liveDisplay, value);
    }

    public bool IsCountUp
    {
        get => _isCountUp;
        private set => Set(ref _isCountUp, value);
    }

    public void Tick(DateTime localNow)
    {
        if (IsAddTile)
            return;
        if (!TryGetEffectiveStart(out var tod))
        {
            LiveDisplay = "—";
            IsCountUp = false;
            return;
        }

        // Live board shows whole seconds only; gun still stores HH:MM:SS.ff for truth.
        LiveDisplay = RaceStartClock.FormatRelative(localNow, tod, centiseconds: false);
        IsCountUp = RaceStartClock.IsCountUp(localNow, tod, centiseconds: false);
    }

    /// <summary>Stamp gun from wall clock (centisecond precision). Live clock switches to this time.</summary>
    public void ApplyGunStart(TimeSpan gunTimeOfDay)
    {
        var ms = (gunTimeOfDay.Milliseconds / 10) * 10;
        _gunStartTimeOfDay = new TimeSpan(
            0,
            (int)gunTimeOfDay.TotalHours % 24,
            gunTimeOfDay.Minutes,
            gunTimeOfDay.Seconds,
            ms);
        OnPropertyChanged(nameof(HasGunStart));
        OnPropertyChanged(nameof(GunStartDisplay));
        Tick(DateTime.Now);
    }

    /// <summary>Clear gun — live clock reverts to scheduled Start Time.</summary>
    public void ClearGunStart()
    {
        if (_gunStartTimeOfDay is null)
            return;
        _gunStartTimeOfDay = null;
        OnPropertyChanged(nameof(HasGunStart));
        OnPropertyChanged(nameof(GunStartDisplay));
        Tick(DateTime.Now);
    }

    public void CommitFromUi()
    {
        if (IsAddTile)
            return;
        _onCommit?.Invoke(this);
    }

    public void RequestRemove()
    {
        if (IsAddTile)
            return;
        _onRemove?.Invoke(this);
    }

    public bool TryBuildEntry(out RaceStartEntry entry, out string? error)
    {
        entry = _entry;
        error = null;
        var category = _categoryText.Trim();
        var hasTime = TryReadTimeOfDay(out var tod);
        var hasGun = _gunStartTimeOfDay is not null;

        if (string.IsNullOrWhiteSpace(category) && !hasTime && !hasGun)
        {
            // Incomplete draft — stay on the board quietly.
            return false;
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            error = "Enter a category name on the clock.";
            return false;
        }

        if (!hasTime)
        {
            if (hasGun)
            {
                // Capture Gun without typed Start Time — seed scheduled HH:MM from gun.
                var g = _gunStartTimeOfDay!.Value;
                if (!RaceStartClock.TryNormalizeHourMinute(
                        (int)g.TotalHours % 24, g.Minutes, out tod))
                {
                    error = "Couldn't derive start time from gun.";
                    return false;
                }
            }
            else if (string.IsNullOrEmpty(_startHourText) && string.IsNullOrEmpty(_startMinuteText))
            {
                // Label typed but time still helper-only — wait without nagging every blur.
                return false;
            }
            else
            {
                error = "Enter start time as HH:MM (24-hour).";
                return false;
            }
        }

        entry = new RaceStartEntry
        {
            Category = category,
            StartTimeOfDay = tod,
            GunStartTimeOfDay = _gunStartTimeOfDay
        };
        _entry = entry;
        _categoryText = category;
        ApplyTimeOfDay(tod);
        OnPropertyChanged(nameof(CategoryText));
        OnPropertyChanged(nameof(StartHourText));
        OnPropertyChanged(nameof(StartMinuteText));
        OnPropertyChanged(nameof(StartTimeText));
        OnPropertyChanged(nameof(ShowStartHourPlaceholder));
        OnPropertyChanged(nameof(ShowStartMinutePlaceholder));
        OnPropertyChanged(nameof(HasStartTime));
        OnPropertyChanged(nameof(HasGunStart));
        OnPropertyChanged(nameof(GunStartDisplay));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(StartDisplay));
        Tick(DateTime.Now);
        return true;
    }

    private bool TryGetEffectiveStart(out TimeSpan tod)
    {
        if (_gunStartTimeOfDay is { } gun)
        {
            tod = gun;
            return true;
        }

        return TryReadTimeOfDay(out tod);
    }

    private void ApplyTimeOfDay(TimeSpan tod)
    {
        _startHourText = ((int)tod.TotalHours).ToString("00", CultureInfo.InvariantCulture);
        _startMinuteText = tod.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    private bool TryReadTimeOfDay(out TimeSpan tod)
    {
        tod = default;
        // Both parts required — empty fields are helper text, not midnight.
        if (_startHourText.Length == 0 || _startMinuteText.Length == 0)
            return false;
        if (!int.TryParse(_startHourText, NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(_startMinuteText, NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
            return false;
        return RaceStartClock.TryNormalizeHourMinute(hour, minute, out tod);
    }

    private static string DigitsOnly(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var sb = new StringBuilder(maxLen);
        foreach (var ch in value)
        {
            if (!char.IsDigit(ch))
                continue;
            sb.Append(ch);
            if (sb.Length >= maxLen)
                break;
        }

        return sb.ToString();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
