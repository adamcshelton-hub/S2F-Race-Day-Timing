using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class RunScorePreferencesServiceTests
{
    private static (RunScorePreferencesService Svc, MemoryRunScorePreferenceStore Store, string SopPath, string Backup, string NativePath) CreateSvc(
        Action<RunScoreSopProfile>? customize = null)
    {
        var sopPath = Path.Combine(Path.GetTempPath(), "s2f-sop-" + Guid.NewGuid().ToString("N") + ".json");
        var nativePath = Path.Combine(Path.GetTempPath(), "s2f-native-" + Guid.NewGuid().ToString("N") + ".json");
        var sopStore = new RunScoreSopStore(sopPath);
        var profile = RunScoreSopProfile.CreateDefaults();
        customize?.Invoke(profile);
        sopStore.Save(profile);

        var store = new MemoryRunScorePreferenceStore();
        var svc = new RunScorePreferencesService(store, sopStore, new NativeRunScorePrefsStore(nativePath));
        var backup = Path.Combine(Path.GetTempPath(), "s2f-rs-prefs-" + Guid.NewGuid().ToString("N"));
        return (svc, store, sopPath, backup, nativePath);
    }

    private static void Cleanup(string sopPath, string? nativePath = null, params string?[] dirs)
    {
        try { File.Delete(sopPath); } catch { }
        if (nativePath is not null)
        {
            try { File.Delete(nativePath); } catch { }
        }
        foreach (var d in dirs)
        {
            if (d is null) continue;
            try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ApplySop_SetsAlternateFoldersAndOneMinuteAutoSave()
    {
        var (svc, store, sopPath, backup, nativePath) = CreateSvc();
        try
        {
            var result = svc.ApplySop(backup);

            Assert.True(result.Success);
            Assert.True(result.UpdatedCount > 0);
            var expected = RunScorePreferencesService.WithTrailingSeparator(backup);
            Assert.Equal(expected, store.TryGetString(RunScorePreferencesService.SaveEntriesDir));
            Assert.Equal(expected, store.TryGetString(RunScorePreferencesService.SaveResultsDir));
            Assert.Equal(RunScorePreferencesService.SopAutoSaveCounts, store.TryGetInt(RunScorePreferencesService.AutoSaveCounts));
            Assert.Equal(300, store.TryGetInt(RunScorePreferencesService.AutoSaveCounts));
            Assert.True(store.TryGetBool("Save all results"));
            Assert.True(store.TryGetBool("Always delete results"));
            Assert.False(store.TryGetBool("Always keep results"));
            Assert.Equal("English", store.TryGetString("Language"));
            Assert.Equal(51760, store.TryGetInt("TCP server port"));
        }
        finally
        {
            Cleanup(sopPath, nativePath, backup);
        }
    }

    [Fact]
    public void ApplySop_ThenRestore_ReturnsOfficeAlternateFolders()
    {
        var (svc, store, sopPath, backup, nativePath) = CreateSvc();
        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var raceBackup = Path.Combine(raceDay, PathService.BackupFolderName, "Memphis", "Event");
        try
        {
            store.SetString(RunScorePreferencesService.SaveEntriesDir, @"C:\Office\Entries\");
            store.SetString(RunScorePreferencesService.SaveResultsDir, @"C:\Office\Results\");
            store.SetInt(RunScorePreferencesService.AutoSaveCounts, 1500); // 5 min office habit

            Assert.True(svc.ApplySop(raceBackup, raceDay).Success);
            Assert.Equal(
                RunScorePreferencesService.WithTrailingSeparator(raceBackup),
                store.TryGetString(RunScorePreferencesService.SaveEntriesDir));

            var restore = svc.RestoreNativePrefs();
            Assert.True(restore.Success);
            Assert.Equal(@"C:\Office\Entries\", store.TryGetString(RunScorePreferencesService.SaveEntriesDir));
            Assert.Equal(@"C:\Office\Results\", store.TryGetString(RunScorePreferencesService.SaveResultsDir));
            Assert.Equal(1500, store.TryGetInt(RunScorePreferencesService.AutoSaveCounts));
        }
        finally
        {
            Cleanup(sopPath, nativePath, backup, raceBackup, raceDay);
        }
    }

    [Fact]
    public void ApplySop_SecondRace_DoesNotOverwriteOfficeSnapshot()
    {
        var (svc, store, sopPath, backup, nativePath) = CreateSvc();
        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var race1 = Path.Combine(raceDay, PathService.BackupFolderName, "A");
        var race2 = Path.Combine(raceDay, PathService.BackupFolderName, "B");
        try
        {
            store.SetString(RunScorePreferencesService.SaveEntriesDir, @"D:\Native\");
            store.SetString(RunScorePreferencesService.SaveResultsDir, @"D:\Native\");

            Assert.True(svc.ApplySop(race1, raceDay).Success);
            Assert.True(svc.ApplySop(race2, raceDay).Success); // still race paths — must not snapshot

            Assert.True(svc.RestoreNativePrefs().Success);
            Assert.Equal(@"D:\Native\", store.TryGetString(RunScorePreferencesService.SaveEntriesDir));
        }
        finally
        {
            Cleanup(sopPath, nativePath, backup, race1, race2, raceDay);
        }
    }

    [Fact]
    public void ApplySop_UsesCustomSopFile_AutoSaveMinutesAndBool()
    {
        var (svc, store, sopPath, backup, nativePath) = CreateSvc(p =>
        {
            p.AutoSaveMinutes = 5;
            p.Bools["Listings beep"] = true;
        });
        try
        {
            var result = svc.ApplySop(backup);
            Assert.True(result.Success);
            Assert.Equal(1500, store.TryGetInt(RunScorePreferencesService.AutoSaveCounts));
            Assert.True(store.TryGetBool("Listings beep"));
        }
        finally
        {
            Cleanup(sopPath, nativePath, backup);
        }
    }

    [Fact]
    public void ApplySop_Idempotent_WhenAlreadyMatching()
    {
        var (svc, _, sopPath, backup, nativePath) = CreateSvc();
        try
        {
            var first = svc.ApplySop(backup);
            var second = svc.ApplySop(backup);

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.Equal(0, second.UpdatedCount);
            Assert.True(second.AlreadyMatchedCount >= first.UpdatedCount);
            Assert.Contains("already match", second.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(sopPath, nativePath, backup);
        }
    }

    [Fact]
    public void ApplySop_UpdatesStaleAlternateFolderOnNewCheckout()
    {
        var (svc, store, sopPath, oldBackup, nativePath) = CreateSvc();
        var newBackup = Path.Combine(Path.GetTempPath(), "s2f-rs-new-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(svc.ApplySop(oldBackup).Success);
            Assert.Equal(
                RunScorePreferencesService.WithTrailingSeparator(oldBackup),
                store.TryGetString(RunScorePreferencesService.SaveEntriesDir));

            var result = svc.ApplySop(newBackup);

            Assert.True(result.Success);
            Assert.Contains(RunScorePreferencesService.SaveEntriesDir, result.UpdatedNames);
            Assert.Contains(RunScorePreferencesService.SaveResultsDir, result.UpdatedNames);
            Assert.Equal(
                RunScorePreferencesService.WithTrailingSeparator(newBackup),
                store.TryGetString(RunScorePreferencesService.SaveEntriesDir));
            Assert.Equal(
                RunScorePreferencesService.WithTrailingSeparator(newBackup),
                store.TryGetString(RunScorePreferencesService.SaveResultsDir));
        }
        finally
        {
            Cleanup(sopPath, nativePath, oldBackup, newBackup);
        }
    }

    [Fact]
    public void ApplySop_RewritesAlternateFolderMissingTrailingSeparator()
    {
        var (svc, store, sopPath, backup, nativePath) = CreateSvc();
        try
        {
            // First write sets full SOP (with trailing '\').
            Assert.True(svc.ApplySop(backup).Success);

            // Simulate RunScore / older builds stripping the trailing separator.
            store.SetString(RunScorePreferencesService.SaveEntriesDir, backup.TrimEnd('\\', '/'));
            store.SetString(RunScorePreferencesService.SaveResultsDir, backup.TrimEnd('\\', '/'));

            var result = svc.ApplySop(backup);

            Assert.True(result.Success);
            Assert.Contains(RunScorePreferencesService.SaveEntriesDir, result.UpdatedNames);
            Assert.Contains(RunScorePreferencesService.SaveResultsDir, result.UpdatedNames);
            var expected = RunScorePreferencesService.WithTrailingSeparator(backup);
            Assert.Equal(expected, store.TryGetString(RunScorePreferencesService.SaveEntriesDir));
            Assert.Equal(expected, store.TryGetString(RunScorePreferencesService.SaveResultsDir));
        }
        finally
        {
            Cleanup(sopPath, nativePath, backup);
        }
    }

    [Fact]
    public void BuildSop_ExpandsBackupPathToken()
    {
        var profile = RunScoreSopProfile.CreateDefaults();
        Assert.Equal(RunScoreSopProfile.BackupPathToken, profile.SaveEntriesDir);

        var prefs = RunScorePreferencesService.BuildSop(profile, @"C:\RaceDay\Backup\Memphis\Event");
        // Trailing backslash required — RunScore concatenates path + "ENTRIES.DTA".
        Assert.Equal(@"C:\RaceDay\Backup\Memphis\Event\",
            prefs.Single(p => p.Name == RunScorePreferencesService.SaveEntriesDir).StringValue);
        Assert.Equal(@"C:\RaceDay\Backup\Memphis\Event\",
            prefs.Single(p => p.Name == RunScorePreferencesService.SaveResultsDir).StringValue);
    }

    [Fact]
    public void BuildSop_KeepsTrailingSeparatorWhenAlreadyPresent()
    {
        var prefs = RunScorePreferencesService.BuildSop(@"C:\RaceDay\Backup\Memphis\Event\");
        Assert.Equal(@"C:\RaceDay\Backup\Memphis\Event\",
            prefs.Single(p => p.Name == RunScorePreferencesService.SaveEntriesDir).StringValue);
    }

    [Fact]
    public void BuildSop_AutoSaveCounts_IsMinutesTimes300()
    {
        Assert.Equal(1 * 300, RunScorePreferencesService.SopAutoSaveCounts);
        var prefs = RunScorePreferencesService.BuildSop(@"C:\RaceDay\Backup\Memphis\Event");
        var auto = prefs.Single(p => p.Name == RunScorePreferencesService.AutoSaveCounts);
        Assert.Equal(SopPreferenceKind.Int, auto.Kind);
        Assert.Equal(300, auto.IntValue);
    }
}
