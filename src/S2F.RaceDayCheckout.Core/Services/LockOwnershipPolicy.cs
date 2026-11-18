using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public enum LockCloudVerdict
{
    /// <summary>This machine holds an Active lock. Cloud writes are allowed.</summary>
    Held,

    /// <summary>A downloaded lock is Active and belongs to another machine.</summary>
    TakenOver,

    /// <summary>
    /// Another laptop released the lock after this checkout started.
    /// Do not restore or reclaim — that would check the race out again.
    /// </summary>
    ClosedElsewhere,

    /// <summary>OneDrive did not confirm the lock. Do not upload and do not claim a takeover.</summary>
    Unconfirmed
}

public readonly record struct LockCheckDecision(
    LockCloudVerdict Verdict,
    bool RestoreLocalActive,
    bool ReclaimFromActiveCheckout = false);

/// <summary>
/// Decides whether a checkout lock check may upload, must stop for a real takeover,
/// or must wait. A failed download is not evidence that another laptop holds the race.
/// </summary>
public static class LockOwnershipPolicy
{
    /// <param name="heldBefore">
    /// True when this machine held an Active lock file before the cloud download replaced it.
    /// </param>
    /// <param name="hasLocalTimingActive">
    /// True when <c>active.json</c> still lists a timing checkout for this session.
    /// Covers crash-after-Release / kill-mid-Finish where local+cloud lock are Released
    /// but Active race files remain — Finish must reclaim, not block as Unconfirmed.
    /// </param>
    /// <param name="ourCheckedOutAt">
    /// When this laptop's current checkout started. A Released lock from another laptop
    /// after this time closed the race. An older Released file is stale and must not.
    /// </param>
    public static LockCheckDecision Decide(
        bool downloadConfirmed,
        CheckoutLock? localAfter,
        string ourMachineId,
        bool heldBefore,
        bool hasLocalTimingActive = false,
        DateTimeOffset? ourCheckedOutAt = null,
        string? ourMachineName = null,
        string? ourWindowsUser = null)
    {
        if (!downloadConfirmed)
            return new LockCheckDecision(LockCloudVerdict.Unconfirmed, RestoreLocalActive: false);

        if (IsActiveOther(localAfter, ourMachineId, ourMachineName, ourWindowsUser))
            return new LockCheckDecision(LockCloudVerdict.TakenOver, RestoreLocalActive: false);

        if (IsActiveOurs(localAfter, ourMachineId, ourMachineName, ourWindowsUser))
            return new LockCheckDecision(LockCloudVerdict.Held, RestoreLocalActive: false);

        // Another laptop finished this race. Putting our Active lock back would undo their check-in.
        if (ForeignReleaseSupersedes(
                localAfter, ourMachineId, ourMachineName, ourWindowsUser, ourCheckedOutAt))
        {
            return new LockCheckDecision(LockCloudVerdict.ClosedElsewhere, RestoreLocalActive: false);
        }

        // Cloud answered with Released, missing, or unreadable content. If this laptop
        // held Active before that download replaced the file, put the local lock back.
        // Not used for another laptop's newer release (handled above).
        if (heldBefore)
            return new LockCheckDecision(LockCloudVerdict.Held, RestoreLocalActive: true);

        // Orphan Active: our own lock already Released (mid-Finish kill) but race files remain.
        // Reclaim a fresh Active lock so Finish / heartbeat can proceed. Not a takeover.
        if (hasLocalTimingActive)
            return new LockCheckDecision(
                LockCloudVerdict.Held,
                RestoreLocalActive: false,
                ReclaimFromActiveCheckout: true);

        return new LockCheckDecision(LockCloudVerdict.Unconfirmed, RestoreLocalActive: false);
    }

    /// <summary>
    /// Same laptop when the id matches, or when the PC name and Windows user match.
    /// The id used to include the OS version, so a Windows update overnight made this
    /// PC a stranger to its own lock. Name + user keeps that checkout ours.
    /// </summary>
    public static bool SameLaptop(
        CheckoutLock? lockInfo,
        string ourMachineId,
        string? ourMachineName,
        string? ourWindowsUser)
    {
        if (lockInfo is null)
            return false;
        if (string.Equals(lockInfo.MachineId, ourMachineId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(ourMachineName) || string.IsNullOrWhiteSpace(ourWindowsUser))
            return false;
        if (string.IsNullOrWhiteSpace(lockInfo.MachineName) || string.IsNullOrWhiteSpace(lockInfo.WindowsUser))
            return false;
        return string.Equals(lockInfo.MachineName, ourMachineName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(lockInfo.WindowsUser, ourWindowsUser, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActiveOurs(
        CheckoutLock? lockInfo,
        string ourMachineId,
        string? ourMachineName,
        string? ourWindowsUser) =>
        lockInfo is { Status: LockLifecycleStatus.Active }
        && SameLaptop(lockInfo, ourMachineId, ourMachineName, ourWindowsUser);

    private static bool IsActiveOther(
        CheckoutLock? lockInfo,
        string ourMachineId,
        string? ourMachineName,
        string? ourWindowsUser) =>
        lockInfo is { Status: LockLifecycleStatus.Active }
        && !SameLaptop(lockInfo, ourMachineId, ourMachineName, ourWindowsUser);

    private static bool ForeignReleaseSupersedes(
        CheckoutLock? cloud,
        string ourMachineId,
        string? ourMachineName,
        string? ourWindowsUser,
        DateTimeOffset? ourCheckedOutAt)
    {
        if (cloud is not { Status: LockLifecycleStatus.Released })
            return false;
        if (SameLaptop(cloud, ourMachineId, ourMachineName, ourWindowsUser))
            return false;
        // We checked out again after they finished. Their Released file is stale.
        if (ourCheckedOutAt is { } at && at > cloud.LastSeenAt)
            return false;
        return true;
    }
}
