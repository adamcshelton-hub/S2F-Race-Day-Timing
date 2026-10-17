using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class AutosaveMirrorServiceTests
{
    [Fact]
    public async Task MirrorMisplacedEntriesSiblings_DemanglesConcatenatedNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-sib-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(root, "Backup", "Test Office", "2026 - Heavens Cradle TEST");
        var autosave = Path.Combine(root, "autosave");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);

        // RunScore wrote beside the folder when registry path lacked trailing '\'.
        var sibling = Path.Combine(
            Path.GetDirectoryName(backup)!,
            "2026 - Heavens Cradle TESTENTRIES.DTA");
        File.WriteAllText(sibling, "live");
        File.SetLastWriteTimeUtc(sibling, DateTime.UtcNow);

        await AutosaveMirrorService.MirrorMisplacedEntriesSiblingsAsync(backup, autosave);

        var dest = Path.Combine(autosave, "ENTRIES.DTA");
        Assert.True(File.Exists(dest), "expected demangled ENTRIES.DTA in autosave");
        Assert.Equal("live", File.ReadAllText(dest));
    }
}
