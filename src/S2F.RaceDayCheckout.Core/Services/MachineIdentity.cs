using System.Security.Cryptography;
using System.Text;

namespace S2F.RaceDayCheckout.Core.Services;

public interface IMachineIdentity
{
    string MachineId { get; }
    string MachineName { get; }
    string WindowsUser { get; }
}

public sealed class MachineIdentity : IMachineIdentity
{
    public MachineIdentity()
    {
        MachineName = Environment.MachineName;
        WindowsUser = Environment.UserName;
        MachineId = CreateStableId(MachineName, WindowsUser);
    }

    public string MachineId { get; }
    public string MachineName { get; }
    public string WindowsUser { get; }

    public static string CreateStableId(string machineName, string windowsUser)
    {
        var raw = $"{machineName}|{windowsUser}|{Environment.OSVersion}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }
}
