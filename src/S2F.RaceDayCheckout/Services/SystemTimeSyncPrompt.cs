using System.Windows;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Services;

/// <summary>
/// Windows clock sync. First install (or the first Sync Clock click) asks for administrator
/// permission once and registers a scheduled task. Later runs call that task — no second prompt.
/// Success is a fresh HTTPS time check. Checkout is never blocked.
/// </summary>
public static class SystemTimeSyncPrompt
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _offerOnStartup;

    public static void MarkOfferPending() => _offerOnStartup = true;

    public static async Task RunStartupAsync(Window? owner, Action<string>? setStatus = null)
    {
        if (!await Gate.WaitAsync(0).ConfigureAwait(true))
            return;

        try
        {
            if (_offerOnStartup)
            {
                _offerOnStartup = false;
                EnsureTask(owner, setStatus);
            }

            await SyncCoreAsync(owner, setStatus, manual: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RaceDayTrace.Write($"time-sync exception error={ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task SyncNowAsync(Window? owner, Action<string>? setStatus = null)
    {
        if (!await Gate.WaitAsync(0).ConfigureAwait(true))
        {
            setStatus?.Invoke("Clock sync is already running.");
            return;
        }

        try
        {
            if (!EnsureTask(owner, setStatus))
                return;

            await SyncCoreAsync(owner, setStatus, manual: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RaceDayTrace.Write($"time-sync exception error={ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool EnsureTask(Window? owner, Action<string>? setStatus)
    {
        if (ClockSyncTaskRunner.IsRegistered())
            return true;

        var accepted = TimerDialog.Confirm(
            owner,
            "Sync Laptop Clock",
            "This lets Race Day Timing set the Windows clock from internet time. "
            + "Windows will ask for administrator permission once. "
            + "After that, Sync Clock runs without another prompt.\n\n"
            + "You can skip. Checkout still works.",
            yesLabel: "Enable Sync",
            noLabel: "Skip");
        if (!accepted)
            return false;

        return Register(owner, setStatus);
    }

    private static bool Register(Window? owner, Action<string>? setStatus)
    {
        setStatus?.Invoke("Waiting for administrator approval…");
        var reg = ClockSyncTaskRunner.TryRegister();
        RaceDayTrace.Write(
            $"time-sync register ok={reg.Success} declined={reg.UacDeclined} detail={reg.Message}");
        if (reg.Success)
            return true;

        var title = reg.UacDeclined ? "Clock Sync Needs Administrator" : "Couldn't Set Up Clock Sync";
        var body = string.IsNullOrWhiteSpace(reg.Message)
            ? "Windows didn't create the clock sync task."
            : reg.Message;
        TimerDialog.Alert(
            owner,
            title,
            body + "\n\nSync Clock can try again. Checkout still works.");
        return false;
    }

    private static async Task SyncCoreAsync(Window? owner, Action<string>? setStatus, bool manual)
    {
        var before = await ProbeAsync().ConfigureAwait(true);
        RaceDayTrace.Write(
            $"time-sync probe ok={before.Success} skew={(before.AbsoluteSkew is { } s ? s.TotalSeconds.ToString("0.0") + "s" : "n/a")} detail={before.Detail}");

        if (SystemTimeSyncPolicy.ClassifySkew(before.AbsoluteSkew) == ClockSyncReading.Confirmed)
        {
            PersistSuccess();
            if (manual)
                setStatus?.Invoke("Laptop clock synced.");
            return;
        }

        if (!ClockSyncTaskRunner.IsRegistered())
        {
            if (manual)
            {
                TimerDialog.Alert(
                    owner,
                    "Couldn't Sync Clock",
                    "Clock sync isn't set up on this laptop yet.\n\nCheckout still works.");
            }
            else
            {
                MaybeWarn(owner, before.AbsoluteSkew, DateTimeOffset.Now);
            }

            return;
        }

        if (!manual && !SystemTimeSyncPolicy.ShouldAttemptCorrection(before.AbsoluteSkew))
            return;

        setStatus?.Invoke("Syncing laptop clock…");
        var trigger = await TriggerAsync(owner, setStatus, before.ReferenceUtc, manual, allowRepair: manual)
            .ConfigureAwait(true);
        if (trigger is null)
        {
            if (!manual)
                MaybeWarn(owner, before.AbsoluteSkew, DateTimeOffset.Now);
            return;
        }

        var after = await ProbeAsync().ConfigureAwait(true);
        RaceDayTrace.Write(
            $"time-sync after ok={after.Success} skew={(after.AbsoluteSkew is { } a ? a.TotalSeconds.ToString("0.0") + "s" : "n/a")} detail={after.Detail} task={trigger.Value.Detail}");

        var reading = SystemTimeSyncPolicy.ClassifySkew(after.AbsoluteSkew);
        if (SystemTimeSyncPolicy.ShouldPersistSuccess(reading))
        {
            PersistSuccess();
            setStatus?.Invoke(reading == ClockSyncReading.Confirmed
                ? "Laptop clock synced."
                : "Laptop clock is within a couple of seconds of internet time.");
            return;
        }

        var skew = after.AbsoluteSkew ?? before.AbsoluteSkew;
        if (manual)
        {
            if (reading == ClockSyncReading.Off && skew is { } seconds)
            {
                TimerDialog.Alert(
                    owner,
                    "Laptop Clock Still Off",
                    "This laptop’s clock is still about "
                    + FormatSkew(seconds.TotalSeconds)
                    + " off from internet time.\n\n"
                    + "RunScore and chip timing use the Windows clock. "
                    + "Try Sync Clock again before the race start.\n\n"
                    + "Checkout still works.");
            }
            else
            {
                TimerDialog.Alert(
                    owner,
                    "Couldn't Confirm Laptop Clock",
                    "Race Day Timing couldn't confirm the laptop clock against internet time. "
                    + "Try Sync Clock again before the race start.\n\n"
                    + "Checkout still works.");
            }

            return;
        }

        MaybeWarn(owner, skew, DateTimeOffset.Now);
    }

    private static async Task<ClockSyncTaskRunner.TriggerResult?> TriggerAsync(
        Window? owner,
        Action<string>? setStatus,
        DateTimeOffset? referenceUtc,
        bool manual,
        bool allowRepair)
    {
        var id = Guid.NewGuid().ToString("N");
        if (!TryWrite(owner, setStatus, id, referenceUtc, manual))
            return null;

        var trigger = await ClockSyncTaskRunner
            .RunAsync(id, TimeSpan.FromSeconds(25), CancellationToken.None)
            .ConfigureAwait(true);

        if (trigger.AccessDenied && allowRepair)
        {
            RaceDayTrace.Write("time-sync task access denied; registering again");
            if (!Register(owner, setStatus))
                return null;

            id = Guid.NewGuid().ToString("N");
            if (!TryWrite(owner, setStatus, id, referenceUtc, manual))
                return null;

            trigger = await ClockSyncTaskRunner
                .RunAsync(id, TimeSpan.FromSeconds(25), CancellationToken.None)
                .ConfigureAwait(true);
        }

        if (!trigger.Started)
        {
            RaceDayTrace.Write("time-sync task start failed detail=" + trigger.Detail);
            if (manual)
            {
                TimerDialog.Alert(
                    owner,
                    "Couldn't Sync Clock",
                    trigger.Detail + "\n\nCheckout still works.");
            }

            return null;
        }

        return trigger;
    }

    private static bool TryWrite(
        Window? owner,
        Action<string>? setStatus,
        string id,
        DateTimeOffset? referenceUtc,
        bool manual)
    {
        try
        {
            var error = ClockSyncTaskRunner.TryWriteRequest(id, referenceUtc);
            if (error is null)
                return true;

            if (manual)
            {
                TimerDialog.Alert(owner, "Couldn't Sync Clock", error + "\n\nCheckout still works.");
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            if (!manual || !Register(owner, setStatus))
                return false;

            try
            {
                var error = ClockSyncTaskRunner.TryWriteRequest(id, referenceUtc);
                if (error is null)
                    return true;

                TimerDialog.Alert(owner, "Couldn't Sync Clock", error + "\n\nCheckout still works.");
                return false;
            }
            catch (Exception ex)
            {
                TimerDialog.Alert(owner, "Couldn't Sync Clock", ex.Message + "\n\nCheckout still works.");
                return false;
            }
        }
        catch (Exception ex)
        {
            RaceDayTrace.Write($"time-sync request error={ex.GetType().Name}: {ex.Message}");
            if (manual)
            {
                TimerDialog.Alert(owner, "Couldn't Sync Clock", ex.Message + "\n\nCheckout still works.");
            }

            return false;
        }
    }

    private static async Task<HttpDateProbeResult> ProbeAsync()
    {
        using var http = HttpDateTimeProbe.CreateDefaultClient();
        var prober = new HttpDateTimeProbe(http, () => DateTimeOffset.UtcNow);
        using var cts = new CancellationTokenSource(SystemTimeSyncPolicy.ProbeBudget);
        try
        {
            return await prober.ProbeAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return new HttpDateProbeResult(false, null, null, "HTTPS time probe timed out.");
        }
    }

    private static void PersistSuccess()
    {
        var store = new SettingsStore();
        var settings = store.Load();
        SystemTimeSyncPolicy.MarkSuccess(settings, DateTimeOffset.UtcNow);
        store.Save(settings);
    }

    private static void MaybeWarn(Window? owner, TimeSpan? absoluteSkew, DateTimeOffset localNow)
    {
        var store = new SettingsStore();
        var settings = store.Load();
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
            + "Use Sync Clock under the time at the top of the window.\n\n"
            + "Checkout still works — fix the clock before race start if you can.");
    }

    private static string FormatSkew(double seconds)
    {
        var abs = Math.Abs(seconds);
        if (abs >= 120)
            return (abs / 60.0).ToString("0") + " minutes";
        return abs.ToString("0") + " seconds";
    }
}
