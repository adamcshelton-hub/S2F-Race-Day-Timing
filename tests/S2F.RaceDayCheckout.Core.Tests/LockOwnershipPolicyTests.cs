using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class LockOwnershipPolicyTests
{
    private static CheckoutLock Lock(
        string machineId,
        LockLifecycleStatus status,
        DateTimeOffset? lastSeen = null,
        string? machineName = null,
        string? windowsUser = null) => new()
    {
        MachineId = machineId,
        MachineName = machineName ?? "PC",
        WindowsUser = windowsUser ?? string.Empty,
        HolderName = "Timer",
        Status = status,
        LastSeenAt = lastSeen ?? default,
        CheckedOutAt = lastSeen ?? default
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

    [Fact]
    public void ReleasedByOtherLaptop_DoesNotRestoreOrReclaim()
    {
        var finished = new DateTimeOffset(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);
        var cloud = Lock("BBB", LockLifecycleStatus.Released, finished, "ADAM-VM", "Adam");
        var decision = LockOwnershipPolicy.Decide(
            downloadConfirmed: true,
            localAfter: cloud,
            ourMachineId: "AAA",
            heldBefore: true,
            hasLocalTimingActive: true,
            ourCheckedOutAt: finished.AddHours(-10),
            ourMachineName: "DENISE-PC",
            ourWindowsUser: "Denise");

        Assert.Equal(LockCloudVerdict.ClosedElsewhere, decision.Verdict);
        Assert.False(decision.RestoreLocalActive);
        Assert.False(decision.ReclaimFromActiveCheckout);
    }

    [Fact]
    public void StaleReleasedByOtherLaptop_AfterOurNewCheckout_RestoresLocalActive()
    {
        var theirFinish = new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);
        var cloud = Lock("BBB", LockLifecycleStatus.Released, theirFinish, "ADAM-VM", "Adam");
        var decision = LockOwnershipPolicy.Decide(
            downloadConfirmed: true,
            localAfter: cloud,
            ourMachineId: "AAA",
            heldBefore: true,
            hasLocalTimingActive: true,
            ourCheckedOutAt: theirFinish.AddHours(2),
            ourMachineName: "DENISE-PC",
            ourWindowsUser: "Denise");

        Assert.Equal(LockCloudVerdict.Held, decision.Verdict);
        Assert.True(decision.RestoreLocalActive);
        Assert.False(decision.ReclaimFromActiveCheckout);
    }

    [Fact]
    public void ActiveLock_SamePcNameAndUser_DifferentId_IsHeld()
    {
        var cloud = Lock(
            "OLDID",
            LockLifecycleStatus.Active,
            machineName: "DENISE-PC",
            windowsUser: "Denise");
        var decision = LockOwnershipPolicy.Decide(
            downloadConfirmed: true,
            localAfter: cloud,
            ourMachineId: "NEWID",
            heldBefore: false,
            ourMachineName: "DENISE-PC",
            ourWindowsUser: "Denise");

        Assert.Equal(LockCloudVerdict.Held, decision.Verdict);
        Assert.False(decision.RestoreLocalActive);
    }
}
