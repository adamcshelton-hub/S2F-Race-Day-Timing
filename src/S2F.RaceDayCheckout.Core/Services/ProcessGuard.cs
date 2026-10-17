using System.Diagnostics;

namespace S2F.RaceDayCheckout.Core.Services;

public interface IProcessGuard
{
    IReadOnlyList<string> GetBlockingProcessNames();

    /// <summary>RunScore server/client only (for launch-before-prefs warning).</summary>
    IReadOnlyList<string> GetRunScoreProcessNames();
}

public sealed class ProcessGuard : IProcessGuard
{
    /// <summary>
    /// Exact process names (no .exe). Keep this list precise — short tokens like "rs"
    /// falsely match powershell/csrss via substring matching.
    /// </summary>
    public static readonly string[] DefaultBlockingNames =
    [
        "RSServer",
        "rsserver",
        "RSClient",
        "rsclient",
        "RunScore",
        "mylaps_ts_launch",
        "TimingAndScoring",
        "TimingAndScoring64",
        "MyLapsTimingAndScoring",
        "Orbits"
    ];

    /// <summary>Longer substrings only (safe Contains matches).</summary>
    public static readonly string[] DefaultBlockingSubstrings =
    [
        "TimingAndScoring",
        "MyLapsTiming",
        "rsserver",
        "RSServer"
    ];

    private readonly string[] _exactNames;
    private readonly string[] _substrings;

    public ProcessGuard(IEnumerable<string>? exactNames = null, IEnumerable<string>? substrings = null)
    {
        _exactNames = exactNames?.ToArray() ?? DefaultBlockingNames;
        _substrings = substrings?.ToArray() ?? DefaultBlockingSubstrings;
    }

    private static readonly string[] RunScoreExactNames =
    [
        "RSServer",
        "rsserver",
        "RSClient",
        "rsclient",
        "RunScore"
    ];

    public IReadOnlyList<string> GetBlockingProcessNames() =>
        EnumerateMatching(IsBlocking);

    public IReadOnlyList<string> GetRunScoreProcessNames() =>
        EnumerateMatching(IsRunScoreProcess);

    /// <summary>
    /// Poll until blocking timing apps exit. RSServer often lingers several seconds after
    /// the RunScore window is closed while it flushes files.
    /// </summary>
    /// <returns>Empty when clear; otherwise the names still running when <paramref name="timeout"/> elapsed.</returns>
    public async Task<IReadOnlyList<string>> WaitForBlockingClearedAsync(
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        IReadOnlyList<string> blocking = GetBlockingProcessNames();
        if (blocking.Count == 0)
            return blocking;

        progress?.Report(FormatWaitingMessage(blocking));

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);

            blocking = GetBlockingProcessNames();
            if (blocking.Count == 0)
                return blocking;

            progress?.Report(FormatWaitingMessage(blocking));
        }

        return blocking;
    }

    private static string FormatWaitingMessage(IReadOnlyList<string> blocking)
    {
        var names = string.Join(", ", blocking);
        if (blocking.Any(n => IsRunScoreProcess(n)))
            return $"Waiting for RunScore to finish closing ({names})…";
        return $"Waiting for timing apps to close ({names})…";
    }

    public bool IsBlocking(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        if (_exactNames.Any(n => processName.Equals(n, StringComparison.OrdinalIgnoreCase)))
            return true;

        return _substrings.Any(s =>
            s.Length >= 6 && processName.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsRunScoreProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        return RunScoreExactNames.Any(n => processName.Equals(n, StringComparison.OrdinalIgnoreCase))
               || processName.Contains("rsserver", StringComparison.OrdinalIgnoreCase)
               || processName.Contains("RSServer", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> EnumerateMatching(Func<string, bool> match)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (match(name))
                        found.Add(name);
                }
                catch
                {
                    // access denied / exited
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // ignore enumeration failures
        }

        return found.OrderBy(n => n).ToList();
    }
}
