using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class ProcessGuardTests
{
    [Theory]
    [InlineData("powershell", false)]
    [InlineData("csrss", false)]
    [InlineData("DefenderSessionHelper", false)]
    [InlineData("explorer", false)]
    [InlineData("RSServer", true)]
    [InlineData("rsserver", true)]
    [InlineData("mylaps_ts_launch", true)]
    [InlineData("TimingAndScoring64", true)]
    public void IsBlocking_DoesNotFalsePositiveOnShortRs(string name, bool expected)
    {
        var guard = new ProcessGuard();
        Assert.Equal(expected, guard.IsBlocking(name));
    }
}
