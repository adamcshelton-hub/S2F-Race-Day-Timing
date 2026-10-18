using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class SecondaryEventSelectorTests
{
    [Fact]
    public void SidecarPrimary_Wins_AndSecondariesAreTheRest()
    {
        var files = new[]
        {
            "2025 RCM Half_Full.event",
            "RCM Announcer.event",
            "RCM Video Display.event"
        };

        Assert.Equal(
            "2025 RCM Half_Full.event",
            SecondaryEventSelector.ResolvePrimary(files, "2025 RCM Half_Full.event"));

        var secondaries = SecondaryEventSelector.ListSecondaries(files, "2025 RCM Half_Full.event");
        Assert.Equal(2, secondaries.Count);
        Assert.Contains("RCM Announcer.event", secondaries);
        Assert.Contains("RCM Video Display.event", secondaries);
        Assert.True(SecondaryEventSelector.HasSecondary(files, "2025 RCM Half_Full.event"));
    }

    [Fact]
    public void WithoutSidecar_ChoosePrimary_LeavesOthersAsSecondaries()
    {
        var files = new[]
        {
            "Race.event",
            "Race-announcer.event",
            "Race Video Display.event"
        };

        var primary = SecondaryEventSelector.ResolvePrimary(files, null);
        Assert.Equal("Race.event", primary);

        var secondaries = SecondaryEventSelector.ListSecondaries(files, null);
        Assert.DoesNotContain(primary!, secondaries);
        Assert.Equal(2, secondaries.Count);
    }

    [Fact]
    public void SingleEvent_HasNoSecondary()
    {
        var files = new[] { "Only.event" };
        Assert.False(SecondaryEventSelector.HasSecondary(files, null));
        Assert.Empty(SecondaryEventSelector.ListSecondaries(files, "Only.event"));
    }
}
