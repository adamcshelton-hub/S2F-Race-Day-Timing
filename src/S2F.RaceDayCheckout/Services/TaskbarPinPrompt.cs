using System.Runtime.InteropServices;
using System.Windows;
using Windows.Foundation.Metadata;
using Windows.UI.Shell;

namespace S2F.RaceDayCheckout.Services;

/// <summary>
/// After first install, offer to pin the app to the taskbar.
/// Windows requires a user-confirmed pin dialog — installers cannot pin silently.
/// </summary>
public static class TaskbarPinPrompt
{
    /// <summary>Must match Velopack --aumid / SetAppUserModelId.</summary>
    public const string AppUserModelId = "Start2Finish.RaceDayCheckout";

    private static bool _offerOnStartup;

    public static void MarkOfferPending() => _offerOnStartup = true;

    public static async Task TryOfferAsync(Window? owner)
    {
        if (!_offerOnStartup)
            return;
        _offerOnStartup = false;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        if (!TimerDialog.Confirm(
                owner,
                "Pin to taskbar?",
                "Pin S2F Race Day Timing to the taskbar so it's easy to open on race morning?\n\n"
                + "Windows will ask you to confirm the pin.",
                yesLabel: "Pin…",
                noLabel: "Not now"))
            return;

        try
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.Shell.TaskbarManager"))
            {
                ShowManualPinHelp(owner, "Taskbar pin API isn't available on this Windows build.");
                return;
            }

            var manager = TaskbarManager.GetDefault();
            if (!manager.IsPinningAllowed)
            {
                ShowManualPinHelp(owner, "Taskbar pinning is turned off on this PC (policy or settings).");
                return;
            }

            if (await manager.IsCurrentAppPinnedAsync())
            {
                TimerDialog.Alert(owner, "Already pinned", "S2F Race Day Timing is already on the taskbar.");
                return;
            }

            // Shows the Windows system confirmation UI (cannot be silent).
            var pinned = await manager.RequestPinCurrentAppAsync();
            if (!pinned)
            {
                TimerDialog.Alert(
                    owner,
                    "Not pinned",
                    "Pin was declined or cancelled.\n\n"
                    + "You can pin later: right-click this app on the taskbar → Pin to taskbar.");
            }
        }
        catch (Exception ex)
        {
            ShowManualPinHelp(owner, ex.Message);
        }
    }

    private static void ShowManualPinHelp(Window? owner, string detail) =>
        TimerDialog.Alert(
            owner,
            "Pin manually",
            "Windows couldn't pin the app automatically.\n\n"
            + "With the app open: right-click its icon on the taskbar → Pin to taskbar.\n\n"
            + detail);
}
