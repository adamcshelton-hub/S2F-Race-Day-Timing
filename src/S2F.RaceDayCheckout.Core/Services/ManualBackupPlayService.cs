using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class ManualBackupPlayResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int LinesSent { get; init; }
}

/// <summary>Play / Replay Manual Backup segments through RunScore Open (Main Timing only).</summary>
public sealed class ManualBackupPlayService
{
    private readonly ManualBackupStore _store;
    private readonly RunScoreOpenClient _open;

    public ManualBackupPlayService(ManualBackupStore store, RunScoreOpenClient open)
    {
        _store = store;
        _open = open;
    }

    public RunScoreOpenClient Open => _open;

    public async Task<ManualBackupPlayResult> PlayAllAsync(
        string sessionPath,
        string location,
        CancellationToken ct = default)
    {
        if (!_open.IsConnected)
        {
            try { await _open.EnsureConnectedAsync(ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                return new ManualBackupPlayResult
                {
                    Success = false,
                    Message = "RunScore Open is not connected (" + ex.Message + "). Start RunScore External Timers (Open), then try again."
                };
            }
        }

        var deviceIds = _store.ListDeviceIds(sessionPath, location);
        var batch = new List<(ManualBackupTap Tap, string DeviceId, int AbsoluteIndex)>();
        foreach (var deviceId in deviceIds)
        {
            var state = _store.LoadOrCreateDevice(sessionPath, location, deviceId, deviceId);
            if (state.Paused)
                continue;
            var taps = _store.ReadCurrentTaps(sessionPath, location, deviceId);
            for (var i = state.PlayCursor; i < taps.Count; i++)
            {
                var t = taps[i];
                if (t.Voided)
                    continue;
                batch.Add((t, deviceId, i));
            }
        }

        if (batch.Count == 0)
        {
            return new ManualBackupPlayResult
            {
                Success = true,
                Message = "Nothing new to Play for " + location + ".",
                LinesSent = 0
            };
        }

        var ordered = batch.OrderBy(x => x.Tap.TimestampUtc).ThenBy(x => x.Tap.Id, StringComparer.Ordinal).ToList();
        var lines = ordered.Select(x =>
            RunScoreOpenClient.FormatRsbi(
                x.Tap.Bib,
                ManualBackupStore.FormatTodLocal(x.Tap.TimestampUtc),
                location)).ToList();

        try
        {
            await _open.SendLinesAsync(lines, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ManualBackupPlayResult
            {
                Success = false,
                Message = "Play failed: " + ex.Message
            };
        }

        // Advance each device cursor to end of its current file (all lines considered played through).
        foreach (var deviceId in deviceIds)
        {
            var state = _store.LoadOrCreateDevice(sessionPath, location, deviceId, deviceId);
            if (state.Paused)
                continue;
            var taps = _store.ReadCurrentTaps(sessionPath, location, deviceId);
            _store.SetPlayCursor(sessionPath, location, deviceId, taps.Count);
        }

        return new ManualBackupPlayResult
        {
            Success = true,
            Message = "Played " + ordered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " time(s) for " + location + ".",
            LinesSent = ordered.Count
        };
    }

    public async Task<ManualBackupPlayResult> PlaySegmentAsync(
        string sessionPath,
        string location,
        string deviceId,
        string? segmentFileName,
        bool replay,
        CancellationToken ct = default)
    {
        if (!_open.IsConnected)
        {
            try { await _open.EnsureConnectedAsync(ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                return new ManualBackupPlayResult
                {
                    Success = false,
                    Message = "RunScore Open is not connected (" + ex.Message + ")."
                };
            }
        }

        var state = _store.LoadOrCreateDevice(sessionPath, location, deviceId, deviceId);
        if (!replay && state.Paused)
        {
            return new ManualBackupPlayResult
            {
                Success = false,
                Message = "Device is paused. Unpause to Play, or use Replay on an archive."
            };
        }

        List<ManualBackupTap> taps;
        var isCurrent = string.IsNullOrWhiteSpace(segmentFileName)
            || string.Equals(segmentFileName, ManualBackupStore.CurrentFileName, StringComparison.OrdinalIgnoreCase);
        if (isCurrent)
        {
            taps = _store.ReadCurrentTaps(sessionPath, location, deviceId).ToList();
            if (!replay)
                taps = taps.Skip(state.PlayCursor).Where(t => !t.Voided).ToList();
            else
                taps = taps.Where(t => !t.Voided).ToList();
        }
        else
        {
            var path = Path.Combine(
                _store.GetDeviceDir(sessionPath, location, deviceId),
                segmentFileName!);
            taps = _store.ReadSegmentTaps(path).Where(t => !t.Voided).ToList();
        }

        if (taps.Count == 0)
        {
            return new ManualBackupPlayResult
            {
                Success = true,
                Message = "Nothing to send.",
                LinesSent = 0
            };
        }

        var lines = taps.Select(t =>
            RunScoreOpenClient.FormatRsbi(
                t.Bib,
                ManualBackupStore.FormatTodLocal(t.TimestampUtc),
                location)).ToList();

        try
        {
            await _open.SendLinesAsync(lines, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ManualBackupPlayResult
            {
                Success = false,
                Message = "Play failed: " + ex.Message
            };
        }

        if (isCurrent && !replay)
        {
            var all = _store.ReadCurrentTaps(sessionPath, location, deviceId);
            _store.SetPlayCursor(sessionPath, location, deviceId, all.Count);
        }

        return new ManualBackupPlayResult
        {
            Success = true,
            Message = (replay ? "Replayed " : "Played ")
                + taps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " time(s).",
            LinesSent = taps.Count
        };
    }
}

