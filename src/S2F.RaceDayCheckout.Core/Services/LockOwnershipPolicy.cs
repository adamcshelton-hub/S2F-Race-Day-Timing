using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public enum LockCloudVerdict
{
    /// <summary>This machine holds an Active lock. Cloud writes are allowed.</summary>
    Held,

    /// <summary>A downloaded lock is Active and belongs to another machine.</summary>
    TakenOver,

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
    public static LockCheckDecision Decide(
        bool downloadConfirmed,
        CheckoutLock? localAfter,
        string ourMachineId,
        bool heldBefore,
        bool hasLocalTimingActive = false)
    {
        if (!downloadConfirmed)
            return new LockCheckDecision(LockCloudVerdict.Unconfirmed, RestoreLocalActive: false);

        if (IsActiveOther(localAfter, ourMachineId))
            return new LockCheckDecision(LockCloudVerdict.TakenOver, RestoreLocalActive: false);

        if (IsActiveOurs(localAfter, ourMachineId))
            return new LockCheckDecision(LockCloudVerdict.Held, RestoreLocalActive: false);

        // Cloud answered with Released, missing, or unreadable content. If this laptop
        // held Active before that download replaced the file, put the local lock back.
        if (heldBefore)
            return new LockCheckDecision(LockCloudVerdict.Held, RestoreLocalActive: true);

        // Orphan Active: lock already Released (mid-Finish kill) but race files still checked out.
        // Reclaim a fresh Active lock so Finish / heartbeat can proceed. Not a takeover.
        if (hasLocalTimingActive)
            return new LockCheckDecision(
                LockCloudVerdict.Held,
                RestoreLocalActive: false,
                ReclaimFromActiveCheckout: true);

        return new LockCheckDecision(LockCloudVerdict.Unconfirmed, RestoreLocalActive: false);
    }

    private static bool IsActiveOurs(CheckoutLock? lockInfo, string ourMachineId) =>
        lockInfo is { Status: LockLifecycleStatus.Active }
        && SameMachine(lockInfo.MachineId, ourMachineId);

    private static bool IsActiveOther(CheckoutLock? lockInfo, string ourMachineId) =>
        lockInfo is { Status: LockLifecycleStatus.Active }
        && !SameMachine(lockInfo.MachineId, ourMachineId);

    private static bool SameMachine(string lockMachineId, string ourMachineId) =>
        string.Equals(lockMachineId, ourMachineId, StringComparison.OrdinalIgnoreCase);
}
