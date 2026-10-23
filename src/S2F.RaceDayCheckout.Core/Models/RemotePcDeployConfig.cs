using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S2F.RaceDayCheckout.Core.Models;

/// <summary>
/// Silent RemotePC host deploy parameters (msiexec). Sourced from
/// <c>redist/remotepc-deploy.json</c> next to the bundled MSI.
/// </summary>
public sealed class RemotePcDeployConfig
{
    public const string ConfigFileName = "remotepc-deploy.json";
    public const string BundledMsiFileName = "remotepc-host.bin";

    /// <summary>Mandatory Configuration ID from RemotePC → Deploy Package → Group deployment via MSI.</summary>
    [JsonPropertyName("configurationId")]
    public string ConfigurationId { get; set; } = string.Empty;

    /// <summary>Optional group (created if missing). Empty = RemotePC default group.</summary>
    [JsonPropertyName("groupName")]
    public string GroupName { get; set; } = string.Empty;

    /// <summary>Optional Personal Key for the host.</summary>
    [JsonPropertyName("personalKey")]
    public string PersonalKey { get; set; } = string.Empty;

    /// <summary>When false, host configures without the on-screen consent window (SHOW_CONFIRM=0).</summary>
    [JsonPropertyName("showConfirm")]
    public bool ShowConfirm { get; set; }

    /// <summary>When true, HIDETRAY=1.</summary>
    [JsonPropertyName("hideTray")]
    public bool HideTray { get; set; }

    /// <summary>
    /// CONNECT_PERMISSION: 0 disabled, 1 auto-decline, 2 auto-allow, 3 allow after expire.
    /// Default 0 — no prompt on the race laptop.
    /// </summary>
    [JsonPropertyName("connectPermission")]
    public int ConnectPermission { get; set; }

    /// <summary>Optional display name in the RemotePC console. Empty = machine name.</summary>
    [JsonPropertyName("computerName")]
    public string ComputerName { get; set; } = string.Empty;

    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(ConfigurationId);

    /// <summary>msiexec argument list after <c>/i "msi"</c> (includes <c>/qn</c>).</summary>
    public string BuildMsiexecArguments(string msiPath)
    {
        if (string.IsNullOrWhiteSpace(msiPath))
            throw new ArgumentException("MSI path is required.", nameof(msiPath));
        if (!IsUsable)
            throw new InvalidOperationException("RemotePC Configuration ID is missing.");

        var sb = new StringBuilder();
        sb.Append("/i \"").Append(msiPath.Trim()).Append("\" /qn");
        sb.Append(" ID=").Append(ConfigurationId.Trim());
        if (!string.IsNullOrWhiteSpace(GroupName))
            sb.Append(" GROUPNAME=").Append(QuoteIfNeeded(GroupName.Trim()));
        if (!string.IsNullOrWhiteSpace(PersonalKey))
            sb.Append(" PERSONALKEY=").Append(QuoteIfNeeded(PersonalKey.Trim()));
        if (!string.IsNullOrWhiteSpace(ComputerName))
            sb.Append(" COMPUTERNAME=").Append(QuoteIfNeeded(ComputerName.Trim()));
        sb.Append(" HIDETRAY=").Append(HideTray ? "1" : "0");
        sb.Append(" CONNECT_PERMISSION=").Append(Math.Clamp(ConnectPermission, 0, 3));
        sb.Append(" SHOW_CONFIRM=").Append(ShowConfirm ? "1" : "0");
        return sb.ToString();
    }

    public static RemotePcDeployConfig? TryLoad(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            return null;

        try
        {
            var json = File.ReadAllText(jsonPath);
            var cfg = JsonSerializer.Deserialize<RemotePcDeployConfig>(json, JsonOptions);
            return cfg is { IsUsable: true } ? cfg : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static string QuoteIfNeeded(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? "\"" + value.Replace("\"", "", StringComparison.Ordinal) + "\"" : value;
}
