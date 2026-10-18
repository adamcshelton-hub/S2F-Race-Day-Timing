using System.Runtime.InteropServices;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class NotepadPlusPlusArchitectureTests
{
    [Fact]
    public void ArmWindows_WantsArm64Installer_EvenWhenProcessIsX64()
    {
        Assert.Equal(
            NotepadPlusPlusArchitecture.MachineArm64,
            NotepadPlusPlusArchitecture.DesiredMachine(Architecture.Arm64));
        Assert.Equal("npp-setup-arm64.bin", NotepadPlusPlusArchitecture.BundledInstallerFileName(Architecture.Arm64));
        Assert.False(NotepadPlusPlusArchitecture.InstalledBuildMatches(
            NotepadPlusPlusArchitecture.MachineAmd64,
            Architecture.Arm64));
    }

    [Fact]
    public void X64Windows_WantsX64Installer()
    {
        Assert.Equal(
            NotepadPlusPlusArchitecture.MachineAmd64,
            NotepadPlusPlusArchitecture.DesiredMachine(Architecture.X64));
        Assert.Equal("npp-setup-x64.bin", NotepadPlusPlusArchitecture.BundledInstallerFileName(Architecture.X64));
        Assert.True(NotepadPlusPlusArchitecture.InstalledBuildMatches(
            NotepadPlusPlusArchitecture.MachineAmd64,
            Architecture.X64));
    }

    [Fact]
    public void ReadPeMachine_ReadsArm64AndX64Headers()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-pe-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, FakePe(NotepadPlusPlusArchitecture.MachineArm64));
            Assert.Equal(NotepadPlusPlusArchitecture.MachineArm64, NotepadPlusPlusArchitecture.ReadPeMachine(path));

            File.WriteAllBytes(path, FakePe(NotepadPlusPlusArchitecture.MachineAmd64));
            Assert.Equal(NotepadPlusPlusArchitecture.MachineAmd64, NotepadPlusPlusArchitecture.ReadPeMachine(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static byte[] FakePe(ushort machine)
    {
        var bytes = new byte[70];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        bytes[0x3C] = 64;
        bytes[64] = (byte)'P';
        bytes[65] = (byte)'E';
        bytes[68] = (byte)(machine & 0xFF);
        bytes[69] = (byte)(machine >> 8);
        return bytes;
    }
}
