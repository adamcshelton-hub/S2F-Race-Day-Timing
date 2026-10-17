using System.Diagnostics;

namespace S2F.RaceDayCheckout.Core.Services;

public interface IProcessGuard
{
    IReadOnlyList<string> GetBlockingProcessNames();

    /// <summary>RunScore server/client only (for launch-before-prefs warning).</summary>
    IReadOnlyList<string> GetRunScoreProcessNames();
}

/// <summary>Result of Finish's "close RunScore first" gate.</summary>
public sealed class BlockingWaitResult
{
    public static BlockingWaitResult Cleared() => new() { Kind = BlockingWaitKind.Cleared };

    public static BlockingWaitResult WindowStillOpen(IReadOnlyList<string> names) => new()
    {
        Kind = BlockingWaitKind.WindowStillOpen,
        ProcessNames = names
    };

    public static BlockingWaitResult StillRunning(IReadOnlyList<string> names) => new()
    {
        Kind = BlockingWaitKind.StillRunning,
        ProcessNames = names
    };

    public BlockingWaitKind Kind { get; init; }
    public IReadOnlyList<string> ProcessNames { get; init; } = Array.Empty<string>();
}

public enum BlockingWaitKind
{
    Cleared,
    /// <summary>A timing app still has a visible window — user must close it.</summary>
    WindowStillOpen,
    /// <summary>No window, but process(es) did not exit before timeout.</summary>
    StillRunning
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
        EnumerateMatching(IsBlocking).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();

    public IReadOnlyList<string> GetRunScoreProcessNames() =>
        EnumerateMatching(IsRunScoreProcess).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();

    /// <summary>
    /// Finish gate: if a timing app still has a visible window, fail immediately.
    /// Only then wait for headless leftovers (RSServer flushing after the UI closed).
    /// </summary>
    public async Task<BlockingWaitResult> WaitForBlockingClearedAsync(
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = EnumerateMatching(IsBlocking);
        if (snapshot.Count == 0)
            return BlockingWaitResult.Cleared();

        var withWindow = snapshot.Where(p => p.HasMainWindow).Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        if (withWindow.Count > 0)
            return BlockingWaitResult.WindowStillOpen(withWindow);

        // Window is gone — give RSServer a short window to finish exiting.
        var deadline = DateTime.UtcNow + timeout;
        var blocking = snapshot.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        progress?.Report(FormatWaitingMessage(blocking, deadline - DateTime.UtcNow));

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);

            snapshot = EnumerateMatching(IsBlocking);
            if (snapshot.Count == 0)
                return BlockingWaitResult.Cleared();

            // User reopened the UI while we were waiting.
            withWindow = snapshot.Where(p => p.HasMainWindow).Select(p => p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            if (withWindow.Count > 0)
                return BlockingWaitResult.WindowStillOpen(withWindow);

            blocking = snapshot.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            progress?.Report(FormatWaitingMessage(blocking, deadline - DateTime.UtcNow));
        }

        return BlockingWaitResult.StillRunning(blocking);
    }

    private static string FormatWaitingMessage(IReadOnlyList<string> blocking, TimeSpan remaining)
    {
        var names = string.Join(", ", blocking);
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        if (blocking.Any(IsRunScoreProcess))
            return $"RunScore window is closed.\nWaiting for background process to exit ({names}) — up to {seconds}s…";
        return $"App window is closed.\nWaiting for background process to exit ({names}) — up to {seconds}s…";
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

    private readonly record struct ProcessMatch(string Name, bool HasMainWindow);

    private static IReadOnlyList<ProcessMatch> EnumerateMatching(Func<string, bool> match)
    {
        var found = new List<ProcessMatch>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (!match(name))
                        continue;

                    var hasWindow = false;
                    try
                    {
                        // MainWindowHandle != 0 ⇒ process still owns a visible top-level window.
                        hasWindow = process.MainWindowHandle != IntPtr.Zero;
                    }
                    catch
                    {
                        // access denied — treat as unknown/no window so we still wait rather than false "open"
                    }

                    found.Add(new ProcessMatch(name, hasWindow));
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

        return found;
    }
}
