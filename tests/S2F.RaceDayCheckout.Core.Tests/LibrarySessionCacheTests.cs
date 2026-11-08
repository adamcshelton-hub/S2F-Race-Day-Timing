using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class LibrarySessionCacheTests
{
    [Fact]
    public void Prune_RemovesLocalSessionNotOnRemote_KeepsRemoteMatch()
    {
        var office = Path.Combine(Path.GetTempPath(), "s2f-prune-" + Guid.NewGuid().ToString("N"));
        var keep = Path.Combine(office, "2026 - Keep");
        var gone = Path.Combine(office, "2025 - Moved To Past");
        Directory.CreateDirectory(Path.Combine(keep, PathService.RunScoreFolderName));
        Directory.CreateDirectory(Path.Combine(gone, PathService.RunScoreFolderName));
        File.WriteAllText(Path.Combine(gone, "old.event"), "x");

        try
        {
            var remote = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2026 - Keep" };
            var pruned = LibrarySessionCache.PruneSessionsNotOnRemote(office, remote);

            Assert.Equal(1, pruned);
            Assert.True(Directory.Exists(keep));
            Assert.False(Directory.Exists(gone));
        }
        finally
        {
            try { Directory.Delete(office, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Prune_SkipsProtectedActivePath()
    {
        var office = Path.Combine(Path.GetTempPath(), "s2f-prune-" + Guid.NewGuid().ToString("N"));
        var active = Path.Combine(office, "2026 - Active Race");
        Directory.CreateDirectory(Path.Combine(active, PathService.RunScoreFolderName));

        try
        {
            var remote = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // empty = would prune all
            var pruned = LibrarySessionCache.PruneSessionsNotOnRemote(office, remote, protectSessionPath: active);

            Assert.Equal(0, pruned);
            Assert.True(Directory.Exists(active));
        }
        finally
        {
            try { Directory.Delete(office, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Prune_RemovesEmptyParentAfterNestedSessionGone()
    {
        var office = Path.Combine(Path.GetTempPath(), "s2f-prune-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(office, "2026 - Weekend", "Sunday");
        Directory.CreateDirectory(Path.Combine(nested, PathService.RunScoreFolderName));

        try
        {
            var pruned = LibrarySessionCache.PruneSessionsNotOnRemote(
                office, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.Equal(1, pruned);
            Assert.False(Directory.Exists(Path.Combine(office, "2026 - Weekend")));
        }
        finally
        {
            try { Directory.Delete(office, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void PruneImmediate_RemovesFoldersMissingFromOneLevelList_KeepsDeeperMatchAndActive()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-level-" + Guid.NewGuid().ToString("N"));
        var keep = Path.Combine(root, "St. Louis Past Races");
        var gone = Path.Combine(root, "St. Louis");
        var active = Path.Combine(root, "Memphis", "2025");
        Directory.CreateDirectory(Path.Combine(keep, "2025"));
        Directory.CreateDirectory(gone);
        Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(root, "WinHttpHeader.txt"), "x");

        try
        {
            var pruned = LibrarySessionCache.PruneImmediateFoldersNotOnRemote(
                root,
                ["St. Louis Past Races", "Memphis Past Races"],
                protectSessionPath: active);

            Assert.Equal(1, pruned);
            Assert.True(Directory.Exists(keep));
            Assert.True(Directory.Exists(Path.Combine(keep, "2025")));
            Assert.True(Directory.Exists(active));
            Assert.False(Directory.Exists(gone));
            Assert.True(File.Exists(Path.Combine(root, "WinHttpHeader.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void PruneOffices_RemovesConfirmedAbsent_KeepsOthers()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-offices-" + Guid.NewGuid().ToString("N"));
        var testOffice = Path.Combine(root, "Test Office");
        var memphis = Path.Combine(root, "Memphis");
        Directory.CreateDirectory(Path.Combine(testOffice, "Sandbox", PathService.RunScoreFolderName));
        Directory.CreateDirectory(memphis);
        Directory.CreateDirectory(Path.Combine(root, "Not An Office"));

        try
        {
            var removed = LibrarySessionCache.PruneOfficesConfirmedAbsent(
                root,
                new[] { "Test Office", "Not An Office" });

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(testOffice));
            Assert.True(Directory.Exists(memphis));
            Assert.True(Directory.Exists(Path.Combine(root, "Not An Office")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void PruneOffices_SkipsOfficeThatContainsActiveCheckout()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-offices-" + Guid.NewGuid().ToString("N"));
        var session = Path.Combine(root, "Test Office", "2026 - Live");
        Directory.CreateDirectory(Path.Combine(session, PathService.RunScoreFolderName));

        try
        {
            var removed = LibrarySessionCache.PruneOfficesConfirmedAbsent(
                root,
                new[] { "Test Office" },
                protectSessionPath: session);

            Assert.Equal(0, removed);
            Assert.True(Directory.Exists(session));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
