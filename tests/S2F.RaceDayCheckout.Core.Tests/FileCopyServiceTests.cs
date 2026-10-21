using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class FileCopyServiceTests
{
    [Fact]
    public async Task OverlayAllFiles_OverwritesSameSizeDifferentContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-overlay-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            const string live = "live-from-laptop1!!"; // 18 chars
            const string stale = "stale-same-length!!"; // 18 chars
            File.WriteAllText(Path.Combine(src, "ENTRIES.dta"), live);
            File.WriteAllText(Path.Combine(dst, "ENTRIES.dta"), stale);
            Assert.Equal(live.Length, stale.Length);
            // Destination stamped newer — MirrorNewer would skip; Overlay must not.
            File.SetLastWriteTimeUtc(Path.Combine(dst, "ENTRIES.dta"), DateTime.UtcNow.AddMinutes(5));

            var copied = await new FileCopyService().OverlayAllFilesAsync(src, dst);
            Assert.Contains(copied, p => p.EndsWith("ENTRIES.dta", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(live, File.ReadAllText(Path.Combine(dst, "ENTRIES.dta")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task OverlayAllFiles_ContinuesWhenOneDestinationFileIsLocked()
    {
        if (!OperatingSystem.IsWindows())
            return; // exclusive locks / File.Replace semantics differ on Unix CI agents

        var root = Path.Combine(Path.GetTempPath(), "s2f-lock-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var locked = Path.Combine(dst, "ENTRIES.dta");
        var free = Path.Combine(dst, "Finish.FN2");
        File.WriteAllText(Path.Combine(src, "ENTRIES.dta"), "new-entries");
        File.WriteAllText(Path.Combine(src, "Finish.FN2"), "new-finish");
        File.WriteAllText(locked, "old-entries");
        File.WriteAllText(free, "old-finish");

        await using var hold = new FileStream(
            locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var copied = await new FileCopyService().OverlayAllFilesAsync(src, dst);
            Assert.Contains(copied, p => p.EndsWith("Finish.FN2", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(copied, p => p.EndsWith("ENTRIES.dta", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("new-finish", File.ReadAllText(free));
            Assert.Equal("old-entries", File.ReadAllText(locked));
        }
        finally
        {
            await hold.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }
}
