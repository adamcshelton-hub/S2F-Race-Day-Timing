using System.Text.Json;
using System.Text.Json.Serialization;

namespace S2F.RaceDayCheckout.Core.Models;

/// <summary>
/// Company RunScore vendor logins (SMS / RaceRoster / RunSignUp).
/// Sourced from OneDrive <c>._s2f/runscore-vendor-secrets.json</c> via Graph — never from GitHub or Admin SOP.
/// </summary>
public sealed class RunScoreVendorSecrets
{
    public string SmsLogin { get; set; } = string.Empty;
    public string SmsPassword { get; set; } = string.Empty;
    public string RaceRosterLogin { get; set; } = string.Empty;
    public string RaceRosterPassword { get; set; } = string.Empty;
    public string RunSignUpLogin { get; set; } = string.Empty;
    public string RunSignUpPassword { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool TryParse(string? json, out RunScoreVendorSecrets secrets, out string? error)
    {
        secrets = new RunScoreVendorSecrets();
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RunScoreVendorSecrets>(json, JsonOptions);
            if (parsed is null)
            {
                error = "null document";
                return false;
            }

            secrets = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>True when at least one login/password pair is complete.</summary>
    public bool HasAnyCompletePair =>
        IsComplete(SmsLogin, SmsPassword)
        || IsComplete(RaceRosterLogin, RaceRosterPassword)
        || IsComplete(RunSignUpLogin, RunSignUpPassword);

    public static bool IsComplete(string? login, string? password) =>
        !string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(password);
}
