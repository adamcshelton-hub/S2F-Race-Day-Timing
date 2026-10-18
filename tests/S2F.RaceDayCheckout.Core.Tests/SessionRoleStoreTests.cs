using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class SessionRoleStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsCamelCaseSidecar()
    {
        var session = Path.Combine(Path.GetTempPath(), "s2f-roles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);

        SessionRoleStore.Save(session, new SessionRoles
        {
            PrimaryEventFile = "26 LRM Sunday.event",
            AnnouncerEventFile = "26 LRM Sunday-announcer.event"
        });

        var json = File.ReadAllText(SessionRoleStore.GetPath(session));
        Assert.Contains("\"primaryEventFile\"", json, StringComparison.Ordinal);
        Assert.Contains("\"announcerEventFile\"", json, StringComparison.Ordinal);
        Assert.Contains("26 LRM Sunday.event", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PrimaryEventFile", json, StringComparison.Ordinal);

        var loaded = SessionRoleStore.TryLoad(session);
        Assert.NotNull(loaded);
        Assert.Equal("26 LRM Sunday.event", loaded!.PrimaryEventFile);
        Assert.Equal("26 LRM Sunday-announcer.event", loaded.AnnouncerEventFile);
    }

    [Fact]
    public void Save_RejectsTheSameFileForBothRoles()
    {
        var session = Path.Combine(Path.GetTempPath(), "s2f-roles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var roles = new SessionRoles
        {
            PrimaryEventFile = "Race.event",
            AnnouncerEventFile = "race.event"
        };

        Assert.NotNull(SessionRoleStore.Validate(roles));
        Assert.Throws<InvalidOperationException>(() => SessionRoleStore.Save(session, roles));
        Assert.False(File.Exists(SessionRoleStore.GetPath(session)));
    }

    [Fact]
    public void Save_AllowsBlankAnnouncer()
    {
        var session = Path.Combine(Path.GetTempPath(), "s2f-roles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        SessionRoleStore.Save(session, new SessionRoles
        {
            PrimaryEventFile = "Race.event",
            AnnouncerEventFile = "  "
        });

        var loaded = SessionRoleStore.TryLoad(session);
        Assert.Equal("Race.event", loaded!.PrimaryEventFile);
        Assert.Null(loaded.AnnouncerEventFile);
    }
}
