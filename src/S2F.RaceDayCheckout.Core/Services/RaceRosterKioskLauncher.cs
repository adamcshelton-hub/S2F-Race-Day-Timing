using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Opens the RaceRoster timer dashboard the way the race-day desktop shortcut did:
/// Chrome (or Edge) with <c>--kiosk --kiosk-printing</c>.
/// A separate profile keeps kiosk mode working when everyday Chrome is already open,
/// and keeps the timer sign-in on this laptop.
/// </summary>
public sealed class RaceRosterKioskLauncher
{
    /// <summary>Same URL as the RaceRoster Kiosk desktop shortcut.</summary>
    public const string SignInUrl =
        "https://raceroster.com/signin?redirect=https%3A%2F%2Ftimer.raceroster.com%2Fdashboard%2Ftimers";

    private readonly Func<string, bool> _exists;
    private readonly Func<IEnumerable<string>> _candidates;
    private readonly Func<string, string?> _appPath;
    private readonly Func<ProcessStartInfo, bool> _start;
    private readonly Func<string> _profileDirectory;

    public RaceRosterKioskLauncher(
        Func<string, bool>? exists = null,
        Func<IEnumerable<string>>? candidates = null,
        Func<string, string?>? appPath = null,
        Func<ProcessStartInfo, bool>? start = null,
        Func<string>? profileDirectory = null)
    {
        _exists = exists ?? File.Exists;
        _candidates = candidates ?? CandidatePaths;
        _appPath = appPath ?? ReadAppPath;
        _start = start ?? StartAndRelease;
        _profileDirectory = profileDirectory ?? DefaultProfileDirectory;
    }

    public static string DefaultProfileDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.GetTempPath();
        return Path.Combine(local, "S2F", "RaceRosterKiosk");
    }

    /// <summary>Chrome install locations first, then Edge. Order is the preference order.</summary>
    public static IEnumerable<string> CandidatePaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        foreach (var root in new[] { pf, pf86 })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
        }

        if (!string.IsNullOrWhiteSpace(local))
            yield return Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe");

        foreach (var root in new[] { pf86, pf })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
        }
    }

    /// <summary>
    /// Flags match the desktop shortcut, then a few that keep a dedicated profile from
    /// stopping on Chrome's first-run or "restore pages" prompts. The timer URL is last.
    /// </summary>
    public static string BuildArguments(string profileDirectory)
    {
        var profile = string.IsNullOrWhiteSpace(profileDirectory)
            ? DefaultProfileDirectory()
            : profileDirectory.Trim().TrimEnd('\\', '/');
        return "--kiosk --kiosk-printing --no-first-run --no-default-browser-check "
               + "--disable-session-crashed-bubble "
               + "--user-data-dir=\"" + profile + "\" "
               + "\"" + SignInUrl + "\"";
    }

    public LaunchResult Launch()
    {
        var exe = Resolve(out var error);
        if (exe is null)
        {
            return new LaunchResult
            {
                Success = false,
                Message = error ?? "Chrome or Edge was not found on this laptop. Install Google Chrome, then try again."
            };
        }

        var profile = _profileDirectory();
        var firstOpen = !File.Exists(Path.Combine(profile, "Local State"));
        try
        {
            Directory.CreateDirectory(profile);
        }
        catch (Exception ex)
        {
            return new LaunchResult
            {
                Success = false,
                Message = "Could not prepare the RaceRoster Kiosk folder: " + ex.Message
            };
        }

        try
        {
            if (!_start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = BuildArguments(profile),
                UseShellExecute = false
            }))
            {
                return new LaunchResult
                {
                    Success = false,
                    Message = "Could not open RaceRoster Kiosk."
                };
            }
        }
        catch (Exception ex)
        {
            return new LaunchResult
            {
                Success = false,
                Message = "Could not open RaceRoster Kiosk: " + ex.Message
            };
        }

        return new LaunchResult
        {
            Success = true,
            Message = firstOpen
                ? "RaceRoster Kiosk opening… sign in once in that window."
                : "RaceRoster Kiosk opening…"
        };
    }

    public string? Resolve(out string? error)
    {
        error = null;
        foreach (var path in _candidates())
        {
            if (!string.IsNullOrWhiteSpace(path) && _exists(path))
                return path;
        }

        foreach (var name in new[] { "chrome.exe", "msedge.exe" })
        {
            var fromRegistry = _appPath(name);
            if (string.IsNullOrWhiteSpace(fromRegistry))
                continue;
            var trimmed = fromRegistry.Trim().Trim('"');
            if (_exists(trimmed))
                return trimmed;
        }

        error = "Chrome or Edge was not found on this laptop. Install Google Chrome, then try again.";
        return null;
    }

    private static bool StartAndRelease(ProcessStartInfo info)
    {
        // Dispose releases our handle. It does not close the browser.
        using var process = Process.Start(info);
        return process is not null;
    }

    private static string? ReadAppPath(string exeName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(exeName))
            return null;
        return ReadAppPathWindows(exeName);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadAppPathWindows(string exeName)
    {
        var keys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exeName
        };

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var keyPath in keys)
            {
                try
                {
                    using var key = hive.OpenSubKey(keyPath);
                    if (key?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path))
                        return path;
                }
                catch
                {
                    // Missing key or no registry access — keep searching.
                }
            }
        }

        return null;
    }
}
