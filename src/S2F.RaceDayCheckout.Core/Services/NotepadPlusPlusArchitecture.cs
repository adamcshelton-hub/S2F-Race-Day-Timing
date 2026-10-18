using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Notepad++ must match the Windows OS, not the app process. An x64 Race Day
/// install still runs on ARM Windows (Parallels); that must not leave the x64 editor in place.
/// </summary>
public static class NotepadPlusPlusArchitecture
{
    public const ushort MachineAmd64 = 0x8664;
    public const ushort MachineArm64 = 0xAA64;

    public static ushort DesiredMachine(Architecture osArchitecture) =>
        osArchitecture == Architecture.Arm64 ? MachineArm64 : MachineAmd64;

    public static string BundledInstallerFileName(Architecture osArchitecture) =>
        osArchitecture == Architecture.Arm64 ? "npp-setup-arm64.bin" : "npp-setup-x64.bin";

    public static bool InstalledBuildMatches(ushort? installedMachine, Architecture osArchitecture) =>
        installedMachine == DesiredMachine(osArchitecture);

    public static ushort? ReadPeMachine(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadPeMachine(stream);
    }

    public static ushort? ReadPeMachine(Stream stream)
    {
        Span<byte> dos = stackalloc byte[64];
        if (stream.Read(dos) < 64 || dos[0] != (byte)'M' || dos[1] != (byte)'Z')
            return null;

        var lfanew = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
        if (lfanew < 0)
            return null;

        if (stream.CanSeek && stream.Length < lfanew + 6)
            return null;

        stream.Position = lfanew;
        Span<byte> pe = stackalloc byte[6];
        if (stream.Read(pe) < 6 || pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0)
            return null;

        return BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]);
    }
}
