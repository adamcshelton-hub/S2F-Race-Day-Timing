using System.Diagnostics;

namespace S2F.RaceDayCheckout.Core.Services;

public interface IProcessGuard
{
    IReadOnlyList<string> GetBlockingProcessNames();
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

    public IReadOnlyList<string> GetBlockingProcessNames()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (IsBlocking(name))
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

    public bool IsBlocking(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        if (_exactNames.Any(n => processName.Equals(n, StringComparison.OrdinalIgnoreCase)))
            return true;

        return _substrings.Any(s =>
            s.Length >= 6 && processName.Contains(s, StringComparison.OrdinalIgnoreCase));
    }
}
