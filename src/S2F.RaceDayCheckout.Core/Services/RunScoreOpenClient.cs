using System.Net.Sockets;
using System.Text;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// TCP client to RunScore Open (RS listens; we connect). Used only by Main Timing Play/Replay.
/// </summary>
public sealed class RunScoreOpenClient : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);
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
                return IsSocketAliveUnlocked();
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
                    // Drop stale handle so the next probe / Play reconnects cleanly.
                    if (await _io.WaitAsync(0).ConfigureAwait(false))
                    {
                        try { DisconnectUnlocked(); }
                        finally { _io.Release(); }
                    }
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
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedUnlockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task SendLinesAsync(IEnumerable<string> lines, CancellationToken ct = default)
    {
        var list = lines as IList<string> ?? lines.ToList();
        if (list.Count == 0)
            return;

        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Exception? last = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (attempt > 0)
                        DisconnectUnlocked();

                    await EnsureConnectedUnlockedAsync(ct).ConfigureAwait(false);
                    NetworkStream stream;
                    lock (_gate)
                    {
                        stream = _stream
                            ?? throw new InvalidOperationException("RunScore Open is not connected.");
                    }

                    foreach (var raw in list)
                    {
                        ct.ThrowIfCancellationRequested();
                        var line = EnsureMinLength(raw.TrimEnd('\r', '\n')) + "\r\n";
                        var bytes = Encoding.ASCII.GetBytes(line);
                        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    }

                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt == 0 && IsTransientTransportFailure(ex))
                {
                    last = ex;
                    DisconnectUnlocked();
                }
            }

            throw last ?? new IOException("RunScore Open send failed.");
        }
        finally
        {
            _io.Release();
        }
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
        if (!_io.Wait(0))
        {
            // Play holds the IO lock — force-close sockets; Play will see the abort and retry.
            lock (_gate)
                DisconnectUnlocked();
            return;
        }

        try
        {
            DisconnectUnlocked();
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task EnsureConnectedUnlockedAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (IsSocketAliveUnlocked())
                return;
        }

        DisconnectUnlocked();
        var client = new TcpClient();
        using var reg = ct.Register(() =>
        {
            try { client.Close(); } catch { /* ignore */ }
        });
        await client.ConnectAsync(Host, Port, ct).ConfigureAwait(false);
        // Detect peer close sooner than the default infinite read timeout.
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        var stream = client.GetStream();
        lock (_gate)
        {
            _client = client;
            _stream = stream;
        }
    }

    private void DisconnectUnlocked()
    {
        lock (_gate)
        {
            try { _stream?.Dispose(); } catch { /* ignore */ }
            try { _client?.Dispose(); } catch { /* ignore */ }
            _stream = null;
            _client = null;
        }
    }

    /// <summary>
    /// <see cref="TcpClient.Connected"/> stays true after RS aborts; Poll catches a half-open socket.
    /// </summary>
    private bool IsSocketAliveUnlocked()
    {
        if (_client is null || _stream is null)
            return false;
        try
        {
            var sock = _client.Client;
            if (sock is null || !_client.Connected)
                return false;
            // Readable + no data ⇒ peer closed / reset.
            if (sock.Poll(0, SelectMode.SelectRead) && sock.Available == 0)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTransientTransportFailure(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException!)
        {
            if (cur is IOException or SocketException)
                return true;
            if (cur.Message.Contains("aborted", StringComparison.OrdinalIgnoreCase)
                || cur.Message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                || cur.Message.Contains("transport connection", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public ValueTask DisposeAsync()
    {
        StopKeepAlive();
        _io.Dispose();
        return ValueTask.CompletedTask;
    }
}
