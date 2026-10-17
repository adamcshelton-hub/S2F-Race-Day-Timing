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

    [Fact]
    public async Task WaitForBlockingClearedAsync_ReturnsClearedWhenNothingBlocking()
    {
        // Use names that never match real processes on the test host.
        var guard = new ProcessGuard(
            exactNames: ["__S2F_NeverRunning_XYZ__"],
            substrings: ["__S2F_NeverSub_XYZ__"]);

        var result = await guard.WaitForBlockingClearedAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(BlockingWaitKind.Cleared, result.Kind);
        Assert.Empty(result.ProcessNames);
    }
}
