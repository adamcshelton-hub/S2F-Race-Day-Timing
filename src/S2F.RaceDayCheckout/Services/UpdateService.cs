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

    /// <summary>
    /// One check at a time, including the download prompt. Startup and Check for Updates
    /// otherwise each call GitHub and each open "Update Available".
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string CurrentChannel =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    /// <summary>
    /// Startup check. The window is blocked until GitHub answers so staff cannot open a race
    /// on an old build. When a newer version exists and no race is checked out, download and
    /// restart — there is no "Not Now". A checked-out race is left running; they can download
    /// for after Finish. A failed check (weak cell) does not trap the laptop.
    /// </summary>
    public static async Task CheckForUpdatesQuietAsync(
        bool hasActiveCheckout,
        Window? owner = null,
        Action<string>? setStatus = null)
    {
        // Do not wait. The in-flight check may need this UI thread to show its dialog.
        if (!await Gate.WaitAsync(0).ConfigureAwait(true))
            return;

        TimerDialog.ProgressSession? progress = null;
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled)
            {
                setStatus?.Invoke($"Dev build {FormatVersion(mgr.CurrentVersion)} — install from Setup for auto-updates.");
                return;
            }

            var current = FormatVersion(mgr.CurrentVersion);
            progress = TimerDialog.ShowProgress(
                owner,
                "Checking for Updates",
                $"Looking for a newer version… (v{current})");
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            setStatus?.Invoke($"Checking for updates… (v{current})");
            // Weak cell must not sit on this overlay. Velopack's check has no cancel token.
            UpdateInfo? update;
            try
            {
                update = await mgr.CheckForUpdatesAsync()
                    .WaitAsync(TimeSpan.FromSeconds(20))
                    .ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                progress.Dispose();
                progress = null;
                setStatus?.Invoke("Update check timed out — continuing. Use Check for Updates when signal is better.");
                return;
            }

            progress.Dispose();
            progress = null;

            if (update is null)
            {
                setStatus?.Invoke($"v{current} — up to date");
                return;
            }

            // No race open: install before they can use this build. Mid-race: do not restart.
            await OfferDownloadAndInstallAsync(
                    mgr, update, hasActiveCheckout, owner, setStatus, forceInstall: !hasActiveCheckout)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            progress?.Dispose();
            progress = null;
            setStatus?.Invoke("Couldn't check for updates: " + ShortError(ex));
        }
        finally
        {
            progress?.Dispose();
            Gate.Release();
        }
    }

    public static async Task CheckForUpdatesInteractiveAsync(
        bool hasActiveCheckout,
        Window? owner = null,
        Action<string>? setStatus = null)
    {
        if (!await Gate.WaitAsync(0).ConfigureAwait(true))
        {
            setStatus?.Invoke("Already checking for updates…");
            return;
        }

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
            Gate.Release();
        }
    }

    /// <summary>
    /// Launch with no checkout (<paramref name="forceInstall"/>): download and restart, no skip.
    /// Otherwise prompt first. A checked-out race can download only — restart waits until Finish.
    /// </summary>
    private static async Task OfferDownloadAndInstallAsync(
        UpdateManager mgr,
        UpdateInfo update,
        bool hasActiveCheckout,
        Window? owner,
        Action<string>? setStatus,
        bool forceInstall = false)
    {
        var current = FormatVersion(mgr.CurrentVersion);
        var target = FormatVersion(update.TargetFullRelease.Version);

        if (hasActiveCheckout)
        {
            var accept = await Application.Current.Dispatcher.InvokeAsync(() =>
                TimerDialog.Confirm(
                    owner,
                    "Update Available",
                    $"Version {target} is available (you're on v{current}).\n\n"
                    + "A race is still checked out on this laptop, so we cannot restart yet.\n\n"
                    + "Download now, then Finish the race and restart to install?",
                    yesLabel: "Download for Later",
                    noLabel: "Not Now"));
            if (!accept)
            {
                setStatus?.Invoke($"v{target} available — Finish this race, then restart to update");
                return;
            }
        }
        else if (!forceInstall)
        {
            var accept = await Application.Current.Dispatcher.InvokeAsync(() =>
                TimerDialog.Confirm(
                    owner,
                    "Update Available",
                    $"Version {target} is available (you're on v{current}).\n\n"
                    + "Download, restart, and install now? Stay on this screen until it finishes.",
                    yesLabel: "Download, Restart & Install",
                    noLabel: "Not Now"));
            if (!accept)
            {
                setStatus?.Invoke($"v{target} available — use Check for Updates when ready");
                return;
            }
        }
        else
        {
            setStatus?.Invoke($"v{target} is required — downloading update…");
        }

        TimerDialog.ProgressSession? progress = null;
        try
        {
            var startingMessage = forceInstall
                ? $"Version {target} is required (you're on v{current}). Downloading…"
                : $"Downloading v{target}…";
            progress = await Application.Current.Dispatcher.InvokeAsync(() =>
                TimerDialog.ShowProgress(
                    owner,
                    "Downloading Update",
                    startingMessage));
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
