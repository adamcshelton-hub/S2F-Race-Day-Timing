using System.Windows;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Services;

/// <summary>
/// Windows clock sync. First install asks for administrator permission once and registers a
/// scheduled task. Later, clicking the time runs that task. The line under the clock follows
/// the latest HTTPS check. Checkout is never blocked.
/// </summary>
public static class SystemTimeSyncPrompt
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _offerOnStartup;

    public static void MarkOfferPending() => _offerOnStartup = true;

    public static async Task RunStartupAsync(Window? owner, Action<ClockHeaderNotice>? notify = null)
    {
        if (!await Gate.WaitAsync(0).ConfigureAwait(true))
            return;

        try
        {
            if (_offerOnStartup)
            {
                _offerOnStartup = false;
                EnsureTask(owner, notify);
            }

            await SyncCoreAsync(owner, notify, manual: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RaceDayTrace.Write($"time-sync exception error={ex.GetType().Name}: {ex.Message}");
            notify?.Invoke(ClockHeaderNotice.Hidden);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task SyncNowAsync(Window? owner, Action<ClockHeaderNotice>? notify = null)
    {
        if (!await Gate.WaitAsync(0).ConfigureAwait(true))
            return;

        try
        {
            await SyncCoreAsync(owner, notify, manual: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RaceDayTrace.Write($"time-sync exception error={ex.GetType().Name}: {ex.Message}");
            notify?.Invoke(ClockHeaderNotice.Unconfirmed);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool EnsureTask(Window? owner, Action<ClockHeaderNotice>? notify)
    {
        if (ClockSyncTaskRunner.IsRegistered())
            return true;

        var accepted = TimerDialog.Confirm(
            owner,
            "Sync Laptop Clock",
            "This lets Race Day Timing set the Windows clock from internet time. "
            + "Windows will ask for administrator permission once. "
            + "After that, clicking the time does not ask again.\n\n"
            + "You can skip. Checkout still works.",
            yesLabel: "Enable Sync",
            noLabel: "Skip");
        if (!accepted)
            return false;

        return Register(owner, notify);
    }

    private static bool Register(Window? owner, Action<ClockHeaderNotice>? notify)
    {
        notify?.Invoke(ClockHeaderNotice.WaitingForAdmin);
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
            body + "\n\nClick the time to try again. Checkout still works.");
        return false;
    }

    private static async Task SyncCoreAsync(Window? owner, Action<ClockHeaderNotice>? notify, bool manual)
    {
        if (manual)
            notify?.Invoke(ClockHeaderNotice.Checking);

        var before = await ProbeAsync().ConfigureAwait(true);
        var beforeReading = SystemTimeSyncPolicy.ClassifySkew(before.AbsoluteSkew);
        RaceDayTrace.Write(
            $"time-sync probe ok={before.Success} skew={(before.AbsoluteSkew is { } s ? s.TotalSeconds.ToString("0.0") + "s" : "n/a")} detail={before.Detail}");

        if (beforeReading == ClockSyncReading.Confirmed)
        {
            PersistSuccess();
            notify?.Invoke(manual ? ClockHeaderNotice.Synced : ClockHeaderNotice.Hidden);
            return;
        }

        if (!manual && !SystemTimeSyncPolicy.ShouldAttemptCorrection(before.AbsoluteSkew))
        {
            notify?.Invoke(ClockHeaderNotice.Hidden);
            return;
        }

        if (!ClockSyncTaskRunner.IsRegistered())
        {
            if (!manual)
            {
                MaybeWarn(owner, before.AbsoluteSkew, DateTimeOffset.Now);
                notify?.Invoke(ClockHeaderNotice.ForReading(beforeReading, before.AbsoluteSkew, attemptedCorrection: false, manual: false));
                return;
            }

            if (!EnsureTask(owner, notify))
            {
                notify?.Invoke(ClockHeaderNotice.ForReading(beforeReading, before.AbsoluteSkew, attemptedCorrection: false, manual: true));
                return;
            }
        }

        if (manual)
            notify?.Invoke(ClockHeaderNotice.Checking);

        var trigger = await TriggerAsync(owner, notify, before.ReferenceUtc, manual, allowRepair: manual)
            .ConfigureAwait(true);
        if (trigger is null)
        {
            if (!manual)
                MaybeWarn(owner, before.AbsoluteSkew, DateTimeOffset.Now);
            notify?.Invoke(ClockHeaderNotice.ForReading(beforeReading, before.AbsoluteSkew, attemptedCorrection: false, manual));
            return;
        }

        var after = await ProbeAsync().ConfigureAwait(true);
        RaceDayTrace.Write(
            $"time-sync after ok={after.Success} skew={(after.AbsoluteSkew is { } a ? a.TotalSeconds.ToString("0.0") + "s" : "n/a")} detail={after.Detail} task={trigger.Value.Detail}");

        var reading = SystemTimeSyncPolicy.ClassifySkew(after.AbsoluteSkew);
        if (SystemTimeSyncPolicy.ShouldPersistSuccess(reading))
        {
            PersistSuccess();
            notify?.Invoke(manual ? ClockHeaderNotice.Synced : ClockHeaderNotice.Hidden);
            return;
        }

        // Follow-up probe failed. Keep the earlier off reading so a dead network
        // does not clear a warning we already earned.
        if (reading == ClockSyncReading.Unknown && beforeReading == ClockSyncReading.Off)
        {
            reading = beforeReading;
        }

        var skew = after.AbsoluteSkew ?? before.AbsoluteSkew;
        var notice = ClockHeaderNotice.ForReading(reading, skew, attemptedCorrection: true, manual);
        notify?.Invoke(notice);

        if (manual && reading == ClockSyncReading.Off && skew is { } seconds)
        {
            TimerDialog.Alert(
                owner,
                "Laptop Clock Still Off",
                "This laptop’s clock is still about "
                + ClockHeaderNotice.FormatDuration(seconds.TotalSeconds)
                + " off from internet time.\n\n"
                + "RunScore and chip timing use the Windows clock. "
                + "Click the time at the top of the window to try again before the race start.\n\n"
                + "Checkout still works.");
        }

        if (!manual)
            MaybeWarn(owner, skew, DateTimeOffset.Now);
    }

    private static async Task<ClockSyncTaskRunner.TriggerResult?> TriggerAsync(
        Window? owner,
        Action<ClockHeaderNotice>? notify,
        DateTimeOffset? referenceUtc,
        bool manual,
        bool allowRepair)
    {
        var id = Guid.NewGuid().ToString("N");
        if (!TryWrite(owner, notify, id, referenceUtc, manual))
            return null;

        var trigger = await ClockSyncTaskRunner
            .RunAsync(id, TimeSpan.FromSeconds(25), CancellationToken.None)
            .ConfigureAwait(true);

        if (trigger.AccessDenied && allowRepair)
        {
            RaceDayTrace.Write("time-sync task access denied; registering again");
            if (!Register(owner, notify))
                return null;

            id = Guid.NewGuid().ToString("N");
            if (!TryWrite(owner, notify, id, referenceUtc, manual))
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
        Action<ClockHeaderNotice>? notify,
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
            if (!manual || !Register(owner, notify))
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
            + ClockHeaderNotice.FormatDuration(seconds)
            + " off from internet time.\n\n"
            + "RunScore and chip timing use the Windows clock. "
            + "Click the time at the top of the window to sync it.\n\n"
            + "Checkout still works — fix the clock before race start if you can.");
    }

}
