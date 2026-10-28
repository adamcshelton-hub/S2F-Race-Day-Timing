using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;
using S2F.RaceDayCheckout.ViewModels;

namespace S2F.RaceDayCheckout.Services;

/// <summary>
/// Silent RemotePC host deploy for bare Windows timing laptops only.
/// Skips when: Parallels guest (RemotePC lives on the Mac), any existing RemotePC
/// host/suite install, or missing bundled redist.
/// Never re-runs the deploy MSI over a full viewer+host suite.
/// </summary>
public static class RemotePcSetup
{
    public static async Task EnsureAsync(Window owner, MainViewModel vm)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Mac Parallels: RemotePC is on the Mac host — do not install into the Windows VM.
        if (RemotePcInstallProbe.IsParallelsGuest())
            return;

        // Host-only or full suite already present — leave it alone (no MSI overwrite).
        if (RemotePcInstallProbe.IsRemotePcInstalledOnWindows())
            return;

        var bundled = FindBundledMsi();
        var configPath = FindDeployConfigPath();
        var config = configPath is null ? null : RemotePcDeployConfig.TryLoad(configPath);
        if (bundled is null || config is null)
            return;

        vm.BusyTitle = "Installing RemotePC";
        vm.ProgressText =
            "Windows will ask for administrator permission. This happens once on new laptops.";
        vm.Busy = true;
        try
        {
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var result = await Task.Run(() => InstallSilent(bundled, config));
            if (!result.Success)
            {
                TimerDialog.Alert(owner, "RemotePC", result.Message);
                return;
            }

            vm.Status = "RemotePC host installed for company remote access.";
        }
        finally
        {
            vm.Busy = false;
        }
    }

    internal static string? FindBundledMsi()
    {
        foreach (var path in CandidateRedistPaths(RemotePcDeployConfig.BundledMsiFileName))
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    internal static string? FindDeployConfigPath()
    {
        foreach (var path in CandidateRedistPaths(RemotePcDeployConfig.ConfigFileName))
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    internal static IEnumerable<string> CandidateRedistPaths(string fileName)
    {
        var relative = Path.Combine("redist", fileName);
        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(processDir))
            yield return Path.Combine(processDir, relative);
        yield return Path.Combine(AppContext.BaseDirectory, relative);
    }

    private readonly record struct InstallResult(bool Success, string Message);

    private static InstallResult InstallSilent(string bundledMsiPath, RemotePcDeployConfig config)
    {
        string? tempMsi = null;
        try
        {
            // Re-check immediately before msiexec — suite may have been installed mid-prompt.
            if (RemotePcInstallProbe.IsRemotePcInstalledOnWindows()
                || RemotePcInstallProbe.IsParallelsGuest())
            {
                return new InstallResult(true, string.Empty);
            }

            tempMsi = Path.Combine(
                Path.GetTempPath(),
                "s2f-remotepc-" + Guid.NewGuid().ToString("N") + ".msi");
            File.Copy(bundledMsiPath, tempMsi, overwrite: true);
            TryDeleteZoneIdentifier(tempMsi);

            var args = config.BuildMsiexecArguments(tempMsi);
            var psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new InstallResult(false, "Couldn't start the RemotePC installer.");

            // MSI silent deploy — leave running if it overruns rather than kill mid-write.
            if (!proc.WaitForExit(300_000))
            {
                tempMsi = null;
                return new InstallResult(false,
                    "RemotePC setup is still running. Check the system tray after it finishes.");
            }

            var code = proc.ExitCode;
            if (RemotePcInstallProbe.IsRemotePcInstalledOnWindows())
                return new InstallResult(true, string.Empty);

            // msiexec 0 / 3010 (reboot required) often return before Program Files markers appear.
            if (code is 0 or 3010)
            {
                if (RemotePcInstallProbe.WaitUntilRemotePcInstalledOnWindows())
                    return new InstallResult(true, string.Empty);

                return new InstallResult(false,
                    "RemotePC setup finished but the host was not detected. "
                    + "Confirm the Configuration ID and try again, or install RemotePC manually.");
            }

            return new InstallResult(false, "RemotePC setup exited with code " + code + ".");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new InstallResult(false,
                "Administrator permission was cancelled. RemotePC will be requested again next launch.");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, ex.Message);
        }
        finally
        {
            if (tempMsi is not null)
            {
                try { File.Delete(tempMsi); } catch { /* msiexec may still hold it */ }
            }
        }
    }

    private static void TryDeleteZoneIdentifier(string path)
    {
        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch
        {
            // No mark-of-the-web stream.
        }
    }
}
