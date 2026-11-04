using System.Diagnostics;
using System.Runtime.Versioning;
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
    /// True when RemotePC already looks present on this Windows machine — classic Program Files
    /// markers, uninstall registry, host Windows service ImagePath, or a running host process.
    /// Full suite (viewer + host) and host-only both count — do not re-run the deploy MSI.
    /// </summary>
    public static bool IsRemotePcInstalledOnWindows(
        Func<string, bool>? fileExists = null,
        Func<Environment.SpecialFolder, string>? getFolderPath = null,
        Func<IEnumerable<string>>? enumerateUninstallDisplayNames = null,
        Func<IEnumerable<string>>? enumerateServiceImagePaths = null,
        Func<IEnumerable<string>>? enumerateProcessNames = null)
    {
        var exists = fileExists ?? File.Exists;
        var folder = getFolderPath ?? Environment.GetFolderPath;

        foreach (var path in EnumerateInstallMarkers(folder))
        {
            if (exists(path))
                return true;
        }

        var uninstallNames = enumerateUninstallDisplayNames ?? EnumerateUninstallDisplayNames;
        foreach (var name in uninstallNames())
        {
            if (LooksLikeRemotePcProduct(name))
                return true;
        }

        var imagePaths = enumerateServiceImagePaths ?? EnumerateServiceImagePaths;
        foreach (var imagePath in imagePaths())
        {
            if (LooksLikeRemotePcPath(imagePath))
                return true;
        }

        var processNames = enumerateProcessNames ?? EnumerateRunningProcessNames;
        foreach (var processName in processNames())
        {
            if (LooksLikeRemotePcProcess(processName))
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
        Func<IEnumerable<string>>? enumerateUninstallDisplayNames = null,
        Func<IEnumerable<string>>? enumerateServiceImagePaths = null,
        Func<IEnumerable<string>>? enumerateProcessNames = null,
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
            if (IsRemotePcInstalledOnWindows(
                    fileExists,
                    getFolderPath,
                    enumerateUninstallDisplayNames,
                    enumerateServiceImagePaths,
                    enumerateProcessNames))
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

    public static bool LooksLikeRemotePcProduct(string? displayName) =>
        !string.IsNullOrWhiteSpace(displayName)
        && displayName.Contains("RemotePC", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeRemotePcPath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return false;

        // Quoted ImagePath: "C:\Program Files\RemotePC\RemotePCService.exe" -args
        var raw = imagePath.Trim().Trim('"');
        return raw.Contains("RemotePC", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeRemotePcProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        // Exact / prefix matches for known host + suite process names (avoid random "RPC*" noise).
        foreach (var known in KnownRemotePcProcessNames)
        {
            if (name.Equals(known, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return name.Contains("RemotePC", StringComparison.OrdinalIgnoreCase);
    }

    internal static readonly string[] KnownRemotePcProcessNames =
    [
        "RemotePCService",
        "RemotePC",
        "RemotePCLauncher",
        "RemotePCUIU",
        "SuiteLauncher",
        "RPCService",
        "RemotePCHost",
        "RemotePCViewer",
    ];

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

    private static IEnumerable<string> EnumerateUninstallDisplayNames()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        foreach (var root in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                 })
        {
            foreach (var name in TryEnumerateSubKeyDisplayNames(RegistryHive.LocalMachine, root))
                yield return name;
        }
    }

    private static IEnumerable<string> EnumerateServiceImagePaths()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        const string servicesRoot = @"SYSTEM\CurrentControlSet\Services";
        foreach (var imagePath in TryEnumerateServiceImagePaths(RegistryHive.LocalMachine, servicesRoot))
            yield return imagePath;
    }

    private static IEnumerable<string> EnumerateRunningProcessNames()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            yield break;
        }

        foreach (var process in processes)
        {
            string? name = null;
            try
            {
                name = process.ProcessName;
            }
            catch
            {
                // Access denied / exited.
            }
            finally
            {
                try { process.Dispose(); } catch { /* ignore */ }
            }

            if (!string.IsNullOrWhiteSpace(name))
                yield return name;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> TryEnumerateSubKeyDisplayNames(RegistryHive hive, string subKeyPath)
    {
        RegistryKey? root = null;
        RegistryKey? key = null;
        try
        {
            root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            key = root.OpenSubKey(subKeyPath);
            if (key is null)
                yield break;

            foreach (var subName in key.GetSubKeyNames())
            {
                RegistryKey? sub = null;
                string? displayName = null;
                try
                {
                    sub = key.OpenSubKey(subName);
                    displayName = sub?.GetValue("DisplayName") as string;
                }
                catch
                {
                    // ignore per-key failures
                }
                finally
                {
                    sub?.Dispose();
                }

                if (!string.IsNullOrWhiteSpace(displayName))
                    yield return displayName;
            }
        }
        finally
        {
            key?.Dispose();
            root?.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> TryEnumerateServiceImagePaths(RegistryHive hive, string servicesRoot)
    {
        RegistryKey? root = null;
        RegistryKey? key = null;
        try
        {
            root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            key = root.OpenSubKey(servicesRoot);
            if (key is null)
                yield break;

            foreach (var subName in key.GetSubKeyNames())
            {
                // Cheap name filter — avoid opening RpcSs / RpcEptMapper / every service.
                if (!subName.Contains("RemotePC", StringComparison.OrdinalIgnoreCase)
                    && !subName.Equals("RPCService", StringComparison.OrdinalIgnoreCase))
                    continue;

                RegistryKey? sub = null;
                string? imagePath = null;
                try
                {
                    sub = key.OpenSubKey(subName);
                    imagePath = sub?.GetValue("ImagePath") as string;
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    sub?.Dispose();
                }

                if (!string.IsNullOrWhiteSpace(imagePath))
                    yield return imagePath;
            }
        }
        finally
        {
            key?.Dispose();
            root?.Dispose();
        }
    }

    private static string? TryReadBiosRegistryString(string? keyPath, string? valueName)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        if (string.IsNullOrWhiteSpace(keyPath) || string.IsNullOrWhiteSpace(valueName))
            return null;
        return ReadBiosRegistryStringWindows(keyPath, valueName);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadBiosRegistryStringWindows(string keyPath, string valueName)
    {
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
