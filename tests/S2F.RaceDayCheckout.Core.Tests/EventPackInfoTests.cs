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
    public void DisplayLock_StaleIncludesLastSeen_NotWas()
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
        Assert.Equal(
            "Open on BECKBARRETT3C75 · Last Seen 2 Hr Ago · Soft Take Over Available",
            pack.DisplayLock);
        Assert.DoesNotContain("was", pack.DisplayLock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", pack.DisplayLock);
    }

    [Theory]
    [InlineData(30, "Just Now")]
    [InlineData(120, "2 Min Ago")]
    [InlineData(7200, "2 Hr Ago")]
    [InlineData(172800, "2 Days Ago")]
    public void FormatLockLastSeen_RelativeBuckets(int ageSeconds, string expected) =>
        Assert.Equal(
            expected,
            EventPackInfo.FormatLockLastSeen(DateTimeOffset.UtcNow.AddSeconds(-ageSeconds)));

    [Fact]
    public void FormatLockLastSeen_OldLeftoverUsesCalendarDate()
    {
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-30);
        var text = EventPackInfo.FormatLockLastSeen(lastSeen);
        Assert.Equal(lastSeen.ToLocalTime().ToString("MMM d"), text);
    }
}
