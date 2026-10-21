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

    [Theory]
    [InlineData(new[] { "RSServer" }, true)]
    [InlineData(new[] { "RSServer", "RSClient" }, true)]
    [InlineData(new[] { "RSServer", "TimingAndScoring" }, false)]
    [InlineData(new[] { "TimingAndScoring64" }, false)]
    [InlineData(new string[0], false)]
    public void AreOnlyRunScoreProcesses_GatesEndOrphanOffer(string[] names, bool expected) =>
        Assert.Equal(expected, ProcessGuard.AreOnlyRunScoreProcesses(names));

    [Fact]
    public void TryEndHeadlessRunScoreProcesses_NoOpWhenNoneRunning()
    {
        var guard = new ProcessGuard();
        // Never kill a live race-day RSServer from unit tests.
        if (guard.GetRunScoreProcessNames().Count > 0)
            return;

        var result = guard.TryEndHeadlessRunScoreProcesses();
        Assert.True(result.Success);
        Assert.Contains("No RunScore", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
