using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.ViewModels;

namespace S2F.RaceDayCheckout.Services;

/// <summary>
/// Installs the official Notepad++ setup to <see cref="RunScoreSopProfile.NotepadPlusPlusExePath"/>
/// when that file is missing. The installer is shipped beside the app as <c>redist/npp-setup.bin</c>
/// (not an .exe, so Velopack code-signing does not rewrite the Notepad++ signature).
/// </summary>
public static class NotepadPlusPlusSetup
{
    public const string BundledInstallerFileName = "npp-setup.bin";

    /// <summary>
    /// Silent install. <c>/D=</c> must be last and unquoted — it forces Program Files even when
    /// an older Notepad++ registration points somewhere else. Do not pass
    /// <c>/runNppAfterSilentInstall</c>; that opens the editor when setup finishes.
    /// </summary>
    public const string SilentArguments = "/S /D=C:\\Program Files\\Notepad++";

    public static async Task EnsureAsync(Window owner, MainViewModel vm)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (File.Exists(RunScoreSopProfile.NotepadPlusPlusExePath))
            return;

        var bundled = FindBundledInstaller();
        if (bundled is null)
            return;

        vm.BusyTitle = "Installing Notepad++";
        vm.ProgressText = "Windows will ask for administrator permission. This happens once.";
        vm.Busy = true;
        try
        {
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var result = await Task.Run(() => InstallSilent(bundled));
            if (!result.Success)
            {
                TimerDialog.Alert(
                    owner,
                    "Notepad++",
                    result.Message + "\n\nRunScore listing output expects:\n"
                    + RunScoreSopProfile.NotepadPlusPlusExePath);
            }
        }
        finally
        {
            vm.Busy = false;
        }
    }

    internal static string? FindBundledInstaller()
    {
        foreach (var path in CandidateInstallerPaths())
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    internal static IEnumerable<string> CandidateInstallerPaths()
    {
        var relative = Path.Combine("redist", BundledInstallerFileName);
        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(processDir))
            yield return Path.Combine(processDir, relative);
        yield return Path.Combine(AppContext.BaseDirectory, relative);
    }

    private readonly record struct InstallResult(bool Success, string Message);

    private static InstallResult InstallSilent(string bundledInstallerPath)
    {
        string? tempExe = null;
        try
        {
            tempExe = Path.Combine(Path.GetTempPath(), "s2f-npp-setup-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(bundledInstallerPath, tempExe, overwrite: true);
            TryDeleteZoneIdentifier(tempExe);

            var psi = new ProcessStartInfo
            {
                FileName = tempExe,
                Arguments = SilentArguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new InstallResult(false, "Couldn't start the Notepad++ installer.");

            // Official silent setup is usually under a minute. Leave it running if it overruns
            // so we don't kill a half-written Program Files copy.
            if (!proc.WaitForExit(180_000))
            {
                tempExe = null;
                return new InstallResult(false,
                    "Notepad++ setup is still running. Open RunScore after it finishes.");
            }

            if (File.Exists(RunScoreSopProfile.NotepadPlusPlusExePath))
                return new InstallResult(true, string.Empty);

            var code = proc.ExitCode;
            var why = code == 0
                ? "Setup finished but Notepad++ is not at the expected path."
                : "Notepad++ setup exited with code " + code + ".";
            return new InstallResult(false, why);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new InstallResult(false,
                "Administrator permission was cancelled. Notepad++ will be requested again next launch.");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, ex.Message);
        }
        finally
        {
            if (tempExe is not null)
            {
                try { File.Delete(tempExe); } catch { /* installer may still be running */ }
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
