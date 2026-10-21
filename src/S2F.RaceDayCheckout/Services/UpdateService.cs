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
    /// Startup check. When an update exists, prompt immediately (before any download) so the
    /// timer cannot race ahead into checkout while a silent download is still running.
    /// One click downloads, then restarts to install — unless a race is still checked out.
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
            var update = await mgr.CheckForUpdatesAsync().ConfigureAwait(true);
            if (update is null)
            {
                setStatus?.Invoke($"v{FormatVersion(mgr.CurrentVersion)} — up to date");
                return;
            }

            await OfferDownloadAndInstallAsync(mgr, update, hasActiveCheckout, owner, setStatus)
                .ConfigureAwait(true);
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
                "Checking for Updates",
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
            var update = await mgr.CheckForUpdatesAsync().ConfigureAwait(true);
            progress.Dispose();
            progress = null;

            if (update is null)
            {
                TimerDialog.Alert(owner, "Updates",
                    $"You're on the latest version (v{FormatVersion(mgr.CurrentVersion)}) for {CurrentChannel}.");
                return;
            }

            await OfferDownloadAndInstallAsync(mgr, update, hasActiveCheckout, owner, setStatus: null)
                .ConfigureAwait(true);
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

    /// <summary>
    /// Prompt first (no silent download). On accept: progress overlay → download → restart
    /// (or download-only when a race is still checked out).
    /// </summary>
    private static async Task OfferDownloadAndInstallAsync(
        UpdateManager mgr,
        UpdateInfo update,
        bool hasActiveCheckout,
        Window? owner,
        Action<string>? setStatus)
    {
        var current = FormatVersion(mgr.CurrentVersion);
        var target = FormatVersion(update.TargetFullRelease.Version);

        bool accept;
        if (hasActiveCheckout)
        {
            accept = await Application.Current.Dispatcher.InvokeAsync(() =>
                TimerDialog.Confirm(
                    owner,
                    "Update Available",
                    $"Version {target} is available (you're on v{current}).\n\n"
                    + "A race is still checked out on this laptop, so we cannot restart yet.\n\n"
                    + "Download now, then Finish the race and restart to install?",
                    yesLabel: "Download for Later",
                    noLabel: "Not Now"));
        }
        else
        {
            accept = await Application.Current.Dispatcher.InvokeAsync(() =>
                TimerDialog.Confirm(
                    owner,
                    "Update Available",
                    $"Version {target} is available (you're on v{current}).\n\n"
                    + "Download, restart, and install now? Stay on this screen until it finishes.",
                    yesLabel: "Download, Restart & Install",
                    noLabel: "Not Now"));
        }

        if (!accept)
        {
            setStatus?.Invoke($"v{target} available — use Check for Updates when ready");
            return;
        }

        TimerDialog.ProgressSession? progress = null;
        try
        {
            progress = await Application.Current.Dispatcher.InvokeAsync(() =>
                TimerDialog.ShowProgress(
                    owner,
                    "Downloading Update",
                    $"Downloading v{target}…"));
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            var downloadProgress = progress;
            await mgr.DownloadUpdatesAsync(update, percent =>
                downloadProgress.SetMessage($"Downloading v{target}… ({percent}%)")).ConfigureAwait(true);

            progress.Dispose();
            progress = null;

            if (hasActiveCheckout)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                    TimerDialog.Alert(
                        owner,
                        "Update Downloaded",
                        $"Version {target} is downloaded.\n\n"
                        + "Finish this race, then restart the app to install the update."));
                setStatus?.Invoke($"v{target} ready — Finish race, then restart to update");
                return;
            }

            // One-click path: no second confirm — restart and install immediately.
            setStatus?.Invoke($"Installing v{target}…");
            mgr.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
        catch (Exception ex)
        {
            progress?.Dispose();
            progress = null;
            var msg = "Couldn't download the update.\n\n" + ShortError(ex);
            await Application.Current.Dispatcher.InvokeAsync(() =>
                TimerDialog.Alert(owner, "Updates", msg));
            setStatus?.Invoke("Couldn't download update: " + ShortError(ex));
        }
        finally
        {
            progress?.Dispose();
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
