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
    public void FinishBudget_MatchesSessionDownload_ForGraphLatency()
    {
        Assert.Equal(GraphSyncPolicy.SessionDownloadBudget, GraphSyncPolicy.FinishUploadBudget);
        Assert.True(GraphSyncPolicy.FinishUploadBudget <= TimeSpan.FromMinutes(3));
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

    [Fact]
    public void NeedsSessionFileDownload_MissingLocal_True() =>
        Assert.True(GraphSyncPolicy.NeedsSessionFileDownload(
            localExists: false,
            localLength: 0,
            localLastWriteUtc: DateTime.MinValue,
            remoteSize: 996,
            remoteLastModifiedUtc: DateTime.UtcNow));

    [Fact]
    public void NeedsSessionFileDownload_SizeDiffers_True() =>
        Assert.True(GraphSyncPolicy.NeedsSessionFileDownload(
            localExists: true,
            localLength: 879,
            localLastWriteUtc: DateTime.UtcNow.AddDays(-1),
            remoteSize: 996,
            remoteLastModifiedUtc: DateTime.UtcNow));

    [Fact]
    public void NeedsSessionFileDownload_SameSizeRemoteNewer_True()
    {
        // Entries.ini timestamp edit: same 996 bytes, newer OneDrive version on 2nd laptop.
        var localWrite = new DateTime(2026, 10, 27, 15, 42, 0, DateTimeKind.Utc);
        var remoteWrite = new DateTime(2026, 10, 31, 15, 27, 0, DateTimeKind.Utc);
        Assert.True(GraphSyncPolicy.NeedsSessionFileDownload(
            localExists: true,
            localLength: 996,
            localLastWriteUtc: localWrite,
            remoteSize: 996,
            remoteLastModifiedUtc: remoteWrite));
    }

    [Fact]
    public void NeedsSessionFileDownload_SameSizeLocalCurrent_False()
    {
        var stamp = new DateTime(2026, 10, 31, 15, 27, 0, DateTimeKind.Utc);
        Assert.False(GraphSyncPolicy.NeedsSessionFileDownload(
            localExists: true,
            localLength: 996,
            localLastWriteUtc: stamp,
            remoteSize: 996,
            remoteLastModifiedUtc: stamp));
    }
}
