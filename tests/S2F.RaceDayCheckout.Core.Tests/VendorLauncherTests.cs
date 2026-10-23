using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class VendorLauncherTests
{
    private static ActiveCheckout Active(
        string activePath,
        string sessionName = "2026 - Race",
        string primaryEventFile = "") =>
        new()
        {
            Office = "Test",
            ParentEvent = sessionName,
            SessionName = sessionName,
            RelativePath = sessionName,
            LibrarySessionPath = activePath,
            ActivePath = activePath,
            BackupPath = Path.Combine(activePath, "_backup"),
            AutosavePath = Path.Combine(activePath, "_autosave"),
            DbsPath = Path.Combine(activePath, "DB's"),
            PrimaryEventFile = primaryEventFile
        };

    [Fact]
    public void SuggestEventFileName_StripsYearPrefix()
    {
        var name = VendorLauncher.SuggestEventFileName(
            Active(@"C:\S2F\RaceDay\Active\x", "2026 - Adam Test Duplicate"));
        Assert.Equal("Adam Test Duplicate.event", name);
    }

    [Fact]
    public void FormatSaveAsFolderGuide_ShowsThisPcClickThrough()
    {
        var guide = VendorLauncher.FormatSaveAsFolderGuide(
            Active(@"C:\S2F\RaceDay\Active\2026 - Adam Test Duplicate", "2026 - Adam Test Duplicate"));
        Assert.Contains("This PC", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not Documents", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Local Disk (C:)", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("S2F", guide);
        Assert.Contains("RaceDay", guide);
        Assert.Contains("Active", guide);
        Assert.Contains("2026 - Adam Test Duplicate", guide);
        Assert.Contains("Adam Test Duplicate.event", guide);
        Assert.Contains("← open this folder", guide);
        Assert.Contains("← File name", guide);
    }

    [Fact]
    public void FormatSaveAsPathInline_IsSingleWindowsPath_NoNoEventCopy()
    {
        var path = VendorLauncher.FormatSaveAsPathInline(
            @"C:\S2F\RaceDay\Active\Memphis\2026 - Gateway to the Delta",
            "Gateway to the Delta.event");
        Assert.Equal(
            @"C:\S2F\RaceDay\Active\Memphis\2026 - Gateway to the Delta\Gateway to the Delta.event",
            path);
        Assert.DoesNotContain("No Timing", path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("This PC", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatActiveSaveAsReminder_KeepsFolderTreeForActiveScreen()
    {
        var guide = VendorLauncher.FormatActiveSaveAsReminder(
            Active(@"C:\S2F\RaceDay\Active\2026 - Weekend\Saturday", "Saturday"));
        Assert.Contains("Save the Timing & Scoring .event here", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("announcer", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("This PC", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not Documents", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Saturday.event", guide);
        Assert.Contains("← open this folder", guide);
    }

    [Fact]
    public void ResolvePrimaryEventFile_FindsSaveAsOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-vl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Created Midweek.event"), "zip");
            Assert.Equal(
                "Created Midweek.event",
                VendorLauncher.ResolvePrimaryEventFile(Active(root, "2026 - Created Midweek")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ResolvePrimaryEventFile_KeepsExistingWhenPresent()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-vl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Primary.event"), "a");
            File.WriteAllText(Path.Combine(root, "Other.event"), "b");
            Assert.Equal(
                "Primary.event",
                VendorLauncher.ResolvePrimaryEventFile(Active(root, primaryEventFile: "Primary.event")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
