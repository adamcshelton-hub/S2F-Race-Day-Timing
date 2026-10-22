using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public interface IRunScoreVendorSecretsStore
{
    string CachePath { get; }
    RunScoreVendorSecrets? Load();
    void Save(RunScoreVendorSecrets secrets);
    bool HasCache { get; }
}

/// <summary>
/// Local AppData cache of company vendor credentials pulled from OneDrive.
/// Not the Admin-editable SOP file.
/// </summary>
public sealed class RunScoreVendorSecretsStore : IRunScoreVendorSecretsStore
{
    public const string FileName = "runscore-vendor-secrets.json";

    /// <summary>OneDrive path under GraphDriveRoot (usually My files).</summary>
    public const string CloudRelativePath = "._s2f/runscore-vendor-secrets.json";

    private readonly string _path;

    public RunScoreVendorSecretsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S2FRaceDayCheckout",
            FileName);
    }

    public string CachePath => _path;

    public bool HasCache => File.Exists(_path);

    public RunScoreVendorSecrets? Load()
    {
        if (!File.Exists(_path))
            return null;
        try
        {
            var json = File.ReadAllText(_path);
            return RunScoreVendorSecrets.TryParse(json, out var secrets, out _)
                ? secrets
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(RunScoreVendorSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, secrets.ToJson());
    }
}
