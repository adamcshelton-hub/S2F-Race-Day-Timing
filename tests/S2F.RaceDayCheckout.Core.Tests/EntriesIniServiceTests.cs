using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class EntriesIniServiceTests
{
    private static readonly string Sample = """
        race name           
        Date Format         MM/DD/YYYY
        race date           
        %resultsid%         
        Date Of Birth Field Birthdate
        [control]
        Selected            Resultsid
        Selected Message    https://results.raceroster.com/results/%resultsid%/bib/
        """;

    [Fact]
    public void FormatKeyedLine_PadsWithSpacesToColumn21()
    {
        var line = EntriesIniService.FormatKeyedLine("race name", "Pumpkin Run");
        Assert.StartsWith("race name", line, StringComparison.Ordinal);
        Assert.EndsWith("Pumpkin Run", line);
        Assert.Equal(EntriesIniService.ValueColumnIndex + "Pumpkin Run".Length, line.Length);
        Assert.Equal(' ', line[EntriesIniService.ValueColumnIndex - 1]);
        Assert.DoesNotContain('.', line);
    }

    [Fact]
    public void UpsertKey_WritesRaceFields_PreservesControl()
    {
        var updated = EntriesIniService.UpsertKey(Sample, EntriesIniService.KeyRaceName, "Le Bonheur Pumpkin Run");
        updated = EntriesIniService.UpsertKey(updated, EntriesIniService.KeyRaceDate, "10/17/2026");
        updated = EntriesIniService.UpsertKey(updated, EntriesIniService.KeyResultsId, "xxu6s5mps2mhjpnh");

        Assert.Equal("Le Bonheur Pumpkin Run", EntriesIniService.ReadKey(updated, EntriesIniService.KeyRaceName));
        Assert.Equal("10/17/2026", EntriesIniService.ReadKey(updated, EntriesIniService.KeyRaceDate));
        Assert.Equal("xxu6s5mps2mhjpnh", EntriesIniService.ReadKey(updated, EntriesIniService.KeyResultsId));
        Assert.Contains("[control]", updated, StringComparison.Ordinal);
        Assert.Contains("Selected Message    https://results.raceroster.com/results/%resultsid%/bib/", updated);
        Assert.Contains("race name           Le Bonheur Pumpkin Run", updated);
    }

    [Theory]
    [InlineData("xxu6s5mps2mhjpnh", "xxu6s5mps2mhjpnh")]
    [InlineData("https://results.raceroster.com/v3/events/xxu6s5mps2mhjpnh", "xxu6s5mps2mhjpnh")]
    [InlineData("https://results.raceroster.com/v3/events/xxu6s5mps2mhjpnh/progress", "xxu6s5mps2mhjpnh")]
    [InlineData("  https://results.raceroster.com/results/abc123/bib/  ", "abc123")]
    public void NormalizeResultsId_AcceptsBareOrUrl(string input, string expected)
    {
        Assert.Equal(expected, EntriesIniService.NormalizeResultsId(input));
    }

    [Fact]
    public void BuildSessionFolderName_UsesYearFromDate()
    {
        Assert.Equal(
            "2026 - Le Bonheur Pumpkin Run",
            EntriesIniService.BuildSessionFolderName("Le Bonheur Pumpkin Run", "10/17/2026"));
    }

    [Fact]
    public void Write_RoundTripsFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2f-ini-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "RUNSCORE"));
        var path = Path.Combine(dir, "RUNSCORE", "Entries.ini");
        File.WriteAllText(path, Sample);
        try
        {
            EntriesIniService.Write(path, new EntriesIniService.Fields(
                "Test Race",
                "Oct 17, 2026",
                "https://results.raceroster.com/v3/events/xxu6s5mps2mhjpnh"));
            var fields = EntriesIniService.Read(path);
            Assert.Equal("Test Race", fields.RaceName);
            Assert.Equal("10/17/2026", fields.RaceDate);
            Assert.Equal("xxu6s5mps2mhjpnh", fields.ResultsId);
            Assert.Contains("[control]", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
