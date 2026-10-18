using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class LibraryScannerTests
{
    [Fact]
    public void ListsSessionLeaves_FlatAndNested_OnlyAllowedOffices()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-lib-" + Guid.NewGuid().ToString("N"));
        // Flat event
        Directory.CreateDirectory(Path.Combine(root, "Memphis", "2026 - Heavens Cradle", "RUNSCORE"));
        File.WriteAllText(Path.Combine(root, "Memphis", "2026 - Heavens Cradle", "Heavens Cradle.event"), "e");
        // Nested multi-day
        Directory.CreateDirectory(Path.Combine(root, "Memphis", "2026 - LRM Weekend", "Sunday", "RUNSCORE"));
        Directory.CreateDirectory(Path.Combine(root, "Memphis", "2026 - LRM Weekend", "Saturday", "RUNSCORE"));
        Directory.CreateDirectory(Path.Combine(root, "Memphis", "2026 - LRM Weekend", "Expo", "RUNSCORE"));
        File.WriteAllText(Path.Combine(root, "Memphis", "2026 - LRM Weekend", "Sunday", "26 LRM Sunday.event"), "e");
        File.WriteAllText(Path.Combine(root, "Memphis", "2026 - LRM Weekend", "Sunday", "26 LRM Sunday-S2F-MEM-3.event"), "a");
        Directory.CreateDirectory(Path.Combine(root, "Charlotte", "2026 - Sample", "RUNSCORE"));
        Directory.CreateDirectory(Path.Combine(root, "Templates", "5K", "RUNSCORE"));
        Directory.CreateDirectory(Path.Combine(root, "Past Races", "Memphis Past Races", "RUNSCORE"));
        Directory.CreateDirectory(Path.Combine(root, "Nashville", "Should Not Show", "RUNSCORE"));
        Directory.CreateDirectory(Path.Combine(root, "Test Office", "Sandbox 5K", "RUNSCORE"));

        var paths = new PathService();
        var scanner = new LibraryScanner(paths, new CheckoutLockService(paths, new MachineIdentity()));

        var offices = scanner.ListOffices(root);
        Assert.Equal(new[] { "Charlotte", "Memphis", "Past Races", "Test Office" },
            offices.Select(o => o.Name).ToArray());

        var sessions = scanner.ListSessions(root, officeFilter: "Memphis");
        Assert.Equal(4, sessions.Count); // Heavens + Expo + Saturday + Sunday
        Assert.Contains(sessions, s => s.DisplayName == "2026 - Heavens Cradle");
        Assert.Contains(sessions, s => s.DisplayName == "2026 - LRM Weekend / Sunday");
        Assert.Contains(sessions, s => s.PrimaryEventFile == "26 LRM Sunday.event");

        var sunday = Path.Combine(root, "Memphis", "2026 - LRM Weekend", "Sunday");
        SessionRoleStore.Save(sunday, new SessionRoles
        {
            PrimaryEventFile = "26 LRM Sunday-S2F-MEM-3.event",
            AnnouncerEventFile = "26 LRM Sunday-announcer.event"
        });
        File.WriteAllText(Path.Combine(sunday, "26 LRM Sunday-announcer.event"), "a");
        var assigned = scanner.ListSessions(root, officeFilter: "Memphis")
            .Single(s => s.SessionName == "Sunday");
        Assert.Equal("26 LRM Sunday-S2F-MEM-3.event", assigned.PrimaryEventFile);
        Assert.Equal("26 LRM Sunday-announcer.event", assigned.AnnouncerEventFile);

        var search = scanner.ListSessions(root, search: "Sunday");
        Assert.Single(search);
        Assert.Equal("Sunday", search[0].SessionName);
    }

    [Fact]
    public void ChoosePrimary_PrefersNonS2FSuffix()
    {
        var primary = EventFileSelector.ChoosePrimary(new[]
        {
            "26 LRM Sunday-S2F-MEM-3.event",
            "26 LRM Sunday.event"
        });
        Assert.Equal("26 LRM Sunday.event", primary);
    }

    [Fact]
    public void HidesCheckinBackupTimestampFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-lib-" + Guid.NewGuid().ToString("N"));
        var sunday = Path.Combine(root, "Memphis", "2026 - LRM Weekend", "Sunday");
        Directory.CreateDirectory(Path.Combine(sunday, "RUNSCORE"));
        File.WriteAllText(Path.Combine(sunday, "26 LRM Sunday.event"), "e");

        // Finish/check-in creates …/._checkin/backup/yyyyMMdd_HHmmss/RUNSCORE — must not appear as a session.
        var stamp = Path.Combine(sunday, "._checkin", "backup", "20261016_152737");
        Directory.CreateDirectory(Path.Combine(stamp, "RUNSCORE"));
        File.WriteAllText(Path.Combine(stamp, "26 LRM Sunday.event"), "e");
        File.WriteAllText(Path.Combine(sunday, "._checkin", "_checkout.json"), "{}");

        var paths = new PathService();
        var scanner = new LibraryScanner(paths, new CheckoutLockService(paths, new MachineIdentity()));
        var sessions = scanner.ListSessions(root, officeFilter: "Memphis");

        Assert.Single(sessions);
        Assert.Equal("2026 - LRM Weekend / Sunday", sessions[0].DisplayName);
        Assert.DoesNotContain(sessions, s => s.SessionName.Contains("20261016", StringComparison.Ordinal));
    }
}
