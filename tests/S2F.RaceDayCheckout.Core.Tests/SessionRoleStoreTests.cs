using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class SessionRoleStoreTests
{
    [Fact]
    public void SaveAndLoad_WritesPrimaryOnly()
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
        Assert.DoesNotContain("announcerEventFile", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("26 LRM Sunday.event", json, StringComparison.Ordinal);

        var loaded = SessionRoleStore.TryLoad(session);
        Assert.NotNull(loaded);
        Assert.Equal("26 LRM Sunday.event", loaded!.PrimaryEventFile);
        Assert.Null(loaded.AnnouncerEventFile);
    }

    [Fact]
    public void TryLoad_ReadsLegacyAnnouncerField()
    {
        var session = Path.Combine(Path.GetTempPath(), "s2f-roles-" + Guid.NewGuid().ToString("N"));
        var meta = Path.Combine(session, PathService.AppMetaFolderName);
        Directory.CreateDirectory(meta);
        File.WriteAllText(
            SessionRoleStore.GetPath(session),
            """
            {
              "primaryEventFile": "Race.event",
              "announcerEventFile": "Race-announcer.event"
            }
            """);

        var loaded = SessionRoleStore.TryLoad(session);
        Assert.Equal("Race.event", loaded!.PrimaryEventFile);
        Assert.Equal("Race-announcer.event", loaded.AnnouncerEventFile);
    }

    [Fact]
    public void Validate_RejectsTheSameFileForBothRolesWhenBothSet()
    {
        var roles = new SessionRoles
        {
            PrimaryEventFile = "Race.event",
            AnnouncerEventFile = "race.event"
        };

        Assert.NotNull(SessionRoleStore.Validate(roles));
    }

    [Fact]
    public void Save_AllowsPrimaryOnly()
    {
        var session = Path.Combine(Path.GetTempPath(), "s2f-roles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        SessionRoleStore.Save(session, new SessionRoles
        {
            PrimaryEventFile = "Race.event"
        });

        var loaded = SessionRoleStore.TryLoad(session);
        Assert.Equal("Race.event", loaded!.PrimaryEventFile);
        Assert.Null(loaded.AnnouncerEventFile);
    }
}
