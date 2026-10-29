using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class ManualBackupJsonlTests
{
    [Fact]
    public void MergeIncoming_IsIdempotent_AndPreservesExisting()
    {
        var existing = new List<ManualBackupTap>
        {
            new() { Id = "a", Bib = "1", Location = "Finish", DeviceId = "dev" },
            new() { Id = "b", Bib = "2", Location = "Finish", DeviceId = "dev" },
        };
        var incoming = new List<ManualBackupTap>
        {
            new() { Id = "b", Bib = "2", Location = "Finish", DeviceId = "dev" }, // already present
            new() { Id = "c", Bib = "3", Location = "Finish", DeviceId = "dev" },
        };

        var merged = ManualBackupJsonl.MergeIncoming(existing, incoming, out var added);

        Assert.Equal(3, merged.Count);
        Assert.Single(added);
        Assert.Equal("c", added[0].Id);
        Assert.True(ManualBackupJsonl.TapsHaveIds(merged, new[] { "a", "b", "c" }));
    }

    [Fact]
    public void Parse_And_Serialize_RoundTrip()
    {
        var taps = new List<ManualBackupTap>
        {
            new()
            {
                Id = "abc123",
                Bib = "101",
                Location = "Backup",
                DeviceId = "laptop",
                TimestampUtc = DateTimeOffset.Parse("2026-10-29T14:00:00Z"),
                CreatedUtc = DateTimeOffset.Parse("2026-10-29T14:00:00Z"),
            },
        };

        var text = ManualBackupJsonl.Serialize(taps);
        var parsed = ManualBackupJsonl.Parse(text);

        Assert.Single(parsed);
        Assert.Equal("abc123", parsed[0].Id);
        Assert.Equal("101", parsed[0].Bib);
        Assert.Equal("Backup", parsed[0].Location);
    }

    [Fact]
    public void SyncBatch_And_MaxBatch_MatchStaffing()
    {
        Assert.Equal(25, ManualBackupStore.SyncBatch);
        Assert.Equal(40, ManualBackupStore.MaxBatch);
        Assert.True(ManualBackupStore.SyncBatch <= ManualBackupStore.MaxBatch);
    }
}
