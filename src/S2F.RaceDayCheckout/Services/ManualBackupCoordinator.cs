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
    private string _locationsText = "";
    private string _openStatusText = "RunScore Open · Not connected";
    private bool _openConnected;
    private string _status = "";
    private CancellationTokenSource? _activePlayCts;
    /// <summary>Location for the in-flight continuous Play (multiple devices may Join).</summary>
    private string? _activePlayLocation;
    private readonly object _playGate = new();
    private readonly HashSet<string> _playingDeviceKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Remembers which device Past expanders stay open across ReloadFromDisk.</summary>
    private readonly HashSet<string> _pastExpandedKeys = new(StringComparer.OrdinalIgnoreCase);

    public ManualBackupCoordinator()
    {
        _play = new ManualBackupPlayService(_store, _open);
        Locations = new ObservableCollection<ManualBackupLocationRow>();
    }

    private static string DevicePlayKey(string location, string deviceId) =>
        location + "\n" + deviceId;

    internal void SetPastExpanded(string location, string deviceId, bool expanded)
    {
        var key = DevicePlayKey(location, deviceId);
        if (expanded) _pastExpandedKeys.Add(key);
        else _pastExpandedKeys.Remove(key);
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

    /// <summary>Clear header status after an Alert dialog already explained the failure.</summary>
    public void ClearStatus() => Status = "";

    public string DeviceId { get; } = ManualBackupStore.GetStableWindowsDeviceId();
    public string DeviceDisplayName { get; } = ManualBackupStore.GetWindowsDeviceDisplayName();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after local office catalog is rewritten (caller may Graph-upload).</summary>
    public event EventHandler? CatalogChanged;

    /// <summary>Raised after Create New File / Create New File All rotates Current → Prior.</summary>
    public event EventHandler? CurrentFileRotated;

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
        CancelActivePlay();
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
        // Collapse twin Priors from older dual Active/Library Create New File.
        try { _store.DeduplicateIdenticalArchives(_sessionPath); }
        catch { /* soft */ }
        var manifest = _store.LoadManifest(_sessionPath);
        IsEnabled = manifest.Enabled;
        LocationsText = manifest.Locations.Count > 0
            ? string.Join(", ", manifest.Locations)
            : "";
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
        Status = enabled ? "Manual Backup saved." : "Manual Backup off.";
    }

    public ManualBackupTap? SubmitBib(string location, string bib)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath) || !IsEnabled)
            return null;
        var digits = new string((bib ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
            return null;
        ManualBackupTap tap;
        try
        {
            tap = _store.AppendTap(
                _sessionPath,
                location.Trim(),
                DeviceId,
                DeviceDisplayName,
                digits);
        }
        catch (IOException ex)
        {
            Status = "Could not save bib — try again. " + ex.Message;
            return null;
        }
        // Also append to library mirror when distinct (Staffing/Graph path).
        TryMirrorTapToLibrary(location, digits, tap);
        ReloadFromDisk();
        Status = "Recorded bib " + digits + " @ " + location.Trim();
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

    public async Task<ManualBackupPlayResult> PlayAllAsync(string location)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
        {
            var missing = new ManualBackupPlayResult { Success = false, Message = "No Active session for Manual Backup." };
            Status = missing.Message;
            return missing;
        }

        var deviceIds = _store.ListDeviceIds(_sessionPath, location).ToList();
        if (deviceIds.Count == 0)
        {
            var empty = new ManualBackupPlayResult
            {
                Success = true,
                Message = "No Manual Backup devices at " + location + " yet.",
                LinesSent = 0
            };
            Status = empty.Message;
            return empty;
        }

        // Play All resumes every device at this location.
        foreach (var id in deviceIds)
            _store.SetPaused(_sessionPath, location, id, false);

        // Already playing this location — Join every device into the live session.
        if (_activePlayCts is not null
            && string.Equals(_activePlayLocation, location, StringComparison.OrdinalIgnoreCase))
        {
            lock (_playGate)
            {
                foreach (var id in deviceIds)
                    _playingDeviceKeys.Add(DevicePlayKey(location, id));
            }

            ApplyPlayingFlagsToRows();
            ReloadFromDisk();
            var joined = new ManualBackupPlayResult
            {
                Success = true,
                Message = "Playing all devices at " + location + "."
            };
            Status = joined.Message;
            return joined;
        }

        if (_activePlayCts is not null)
        {
            var busy = new ManualBackupPlayResult
            {
                Success = false,
                Message = "Already playing " + (_activePlayLocation ?? "another location")
                    + " — Pause first, or Play devices at that location."
            };
            Status = busy.Message;
            return busy;
        }

        return await RunLocationContinuousPlayAsync(location, deviceIds).ConfigureAwait(true);
    }

    public async Task<ManualBackupPlayResult> PlaySegmentAsync(
        string location, string deviceId, string? segment, bool replay)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
        {
            var missing = new ManualBackupPlayResult { Success = false, Message = "No Active session for Manual Backup." };
            Status = missing.Message;
            return missing;
        }

        var isCurrent = string.IsNullOrWhiteSpace(segment)
            || string.Equals(segment, ManualBackupStore.CurrentFileName, StringComparison.OrdinalIgnoreCase);

        // Archives / Replay stay one-shot; Current Play is continuous until Pause.
        if (!isCurrent || replay)
        {
            if (_activePlayCts is not null)
            {
                var busy = new ManualBackupPlayResult
                {
                    Success = false,
                    Message = "Already playing — Pause Current first, then Replay."
                };
                Status = busy.Message;
                return busy;
            }

            Status = replay ? "Replaying…" : "Playing…";
            var oneshot = await _play.PlaySegmentAsync(
                    _sessionPath, location, deviceId, segment, replay, CancellationToken.None)
                .ConfigureAwait(true);
            Status = oneshot.Message;
            ReloadFromDisk();
            return oneshot;
        }

        _store.SetPaused(_sessionPath, location, deviceId, false);

        // Same location already Playing — Join this device without stopping the first.
        if (_activePlayCts is not null
            && string.Equals(_activePlayLocation, location, StringComparison.OrdinalIgnoreCase))
        {
            lock (_playGate)
                _playingDeviceKeys.Add(DevicePlayKey(location, deviceId));
            ApplyPlayingFlagsToRows();
            ReloadFromDisk();
            var joined = new ManualBackupPlayResult
            {
                Success = true,
                Message = "Joined Play for " + deviceId + " @ " + location + "."
            };
            Status = joined.Message;
            return joined;
        }

        if (_activePlayCts is not null)
        {
            var busy = new ManualBackupPlayResult
            {
                Success = false,
                Message = "Already playing " + (_activePlayLocation ?? "another location")
                    + " — Pause first, or Play another device at that location."
            };
            Status = busy.Message;
            return busy;
        }

        return await RunLocationContinuousPlayAsync(location, new[] { deviceId }).ConfigureAwait(true);
    }

    /// <summary>Stops continuous Play for this device only — other devices at the location keep Playing.</summary>
    public void PausePlaying(string location, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        var key = DevicePlayKey(location, deviceId);
        bool empty;
        lock (_playGate)
        {
            if (!_playingDeviceKeys.Contains(key))
                return;
            _playingDeviceKeys.Remove(key);
            empty = _playingDeviceKeys.Count == 0;
        }

        _store.SetPaused(_sessionPath, location, deviceId, true);
        ApplyPlayingFlagsToRows();
        if (empty)
        {
            _activePlayCts?.Cancel();
            Status = "Pausing…";
        }
        else
        {
            Status = "Paused " + deviceId + " — other devices still playing.";
            ReloadFromDisk();
        }
    }

    /// <summary>Pause every Playing device at this location (Play All ↔ Pause All).</summary>
    public void PauseAll(string location)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath) || string.IsNullOrWhiteSpace(location))
            return;

        List<string> deviceIds;
        lock (_playGate)
        {
            var prefix = location + "\n";
            deviceIds = _playingDeviceKeys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k.Substring(prefix.Length))
                .ToList();
            foreach (var id in deviceIds)
                _playingDeviceKeys.Remove(DevicePlayKey(location, id));
        }

        if (deviceIds.Count == 0)
        {
            // Nothing tracked as Playing — still cancel if this location owns the session.
            if (string.Equals(_activePlayLocation, location, StringComparison.OrdinalIgnoreCase))
                _activePlayCts?.Cancel();
            return;
        }

        foreach (var id in deviceIds)
            _store.SetPaused(_sessionPath, location, id, true);

        ApplyPlayingFlagsToRows();
        _activePlayCts?.Cancel();
        Status = "Paused all at " + location + ".";
        ReloadFromDisk();
    }

    async Task<ManualBackupPlayResult> RunLocationContinuousPlayAsync(
        string location,
        IEnumerable<string> deviceIds)
    {
        Status = "Playing " + location + " — waiting for taps…";
        var cts = BeginPlay(location, deviceIds.Select(id => (location, id)));
        var progress = new Progress<int>(n =>
        {
            Status = "Playing " + location + "… " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " sent";
            ReloadFromDisk();
        });
        try
        {
            var result = await _play.PlayAllContinuousAsync(
                    _sessionPath!,
                    location,
                    cts.Token,
                    progress,
                    () => PlayingDeviceIdsForLocation(location))
                .ConfigureAwait(true);
            Status = result.Message;
            return result;
        }
        catch (OperationCanceledException)
        {
            var paused = new ManualBackupPlayResult
            {
                Success = true,
                Message = "Paused for " + location + "."
            };
            Status = paused.Message;
            return paused;
        }
        finally
        {
            EndPlay(cts);
            ReloadFromDisk();
        }
    }

    IReadOnlyCollection<string> PlayingDeviceIdsForLocation(string location)
    {
        var prefix = location + "\n";
        lock (_playGate)
        {
            var list = new List<string>();
            foreach (var key in _playingDeviceKeys)
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(key.Substring(prefix.Length));
            }

            return list;
        }
    }

    public void CreateNewFile(string location, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        if (_store.CreateNewFile(_sessionPath, location, deviceId) is null)
        {
            Status = "No taps to archive for " + deviceId + " @ " + location;
            return;
        }

        Status = "Created new file for " + deviceId + " @ " + location;
        ReloadFromDisk();
        CurrentFileRotated?.Invoke(this, EventArgs.Empty);
    }

    public void CreateNewFileAll(string location)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        var n = 0;
        foreach (var id in _store.ListDeviceIds(_sessionPath, location))
        {
            if (_store.CreateNewFile(_sessionPath, location, id) is not null)
                n++;
        }

        if (n == 0)
        {
            Status = "No taps to archive @ " + location;
            return;
        }

        Status = "Created new files for " + n + " device(s) @ " + location;
        ReloadFromDisk();
        CurrentFileRotated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Delete a Prior archive from Active (+ Library mirror). Does not touch Current.</summary>
    public bool TryDeleteArchive(string location, string deviceId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return false;
        if (!_store.DeleteArchiveFile(_sessionPath, location, deviceId, fileName))
            return false;

        if (!string.IsNullOrWhiteSpace(_libraryRoot)
            && !string.IsNullOrWhiteSpace(_office)
            && !string.IsNullOrWhiteSpace(_relativePath))
        {
            try
            {
                var librarySession = Path.Combine(
                    _libraryRoot!,
                    _office!,
                    _relativePath!.Replace('/', Path.DirectorySeparatorChar));
                if (!string.Equals(librarySession, _sessionPath, StringComparison.OrdinalIgnoreCase))
                    _store.DeleteArchiveFile(librarySession, location, deviceId, fileName);
            }
            catch { /* soft */ }
        }

        Status = "Deleted archive " + Path.GetFileName(fileName);
        ReloadFromDisk();
        return true;
    }

    public void SetPaused(string location, string deviceId, bool paused)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        _store.SetPaused(_sessionPath, location, deviceId, paused);
        if (paused)
        {
            bool empty;
            lock (_playGate)
            {
                _playingDeviceKeys.Remove(DevicePlayKey(location, deviceId));
                empty = _playingDeviceKeys.Count == 0;
            }

            ApplyPlayingFlagsToRows();
            if (empty)
                _activePlayCts?.Cancel();
        }

        ReloadFromDisk();
    }

    private CancellationTokenSource BeginPlay(
        string location,
        IEnumerable<(string Location, string DeviceId)> devices)
    {
        CancelActivePlay();
        var cts = new CancellationTokenSource();
        _activePlayCts = cts;
        _activePlayLocation = location;
        lock (_playGate)
        {
            _playingDeviceKeys.Clear();
            foreach (var (loc, id) in devices)
                _playingDeviceKeys.Add(DevicePlayKey(loc, id));
        }

        ApplyPlayingFlagsToRows();
        return cts;
    }

    private void EndPlay(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_activePlayCts, cts))
        {
            _activePlayCts = null;
            _activePlayLocation = null;
        }

        lock (_playGate)
            _playingDeviceKeys.Clear();
        ApplyPlayingFlagsToRows();
        try { cts.Dispose(); } catch { /* ignore */ }
    }

    private void CancelActivePlay()
    {
        try { _activePlayCts?.Cancel(); } catch { /* ignore */ }
        _activePlayCts = null;
        _activePlayLocation = null;
        lock (_playGate)
            _playingDeviceKeys.Clear();
        ApplyPlayingFlagsToRows();
    }

    private void ApplyPlayingFlagsToRows()
    {
        HashSet<string> snapshot;
        lock (_playGate)
            snapshot = new HashSet<string>(_playingDeviceKeys, StringComparer.OrdinalIgnoreCase);

        foreach (var loc in Locations)
        {
            var anyPlaying = false;
            foreach (var device in loc.Devices)
            {
                var playing = snapshot.Contains(DevicePlayKey(device.Location, device.DeviceId));
                if (playing)
                    anyPlaying = true;
                foreach (var file in device.Files)
                    file.IsPlaying = file.IsCurrent && playing;
            }

            loc.IsPlayingAny = anyPlaying;
        }
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
            var row = new ManualBackupLocationRow { Location = loc, Coordinator = this };
            var anyPlaying = false;
            foreach (var deviceId in _store.ListDeviceIds(_sessionPath!, loc))
            {
                // Refresh this Windows laptop's display name; never overwrite phone names with deviceId.
                var preferredName = string.Equals(deviceId, DeviceId, StringComparison.OrdinalIgnoreCase)
                    ? DeviceDisplayName
                    : "";
                var state = _store.LoadOrCreateDevice(_sessionPath!, loc, deviceId, preferredName);
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
                bool devicePlaying;
                lock (_playGate)
                    devicePlaying = _playingDeviceKeys.Contains(DevicePlayKey(loc, deviceId));
                if (devicePlaying)
                    anyPlaying = true;
                var deviceDir = _store.GetDeviceDir(_sessionPath!, loc, deviceId);
                foreach (var arch in archives)
                {
                    var archTaps = _store.ReadSegmentTaps(Path.Combine(deviceDir, arch));
                    var (archFirst, archLast) = ManualBackupStore.FormatFirstLastTapLines(archTaps);
                    var card = new ManualBackupFileCard
                    {
                        Location = loc,
                        DeviceId = deviceId,
                        FileName = arch,
                        DisplayLabel = ManualBackupStore.FormatArchiveDisplayName(arch),
                        IsCurrent = false,
                        Paused = state.Paused,
                        Count = archTaps.Count,
                        FirstTapLine = archFirst,
                        LastTapLine = archLast,
                        Coordinator = this
                    };
                    deviceRow.Files.Add(card);
                    deviceRow.Archives.Add(card);
                }
                var (curFirst, curLast) = ManualBackupStore.FormatFirstLastTapLines(taps);
                var current = new ManualBackupFileCard
                {
                    Location = loc,
                    DeviceId = deviceId,
                    FileName = ManualBackupStore.CurrentFileName,
                    DisplayLabel = "Current",
                    IsCurrent = true,
                    IsPlaying = devicePlaying,
                    Paused = state.Paused,
                    Count = taps.Count,
                    UnplayedCount = unplayed,
                    FirstTapLine = curFirst,
                    LastTapLine = curLast,
                    Coordinator = this
                };
                deviceRow.Files.Add(current);
                deviceRow.CurrentFile = current;
                deviceRow.PastExpanded = _pastExpandedKeys.Contains(DevicePlayKey(loc, deviceId));
                row.Devices.Add(deviceRow);
            }

            row.IsPlayingAny = anyPlaying;
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

public sealed class ManualBackupLocationRow : INotifyPropertyChanged
{
    private bool _isPlayingAny;

    public string Location { get; set; } = "";
    public ManualBackupCoordinator? Coordinator { get; set; }
    public ObservableCollection<ManualBackupDeviceRow> Devices { get; } = new();

    /// <summary>True while any device Current at this location is mid-Play (Pause All replaces Play All).</summary>
    public bool IsPlayingAny
    {
        get => _isPlayingAny;
        set
        {
            if (_isPlayingAny == value) return;
            _isPlayingAny = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlayingAny)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPlayAll)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPauseAll)));
        }
    }

    public bool ShowPlayAll => !IsPlayingAny;
    public bool ShowPauseAll => IsPlayingAny;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ManualBackupDeviceRow : INotifyPropertyChanged
{
    private bool _pastExpanded;

    public string Location { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Paused { get; set; }
    public int CurrentCount { get; set; }
    public int UnplayedCount { get; set; }
    public ManualBackupCoordinator? Coordinator { get; set; }
    /// <summary>All files (Current + Past) — used for Play pulse flags.</summary>
    public ObservableCollection<ManualBackupFileCard> Files { get; } = new();
    /// <summary>Prior archives only — shown under the Past expander.</summary>
    public ObservableCollection<ManualBackupFileCard> Archives { get; } = new();
    public ManualBackupFileCard? CurrentFile { get; set; }
    public bool HasArchives => Archives.Count > 0;
    public string PastHeader =>
        Archives.Count == 1 ? "Past (1)" : "Past (" + Archives.Count.ToString() + ")";

    public bool PastExpanded
    {
        get => _pastExpanded;
        set
        {
            if (_pastExpanded == value) return;
            _pastExpanded = value;
            Coordinator?.SetPastExpanded(Location, DeviceId, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PastExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ManualBackupFileCard : INotifyPropertyChanged
{
    private bool _isPlaying;

    public string Location { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string DisplayLabel { get; set; } = "";
    public bool IsCurrent { get; set; }
    /// <summary>Sticky device pause (Play All skips). Cleared when Play is pressed.</summary>
    public bool Paused { get; set; }
    public int Count { get; set; }
    public int UnplayedCount { get; set; }
    /// <summary>First non-voided tap (<c>First  h:mm:ss tt</c>), or <c>First —</c>.</summary>
    public string FirstTapLine { get; set; } = "First —";
    /// <summary>Last non-voided tap (<c>Last  …</c>), or <c>Last —</c>.</summary>
    public string LastTapLine { get; set; } = "Last —";
    public ManualBackupCoordinator? Coordinator { get; set; }

    /// <summary>Prior archives only — Current cannot be deleted from the strip.</summary>
    public bool ShowDelete => !IsCurrent;

    /// <summary>True while this Current file is mid-Play (Pause replaces Play).</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPlay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPause)));
        }
    }

    /// <summary>Single transport slot: Play when idle, Pause only while Current is playing.</summary>
    public bool ShowPlay => !IsCurrent || !IsPlaying;
    public bool ShowPause => IsCurrent && IsPlaying;

    public string Title => string.IsNullOrWhiteSpace(DisplayLabel)
        ? (IsCurrent ? "Current" : FileName)
        : DisplayLabel;

    /// <summary>Entry total for the chip (e.g. <c>12 entries</c>).</summary>
    public string CountLabel =>
        Count == 1 ? "1 entry" : Count.ToString() + " entries";

    public event PropertyChangedEventHandler? PropertyChanged;
}

