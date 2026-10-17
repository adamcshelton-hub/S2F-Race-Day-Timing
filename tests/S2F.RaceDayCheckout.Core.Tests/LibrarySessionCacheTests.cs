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
}
