using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class AutosaveMirrorServiceTests
{
    [Fact]
    public async Task Pause_SkipsMirrorTicks()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-pause-" + Guid.NewGuid().ToString("N"));
        var activeStorePath = Path.Combine(root, "active.json");
        var backup = Path.Combine(root, "Backup", "Event");
        var autosave = Path.Combine(root, "autosave");
        var activePath = Path.Combine(root, "Active", "Event");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(Path.Combine(activePath, PathService.RunScoreFolderName));
        File.WriteAllText(Path.Combine(backup, "ENTRIES.DTA"), "live");

        var store = new ActiveCheckoutStore(activeStorePath);
        store.Save(new Models.ActiveCheckout
        {
            Office = "Test",
            ParentEvent = "Event",
            SessionName = "Event",
            RelativePath = "Event",
            LibrarySessionPath = root,
            ActivePath = activePath,
            BackupPath = backup,
            AutosavePath = autosave,
            DbsPath = Path.Combine(root, "DB's"),
            PrimaryEventFile = "Event.event",
            CheckedOutAt = DateTimeOffset.UtcNow
        });

        var mirror = new AutosaveMirrorService(new FileCopyService(), store);
        mirror.Pause();
        await mirror.MirrorOnceAsync();
        Assert.False(Directory.Exists(autosave), "paused mirror must not create _autosave");

        mirror.Resume();
        await mirror.MirrorOnceAsync();
        Assert.True(File.Exists(Path.Combine(autosave, "ENTRIES.DTA")));
    }

    [Fact]
    public async Task MirrorOnce_CopiesBackupOnly_NotActiveRunScore()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-overlay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var activeStorePath = Path.Combine(root, "active.json");
            var backup = Path.Combine(root, "Backup", "Event");
            var autosave = Path.Combine(root, "Library", "Event", "RUNSCORE", "_autosave");
            var activePath = Path.Combine(root, "Active", "Event");
            var activeRs = Path.Combine(activePath, PathService.RunScoreFolderName);
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(activeRs);

            File.WriteAllText(Path.Combine(backup, "ENTRIES.DTA"), "from-backup");
            File.WriteAllText(Path.Combine(activeRs, "race.dat"), "from-active-only");
            File.WriteAllText(Path.Combine(activeRs, "ENTRIES.DTA"), "active-entries-ignored");

            var store = new ActiveCheckoutStore(activeStorePath);
            store.Save(new Models.ActiveCheckout
            {
                Office = "Test",
                ParentEvent = "Event",
                SessionName = "Event",
                RelativePath = "Event",
                LibrarySessionPath = Path.Combine(root, "Library", "Event"),
                ActivePath = activePath,
                BackupPath = backup,
                AutosavePath = autosave,
                DbsPath = Path.Combine(root, "DB's"),
                PrimaryEventFile = "Event.event",
                CheckedOutAt = DateTimeOffset.UtcNow
            });

            var mirror = new AutosaveMirrorService(new FileCopyService(), store);
            await mirror.MirrorOnceAsync();

            Assert.Equal("from-backup", File.ReadAllText(Path.Combine(autosave, "ENTRIES.DTA")));
            Assert.False(
                File.Exists(Path.Combine(autosave, "race.dat")),
                "Active RUNSCORE must not be mirrored into _autosave (overlay-only design)");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task MirrorNewer_ReturnsAbsoluteDestPathsCopied()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-mirror-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(root, "src");
            var dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "ENTRIES.DTA"), "a");
            File.WriteAllText(Path.Combine(src, "Finish.FN2"), "b");

            var copy = new FileCopyService();
            var first = await copy.MirrorNewerFilesAsync(src, dst);
            Assert.Equal(2, first.Count);
            Assert.All(first, p => Assert.True(File.Exists(p)));
            Assert.Contains(first, p => p.EndsWith("ENTRIES.DTA", StringComparison.OrdinalIgnoreCase));

            var second = await copy.MirrorNewerFilesAsync(src, dst);
            Assert.Empty(second);

            File.WriteAllText(Path.Combine(src, "ENTRIES.DTA"), "changed");
            File.SetLastWriteTimeUtc(Path.Combine(src, "ENTRIES.DTA"), DateTime.UtcNow.AddMinutes(1));
            var third = await copy.MirrorNewerFilesAsync(src, dst);
            Assert.Single(third);
            Assert.EndsWith("ENTRIES.DTA", third[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task RecoverMisplacedBackupSiblings_DemanglesAndMovesIntoRaceFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-sib-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(root, "Backup", "Test Office", "2026 - Pumpkin Run Jackson");
        var autosave = Path.Combine(root, "autosave");
        Directory.CreateDirectory(backup);
        var parent = Path.GetDirectoryName(backup)!;

        // RunScore wrote beside the folder when registry path lacked trailing '\'.
        var entries = Path.Combine(parent, "2026 - Pumpkin Run JacksonENTRIES.DTA");
        var finish = Path.Combine(parent, "2026 - Pumpkin Run JacksonFinish.FN2");
        var status = Path.Combine(parent, "2026 - Pumpkin Run Jacksonstatus.TM4");
        File.WriteAllText(entries, "entries");
        File.WriteAllText(finish, "finish");
        File.WriteAllText(status, "status");

        await AutosaveMirrorService.RecoverMisplacedBackupSiblingsAsync(backup, autosave);

        Assert.Equal("entries", File.ReadAllText(Path.Combine(backup, "ENTRIES.DTA")));
        Assert.Equal("finish", File.ReadAllText(Path.Combine(backup, "Finish.FN2")));
        Assert.Equal("status", File.ReadAllText(Path.Combine(backup, "status.TM4")));
        Assert.Equal("entries", File.ReadAllText(Path.Combine(autosave, "ENTRIES.DTA")));
        Assert.False(File.Exists(entries), "mangled sibling should be removed after heal");
        Assert.False(File.Exists(finish));
        Assert.False(File.Exists(status));
    }

    [Theory]
    [InlineData("2026 - EventENTRIES.DTA", "2026 - Event", "ENTRIES.DTA", true)]
    [InlineData("2026 - EventFinish.FN2", "2026 - Event", "Finish.FN2", true)]
    [InlineData("2026 - Event TESTENTRIES.DTA", "2026 - Event", "", false)]
    [InlineData("2026 - Events.pdf", "2026 - Event", "", false)]
    [InlineData("2026 - Event", "2026 - Event", "", false)]
    public void TryDemangleMisplacedSibling_RejectsPrefixCollisions(
        string fileName, string folderName, string expectedDest, bool expectMatch)
    {
        var matched = AutosaveMirrorService.TryDemangleMisplacedSibling(fileName, folderName, out var dest);
        Assert.Equal(expectMatch, matched);
        if (expectMatch)
            Assert.Equal(expectedDest, dest);
    }

    [Fact]
    public async Task MirrorMisplacedEntriesSiblings_DemanglesConcatenatedNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-sib-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(root, "Backup", "Test Office", "2026 - Heavens Cradle TEST");
        var autosave = Path.Combine(root, "autosave");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);

        var sibling = Path.Combine(
            Path.GetDirectoryName(backup)!,
            "2026 - Heavens Cradle TESTENTRIES.DTA");
        File.WriteAllText(sibling, "live");
        File.SetLastWriteTimeUtc(sibling, DateTime.UtcNow);

        await AutosaveMirrorService.MirrorMisplacedEntriesSiblingsAsync(backup, autosave);

        var dest = Path.Combine(autosave, "ENTRIES.DTA");
        Assert.True(File.Exists(dest), "expected demangled ENTRIES.DTA in autosave");
        Assert.Equal("live", File.ReadAllText(dest));
        Assert.Equal("live", File.ReadAllText(Path.Combine(backup, "ENTRIES.DTA")));
    }
}
