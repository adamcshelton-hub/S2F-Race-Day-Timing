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
               || processName.Contains("rsserver", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Timing &amp; Scoring / Orbits — not RunScore.</summary>
    public static bool IsTimingAndScoringProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName) || IsRunScoreProcess(processName))
            return false;

        return processName.Contains("TimingAndScoring", StringComparison.OrdinalIgnoreCase)
               || processName.Contains("MyLapsTiming", StringComparison.OrdinalIgnoreCase)
               || processName.Equals("mylaps_ts_launch", StringComparison.OrdinalIgnoreCase)
               || processName.Equals("Orbits", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// User clicked Close RunScore after another laptop took the lock — request window close,
    /// then force-end leftovers so Close Local Copy can unlock Active files.
    /// </summary>
    public CloseVendorResult TryCloseRunScoreForAbandonedCheckout() =>
        TryCloseMatchingProcesses(IsRunScoreProcess, productLabel: "RunScore");

    /// <summary>User clicked Close Timing &amp; Scoring on the lock-taken-over screen.</summary>
    public CloseVendorResult TryCloseTimingAndScoringForAbandonedCheckout() =>
        TryCloseMatchingProcesses(IsTimingAndScoringProcess, productLabel: "Timing & Scoring");

    private CloseVendorResult TryCloseMatchingProcesses(Func<string, bool> match, string productLabel)
    {
        var targets = new List<Process>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!match(process.ProcessName))
                    {
                        process.Dispose();
                        continue;
                    }

                    targets.Add(process);
                }
                catch
                {
                    try { process.Dispose(); } catch { /* ignore */ }
                }
            }

            if (targets.Count == 0)
            {
                return new CloseVendorResult
                {
                    Success = true,
                    Message = productLabel + " is not running."
                };
            }

            var closedWindows = 0;
            foreach (var process in targets)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
                        closedWindows++;
                }
                catch
                {
                    // access denied / exited
                }
            }

            // Give a graceful close a few seconds (RSServer often lags the UI).
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(400);
                if (!targets.Any(p =>
                    {
                        try { return !p.HasExited; }
                        catch { return false; }
                    }))
                    break;
            }

            var ended = new List<string>();
            var failed = new List<string>();
            foreach (var process in targets)
            {
                try
                {
                    if (process.HasExited)
                    {
                        ended.Add(process.ProcessName);
                        continue;
                    }

                    process.Kill(entireProcessTree: false);
                    ended.Add(process.ProcessName);
                }
                catch
                {
                    try { failed.Add(process.ProcessName); }
                    catch { /* ignore */ }
                }
            }

            Thread.Sleep(400);
            var still = EnumerateMatching(match).Select(m => m.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            if (still.Count > 0)
            {
                return new CloseVendorResult
                {
                    Success = false,
                    ClosedProcessNames = ended.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList(),
                    Message = "Tried to close " + productLabel + ", but still detected: "
                              + string.Join(", ", still)
                              + ". Use Task Manager if needed."
                };
            }

            var names = ended.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            return new CloseVendorResult
            {
                Success = true,
                ClosedProcessNames = names,
                Message = closedWindows > 0
                    ? "Closed " + productLabel + " (" + string.Join(", ", names) + ")."
                    : "Ended " + productLabel + " (" + string.Join(", ", names) + ")."
            };
        }
        finally
        {
            foreach (var process in targets)
            {
                try { process.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// True when every name in <paramref name="blockingNames"/> is a RunScore process
    /// (safe to offer End RSServer — never for Timing &amp; Scoring / Orbits alone).
    /// </summary>
    public static bool AreOnlyRunScoreProcesses(IReadOnlyList<string> blockingNames) =>
        blockingNames.Count > 0 && blockingNames.All(IsRunScoreProcess);

    /// <summary>
    /// End headless RunScore processes only (RSServer / RSClient / RunScore with no main window).
    /// Refuses if any RunScore window is still open — timer must close the UI themselves.
    /// </summary>
    public EndHeadlessRunScoreResult TryEndHeadlessRunScoreProcesses()
    {
        var matches = EnumerateMatching(IsRunScoreProcess);
        if (matches.Count == 0)
        {
            return new EndHeadlessRunScoreResult
            {
                Success = true,
                Message = "No RunScore background process was running."
            };
        }

        if (matches.Any(m => m.HasMainWindow))
        {
            var open = matches.Where(m => m.HasMainWindow).Select(m => m.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            return new EndHeadlessRunScoreResult
            {
                Success = false,
                Message = "RunScore still has a window open (" + string.Join(", ", open)
                          + "). Close it first, then try again."
            };
        }

        var ended = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!IsRunScoreProcess(process.ProcessName))
                        continue;

                    var hasWindow = false;
                    try { hasWindow = process.MainWindowHandle != IntPtr.Zero; }
                    catch { /* treat as headless */ }

                    if (hasWindow)
                        continue;

                    var name = process.ProcessName;
                    try
                    {
                        process.Kill(entireProcessTree: false);
                        ended.Add(name);
                    }
                    catch
                    {
                        failed.Add(name);
                    }
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
        catch (Exception ex)
        {
            return new EndHeadlessRunScoreResult
            {
                Success = false,
                Message = "Could not end RunScore background process: " + ex.Message
            };
        }

        if (failed.Count > 0 && ended.Count == 0)
        {
            return new EndHeadlessRunScoreResult
            {
                Success = false,
                EndedProcessNames = ended,
                Message = "Could not end " + string.Join(", ", failed.Distinct(StringComparer.OrdinalIgnoreCase))
                          + ". Use Task Manager if needed."
            };
        }

        // Confirm they are gone (Kill is async on Windows).
        Thread.Sleep(500);
        var still = GetRunScoreProcessNames();
        if (still.Count > 0)
        {
            return new EndHeadlessRunScoreResult
            {
                Success = false,
                EndedProcessNames = ended,
                Message = "Tried to end leftover RunScore, but still detected: "
                          + string.Join(", ", still)
                          + ". Use Task Manager if needed."
            };
        }

        return new EndHeadlessRunScoreResult
        {
            Success = true,
            EndedProcessNames = ended.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList(),
            Message = ended.Count == 0
                ? "No RunScore background process was running."
                : "Ended leftover RunScore (" + string.Join(", ", ended.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n)) + ")."
        };
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

public sealed class EndHeadlessRunScoreResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> EndedProcessNames { get; init; } = Array.Empty<string>();
}

/// <summary>Result of user-clicked Close RunScore / Close Timing &amp; Scoring on lock-taken-over.</summary>
public sealed class CloseVendorResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> ClosedProcessNames { get; init; } = Array.Empty<string>();
}
