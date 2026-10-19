using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class GraphDriveSyncServiceTests
{
    [Theory]
    [InlineData(@"C:\S2F\Library", @"C:\S2F\Library\Memphis\2026 - Race", "", "Memphis/2026 - Race")]
    [InlineData(@"C:\S2F\Library", @"C:\S2F\Library\Memphis\2026 - Race", "RaceDay", "RaceDay/Memphis/2026 - Race")]
    [InlineData(@"C:\S2F\Library", @"C:\S2F\Library", "RaceDay", "RaceDay")]
    [InlineData(@"C:\S2F\Library", @"C:\Other\Memphis", "", null)]
    public void ToDriveRelative_MapsLibraryPaths(string libraryRoot, string local, string driveRoot, string? expected)
    {
        var actual = GraphDriveSyncService.ToDriveRelative(local, libraryRoot, driveRoot);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void JoinDrivePath_NormalizesSeparators()
    {
        Assert.Equal("a/b/c", GraphDriveSyncService.JoinDrivePath(@"a\b", "c"));
        Assert.Equal("c", GraphDriveSyncService.JoinDrivePath("", "c"));
        Assert.Equal("a/b", GraphDriveSyncService.JoinDrivePath("a/b", ""));
    }

    [Fact]
    public async Task NullCloud_IsNotAvailable()
    {
        var cloud = new NullCloudLibrarySync();
        Assert.False(cloud.IsAvailable);
        var result = await cloud.DownloadSessionAsync(@"C:\x", @"C:\x", "");
        Assert.True(result.Success);
    }

    [Fact]
    public void FormatOfficeSessionsRefreshMessage_AlreadyInSync_DoesNotSayNoEvents()
    {
        // Bug: created=0 pruned=0 used to mean “no RUNSCORE on OneDrive” even when remoteCount > 0.
        var msg = GraphDriveSyncService.FormatOfficeSessionsRefreshMessage("Memphis", 0, 0, 12);
        Assert.Contains("12 races", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no races", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RUNSCORE", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatOfficeSessionsRefreshMessage_EmptyRemote()
    {
        Assert.Equal(
            "Memphis: no races on OneDrive.",
            GraphDriveSyncService.FormatOfficeSessionsRefreshMessage("Memphis", 0, 0, 0));
    }

    [Fact]
    public void FormatOfficeSessionsRefreshMessage_CreatedAndPruned()
    {
        var msg = GraphDriveSyncService.FormatOfficeSessionsRefreshMessage("Memphis", 2, 1, 10);
        Assert.Equal("Memphis: 2 new, 1 removed (moved/gone on OneDrive).", msg);
    }
}
