using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace S2F.RaceDayCheckout.Services;

/// <summary>
/// GitHub Releases updates via Velopack. Channels: win-x64 / win-arm64.
/// Skips update apply while an event is checked out (race-day safety).
/// </summary>
public static class UpdateService
{
    public const string RepoUrl = "https://github.com/adamcshelton-hub/S2F-Race-Day-Timing";
    public const string PackId = "S2F.RaceDayCheckout";

    private static int _interactiveCheckRunning;

    public static string CurrentChannel =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    /// <summary>
    /// Startup check. Always tells the user when an update is available (even if Finish is required first).
    /// Failures set <paramref name="setStatus"/> instead of failing silently.
    /// </summary>
    public static async Task CheckForUpdatesQuietAsync(
        bool hasActiveCheckout,
        Window? owner = null,
        Action<string>? setStatus = null)
    {
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled)
            {
                setStatus?.Invoke($"Dev build {FormatVersion(mgr.CurrentVersion)} — install from Setup for auto-updates.");
                return;
            }

            setStatus?.Invoke($"Checking for updates… (v{FormatVersion(mgr.CurrentVersion)})");
            var update = await mgr.CheckForUpdatesAsync();
            if (update is null)
            {
                setStatus?.Invoke($"v{FormatVersion(mgr.CurrentVersion)} — up to date");
                return;
            }

            var target = FormatVersion(update.TargetFullRelease.Version);
            setStatus?.Invoke($"Downloading update v{target}…");
            await mgr.DownloadUpdatesAsync(update);

            if (hasActiveCheckout)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                    TimerDialog.Alert(
                        owner,
                        "Update ready",
                        $"Version {target} is downloaded.\n\n"
                        + "Finish this race first, then restart the app to install the update."));
                setStatus?.Invoke($"v{target} ready — Finish race, then restart to update");
                return;
            }

            var apply = await Application.Current.Dispatcher.InvokeAsync(() =>
                TimerDialog.Confirm(
                    owner,
                    "Update ready",
                    $"Version {target} is ready.\n\nYou're on v{FormatVersion(mgr.CurrentVersion)}. Restart now to update?",
                    yesLabel: "Restart & update",
                    noLabel: "Later"));

            if (apply)
                mgr.ApplyUpdatesAndRestart(update.TargetFullRelease);
            else
                setStatus?.Invoke($"v{target} ready — restart from Admin → Check for updates when convenient");
        }
        catch (Exception ex)
        {
            setStatus?.Invoke("Couldn't check for updates: " + ShortError(ex));
        }
    }

    public static async Task CheckForUpdatesInteractiveAsync(bool hasActiveCheckout, Window? owner = null)
    {
        if (Interlocked.CompareExchange(ref _interactiveCheckRunning, 1, 0) != 0)
            return;

        TimerDialog.ProgressSession? progress = null;
        try
        {
            progress = TimerDialog.ShowProgress(
                owner,
                "Checking for updates",
                "Looking for a newer version…");
            // Let the overlay paint before the network call blocks the UI feel.
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            var mgr = CreateManager();
            if (!mgr.IsInstalled)
            {
                progress.Dispose();
                progress = null;
                TimerDialog.Alert(owner, "Updates",
                    "This copy wasn't installed with Setup.exe, so auto-update isn't available.\n\n"
                    + "Install from the GitHub Release for your PC (x64 or ARM).");
                return;
            }

            progress.SetMessage($"Checking GitHub… (v{FormatVersion(mgr.CurrentVersion)}, {CurrentChannel})");
            var update = await mgr.CheckForUpdatesAsync();
            if (update is null)
            {
                progress.Dispose();
                progress = null;
                TimerDialog.Alert(owner, "Updates",
                    $"You're on the latest version (v{FormatVersion(mgr.CurrentVersion)}) for {CurrentChannel}.");
                return;
            }

            var target = FormatVersion(update.TargetFullRelease.Version);
            progress.SetMessage($"Downloading update v{target}…");
            await mgr.DownloadUpdatesAsync(update, percent =>
                progress.SetMessage($"Downloading update v{target}… ({percent}%)"));

            progress.Dispose();
            progress = null;

            if (hasActiveCheckout)
            {
                TimerDialog.Alert(owner, "Update downloaded",
                    $"Version {target} is ready, but an event is still checked out.\n\n"
                    + "Finish the race first, then restart the app to update.");
                return;
            }

            if (TimerDialog.Confirm(owner, "Update ready",
                    $"Version {target} is ready.\n\nYou're on v{FormatVersion(mgr.CurrentVersion)}. Restart now to update?",
                    yesLabel: "Restart & update",
                    noLabel: "Later"))
            {
                mgr.ApplyUpdatesAndRestart(update.TargetFullRelease);
            }
        }
        catch (Exception ex)
        {
            progress?.Dispose();
            progress = null;
            TimerDialog.Alert(owner, "Updates", "Couldn't check for updates.\n\n" + ex.Message);
        }
        finally
        {
            progress?.Dispose();
            Interlocked.Exchange(ref _interactiveCheckRunning, 0);
        }
    }

    private static UpdateManager CreateManager()
    {
        // Private GitHub repos require a token for release asset downloads (else 404).
        // Prefer S2F_GITHUB_TOKEN; optional compile-time isn't used — keep repo public for fleet.
        var token = Environment.GetEnvironmentVariable("S2F_GITHUB_TOKEN") ?? "";
        var source = new GithubSource(RepoUrl, token, prerelease: false);
        // Match vpk --channel win-x64 / win-arm64 feeds on the Release.
        var options = new UpdateOptions { ExplicitChannel = CurrentChannel };
        return new UpdateManager(source, options);
    }

    private static string FormatVersion(object? version) =>
        version?.ToString() ?? "?";

    private static string ShortError(Exception ex)
    {
        var msg = ex.Message?.Trim() ?? "unknown error";
        if (msg.Contains("404", StringComparison.OrdinalIgnoreCase))
        {
            return "404 from GitHub — if the repo is private, update downloads fail without a token. "
                   + "Make the repo public, or set S2F_GITHUB_TOKEN. (" + Truncate(msg, 80) + ")";
        }

        return Truncate(msg, 120);
    }

    private static string Truncate(string msg, int max) =>
        msg.Length <= max ? msg : msg[..(max - 1)] + "…";
}
