using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class LockOwnershipPolicyTests
{
    private static CheckoutLock Lock(string machineId, LockLifecycleStatus status) => new()
    {
        MachineId = machineId,
        MachineName = "PC",
        HolderName = "Timer",
        Status = status
    };

    [Fact]
    public void FailedDownload_IsUnconfirmed_EvenWhenWeStillHoldLocally()
    {
        var decision = LockOwnershipPolicy.Decide(
            downloadConfirmed: false,
            localAfter: Lock("AAA", LockLifecycleStatus.Active),
            ourMachineId: "AAA",
            heldBefore: true);

        Assert.Equal(LockCloudVerdict.Unconfirmed, decision.Verdict);
        Assert.False(decision.RestoreLocalActive);
    }

    [Fact]
    public void ConfirmedOurActiveLock_IsHeld()
    {
        var decision = LockOwnershipPolicy.Decide(
            downloadConfirmed: true,
            localAfter: Lock("aaa", LockLifecycleStatus.Active),
            ourMachineId: "AAA",
            heldBefore: true);

        Assert.Equal(LockCloudVerdict.Held, decision.Verdict);
        Assert.False(decision.RestoreLocalActive);
    }

    [Fact]
    public void ConfirmedOtherActiveLock_IsTakenOver_AndDoesNotRestore()
    {
        var decision = LockOwnershipPolicy.Decide(
            downloadConfirmed: true,
            localAfter: Lock("BBB", LockLifecycleStatus.Active),
            ourMachineId: "AAA",
            heldBefore: true);

        Assert.Equal(LockCloudVerdict.TakenOver, decision.Verdict);
        Assert.False(decision.RestoreLocalActive);
    }

    [Fact]
    public void ConfirmedReleasedLock_RestoresOurActiveCopy()
    {
        var decision = LockOwnershipPolicy.Decide(
            downloadConfirmed: true,
            localAfter: Lock("AAA", LockLifecycleStatus.Released),
            ourMachineId: "AAA",
            heldBefore: true);

        Assert.Equal(LockCloudVerdict.Held, decision.Verdict);
        Assert.True(decision.RestoreLocalActive);
    }

    [Fact]
    public void ConfirmedUnreadableLock_WithoutAPriorHold_IsUnconfirmed()
    {
        var decision = LockOwnershipPolicy.Decide(
            downloadConfirmed: true,
            localAfter: null,
            ourMachineId: "AAA",
            heldBefore: false);

        Assert.Equal(LockCloudVerdict.Unconfirmed, decision.Verdict);
        Assert.False(decision.RestoreLocalActive);
        Assert.False(decision.ReclaimFromActiveCheckout);
    }

    [Fact]
    public void ConfirmedReleasedLock_WithOrphanActive_Reclaims()
    {
        var decision = LockOwnershipPolicy.Decide(
            downloadConfirmed: true,
            localAfter: Lock("AAA", LockLifecycleStatus.Released),
            ourMachineId: "AAA",
            heldBefore: false,
            hasLocalTimingActive: true);

        Assert.Equal(LockCloudVerdict.Held, decision.Verdict);
        Assert.False(decision.RestoreLocalActive);
        Assert.True(decision.ReclaimFromActiveCheckout);
    }

    [Fact]
    public void ConfirmedReleasedLock_WithoutOrphanActive_IsUnconfirmed()
    {
        var decision = LockOwnershipPolicy.Decide(
            downloadConfirmed: true,
            localAfter: Lock("AAA", LockLifecycleStatus.Released),
            ourMachineId: "AAA",
            heldBefore: false,
            hasLocalTimingActive: false);

        Assert.Equal(LockCloudVerdict.Unconfirmed, decision.Verdict);
        Assert.False(decision.ReclaimFromActiveCheckout);
    }
}
