using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Services;

/// <summary>UI-facing Manual Backup state for Timing Active + float window.</summary>
public sealed class ManualBackupCoordinator : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly ManualBackupStore _store = new();
    private readonly RunScoreOpenClient _open = new();
    private readonly ManualBackupPlayService _play;
    private string? _sessionPath;
    private string? _libraryRoot;
    private string? _office;
    private string? _relativePath;
    private string? _displayLeaf;
    private bool _enabled;
    private string _locationsText = "Finish";
    private string _openStatusText = "RunScore Open · Not connected";
    private bool _openConnected;
    private string _status = "";

    public ManualBackupCoordinator()
    {
        _play = new ManualBackupPlayService(_store, _open);
        Locations = new ObservableCollection<ManualBackupLocationRow>();
    }

    public ObservableCollection<ManualBackupLocationRow> Locations { get; }

    public bool IsEnabled
    {
        get => _enabled;
        private set
        {
            if (_enabled == value) return;
            _enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowPanel));
            OnPropertyChanged(nameof(ShowEnablePrompt));
        }
    }

    public bool ShowPanel => IsEnabled && !string.IsNullOrWhiteSpace(_sessionPath);

    public bool ShowEnablePrompt =>
        !string.IsNullOrWhiteSpace(_sessionPath) && !IsEnabled;

    public string LocationsText
    {
        get => _locationsText;
        set { if (_locationsText == value) return; _locationsText = value; OnPropertyChanged(); }
    }

    public string OpenStatusText
    {
        get => _openStatusText;
        private set { if (_openStatusText == value) return; _openStatusText = value; OnPropertyChanged(); }
    }

    public bool OpenConnected
    {
        get => _openConnected;
        private set { if (_openConnected == value) return; _openConnected = value; OnPropertyChanged(); RefreshOpenStatusText(); }
    }

    public string Status
    {
        get => _status;
        private set { if (_status == value) return; _status = value; OnPropertyChanged(); }
    }

    public string DeviceId { get; } = ManualBackupStore.GetStableWindowsDeviceId();
    public string DeviceDisplayName { get; } = ManualBackupStore.GetWindowsDeviceDisplayName();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after local office catalog is rewritten (caller may Graph-upload).</summary>
    public event EventHandler? CatalogChanged;

    public void AttachSession(string activeSessionPath, string libraryRoot, string office, string relativePath, string displayLeaf)
    {
        _sessionPath = activeSessionPath;
        _libraryRoot = libraryRoot;
        _office = office;
        _relativePath = relativePath;
        _displayLeaf = displayLeaf;
        ReloadFromDisk();
        _open.StartKeepAlive();
        _ = ProbeOpenLoopAsync();
    }

    public void DetachSession()
    {
        _open.StopKeepAlive();
        _sessionPath = null;
        IsEnabled = false;
        Locations.Clear();
        OnPropertyChanged(nameof(ShowPanel));
        OnPropertyChanged(nameof(ShowEnablePrompt));
    }

    public void ReloadFromDisk()
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        var manifest = _store.LoadManifest(_sessionPath);
        IsEnabled = manifest.Enabled;
        LocationsText = manifest.Locations.Count > 0
            ? string.Join(", ", manifest.Locations)
            : "Finish";
        RebuildLocationRows(manifest);
        OnPropertyChanged(nameof(ShowPanel));
        OnPropertyChanged(nameof(ShowEnablePrompt));
    }


    public void SaveConfig(bool enabled, IEnumerable<string> locations)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        var list = locations
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var manifest = _store.LoadManifest(_sessionPath);
        manifest.Enabled = enabled;
        manifest.Locations = list;
        manifest.Office = _office;
        manifest.SessionRelativePath = _relativePath;
        manifest.DisplayLeaf = _displayLeaf;
        _store.SaveManifest(_sessionPath, manifest);
        _store.EnsureLocationFolders(_sessionPath, list);

        // Mirror to library session if Active ≠ Library.
        if (!string.IsNullOrWhiteSpace(_libraryRoot) && !string.IsNullOrWhiteSpace(_office) && !string.IsNullOrWhiteSpace(_relativePath))
        {
            var librarySession = Path.Combine(_libraryRoot, _office, _relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!string.Equals(librarySession, _sessionPath, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.GetDirectoryName(librarySession)))
            {
                try
                {
                    Directory.CreateDirectory(librarySession);
                    _store.SaveManifest(librarySession, manifest);
                    _store.EnsureLocationFolders(librarySession, list);
                }
                catch { /* soft */ }
            }

            _store.UpsertOfficeCatalogSession(
                _libraryRoot!,
                _office!,
                _relativePath!,
                _displayLeaf ?? Path.GetFileName(_relativePath.Replace('\\', '/')) ?? _relativePath!,
                list,
                enabled);
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }

        IsEnabled = enabled;
        LocationsText = list.Count > 0 ? string.Join(", ", list) : "";
        RebuildLocationRows(manifest);
        Status = enabled ? "Mobile Backup saved." : "Mobile Backup off.";
    }

    public ManualBackupTap? SubmitBib(string location, string bib)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath) || !IsEnabled)
            return null;
        if (string.IsNullOrWhiteSpace(bib))
            return null;
        var tap = _store.AppendTap(
            _sessionPath,
            location.Trim(),
            DeviceId,
            DeviceDisplayName,
            bib.Trim());
        // Also append to library mirror when distinct (Staffing/Graph path).
        TryMirrorTapToLibrary(location, bib, tap);
        ReloadFromDisk();
        Status = "Recorded bib " + bib.Trim() + " @ " + location.Trim();
        return tap;
    }

    public bool TryVoid(string location, string tapId)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return false;
        var ok = _store.TryVoidTap(_sessionPath, location, DeviceId, tapId);
        if (ok)
            ReloadFromDisk();
        return ok;
    }

    public async Task PlayAllAsync(string location)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        var result = await _play.PlayAllAsync(_sessionPath, location).ConfigureAwait(true);
        Status = result.Message;
        ReloadFromDisk();
    }

    public async Task PlaySegmentAsync(string location, string deviceId, string? segment, bool replay)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        var result = await _play.PlaySegmentAsync(_sessionPath, location, deviceId, segment, replay)
            .ConfigureAwait(true);
        Status = result.Message;
        ReloadFromDisk();
    }

    public void CreateNewFile(string location, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        _store.CreateNewFile(_sessionPath, location, deviceId);
        Status = "Created new file for " + deviceId + " @ " + location;
        ReloadFromDisk();
    }

    public void CreateNewFileAll(string location)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        foreach (var id in _store.ListDeviceIds(_sessionPath, location))
            _store.CreateNewFile(_sessionPath, location, id);
        Status = "Created new files for all devices @ " + location;
        ReloadFromDisk();
    }

    public void SetPaused(string location, string deviceId, bool paused)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        _store.SetPaused(_sessionPath, location, deviceId, paused);
        ReloadFromDisk();
    }

    private void TryMirrorTapToLibrary(string location, string bib, ManualBackupTap tap)
    {
        if (string.IsNullOrWhiteSpace(_libraryRoot) || string.IsNullOrWhiteSpace(_office) || string.IsNullOrWhiteSpace(_relativePath))
            return;
        var librarySession = Path.Combine(_libraryRoot, _office, _relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (string.Equals(librarySession, _sessionPath, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            Directory.CreateDirectory(librarySession);
            // Re-append same logical tap (new id ok) so Graph Finish can pick library up.
            _store.AppendTap(librarySession, location, DeviceId, DeviceDisplayName, bib, tap.TimestampUtc);
        }
        catch { /* soft */ }
    }

    private void RebuildLocationRows(ManualBackupManifest manifest)
    {
        Locations.Clear();
        if (!manifest.Enabled || string.IsNullOrWhiteSpace(_sessionPath))
            return;
        foreach (var loc in manifest.Locations)
        {
            var row = new ManualBackupLocationRow { Location = loc };
            foreach (var deviceId in _store.ListDeviceIds(_sessionPath!, loc))
            {
                var state = _store.LoadOrCreateDevice(_sessionPath!, loc, deviceId, deviceId);
                var taps = _store.ReadCurrentTaps(_sessionPath!, loc, deviceId);
                var unplayed = taps.Skip(state.PlayCursor).Count(t => !t.Voided);
                var archives = _store.ListArchiveFiles(_sessionPath!, loc, deviceId);
                var deviceRow = new ManualBackupDeviceRow
                {
                    Location = loc,
                    DeviceId = deviceId,
                    DisplayName = string.IsNullOrWhiteSpace(state.DisplayName) ? deviceId : state.DisplayName,
                    Paused = state.Paused,
                    CurrentCount = taps.Count,
                    UnplayedCount = unplayed,
                    Coordinator = this
                };
                foreach (var arch in archives)
                {
                    deviceRow.Files.Add(new ManualBackupFileCard
                    {
                        Location = loc,
                        DeviceId = deviceId,
                        FileName = arch,
                        IsCurrent = false,
                        Count = _store.ReadSegmentTaps(Path.Combine(_store.GetDeviceDir(_sessionPath!, loc, deviceId), arch)).Count,
                        Coordinator = this
                    });
                }
                deviceRow.Files.Add(new ManualBackupFileCard
                {
                    Location = loc,
                    DeviceId = deviceId,
                    FileName = ManualBackupStore.CurrentFileName,
                    DisplayLabel = "Current",
                    IsCurrent = true,
                    Count = taps.Count,
                    UnplayedCount = unplayed,
                    Coordinator = this
                });
                row.Devices.Add(deviceRow);
            }
            Locations.Add(row);
        }
    }

    private async Task ProbeOpenLoopAsync()
    {
        while (!string.IsNullOrWhiteSpace(_sessionPath))
        {
            OpenConnected = _open.IsConnected;
            try { await Task.Delay(1500).ConfigureAwait(true); }
            catch { break; }
        }
    }

    private void RefreshOpenStatusText() =>
        OpenStatusText = OpenConnected
            ? "RunScore Open · Connected · localhost:" + RunScoreExternalTimers.RunScoreOpenPort
            : "RunScore Open · Not connected · localhost:" + RunScoreExternalTimers.RunScoreOpenPort;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ValueTask DisposeAsync()
    {
        DetachSession();
        return _open.DisposeAsync();
    }
}

public sealed class ManualBackupLocationRow
{
    public string Location { get; set; } = "";
    public ObservableCollection<ManualBackupDeviceRow> Devices { get; } = new();
}

public sealed class ManualBackupDeviceRow
{
    public string Location { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Paused { get; set; }
    public int CurrentCount { get; set; }
    public int UnplayedCount { get; set; }
    public ManualBackupCoordinator? Coordinator { get; set; }
    public ObservableCollection<ManualBackupFileCard> Files { get; } = new();
}

public sealed class ManualBackupFileCard
{
    public string Location { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string DisplayLabel { get; set; } = "";
    public bool IsCurrent { get; set; }
    public int Count { get; set; }
    public int UnplayedCount { get; set; }
    public ManualBackupCoordinator? Coordinator { get; set; }
    public string Title => string.IsNullOrWhiteSpace(DisplayLabel)
        ? (IsCurrent ? "Current" : FileName)
        : DisplayLabel;
}

