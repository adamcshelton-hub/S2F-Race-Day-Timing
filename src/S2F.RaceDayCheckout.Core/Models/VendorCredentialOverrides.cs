namespace S2F.RaceDayCheckout.Core.Models;

/// <summary>
/// Per-laptop Admin overrides for RaceRoster / RunSignUp.
/// When a login override is set, OneDrive <c>runscore-vendor-secrets.json</c> must not overwrite that pair.
/// SMS always comes from the company secrets file.
/// </summary>
public sealed class VendorCredentialOverrides
{
    public string RaceRosterLogin { get; init; } = string.Empty;
    public string RaceRosterPassword { get; init; } = string.Empty;
    public string RunSignUpLogin { get; init; } = string.Empty;
    public string RunSignUpPassword { get; init; } = string.Empty;

    public bool HasRaceRosterLogin => !string.IsNullOrWhiteSpace(RaceRosterLogin);
    public bool HasRunSignUpLogin => !string.IsNullOrWhiteSpace(RunSignUpLogin);

    public static VendorCredentialOverrides From(AppSettings? settings)
    {
        if (settings is null)
            return new VendorCredentialOverrides();

        return new VendorCredentialOverrides
        {
            RaceRosterLogin = settings.RaceRosterLoginOverride ?? string.Empty,
            RaceRosterPassword = settings.RaceRosterPasswordOverride ?? string.Empty,
            RunSignUpLogin = settings.RunSignUpLoginOverride ?? string.Empty,
            RunSignUpPassword = settings.RunSignUpPasswordOverride ?? string.Empty
        };
    }
}
