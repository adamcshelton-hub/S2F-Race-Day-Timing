using System.Text.Json;
using System.Text.Json.Serialization;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// App-owned session assignment. Source of truth for the main timing .event.
/// Announcer T&amp;S secondaries are any other .event in the session folder — not listed here.
/// </summary>
public static class SessionRoleStore
{
    public const string FileName = "s2f_session.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetPath(string librarySessionPath) =>
        Path.Combine(librarySessionPath, PathService.AppMetaFolderName, FileName);

    public static SessionRoles? TryLoad(string? librarySessionPath)
    {
        if (string.IsNullOrWhiteSpace(librarySessionPath))
            return null;

        var path = GetPath(librarySessionPath);
        if (!File.Exists(path))
            return null;

        try
        {
            var roles = JsonSerializer.Deserialize<SessionRoles>(File.ReadAllText(path), JsonOptions);
            if (roles is null)
                return null;
            roles.PrimaryEventFile = NormalizeFileName(roles.PrimaryEventFile);
            roles.AnnouncerEventFile = NormalizeFileName(roles.AnnouncerEventFile);
            return roles;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Null when the assignment is usable. Otherwise a staff-facing reason.</summary>
    public static string? Validate(SessionRoles roles)
    {
        var primary = NormalizeFileName(roles.PrimaryEventFile);
        var announcer = NormalizeFileName(roles.AnnouncerEventFile);
        if (primary is not null && !primary.EndsWith(".event", StringComparison.OrdinalIgnoreCase))
            return "Timing file must be a .event file.";
        if (announcer is not null && !announcer.EndsWith(".event", StringComparison.OrdinalIgnoreCase))
            return "Announcer file must be a .event file.";
        if (primary is not null
            && announcer is not null
            && string.Equals(primary, announcer, StringComparison.OrdinalIgnoreCase))
            return "Timing and announcer must be different .event files.";
        return null;
    }

    public static void Save(string librarySessionPath, SessionRoles roles)
    {
        var error = Validate(roles);
        if (error is not null)
            throw new InvalidOperationException(error);

        var folder = Path.Combine(librarySessionPath, PathService.AppMetaFolderName);
        Directory.CreateDirectory(folder);
        PathService.TryMarkHidden(folder);

        // Primary only — do not re-write legacy announcerEventFile on new saves.
        var normalized = new SessionRoles
        {
            PrimaryEventFile = NormalizeFileName(roles.PrimaryEventFile)
        };
        File.WriteAllText(GetPath(librarySessionPath), JsonSerializer.Serialize(normalized, JsonOptions));
    }

    public static string? NormalizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var file = Path.GetFileName(name.Trim());
        return string.IsNullOrWhiteSpace(file) ? null : file;
    }
}
