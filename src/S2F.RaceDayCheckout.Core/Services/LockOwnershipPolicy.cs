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

public readonly record struct LockCheckDecision(LockCloudVerdict Verdict, bool RestoreLocalActive);

/// <summary>
/// Decides whether a checkout lock check may upload, must stop for a real takeover,
/// or must wait. A failed download is not evidence that another laptop holds the race.
/// </summary>
public static class LockOwnershipPolicy
{
    public static LockCheckDecision Decide(
        bool downloadConfirmed,
        CheckoutLock? localAfter,
        string ourMachineId,
        bool heldBefore)
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
