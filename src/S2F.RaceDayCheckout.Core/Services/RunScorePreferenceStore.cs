using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Read/write RunScore preference values under
/// HKCU\Software\RunTime Software\RunScore (same store RSProfile uses).
/// </summary>
public interface IRunScorePreferenceStore
{
    bool IsAvailable { get; }
    bool? TryGetBool(string name);
    int? TryGetInt(string name);
    string? TryGetString(string name);
    void SetBool(string name, bool value);
    void SetInt(string name, int value);
    void SetString(string name, string value);
}

/// <summary>No-op store when not on Windows (tests / Mac host).</summary>
public sealed class UnavailableRunScorePreferenceStore : IRunScorePreferenceStore
{
    public bool IsAvailable => false;
    public bool? TryGetBool(string name) => null;
    public int? TryGetInt(string name) => null;
    public string? TryGetString(string name) => null;
    public void SetBool(string name, bool value) => throw new PlatformNotSupportedException();
    public void SetInt(string name, int value) => throw new PlatformNotSupportedException();
    public void SetString(string name, string value) => throw new PlatformNotSupportedException();
}

/// <summary>In-memory store for unit tests.</summary>
public sealed class MemoryRunScorePreferenceStore : IRunScorePreferenceStore
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    public bool IsAvailable => true;

    public bool? TryGetBool(string name) =>
        _values.TryGetValue(name, out var v) && v is bool b ? b : null;

    public int? TryGetInt(string name) =>
        _values.TryGetValue(name, out var v) && v is int i ? i : null;

    public string? TryGetString(string name) =>
        _values.TryGetValue(name, out var v) && v is string s ? s : null;

    public void SetBool(string name, bool value) => _values[name] = value;
    public void SetInt(string name, int value) => _values[name] = value;
    public void SetString(string name, string value) => _values[name] = value ?? string.Empty;
}

/// <summary>Windows HKCU registry store used by RSServer/RSClient.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRunScorePreferenceStore : IRunScorePreferenceStore
{
    public const string KeyPath = @"Software\RunTime Software\RunScore";

    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public bool? TryGetBool(string name)
    {
        if (!IsAvailable) return null;
        using var key = OpenKey(writable: false);
        if (key is null) return null;
        var raw = key.GetValue(name);
        return raw switch
        {
            int i => i != 0,
            long l => l != 0,
            string s when int.TryParse(s, out var n) => n != 0,
            _ => null
        };
    }

    public int? TryGetInt(string name)
    {
        if (!IsAvailable) return null;
        using var key = OpenKey(writable: false);
        if (key is null) return null;
        var raw = key.GetValue(name);
        return raw switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var n) => n,
            _ => null
        };
    }

    public string? TryGetString(string name)
    {
        if (!IsAvailable) return null;
        using var key = OpenKey(writable: false);
        return key?.GetValue(name) as string;
    }

    public void SetBool(string name, bool value)
    {
        EnsureWindows();
        using var key = OpenKey(writable: true)
            ?? throw new InvalidOperationException($"Cannot open registry key {KeyPath}.");
        key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }

    public void SetInt(string name, int value)
    {
        EnsureWindows();
        using var key = OpenKey(writable: true)
            ?? throw new InvalidOperationException($"Cannot open registry key {KeyPath}.");
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void SetString(string name, string value)
    {
        EnsureWindows();
        using var key = OpenKey(writable: true)
            ?? throw new InvalidOperationException($"Cannot open registry key {KeyPath}.");
        key.SetValue(name, value ?? string.Empty, RegistryValueKind.String);
    }

    private static void EnsureWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("RunScore registry preferences require Windows.");
    }

    private static RegistryKey? OpenKey(bool writable)
    {
        EnsureWindows();
        return writable
            ? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            : Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
    }
}
