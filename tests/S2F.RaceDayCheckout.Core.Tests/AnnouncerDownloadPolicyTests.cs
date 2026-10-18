using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class AnnouncerDownloadPolicyTests
{
    [Fact]
    public void SelectRelativePaths_KeepsAnnouncerEventAndDbs_SkipsTheRest()
    {
        var paths = new[]
        {
            "RUNSCORE/ENTRIES.FRM",
            "RUNSCORE/_autosave/ENTRIES.DTA",
            "Pre-Race/notes.txt",
            "26 LRM Sunday.event",
            "26 LRM Sunday-announcer.event",
            "26 LRM Sunday-S2F-MEM-1.event",
            @"DB's\live.csv",
            "DB's/exports/race.csv",
            "._checkin/s2f_session.json",
            "._checkin/_checkout.json",
            "._checkin/backup/20261016/RUNSCORE/ENTRIES.FRM"
        };

        var kept = AnnouncerDownloadPolicy.SelectRelativePaths(paths, "26 LRM Sunday-announcer.event");

        Assert.Equal(new[]
        {
            "26 LRM Sunday-announcer.event",
            @"DB's\live.csv",
            "DB's/exports/race.csv"
        }, kept);
    }

    [Fact]
    public void ShouldRecurse_OnlySessionRootAndDbs()
    {
        Assert.True(AnnouncerDownloadPolicy.ShouldRecurse(""));
        Assert.True(AnnouncerDownloadPolicy.ShouldRecurse("DB's"));
        Assert.True(AnnouncerDownloadPolicy.ShouldRecurse("DB's/exports"));
        Assert.False(AnnouncerDownloadPolicy.ShouldRecurse("RUNSCORE"));
        Assert.False(AnnouncerDownloadPolicy.ShouldRecurse("Pre-Race"));
        Assert.False(AnnouncerDownloadPolicy.ShouldRecurse("._checkin"));
        Assert.False(AnnouncerDownloadPolicy.ShouldRecurse("RUNSCORE/_autosave"));
    }

    [Fact]
    public void ShouldIncludeFile_DoesNotGuessALoneEvent()
    {
        Assert.False(AnnouncerDownloadPolicy.ShouldIncludeFile("Only.event", announcerEventFile: null));
        Assert.False(AnnouncerDownloadPolicy.ShouldIncludeFile("Only.event", ""));
    }
}
