using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class LibraryBrowserTests
{
    [Fact]
    public void ListBrowserEntries_PastRaces_ShowsBucketsNotDeepSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-browse-" + Guid.NewGuid().ToString("N"));
        var past = Path.Combine(root, EventBuilderService.PastRacesFolderName);
        Directory.CreateDirectory(Path.Combine(past, "Memphis Past Races", "2025", "2025 - Crosstown", PathService.RunScoreFolderName));
        Directory.CreateDirectory(Path.Combine(past, "Atlanta Past Races"));

        try
        {
            var paths = new PathService();
            var scanner = new LibraryScanner(paths, new CheckoutLockService(paths, new MachineIdentity()));
            var entries = scanner.ListBrowserEntries(root, EventBuilderService.PastRacesFolderName, past);

            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.True(e.IsNavigationFolder));
            Assert.Contains(entries, e => e.SessionName == "Memphis Past Races");
            Assert.Contains(entries, e => e.SessionName == "Atlanta Past Races");
            Assert.DoesNotContain(entries, e => e.SessionName.Contains("Crosstown", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ListBrowserEntries_YearFolder_ShowsSessionLeavesAndNestedFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-browse-" + Guid.NewGuid().ToString("N"));
        var year = Path.Combine(root, "Past Races", "Memphis Past Races", "2025");
        Directory.CreateDirectory(Path.Combine(year, "2025 - Crosstown", PathService.RunScoreFolderName));
        Directory.CreateDirectory(Path.Combine(year, "Nested Weekend", "Sunday", PathService.RunScoreFolderName));

        try
        {
            var paths = new PathService();
            var scanner = new LibraryScanner(paths, new CheckoutLockService(paths, new MachineIdentity()));
            var entries = scanner.ListBrowserEntries(root, "Past Races", year);

            Assert.Contains(entries, e => !e.IsNavigationFolder && e.SessionName == "2025 - Crosstown");
            Assert.Contains(entries, e => e.IsNavigationFolder && e.SessionName == "Nested Weekend");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
