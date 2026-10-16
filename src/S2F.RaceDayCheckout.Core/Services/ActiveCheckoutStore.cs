using System.Text.Json;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>Tracks the single Active checkout on this machine (local state, not the OneDrive lock).</summary>
public sealed class ActiveCheckoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public ActiveCheckoutStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S2FRaceDayCheckout",
            "active.json");
    }

    public ActiveCheckout? Load()
    {
        if (!File.Exists(_path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ActiveCheckout>(File.ReadAllText(_path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(ActiveCheckout? checkout)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (checkout is null)
        {
            if (File.Exists(_path))
                File.Delete(_path);
            return;
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(checkout, JsonOptions));
    }
}
