using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class GraphSyncPolicyTests
{
    [Theory]
    [InlineData("Logs", false)]
    [InlineData("Events", false)]
    [InlineData("_autosave", true)]
    [InlineData("RUNSCORE", false)]
    [InlineData("DB's", false)]
    public void ShouldSkipFolder_CellularNoise(string name, bool skip) =>
        Assert.Equal(skip, GraphSyncPolicy.ShouldSkipFolder(name));

    [Fact]
    public void FinishBudget_IsUnderTwoMinutes_ForCellular()
    {
        Assert.True(GraphSyncPolicy.FinishUploadBudget <= TimeSpan.FromSeconds(90));
        // Past Races year probes reuse download gate (parallel FolderExists).
        Assert.True(GraphSyncPolicy.DownloadParallelism >= 2);
        Assert.True(GraphSyncPolicy.DownloadParallelismFloor >= 1);
        Assert.True(GraphSyncPolicy.DownloadParallelismFloor <= GraphSyncPolicy.DownloadParallelism);
        Assert.True(GraphSyncPolicy.HttpTimeout <= TimeSpan.FromSeconds(30));
        Assert.True(GraphSyncPolicy.OfficeListBudget <= TimeSpan.FromSeconds(20));
        Assert.True(GraphSyncPolicy.OfficeSessionsBudget <= TimeSpan.FromSeconds(75));
        Assert.True(GraphSyncPolicy.SessionDownloadBudget <= TimeSpan.FromMinutes(3));
        Assert.True(GraphSyncPolicy.MidRaceMaxFileBytes < GraphSyncPolicy.FinishMaxFileBytes);
    }

    [Fact]
    public void TransferParallelism_HasThrottleFloor()
    {
        Assert.Equal(8, GraphSyncPolicy.DownloadParallelism);
        Assert.Equal(GraphSyncPolicy.DownloadParallelism, GraphSyncPolicy.UploadParallelism);
        Assert.True(GraphSyncPolicy.DownloadParallelismFloor >= 2);
        Assert.Equal(GraphSyncPolicy.DownloadParallelismFloor, GraphSyncPolicy.UploadParallelismFloor);
        Assert.True(GraphSyncPolicy.DownloadParallelismFloor < GraphSyncPolicy.DownloadParallelism);
        Assert.True(GraphSyncPolicy.DownloadMaxAttempts >= 3);
        Assert.Equal(GraphSyncPolicy.DownloadMaxAttempts, GraphSyncPolicy.UploadMaxAttempts);
    }
}
