using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class FinishUploadPlannerTests
{
    [Fact]
    public void CollectChanged_NoDiffs_ReturnsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-finish-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = Path.Combine(root, "Library", "Race");
            var active = Path.Combine(root, "Active", "Race");
            SeedIdenticalPack(library, active);

            var changed = FinishUploadPlanner.CollectChangedRelativePaths(MakeCheckout(active, library));
            Assert.Empty(changed);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void CollectChanged_RunScoreMtimeBumpSameBytes_ReturnsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-finish-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = Path.Combine(root, "Library", "Race");
            var active = Path.Combine(root, "Active", "Race");
            SeedIdenticalPack(library, active);

            // Simulate RunScore opening files and bumping mtimes without edits.
            var activeDat = Path.Combine(active, PathService.RunScoreFolderName, "race.dat");
            File.SetLastWriteTimeUtc(activeDat, DateTime.UtcNow.AddMinutes(5));

            var changed = FinishUploadPlanner.CollectChangedRelativePaths(MakeCheckout(active, library));
            Assert.Empty(changed);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void CollectChanged_ContentEdit_IncludesRelativePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-finish-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = Path.Combine(root, "Library", "Race");
            var active = Path.Combine(root, "Active", "Race");
            SeedIdenticalPack(library, active);

            var activeDat = Path.Combine(active, PathService.RunScoreFolderName, "race.dat");
            File.WriteAllText(activeDat, "edited-content");
            File.SetLastWriteTimeUtc(activeDat, DateTime.UtcNow.AddMinutes(5));

            var changed = FinishUploadPlanner.CollectChangedRelativePaths(MakeCheckout(active, library));
            Assert.Contains(changed, p => p.Replace('\\', '/').EndsWith("RUNSCORE/race.dat", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(Path.Combine(PathService.RunScoreFolderName, "race.dat")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void CollectChanged_NewEventFile_Included()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-finish-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = Path.Combine(root, "Library", "Race");
            var active = Path.Combine(root, "Active", "Race");
            SeedIdenticalPack(library, active);

            File.WriteAllText(Path.Combine(active, "NewRace.event"), "event-body");

            var changed = FinishUploadPlanner.CollectChangedRelativePaths(MakeCheckout(active, library));
            Assert.Contains(changed, p => p.Equals("NewRace.event", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void NeedsUpload_MissingLibrary_True()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-finish-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var active = Path.Combine(root, "a.bin");
            File.WriteAllBytes(active, new byte[] { 1, 2, 3 });
            Assert.True(FinishUploadPlanner.NeedsUpload(active, Path.Combine(root, "missing.bin")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static ActiveCheckout MakeCheckout(string active, string library) =>
        new()
        {
            Office = "Test",
            ParentEvent = "Race",
            SessionName = "Race",
            RelativePath = "Test/Race",
            LibrarySessionPath = library,
            ActivePath = active,
            BackupPath = Path.Combine(active, "_backup"),
            AutosavePath = Path.Combine(active, "_autosave"),
            DbsPath = Path.Combine(active, SharedFolderLinkService.DbsFolderName),
            PrimaryEventFile = "Race.event",
            CheckedOutAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

    private static void SeedIdenticalPack(string library, string active)
    {
        var libRs = Path.Combine(library, PathService.RunScoreFolderName);
        var actRs = Path.Combine(active, PathService.RunScoreFolderName);
        Directory.CreateDirectory(libRs);
        Directory.CreateDirectory(actRs);

        var stamp = DateTime.UtcNow.AddDays(-2);
        WriteFixed(Path.Combine(libRs, "race.dat"), "pack-bytes", stamp);
        WriteFixed(Path.Combine(actRs, "race.dat"), "pack-bytes", stamp);
        WriteFixed(Path.Combine(libRs, "scores.db"), "db-bytes", stamp);
        WriteFixed(Path.Combine(actRs, "scores.db"), "db-bytes", stamp);
        WriteFixed(Path.Combine(library, "Race.event"), "event", stamp);
        WriteFixed(Path.Combine(active, "Race.event"), "event", stamp);
    }

    private static void WriteFixed(string path, string content, DateTime utc)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, utc);
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
