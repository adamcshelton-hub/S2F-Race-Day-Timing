using Microsoft.Win32;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Detects an existing RemotePC Windows install and Parallels guests.
/// Mac-based Parallels machines run RemotePC on the Mac host — never deploy into the Windows VM.
/// </summary>
public static class RemotePcInstallProbe
{
    /// <summary>
    /// msiexec can return success before host binaries are visible under Program Files.
    /// Poll briefly so a late-appearing install is not reported as failure.
    /// </summary>
    public static readonly TimeSpan PostMsiexecDetectTimeout = TimeSpan.FromSeconds(45);

    public static readonly TimeSpan PostMsiexecDetectPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when any RemotePC host/suite binaries are already under Program Files.
    /// Full suite (viewer + host) and host-only both register these — do not re-run the deploy MSI.
    /// </summary>
    public static bool IsRemotePcInstalledOnWindows(
        Func<string, bool>? fileExists = null,
        Func<Environment.SpecialFolder, string>? getFolderPath = null)
    {
        var exists = fileExists ?? File.Exists;
        var folder = getFolderPath ?? Environment.GetFolderPath;

        foreach (var path in EnumerateInstallMarkers(folder))
        {
            if (exists(path))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Polls <see cref="IsRemotePcInstalledOnWindows"/> until true or <paramref name="timeout"/> elapses.
    /// Returns true as soon as markers appear (including on the first check).
    /// </summary>
    public static bool WaitUntilRemotePcInstalledOnWindows(
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        Func<string, bool>? fileExists = null,
        Func<Environment.SpecialFolder, string>? getFolderPath = null,
        Action<TimeSpan>? delay = null,
        Func<DateTime>? utcNow = null)
    {
        var limit = timeout ?? PostMsiexecDetectTimeout;
        if (limit < TimeSpan.Zero)
            limit = TimeSpan.Zero;

        var interval = pollInterval ?? PostMsiexecDetectPollInterval;
        if (interval <= TimeSpan.Zero)
            interval = TimeSpan.FromMilliseconds(1);

        var sleep = delay ?? (d => Thread.Sleep(d));
        var now = utcNow ?? (() => DateTime.UtcNow);
        var deadline = now() + limit;

        while (true)
        {
            if (IsRemotePcInstalledOnWindows(fileExists, getFolderPath))
                return true;

            var remaining = deadline - now();
            if (remaining <= TimeSpan.Zero)
                return false;

            var wait = remaining < interval ? remaining : interval;
            sleep(wait);
        }
    }

    /// <summary>
    /// True when this Windows session is a Parallels guest (RemotePC belongs on the Mac, not here).
    /// </summary>
    public static bool IsParallelsGuest(
        Func<string, bool>? directoryExists = null,
        Func<string?, string?, string?>? tryReadBiosString = null)
    {
        var dirExists = directoryExists ?? Directory.Exists;
        if (dirExists(@"C:\Program Files\Parallels\Parallels Tools")
            || dirExists(@"C:\Program Files (x86)\Parallels\Parallels Tools"))
            return true;

        var readBios = tryReadBiosString ?? TryReadBiosRegistryString;
        foreach (var name in new[] { "SystemManufacturer", "SystemProductName", "BaseBoardManufacturer" })
        {
            var value = readBios(@"HARDWARE\DESCRIPTION\System\BIOS", name);
            if (LooksLikeParallels(value))
                return true;
        }

        var sysMan = readBios(@"SYSTEM\CurrentControlSet\Control\SystemInformation", "SystemManufacturer");
        return LooksLikeParallels(sysMan);
    }

    public static bool LooksLikeParallels(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains("Parallels", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<string> EnumerateInstallMarkers(
        Func<Environment.SpecialFolder, string> getFolderPath)
    {
        foreach (var root in new[]
                 {
                     getFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     getFolderPath(Environment.SpecialFolder.ProgramFiles)
                 })
        {
            if (string.IsNullOrEmpty(root))
                continue;

            var dir = Path.Combine(root, "RemotePC");
            // Host service — host-only deploy and full suite.
            yield return Path.Combine(dir, "RemotePCService.exe");
            // Suite / viewer+host markers — never overwrite a full install with host MSI.
            yield return Path.Combine(dir, "SuiteLauncher.exe");
            yield return Path.Combine(dir, "RemotePCLauncher.exe");
            yield return Path.Combine(dir, "RemotePCUIU.exe");
            yield return Path.Combine(dir, "RemotePC.exe");
        }
    }

    private static string? TryReadBiosRegistryString(string? keyPath, string? valueName)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        if (string.IsNullOrWhiteSpace(keyPath) || string.IsNullOrWhiteSpace(valueName))
            return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
