using System.Net.Sockets;
using System.Text;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// TCP client to RunScore Open (RS listens; we connect). Used only by Main Timing Play/Replay.
/// </summary>
public sealed class RunScoreOpenClient : IAsyncDisposable
{
    private readonly object _gate = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _probeCts;
    private Task? _probeTask;

    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = RunScoreExternalTimers.RunScoreOpenPort;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return _client is { Connected: true };
        }
    }

    /// <summary>Start background probes so the Active UI can show green/red.</summary>
    public void StartKeepAlive(TimeSpan? interval = null)
    {
        StopKeepAlive();
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        var delay = interval ?? TimeSpan.FromSeconds(3);
        _probeTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await EnsureConnectedAsync(cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    Disconnect();
                }

                try
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, cts.Token);
    }

    public void StopKeepAlive()
    {
        try { _probeCts?.Cancel(); } catch { /* ignore */ }
        _probeCts?.Dispose();
        _probeCts = null;
        _probeTask = null;
        Disconnect();
    }

    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_client is { Connected: true } && _stream is not null)
                return;
        }

        Disconnect();
        var client = new TcpClient();
        using var reg = ct.Register(() =>
        {
            try { client.Close(); } catch { /* ignore */ }
        });
        await client.ConnectAsync(Host, Port, ct).ConfigureAwait(false);
        var stream = client.GetStream();
        lock (_gate)
        {
            _client = client;
            _stream = stream;
        }
    }

    public async Task SendLinesAsync(IEnumerable<string> lines, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        NetworkStream stream;
        lock (_gate)
        {
            stream = _stream ?? throw new InvalidOperationException("RunScore Open is not connected.");
        }

        foreach (var raw in lines)
        {
            ct.ThrowIfCancellationRequested();
            var line = EnsureMinLength(raw.TrimEnd('\r', '\n')) + "\r\n";
            var bytes = Encoding.ASCII.GetBytes(line);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Format one Open bib+TOD+location line (RSBI).</summary>
    public static string FormatRsbi(string bib, string tod, string location)
    {
        var loc = string.IsNullOrWhiteSpace(location) ? "Finish" : location.Trim();
        return $"RSBI,{bib.Trim()},{tod.Trim()},{loc}";
    }

    /// <summary>RunScore Open silently drops TCP messages under 20 characters.</summary>
    public static string EnsureMinLength(string line)
    {
        if (line.Length >= 20)
            return line;
        return line.PadRight(20, ' ');
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            try { _stream?.Dispose(); } catch { /* ignore */ }
            try { _client?.Dispose(); } catch { /* ignore */ }
            _stream = null;
            _client = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        StopKeepAlive();
        return ValueTask.CompletedTask;
    }
}

