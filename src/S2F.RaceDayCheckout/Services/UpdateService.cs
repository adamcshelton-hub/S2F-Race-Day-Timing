using System.Runtime.InteropServices;
using System.Windows;
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

    public static string CurrentChannel =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    public static async Task CheckForUpdatesQuietAsync(bool hasActiveCheckout, Window? owner = null)
    {
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled)
                return; // dev / portable run — no update feed

            var update = await mgr.CheckForUpdatesAsync();
            if (update is null)
                return;

            await mgr.DownloadUpdatesAsync(update);

            if (hasActiveCheckout)
            {
                // Don't restart mid-race; apply after Finish on next launch.
                return;
            }

            var apply = Application.Current.Dispatcher.Invoke(() =>
                TimerDialog.Confirm(
                    owner,
                    "Update ready",
                    $"Version {update.TargetFullRelease.Version} is ready.\n\nRestart the app to update? (Safe — you're not checked out.)",
                    yesLabel: "Restart & update",
                    noLabel: "Later"));

            if (apply)
                mgr.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
        catch
        {
            // Offline / rate limit / unsigned feed — never block race day.
        }
    }

    public static async Task CheckForUpdatesInteractiveAsync(bool hasActiveCheckout, Window? owner = null)
    {
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled)
            {
                TimerDialog.Alert(owner, "Updates",
                    "This copy wasn't installed with Setup.exe, so auto-update isn't available.\n\nInstall from the GitHub Release for your PC (x64 or ARM).");
                return;
            }

            var update = await mgr.CheckForUpdatesAsync();
            if (update is null)
            {
                TimerDialog.Alert(owner, "Updates",
                    $"You're on the latest version ({mgr.CurrentVersion}) for {CurrentChannel}.");
                return;
            }

            await mgr.DownloadUpdatesAsync(update);

            if (hasActiveCheckout)
            {
                TimerDialog.Alert(owner, "Update downloaded",
                    "Update is ready, but an event is still checked out.\n\nFinish the race first, then restart the app to update.");
                return;
            }

            if (TimerDialog.Confirm(owner, "Update ready",
                    $"Version {update.TargetFullRelease.Version} is ready.\n\nRestart now to update?",
                    yesLabel: "Restart & update",
                    noLabel: "Later"))
            {
                mgr.ApplyUpdatesAndRestart(update.TargetFullRelease);
            }
        }
        catch (Exception ex)
        {
            TimerDialog.Alert(owner, "Updates", "Couldn't check for updates.\n\n" + ex.Message);
        }
    }

    private static UpdateManager CreateManager()
    {
        // Public repo: empty token. Private: set S2F_GITHUB_TOKEN env for higher rate limits.
        var token = Environment.GetEnvironmentVariable("S2F_GITHUB_TOKEN") ?? "";
        var source = new GithubSource(RepoUrl, token, prerelease: false);
        var options = new UpdateOptions { ExplicitChannel = CurrentChannel };
        return new UpdateManager(source, options);
    }
}
