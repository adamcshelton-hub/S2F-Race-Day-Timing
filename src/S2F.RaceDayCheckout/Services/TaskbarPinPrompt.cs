using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Windows.Foundation.Metadata;
using Windows.UI.Shell;

namespace S2F.RaceDayCheckout.Services;

/// <summary>
/// After first install, offer to pin the app to the taskbar.
/// Windows blocks silent installer pins. <see cref="TaskbarManager.RequestPinCurrentAppAsync"/>
/// only works for unpackaged Win32 when desktop support is present and the Limited Access
/// Feature is not required (or unlocked). Otherwise we guide a Start-menu pin.
/// </summary>
public static class TaskbarPinPrompt
{
    /// <summary>Must match Velopack --aumid / SetAppUserModelId.</summary>
    public const string AppUserModelId = "Start2Finish.RaceDayCheckout";

    /// <summary>Must match Velopack --packTitle (Start Menu .lnk name).</summary>
    public const string StartMenuShortcutName = "S2F Race Day Timing";

    /// <summary>
    /// Registry value name under the taskbar.pin LAF key — when present and non-zero,
    /// Microsoft requires an unlock token before TaskbarManager pin APIs work.
    /// </summary>
    private const string TaskbarPinLafSeed =
        "4096B239A7295B635C090E647E867B5707DA6AB6CB78340B01FE4E0C8F4953D4";

    private const string TaskbarPinLafKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\LimitedAccessFeatures\com.microsoft.windows.taskbar.pin";

    private static bool _offerOnStartup;

    public static void MarkOfferPending() => _offerOnStartup = true;

    public static async Task TryOfferAsync(Window? owner)
    {
        if (!_offerOnStartup)
            return;
        _offerOnStartup = false;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        // Pin before Defender UAC so we keep foreground; activate after any prior dialogs.
        BringOwnerForward(owner);

        if (CanUsePinApi())
        {
            await OfferApiPinAsync(owner);
            return;
        }

        // API blocked (LAF / no desktop support) — still offer a one-time guided Start-menu pin.
        if (!TimerDialog.Confirm(
                owner,
                "Pin to Taskbar?",
                "Pin S2F Race Day Timing to the taskbar so it's easy to open on race morning?\n\n"
                + "Windows won't pin this install automatically — you'll right-click it in the Start menu.",
                yesLabel: "Show How…",
                noLabel: "Not Now"))
            return;

        OfferManualPin(owner);
    }

    /// <summary>
    /// True when Win32 desktop TaskbarManager support exists and this OS build does not
    /// require a Limited Access Feature token we do not have.
    /// </summary>
    internal static bool CanUsePinApi()
    {
        if (!ApiInformation.IsTypePresent("Windows.UI.Shell.TaskbarManager"))
            return false;
        // Without this marker, RequestPinCurrentAppAsync is UWP-only and fails for Velopack.
        if (!ApiInformation.IsTypePresent("Windows.UI.Shell.ITaskbarManagerDesktopAppSupportStatics"))
            return false;
        if (IsLafTokenRequired())
            return false;
        try
        {
            return TaskbarManager.GetDefault().IsPinningAllowed;
        }
        catch
        {
            return false;
        }
    }

    private static async Task OfferApiPinAsync(Window? owner)
    {
        if (!TimerDialog.Confirm(
                owner,
                "Pin to Taskbar?",
                "Pin S2F Race Day Timing to the taskbar so it's easy to open on race morning?\n\n"
                + "Windows will ask you to confirm the pin.",
                yesLabel: "Pin…",
                noLabel: "Not Now"))
            return;

        BringOwnerForward(owner);

        try
        {
            var manager = TaskbarManager.GetDefault();
            if (!manager.IsPinningAllowed)
            {
                OfferManualPin(owner, "Taskbar pinning is turned off on this PC (policy or settings).");
                return;
            }

            if (await manager.IsCurrentAppPinnedAsync())
            {
                TimerDialog.Alert(owner, "Already Pinned", "S2F Race Day Timing is already on the taskbar.");
                return;
            }

            var pinned = await manager.RequestPinCurrentAppAsync();
            if (!pinned)
            {
                OfferManualPin(
                    owner,
                    "Windows did not complete the pin (declined, cancelled, or notifications off).");
            }
        }
        catch (Exception ex)
        {
            OfferManualPin(owner, ex.Message);
        }
    }

    private static void OfferManualPin(Window? owner, string? detail = null)
    {
        var shortcut = FindStartMenuShortcut();
        var body =
            "Pin from the Start menu:\n"
            + "1. Open Start and find “S2F Race Day Timing”.\n"
            + "2. Right-click → Pin to taskbar.\n\n"
            + "Or with the app open: right-click its taskbar icon → Pin to taskbar.";

        if (!string.IsNullOrWhiteSpace(detail))
            body += "\n\n" + detail.Trim();

        if (shortcut is null)
        {
            TimerDialog.Alert(owner, "Pin to Taskbar", body);
            return;
        }

        // Prefer revealing the .lnk — faster than reading steps on race morning.
        RevealStartMenuShortcut(shortcut);
        TimerDialog.Alert(
            owner,
            "Pin to Taskbar",
            body + "\n\nThe Start menu shortcut is selected in File Explorer — right-click it → Pin to taskbar.");
    }

    /// <summary>
    /// Microsoft docs: when this seed DWORD is present and non-zero under the LAF key,
    /// TryUnlockFeature + token is required. Missing key / zero = pin API is unrestricted.
    /// </summary>
    private static bool IsLafTokenRequired()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TaskbarPinLafKeyPath);
            if (key is null)
                return false;

            var raw = key.GetValue(TaskbarPinLafSeed);
            return raw switch
            {
                int i => i != 0,
                long l => l != 0,
                byte[] bytes when bytes.Length >= 4 => BitConverter.ToInt32(bytes, 0) != 0,
                _ => false
            };
        }
        catch
        {
            // Can't read policy — still attempt API path; failure falls back to manual.
            return false;
        }
    }

    private static string? FindStartMenuShortcut()
    {
        var fileName = StartMenuShortcutName + ".lnk";
        foreach (var root in StartMenuRoots())
        {
            try
            {
                var direct = Path.Combine(root, fileName);
                if (File.Exists(direct))
                    return direct;

                foreach (var path in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                    return path;
            }
            catch
            {
                // Skip unreadable Start Menu trees.
            }
        }

        return null;
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrWhiteSpace(programs))
        {
            // Velopack StartMenuRoot → parent of Programs (Start Menu folder).
            var startMenu = Directory.GetParent(programs)?.FullName;
            if (!string.IsNullOrWhiteSpace(startMenu))
                yield return startMenu;
            yield return programs;
        }

        var commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        if (!string.IsNullOrWhiteSpace(commonPrograms))
        {
            var commonStart = Directory.GetParent(commonPrograms)?.FullName;
            if (!string.IsNullOrWhiteSpace(commonStart))
                yield return commonStart;
            yield return commonPrograms;
        }
    }

    private static void RevealStartMenuShortcut(string shortcutPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + shortcutPath + "\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // Non-fatal — instructions above still stand.
        }
    }

    private static void BringOwnerForward(Window? owner)
    {
        if (owner is null)
            return;
        try
        {
            if (!owner.IsVisible)
                owner.Show();
            if (owner.WindowState == WindowState.Minimized)
                owner.WindowState = WindowState.Normal;
            owner.Activate();
            owner.Focus();
        }
        catch
        {
            // Non-fatal
        }
    }
}
