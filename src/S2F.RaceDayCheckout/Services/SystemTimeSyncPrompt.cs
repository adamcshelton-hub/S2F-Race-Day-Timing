using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Services;

/// <summary>
/// Launch-time Windows clock sync. HTTPS Date measures skew; elevated PowerShell runs
/// <c>w32tm /resync /force</c> and optionally <c>Set-Date</c> when NTP UDP is blocked.
/// Never blocks checkout — warn-only when skew stays large.
/// </summary>
public static class SystemTimeSyncPrompt
{
    public static async Task TryRunAsync(Window? owner, Action<string>? setStatus = null)
    {
        try
        {
            await RunCoreAsync(owner, setStatus).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RaceDayTrace.Write($"time-sync exception error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task RunCoreAsync(Window? owner, Action<string>? setStatus)
    {
        var store = new SettingsStore();
        var settings = store.Load();
        var localNow = DateTimeOffset.Now;

        HttpDateProbeResult probe;
        using (var http = HttpDateTimeProbe.CreateDefaultClient())
        {
            var prober = new HttpDateTimeProbe(http, () => DateTimeOffset.UtcNow);
            using var probeCts = new CancellationTokenSource(SystemTimeSyncPolicy.ProbeBudget);
            try
            {
                probe = await prober.ProbeAsync(probeCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                probe = new HttpDateProbeResult(false, null, null, "HTTPS time probe timed out.");
            }
        }

        RaceDayTrace.Write(
            $"time-sync probe ok={probe.Success} skew={(probe.AbsoluteSkew is { } s ? s.TotalSeconds.ToString("0.0") + "s" : "n/a")} detail={probe.Detail}");

        if (probe.Success
            && probe.AbsoluteSkew is { } goodSkew
            && goodSkew < SystemTimeSyncPolicy.CorrectThreshold)
        {
            SystemTimeSyncPolicy.MarkSuccess(settings, DateTimeOffset.UtcNow);
            store.Save(settings);
            return;
        }

        if (!SystemTimeSyncPolicy.ShouldAttemptCorrection(probe.AbsoluteSkew, settings, localNow))
        {
            MaybeWarn(owner, store, settings, probe.AbsoluteSkew, localNow);
            return;
        }

        setStatus?.Invoke("Syncing laptop clock…");

        var elevate = TryElevatedSync(probe.ReferenceUtc);
        if (elevate.UacDeclined)
        {
            SystemTimeSyncPolicy.MarkUacDeclined(settings, DateTimeOffset.Now);
            store.Save(settings);
            RaceDayTrace.Write("time-sync elevate declined");
            MaybeWarn(owner, store, settings, probe.AbsoluteSkew, DateTimeOffset.Now);
            return;
        }

        RaceDayTrace.Write(
            $"time-sync elevate ok={elevate.Success} detail={elevate.Message}");

        // Re-measure after correction.
        HttpDateProbeResult after;
        using (var http = HttpDateTimeProbe.CreateDefaultClient())
        {
            var prober = new HttpDateTimeProbe(http, () => DateTimeOffset.UtcNow);
            using var afterCts = new CancellationTokenSource(SystemTimeSyncPolicy.ProbeBudget);
            try
            {
                after = await prober.ProbeAsync(afterCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                after = new HttpDateProbeResult(false, null, null, "Post-sync HTTPS probe timed out.");
            }
        }

        RaceDayTrace.Write(
            $"time-sync after ok={after.Success} skew={(after.AbsoluteSkew is { } a ? a.TotalSeconds.ToString("0.0") + "s" : "n/a")} detail={after.Detail}");

        if (after.Success
            && after.AbsoluteSkew is { } afterSkew
            && afterSkew < SystemTimeSyncPolicy.CorrectThreshold)
        {
            SystemTimeSyncPolicy.MarkSuccess(settings, DateTimeOffset.UtcNow);
            store.Save(settings);
            setStatus?.Invoke("Laptop clock synced.");
            return;
        }

        // Elevate reported success but we couldn't re-verify — still record attempt time lightly.
        if (elevate.Success && !after.Success)
        {
            SystemTimeSyncPolicy.MarkSuccess(settings, DateTimeOffset.UtcNow);
            store.Save(settings);
        }

        var warnSkew = after.AbsoluteSkew ?? probe.AbsoluteSkew;
        MaybeWarn(owner, store, settings, warnSkew, DateTimeOffset.Now);
    }

    private static void MaybeWarn(
        Window? owner,
        SettingsStore store,
        Core.Models.AppSettings settings,
        TimeSpan? absoluteSkew,
        DateTimeOffset localNow)
    {
        if (!SystemTimeSyncPolicy.ShouldWarnAboutSkew(absoluteSkew, settings, localNow))
            return;

        var seconds = absoluteSkew?.TotalSeconds ?? 0;
        SystemTimeSyncPolicy.MarkSkewWarned(settings, localNow);
        store.Save(settings);

        TimerDialog.Alert(
            owner,
            "Laptop Clock May Be Wrong",
            "This laptop’s clock is about "
            + FormatSkew(seconds)
            + " off from internet time.\n\n"
            + "RunScore and chip timing use the Windows clock. "
            + "Open Settings → Time & language → Date & time → Sync now, "
            + "or approve the administrator prompt next launch.\n\n"
            + "Checkout still works — fix the clock before race start if you can.");
    }

    private static string FormatSkew(double seconds)
    {
        var abs = Math.Abs(seconds);
        if (abs >= 120)
            return (abs / 60.0).ToString("0") + " minutes";
        return abs.ToString("0") + " seconds";
    }

    private readonly record struct ElevateResult(bool Success, bool UacDeclined, string Message);

    private static ElevateResult TryElevatedSync(DateTimeOffset? setClockFromUtc)
    {
        string? scriptPath = null;
        try
        {
            scriptPath = Path.Combine(
                Path.GetTempPath(),
                "s2f-time-sync-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(
                scriptPath,
                SystemTimeSyncPolicy.BuildElevatedSyncScript(setClockFromUtc));

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
                return new ElevateResult(false, false, "Couldn't start elevated PowerShell.");

            var budgetMs = (int)SystemTimeSyncPolicy.ElevateBudget.TotalMilliseconds;
            if (!proc.WaitForExit(budgetMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new ElevateResult(false, false, "Elevated PowerShell timed out.");
            }

            if (proc.ExitCode != 0)
                return new ElevateResult(false, false, "PowerShell exited with code " + proc.ExitCode + ".");

            return new ElevateResult(true, false, string.Empty);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ElevateResult(false, true, "Administrator permission was cancelled.");
        }
        catch (Exception ex)
        {
            return new ElevateResult(false, false, ex.Message);
        }
        finally
        {
            if (scriptPath is not null)
            {
                try { File.Delete(scriptPath); } catch { /* ignore */ }
            }
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
