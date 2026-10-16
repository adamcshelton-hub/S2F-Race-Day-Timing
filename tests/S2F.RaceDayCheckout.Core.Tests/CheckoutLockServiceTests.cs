using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class CheckoutLockServiceTests
{
    private sealed class FakeMachine : IMachineIdentity
    {
        public FakeMachine(string id, string name = "PC1", string user = "timer")
        {
            MachineId = id;
            MachineName = name;
            WindowsUser = user;
        }

        public string MachineId { get; }
        public string MachineName { get; }
        public string WindowsUser { get; }
    }

    private static readonly string[] Scope = ["RUNSCORE", "race.event"];

    private static (string Root, CheckoutLockService Locks) CreateEnv(
        IMachineIdentity machine,
        Func<DateTimeOffset>? clock = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-lock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var locks = new CheckoutLockService(new PathService(), machine, clock);
        return (root, locks);
    }

    private static CheckoutLock Acquire(CheckoutLockService locks, string root, string holder) =>
        locks.Acquire(root, "Memphis", "2026 - Test", "2026 - Test", holder, Scope, "race.event");

    [Fact]
    public void Acquire_WhenFree_Succeeds()
    {
        var (root, locks) = CreateEnv(new FakeMachine("AAA"));
        var eval = locks.EvaluateAcquire(root, new AppSettings());
        Assert.Equal(LockAcquireOutcome.Acquired, eval.Outcome);

        var acquired = Acquire(locks, root, "Adam");
        Assert.Equal("Adam", acquired.HolderName);
        Assert.Contains("RUNSCORE", acquired.ScopedPaths);
        Assert.Equal(LockLifecycleStatus.Active, acquired.Status);
    }

    [Fact]
    public void SameMachine_CanReclaim()
    {
        var (root, locks) = CreateEnv(new FakeMachine("AAA"));
        Acquire(locks, root, "Adam");
        Assert.Equal(LockAcquireOutcome.SameMachineReclaim, locks.EvaluateAcquire(root, new AppSettings()).Outcome);
    }

    [Fact]
    public void OtherMachine_Alive_Blocks()
    {
        var now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
        var (root, holderLocks) = CreateEnv(new FakeMachine("AAA", "LaptopA"), () => now);
        Acquire(holderLocks, root, "Sam");

        var otherLocks = new CheckoutLockService(new PathService(), new FakeMachine("BBB", "LaptopB"), () => now.AddMinutes(5));
        var eval = otherLocks.EvaluateAcquire(root, new AppSettings { StaleLockMinutes = 18 });
        Assert.Equal(LockAcquireOutcome.BlockedAlive, eval.Outcome);
        Assert.Contains("Sam", eval.Message);
    }

    [Fact]
    public void OtherMachine_Stale_NeedsEmergency()
    {
        var now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
        var (root, holderLocks) = CreateEnv(new FakeMachine("AAA"), () => now);
        Acquire(holderLocks, root, "Sam");

        var otherLocks = new CheckoutLockService(new PathService(), new FakeMachine("BBB"), () => now.AddMinutes(30));
        Assert.Equal(
            LockAcquireOutcome.NeedsEmergencyConfirm,
            otherLocks.EvaluateAcquire(root, new AppSettings { StaleLockMinutes = 18 }).Outcome);
    }

    [Fact]
    public void ForceUnlock_RequiresPin()
    {
        var (root, locks) = CreateEnv(new FakeMachine("AAA"));
        Acquire(locks, root, "Sam");
        var settings = new AppSettings { AdminPin = "9999" };
        Assert.False(locks.ForceUnlock(root, "Admin", "forgot", "0000", settings));
        Assert.True(locks.ForceUnlock(root, "Admin", "forgot", "9999", settings));
        Assert.Equal(LockLifecycleStatus.Released, locks.TryRead(root)!.Status);
    }

    [Fact]
    public void Heartbeat_UpdatesLastSeen()
    {
        var t = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
        var (root, locks) = CreateEnv(new FakeMachine("AAA"), () => t);
        Acquire(locks, root, "Adam");
        t = t.AddMinutes(2);
        locks.Heartbeat(root);
        Assert.Equal(t, locks.TryRead(root)!.LastSeenAt);
    }
}
