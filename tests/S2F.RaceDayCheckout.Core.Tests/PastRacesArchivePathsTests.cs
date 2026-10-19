using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class PastRacesArchivePathsTests
{
    [Fact]
    public void OfficeBucketName_AppendsPastRaces()
    {
        Assert.Equal("Memphis Past Races", PastRacesArchivePaths.OfficeBucketName("Memphis"));
    }

    [Fact]
    public void ResolveArchiveItemName_UsesParentForRaceGroupLeaf()
    {
        var session = new EventPackInfo
        {
            Office = "Memphis",
            ParentEvent = "2026 - LRM Weekend",
            SessionName = "Sunday AM",
            RelativePath = @"2026 - LRM Weekend\Sunday AM",
            Path = @"C:\S2F\Library\Memphis\2026 - LRM Weekend\Sunday AM"
        };
        Assert.Equal("2026 - LRM Weekend", PastRacesArchivePaths.ResolveArchiveItemName(session));
    }

    [Fact]
    public void ResolveArchiveItemName_FlatRaceUsesSessionFolder()
    {
        var session = new EventPackInfo
        {
            Office = "Memphis",
            ParentEvent = "2026 - Crosstown",
            SessionName = "2026 - Crosstown",
            RelativePath = "2026 - Crosstown",
            Path = @"C:\S2F\Library\Memphis\2026 - Crosstown"
        };
        Assert.Equal("2026 - Crosstown", PastRacesArchivePaths.ResolveArchiveItemName(session));
    }

    [Theory]
    [InlineData("2026 - Crosstown", "2026")]
    [InlineData("2025 - Foo", "2025")]
    public void ResolveArchiveYear_FromFolderPrefix(string folder, string year)
    {
        Assert.Equal(year, PastRacesArchivePaths.ResolveArchiveYear(folder, fallbackYear: 1999));
    }

    [Fact]
    public void ResolveArchiveYear_FallsBackWhenNoPrefix()
    {
        Assert.Equal("2024", PastRacesArchivePaths.ResolveArchiveYear("Odd Name", fallbackYear: 2024));
    }

    [Fact]
    public void DestinationPaths_MatchBrowseLayout()
    {
        Assert.Equal(
            "Past Races/Memphis Past Races/2026",
            PastRacesArchivePaths.DestinationYearRelative("Memphis", "2026"));
        Assert.Equal(
            "Past Races/Memphis Past Races/2026/2026 - Crosstown",
            PastRacesArchivePaths.DestinationItemRelative("Memphis", "2026", "2026 - Crosstown"));
    }

    [Fact]
    public void IsLibraryPathUnderArchiveItem_DetectsGroupLeaf()
    {
        var root = @"C:\S2F\Library";
        var leaf = @"C:\S2F\Library\Memphis\2026 - LRM Weekend\Sunday AM";
        Assert.True(PastRacesArchivePaths.IsLibraryPathUnderArchiveItem(
            root, "Memphis", "2026 - LRM Weekend", leaf));
        Assert.False(PastRacesArchivePaths.IsLibraryPathUnderArchiveItem(
            root, "Memphis", "2026 - LRM Weekend", @"C:\S2F\Library\Memphis\2026 - Other"));
    }
}
