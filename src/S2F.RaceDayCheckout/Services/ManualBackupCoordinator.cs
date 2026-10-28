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
        if (_activePlayCts is not null)
        {
            var busy = new ManualBackupPlayResult
            {
                Success = false,
                Message = "Already playing — Pause first, or wait for Play to finish."
            };
            Status = busy.Message;
            return busy;
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

        Status = "Playing " + location + " — waiting for taps…";
        var cts = BeginPlay(deviceIds.Select(id => (location, id)));
        var progress = new Progress<int>(n =>
        {
            Status = "Playing " + location + "… " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " sent";
            ReloadFromDisk();
        });
        try
        {
            var result = await _play.PlayAllContinuousAsync(_sessionPath, location, cts.Token, progress)
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

    public async Task<ManualBackupPlayResult> PlaySegmentAsync(
        string location, string deviceId, string? segment, bool replay)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
        {
            var missing = new ManualBackupPlayResult { Success = false, Message = "No Active session for Manual Backup." };
            Status = missing.Message;
            return missing;
        }
        if (_activePlayCts is not null)
        {
            var busy = new ManualBackupPlayResult
            {
                Success = false,
                Message = "Already playing — Pause first, or wait for Play to finish."
            };
            Status = busy.Message;
            return busy;
        }

        var isCurrent = string.IsNullOrWhiteSpace(segment)
            || string.Equals(segment, ManualBackupStore.CurrentFileName, StringComparison.OrdinalIgnoreCase);

        // Archives / Replay stay one-shot; Current Play is continuous until Pause.
        if (!isCurrent || replay)
        {
            Status = replay ? "Replaying…" : "Playing…";
            var oneshot = await _play.PlaySegmentAsync(
                    _sessionPath, location, deviceId, segment, replay, CancellationToken.None)
                .ConfigureAwait(true);
            Status = oneshot.Message;
            ReloadFromDisk();
            return oneshot;
        }

        _store.SetPaused(_sessionPath, location, deviceId, false);
        Status = "Playing Current — waiting for taps…";
        var cts = BeginPlay(new[] { (location, deviceId) });
        var progress = new Progress<int>(n =>
        {
            Status = "Playing… " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " sent";
            ReloadFromDisk();
        });
        try
        {
            var result = await _play.PlayCurrentContinuousAsync(
                    _sessionPath, location, deviceId, cts.Token, progress)
                .ConfigureAwait(true);
            Status = result.Message;
            return result;
        }
        catch (OperationCanceledException)
        {
            var paused = new ManualBackupPlayResult { Success = true, Message = "Paused." };
            Status = paused.Message;
            return paused;
        }
        finally
        {
            EndPlay(cts);
            ReloadFromDisk();
        }
    }

    /// <summary>Stops continuous Play for this device — Pause replaces Play only while playing.</summary>
    public void PausePlaying(string location, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;
        if (!_playingDeviceKeys.Contains(DevicePlayKey(location, deviceId)))
            return;
        _store.SetPaused(_sessionPath, location, deviceId, true);
        _activePlayCts?.Cancel();
        Status = "Pausing…";
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
            _activePlayCts?.Cancel();
        ReloadFromDisk();
    }

    private CancellationTokenSource BeginPlay(IEnumerable<(string Location, string DeviceId)> devices)
    {
        CancelActivePlay();
        var cts = new CancellationTokenSource();
        _activePlayCts = cts;
        _playingDeviceKeys.Clear();
        foreach (var (loc, id) in devices)
            _playingDeviceKeys.Add(DevicePlayKey(loc, id));
        ApplyPlayingFlagsToRows();
        return cts;
    }

    private void EndPlay(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_activePlayCts, cts))
            _activePlayCts = null;
        _playingDeviceKeys.Clear();
        ApplyPlayingFlagsToRows();
        try { cts.Dispose(); } catch { /* ignore */ }
    }

    private void CancelActivePlay()
    {
        try { _activePlayCts?.Cancel(); } catch { /* ignore */ }
        _activePlayCts = null;
        _playingDeviceKeys.Clear();
        ApplyPlayingFlagsToRows();
    }

    private void ApplyPlayingFlagsToRows()
    {
        foreach (var loc in Locations)
        {
            foreach (var device in loc.Devices)
            {
                var playing = _playingDeviceKeys.Contains(DevicePlayKey(device.Location, device.DeviceId));
                foreach (var file in device.Files)
                    file.IsPlaying = file.IsCurrent && playing;
            }
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
            var row = new ManualBackupLocationRow { Location = loc };
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
                var devicePlaying = _playingDeviceKeys.Contains(DevicePlayKey(loc, deviceId));
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

