using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class EventBuilderServiceTests
{
    [Fact]
    public async Task BuildAsync_CopiesTemplate_AndWritesEntriesIni()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-build-" + Guid.NewGuid().ToString("N"));
        var template = Path.Combine(root, "Templates", "NEW RACE Chip Start");
        Directory.CreateDirectory(Path.Combine(template, "RUNSCORE"));
        Directory.CreateDirectory(Path.Combine(template, "DB's"));
        File.WriteAllText(
            Path.Combine(template, "RUNSCORE", "Entries.ini"),
            "race name................\r\nDate format..............MM/DD/YYYY\r\nrace date................\r\n%resultsid%..............\r\n[control]\r\nSelected.................Resultsid\r\n");
        File.WriteAllText(Path.Combine(template, "RUNSCORE", "ENTRIES.FRM"), "frm");
        File.WriteAllText(Path.Combine(template, "Sample.event"), "event");

        try
        {
            var svc = new EventBuilderService();
            var result = await svc.BuildAsync(new EventBuildRequest
            {
                LibraryRoot = root,
                Office = "Test Office",
                SourceSessionPath = template,
                RaceName = "Le Bonheur Pumpkin Run",
                RaceDate = "10/17/2026",
                ResultsId = "https://results.raceroster.com/v3/events/xxu6s5mps2mhjpnh"
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal("2026 - Le Bonheur Pumpkin Run", result.SessionFolderName);
            Assert.True(Directory.Exists(result.SessionPath));
            Assert.True(File.Exists(Path.Combine(result.SessionPath!, "RUNSCORE", "ENTRIES.FRM")));
            Assert.True(File.Exists(Path.Combine(result.SessionPath!, "Sample.event")));
            // Template had empty DB's — seed file so OneDrive/Graph keep the folder.
            Assert.True(File.Exists(Path.Combine(result.SessionPath!, "DB's", "Exports land here.txt")));

            var fields = EntriesIniService.Read(result.EntriesIniPath!);
            Assert.Equal("Le Bonheur Pumpkin Run", fields.RaceName);
            Assert.Equal("10/17/2026", fields.RaceDate);
            Assert.Equal("xxu6s5mps2mhjpnh", fields.ResultsId);
            Assert.Contains("[control]", File.ReadAllText(result.EntriesIniPath!), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task BuildAsync_KeepsExistingDbsExports_WithoutSeed()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-dbs-" + Guid.NewGuid().ToString("N"));
        var template = Path.Combine(root, "Templates", "NEW RACE Chip Start");
        Directory.CreateDirectory(Path.Combine(template, "RUNSCORE"));
        Directory.CreateDirectory(Path.Combine(template, "DB's"));
        File.WriteAllText(Path.Combine(template, "RUNSCORE", "Entries.ini"), "race name................\n");
        File.WriteAllText(Path.Combine(template, "RUNSCORE", "ENTRIES.FRM"), "frm");
        File.WriteAllText(Path.Combine(template, "DB's", "template backupsave.csv"), "csv");

        try
        {
            var result = await new EventBuilderService().BuildAsync(new EventBuildRequest
            {
                LibraryRoot = root,
                Office = "Test Office",
                SourceSessionPath = template,
                RaceName = "With Exports",
                RaceDate = "10/18/2026",
                ResultsId = ""
            });

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(Path.Combine(result.SessionPath!, "DB's", "template backupsave.csv")));
            Assert.False(File.Exists(Path.Combine(result.SessionPath!, "DB's", "Exports land here.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task BuildAsync_TemplateWithoutEvent_DoesNotCreateFakeZip()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-build-" + Guid.NewGuid().ToString("N"));
        var template = Path.Combine(root, "Templates", "NEW RACE Chip Start");
        Directory.CreateDirectory(Path.Combine(template, "RUNSCORE"));
        File.WriteAllText(Path.Combine(template, "RUNSCORE", "Entries.ini"), "race name................\n");
        File.WriteAllText(Path.Combine(template, "RUNSCORE", "ENTRIES.FRM"), "frm");

        try
        {
            var result = await new EventBuilderService().BuildAsync(new EventBuildRequest
            {
                LibraryRoot = root,
                Office = "Test Office",
                SourceSessionPath = template,
                RaceName = "Adam Test Duplicate",
                RaceDate = "10/17/2026",
                ResultsId = "abc123"
            });

            Assert.True(result.Success, result.Message);
            Assert.True(result.MissingEventFile);
            Assert.Null(result.PrimaryEventFile);
            Assert.Empty(Directory.GetFiles(result.SessionPath!, "*.event"));
            Assert.Contains("Active", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Adam Test Duplicate.event", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Test Office", result.Office);
            Assert.True(Directory.Exists(Path.Combine(result.SessionPath!, "DB's")));
            Assert.True(File.Exists(Path.Combine(result.SessionPath!, "DB's", "Exports land here.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task BuildAsync_RaceGroup_CreatesLeafUnderParent()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-group-" + Guid.NewGuid().ToString("N"));
        var template = Path.Combine(root, "Templates", "NEW RACE Chip Start");
        Directory.CreateDirectory(Path.Combine(template, "RUNSCORE"));
        File.WriteAllText(Path.Combine(template, "RUNSCORE", "Entries.ini"), "race name................\n");
        File.WriteAllText(Path.Combine(template, "RUNSCORE", "ENTRIES.FRM"), "frm");

        try
        {
            var result = await new EventBuilderService().BuildAsync(new EventBuildRequest
            {
                LibraryRoot = root,
                Office = "Test Office",
                SourceSessionPath = template,
                RaceName = "Sunday AM",
                RaceDate = "10/18/2026",
                ResultsId = "abc",
                ParentFolderName = "2026 - LRM Weekend"
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal("Sunday AM", result.SessionFolderName);
            Assert.Equal("2026 - LRM Weekend", result.ParentFolderName);
            Assert.Equal(Path.Combine("2026 - LRM Weekend", "Sunday AM"), result.RelativePath);
            Assert.True(Directory.Exists(Path.Combine(root, "Test Office", "2026 - LRM Weekend", "Sunday AM", "RUNSCORE")));
            Assert.True(File.Exists(Path.Combine(
                root, "Test Office", "2026 - LRM Weekend", "Sunday AM", "DB's", "Exports land here.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ListPastRaceSessions_FindsOfficeYearRaceLeaves()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-past-" + Guid.NewGuid().ToString("N"));
        var race = Path.Combine(root, "Past Races", "Memphis", "2025", "Pumpkin Run");
        Directory.CreateDirectory(Path.Combine(race, "RUNSCORE"));
        File.WriteAllText(Path.Combine(race, "RUNSCORE", "ENTRIES.FRM"), "x");

        try
        {
            var list = new EventBuilderService().ListPastRaceSessions(root, "Memphis");
            Assert.Single(list);
            Assert.Equal(Path.Combine("2025", "Pumpkin Run"), list[0].Display);
            Assert.Equal(race, list[0].Path);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task BuildAsync_FailsWhenDestinationExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-build-" + Guid.NewGuid().ToString("N"));
        var template = Path.Combine(root, "Templates", "T");
        Directory.CreateDirectory(Path.Combine(template, "RUNSCORE"));
        File.WriteAllText(Path.Combine(template, "RUNSCORE", "Entries.ini"), "race name................\n");
        var dest = Path.Combine(root, "Test Office", "2026 - Exists");
        Directory.CreateDirectory(dest);

        try
        {
            var result = await new EventBuilderService().BuildAsync(new EventBuildRequest
            {
                LibraryRoot = root,
                Office = "Test Office",
                SourceSessionPath = template,
                RaceName = "Exists",
                RaceDate = "01/01/2026",
                ResultsId = "abc"
            });
            Assert.False(result.Success);
            Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
