using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Tests;

public class EventPackInfoTests
{
    [Fact]
    public void FormatLockWho_DedupesIdenticalHolderAndMachine()
    {
        var lockInfo = new CheckoutLock
        {
            HolderName = "BECKBARRETT3C75",
            MachineName = "BECKBARRETT3C75"
        };
        Assert.Equal("BECKBARRETT3C75", EventPackInfo.FormatLockWho(lockInfo));
    }

    [Fact]
    public void FormatLockWho_KeepsDistinctHolderAndMachine()
    {
        var lockInfo = new CheckoutLock
        {
            HolderName = "Adam",
            MachineName = "TIMING-LAPTOP"
        };
        Assert.Equal("Adam on TIMING-LAPTOP", EventPackInfo.FormatLockWho(lockInfo));
    }

    [Fact]
    public void DisplayLock_StaleUsesPresentTense_NotWas()
    {
        var pack = new EventPackInfo
        {
            Office = "Test",
            ParentEvent = "Event",
            SessionName = "Event",
            RelativePath = "Event",
            Path = @"C:\x\Event",
            EventFiles = Array.Empty<string>(),
            Lock = new CheckoutLock
            {
                Status = LockLifecycleStatus.Active,
                HolderName = "BECKBARRETT3C75",
                MachineName = "BECKBARRETT3C75",
                LastSeenAt = DateTimeOffset.UtcNow.AddHours(-2),
                CheckedOutAt = DateTimeOffset.UtcNow.AddHours(-3)
            }
        };

        Assert.True(pack.IsLockStale);
        Assert.Equal("Open on BECKBARRETT3C75 · Soft Take Over Available", pack.DisplayLock);
        Assert.DoesNotContain("was", pack.DisplayLock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", pack.DisplayLock);
    }
}
