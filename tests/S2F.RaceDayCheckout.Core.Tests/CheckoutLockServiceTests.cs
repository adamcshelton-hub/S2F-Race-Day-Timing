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
        var eval = otherLocks.EvaluateAcquire(root, new AppSettings { StaleLockMinutes = AppSettings.DefaultStaleLockMinutes });
        Assert.Equal(LockAcquireOutcome.BlockedAlive, eval.Outcome);
        Assert.Contains("Sam", eval.Message);
    }

    [Fact]
    public void OtherMachine_Stale_NeedsEmergency()
    {
        var now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
        var (root, holderLocks) = CreateEnv(new FakeMachine("AAA"), () => now);
        Acquire(holderLocks, root, "Sam");

        // Just past the soft-takeover window (default 6 minutes).
        var otherLocks = new CheckoutLockService(
            new PathService(),
            new FakeMachine("BBB"),
            () => now.AddMinutes(AppSettings.DefaultStaleLockMinutes + 1));
        Assert.Equal(
            LockAcquireOutcome.NeedsEmergencyConfirm,
            otherLocks.EvaluateAcquire(root, new AppSettings { StaleLockMinutes = AppSettings.DefaultStaleLockMinutes }).Outcome);
    }

    [Fact]
    public void ForceUnlock_RequiresFixedCompanyPin()
    {
        var (root, locks) = CreateEnv(new FakeMachine("AAA"));
        Acquire(locks, root, "Sam");
        var settings = new AppSettings();
        Assert.False(locks.ForceUnlock(root, "Admin", "forgot", "0000", settings));
        Assert.True(locks.ForceUnlock(root, "Admin", "forgot", AppSettings.DefaultAdminPin, settings));
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

    [Fact]
    public void ThisMachineHoldsActiveLock_FalseWhenOtherMachine()
    {
        var (root, holderLocks) = CreateEnv(new FakeMachine("AAA", "LaptopA"));
        Acquire(holderLocks, root, "Sam");

        var other = new CheckoutLockService(new PathService(), new FakeMachine("BBB", "LaptopB"));
        Assert.False(other.ThisMachineHoldsActiveLock(root));
        Assert.NotNull(other.TryGetOtherMachineActiveLock(root));
        Assert.Equal("Sam", other.TryGetOtherMachineActiveLock(root)!.HolderName);
    }

    [Fact]
    public void ThisMachineHoldsActiveLock_TrueWhenOurs()
    {
        var (root, locks) = CreateEnv(new FakeMachine("AAA"));
        Acquire(locks, root, "Adam");
        Assert.True(locks.ThisMachineHoldsActiveLock(root));
        Assert.Null(locks.TryGetOtherMachineActiveLock(root));
    }

    [Fact]
    public void Heartbeat_DoesNotRefreshWhenOtherMachineHoldsLock()
    {
        var t0 = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
        var (root, holderLocks) = CreateEnv(new FakeMachine("AAA"), () => t0);
        Acquire(holderLocks, root, "Sam");
        var lastSeen = holderLocks.TryRead(root)!.LastSeenAt;

        var t1 = t0.AddMinutes(3);
        var zombie = new CheckoutLockService(new PathService(), new FakeMachine("BBB"), () => t1);
        Assert.Equal(lastSeen, zombie.Heartbeat(root)!.LastSeenAt);
    }
}
