using System.Text.Json.Serialization;

namespace S2F.RaceDayCheckout.Core.Models;

/// <summary>Session-level Manual Backup config under <c>._checkin/manual_backup/manifest.json</c>.</summary>
public sealed class ManualBackupManifest
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Location names (= RunScore event names when possible).</summary>
    [JsonPropertyName("locations")]
    public List<string> Locations { get; set; } = new();

    [JsonPropertyName("devices")]
    public List<ManualBackupDeviceInfo> Devices { get; set; } = new();

    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("office")]
    public string? Office { get; set; }

    [JsonPropertyName("sessionRelativePath")]
    public string? SessionRelativePath { get; set; }

    [JsonPropertyName("displayLeaf")]
    public string? DisplayLeaf { get; set; }
}

public sealed class ManualBackupDeviceInfo
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Per-device state under <c>{Location}/{deviceId}/device.json</c>.</summary>
public sealed class ManualBackupDeviceState
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    /// <summary>Count of lines already Played from <c>current.jsonl</c> (Play All / Play advances this).</summary>
    [JsonPropertyName("playCursor")]
    public int PlayCursor { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One bib tap line in a segment jsonl file.</summary>
public sealed class ManualBackupTap
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("bib")]
    public string Bib { get; set; } = "";

    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; set; }

    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("staffingUserId")]
    public int? StaffingUserId { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("voided")]
    public bool Voided { get; set; }
}

/// <summary>Office-level catalog entry for Staffing / Backup Laptop role.</summary>
public sealed class ManualBackupCatalog
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("office")]
    public string Office { get; set; } = "";

    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("sessions")]
    public List<ManualBackupCatalogSession> Sessions { get; set; } = new();
}

public sealed class ManualBackupCatalogSession
{
    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("displayLeaf")]
    public string DisplayLeaf { get; set; } = "";

    [JsonPropertyName("locations")]
    public List<string> Locations { get; set; } = new();

    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

