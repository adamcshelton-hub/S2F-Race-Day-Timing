using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Services;

/// <summary>
/// Registers and runs the one-time SYSTEM clock-sync task. Query and run stay unelevated.
/// </summary>
public static class ClockSyncTaskRunner
{
    public readonly record struct RegisterResult(bool Success, bool UacDeclined, string Message);

    public readonly record struct TriggerResult(bool Started, bool AccessDenied, string Detail);

    public static bool IsRegistered()
    {
        var ran = RunSchtasks(
            "/Query /TN \"" + ClockSyncTaskScript.TaskQueryName + "\" /XML",
            timeoutMs: 10_000);
        return ran.ExitCode == 0
            && ran.Text.Contains(ClockSyncTaskScript.TaskVersionToken, StringComparison.Ordinal);
    }

    public static RegisterResult TryRegister()
    {
        string? scriptPath = null;
        try
        {
            scriptPath = Path.Combine(
                Path.GetTempPath(),
                "s2f-clock-task-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(
                scriptPath,
                ClockSyncTaskScript.BuildRegistrationScript(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (!IsProcessElevated())
                psi.Verb = "runas";

            using var proc = Process.Start(psi);
            if (proc is null)
                return new RegisterResult(false, false, "Couldn't start elevated PowerShell.");

            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new RegisterResult(false, false, "Administrator approval timed out.");
            }

            if (proc.ExitCode != 0)
            {
                return new RegisterResult(
                    false,
                    false,
                    "Couldn't create the clock sync task (exit " + proc.ExitCode + ").");
            }

            if (!IsRegistered())
            {
                return new RegisterResult(
                    false,
                    false,
                    "The clock sync task was created, but this account can't run it.");
            }

            return new RegisterResult(true, false, string.Empty);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new RegisterResult(false, true, "Administrator permission was cancelled.");
        }
        catch (Exception ex)
        {
            return new RegisterResult(false, false, ex.Message);
        }
        finally
        {
            if (scriptPath is not null)
            {
                try { File.Delete(scriptPath); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>Returns an error message, or null when the request file was written.</summary>
    public static string? TryWriteRequest(string id, DateTimeOffset? referenceUtc)
    {
        var json = ClockSyncTaskScript.BuildRequestJson(id, referenceUtc);
        if (json is null)
            return "Couldn't build the clock sync request.";

        Directory.CreateDirectory(ClockSyncTaskScript.StateDirectory);
        File.WriteAllText(
            ClockSyncTaskScript.RequestFilePath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return null;
    }

    public static async Task<TriggerResult> RunAsync(string id, TimeSpan budget, CancellationToken cancellationToken)
    {
        var started = RunSchtasks(
            "/Run /TN \"" + ClockSyncTaskScript.TaskQueryName + "\"",
            timeoutMs: 15_000);
        if (started.ExitCode != 0)
        {
            var denied = started.Text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);
            var detail = string.IsNullOrWhiteSpace(started.Text)
                ? "Couldn't start clock sync."
                : started.Text.Trim();
            return new TriggerResult(false, denied, detail);
        }

        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadMatching(id, out var result))
                return new TriggerResult(true, false, result.Detail);

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        return new TriggerResult(true, false, "Clock sync didn't finish.");
    }

    private static bool TryReadMatching(string id, out ClockSyncTaskResult result)
    {
        result = default;
        try
        {
            if (!File.Exists(ClockSyncTaskScript.ResultFilePath))
                return false;

            var json = File.ReadAllText(ClockSyncTaskScript.ResultFilePath);
            return ClockSyncTaskScript.TryParseResult(json, out result)
                && string.Equals(result.Id, id, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private readonly record struct ProcResult(int ExitCode, string Text);

    private static ProcResult RunSchtasks(string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new ProcResult(-1, "Couldn't start schtasks.");

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new ProcResult(-1, "schtasks timed out.");
            }

            var text = (stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()).Trim();
            return new ProcResult(proc.ExitCode, text);
        }
        catch (Exception ex)
        {
            return new ProcResult(-1, ex.Message);
        }
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
