using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class RunScoreVendorSecretsTests
{
    [Fact]
    public void TryParse_TemplateKeys_RoundTrips()
    {
        const string json = """
            {
              "smsLogin": "AC123",
              "smsPassword": "tok",
              "raceRosterLogin": "rr@x.com",
              "raceRosterPassword": "rrPass",
              "runSignUpLogin": "rsu@x.com",
              "runSignUpPassword": "rsuPass"
            }
            """;

        Assert.True(RunScoreVendorSecrets.TryParse(json, out var secrets, out var error));
        Assert.Null(error);
        Assert.Equal("AC123", secrets.SmsLogin);
        Assert.Equal("tok", secrets.SmsPassword);
        Assert.Equal("rr@x.com", secrets.RaceRosterLogin);
        Assert.Equal("rrPass", secrets.RaceRosterPassword);
        Assert.Equal("rsu@x.com", secrets.RunSignUpLogin);
        Assert.Equal("rsuPass", secrets.RunSignUpPassword);
        Assert.True(secrets.HasAnyCompletePair);
    }

    [Fact]
    public void TryParse_Invalid_ReturnsFalse()
    {
        Assert.False(RunScoreVendorSecrets.TryParse("{not json", out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Store_SaveLoad_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-vendor-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new RunScoreVendorSecretsStore(path);
            Assert.False(store.HasCache);
            store.Save(new RunScoreVendorSecrets
            {
                SmsLogin = "AC",
                SmsPassword = "secret"
            });
            Assert.True(store.HasCache);
            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal("AC", loaded!.SmsLogin);
            Assert.Equal("secret", loaded.SmsPassword);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void EnsureVendorCredentials_WritesCompletePairs_SkipsEmpty()
    {
        var vendorPath = Path.Combine(Path.GetTempPath(), "s2f-vendor-" + Guid.NewGuid().ToString("N") + ".json");
        var sopPath = Path.Combine(Path.GetTempPath(), "s2f-sop-" + Guid.NewGuid().ToString("N") + ".json");
        var nativePath = Path.Combine(Path.GetTempPath(), "s2f-native-" + Guid.NewGuid().ToString("N") + ".json");
        var backup = Path.Combine(Path.GetTempPath(), "s2f-rs-prefs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vendorStore = new RunScoreVendorSecretsStore(vendorPath);
            vendorStore.Save(new RunScoreVendorSecrets
            {
                SmsLogin = "AC-sms",
                SmsPassword = "SmsSecret",
                RaceRosterLogin = "rr@x.com",
                // password missing — skip RaceRoster pair
                RunSignUpLogin = "rsu@x.com",
                RunSignUpPassword = "RsuSecret"
            });

            var sopStore = new RunScoreSopStore(sopPath);
            sopStore.Save(RunScoreSopProfile.CreateDefaults());
            var store = new MemoryRunScorePreferenceStore();
            var svc = new RunScorePreferencesService(store, sopStore, new NativeRunScorePrefsStore(nativePath), vendorStore);

            var none = new VendorCredentialOverrides();
            var pin = svc.EnsureVendorCredentials(overrides: none);
            Assert.True(pin.Success);
            Assert.Equal("AC-sms", store.TryGetString(RunScorePreferencesService.SmsLogin));
            Assert.Equal("SmsSecret", store.TryGetString(RunScorePreferencesService.SmsPassword));
            Assert.Null(store.TryGetString(RunScorePreferencesService.RaceRosterLogin));
            Assert.Equal("rsu@x.com", store.TryGetString(RunScorePreferencesService.RunSignUpLogin));
            Assert.Equal("RsuSecret", store.TryGetString(RunScorePreferencesService.RunSignUpPassword));

            // Case-sensitive: lowercase password must update
            store.SetString(RunScorePreferencesService.SmsPassword, "smssecret");
            var again = svc.EnsureVendorCredentials(overrides: none);
            Assert.Contains(RunScorePreferencesService.SmsPassword, again.UpdatedNames);
            Assert.Equal("SmsSecret", store.TryGetString(RunScorePreferencesService.SmsPassword));

            var apply = svc.ApplySop(backup);
            Assert.True(apply.Success);
            Assert.Equal("AC-sms", store.TryGetString(RunScorePreferencesService.SmsLogin));
        }
        finally
        {
            try { File.Delete(vendorPath); } catch { }
            try { File.Delete(sopPath); } catch { }
            try { File.Delete(nativePath); } catch { }
            try { Directory.Delete(backup, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureVendorCredentials_EmptyCache_LeavesRegistryAlone()
    {
        var vendorPath = Path.Combine(Path.GetTempPath(), "s2f-vendor-" + Guid.NewGuid().ToString("N") + ".json");
        var sopPath = Path.Combine(Path.GetTempPath(), "s2f-sop-" + Guid.NewGuid().ToString("N") + ".json");
        var nativePath = Path.Combine(Path.GetTempPath(), "s2f-native-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new MemoryRunScorePreferenceStore();
            store.SetString(RunScorePreferencesService.SmsLogin, "keep-me");
            var svc = new RunScorePreferencesService(
                store,
                new RunScoreSopStore(sopPath),
                new NativeRunScorePrefsStore(nativePath),
                new RunScoreVendorSecretsStore(vendorPath));

            var pin = svc.EnsureVendorCredentials(overrides: new VendorCredentialOverrides());
            Assert.True(pin.Success);
            Assert.Equal(0, pin.UpdatedCount);
            Assert.Equal("keep-me", store.TryGetString(RunScorePreferencesService.SmsLogin));
        }
        finally
        {
            try { File.Delete(vendorPath); } catch { }
            try { File.Delete(sopPath); } catch { }
            try { File.Delete(nativePath); } catch { }
        }
    }

    [Fact]
    public void EnsureVendorCredentials_CustomRaceRosterLogin_NotOverwrittenBySecrets()
    {
        var vendorPath = Path.Combine(Path.GetTempPath(), "s2f-vendor-" + Guid.NewGuid().ToString("N") + ".json");
        var sopPath = Path.Combine(Path.GetTempPath(), "s2f-sop-" + Guid.NewGuid().ToString("N") + ".json");
        var nativePath = Path.Combine(Path.GetTempPath(), "s2f-native-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vendorStore = new RunScoreVendorSecretsStore(vendorPath);
            vendorStore.Save(new RunScoreVendorSecrets
            {
                SmsLogin = "AC",
                SmsPassword = "tok",
                RaceRosterLogin = "company@rr.com",
                RaceRosterPassword = "companyRrPass",
                RunSignUpLogin = "company@rsu.com",
                RunSignUpPassword = "companyRsuPass"
            });

            var store = new MemoryRunScorePreferenceStore();
            store.SetString(RunScorePreferencesService.RaceRosterPassword, "keep-existing-rr-pass");
            var svc = new RunScorePreferencesService(
                store,
                new RunScoreSopStore(sopPath),
                new NativeRunScorePrefsStore(nativePath),
                vendorStore);

            var ovr = new VendorCredentialOverrides
            {
                RaceRosterLogin = "custom@rr.com",
                RaceRosterPassword = "", // blank → keep registry password
                RunSignUpLogin = "", // blank → use company secrets
                RunSignUpPassword = ""
            };

            var pin = svc.EnsureVendorCredentials(overrides: ovr);
            Assert.True(pin.Success);
            Assert.Equal("custom@rr.com", store.TryGetString(RunScorePreferencesService.RaceRosterLogin));
            Assert.Equal("keep-existing-rr-pass", store.TryGetString(RunScorePreferencesService.RaceRosterPassword));
            Assert.Equal("company@rsu.com", store.TryGetString(RunScorePreferencesService.RunSignUpLogin));
            Assert.Equal("companyRsuPass", store.TryGetString(RunScorePreferencesService.RunSignUpPassword));
            Assert.Equal("AC", store.TryGetString(RunScorePreferencesService.SmsLogin));

            // Second pull with company secrets must not clobber custom RaceRoster login
            var again = svc.EnsureVendorCredentials(overrides: ovr);
            Assert.Equal("custom@rr.com", store.TryGetString(RunScorePreferencesService.RaceRosterLogin));
            Assert.Equal(0, again.UpdatedNames.Count(n =>
                n.Equals(RunScorePreferencesService.RaceRosterLogin, StringComparison.OrdinalIgnoreCase)
                || n.Equals(RunScorePreferencesService.RaceRosterPassword, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            try { File.Delete(vendorPath); } catch { }
            try { File.Delete(sopPath); } catch { }
            try { File.Delete(nativePath); } catch { }
        }
    }

    [Fact]
    public void EnsureVendorCredentials_CustomRunSignUp_WithPassword_PinsBoth()
    {
        var vendorPath = Path.Combine(Path.GetTempPath(), "s2f-vendor-" + Guid.NewGuid().ToString("N") + ".json");
        var sopPath = Path.Combine(Path.GetTempPath(), "s2f-sop-" + Guid.NewGuid().ToString("N") + ".json");
        var nativePath = Path.Combine(Path.GetTempPath(), "s2f-native-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vendorStore = new RunScoreVendorSecretsStore(vendorPath);
            vendorStore.Save(new RunScoreVendorSecrets
            {
                RunSignUpLogin = "company@rsu.com",
                RunSignUpPassword = "companyPass"
            });

            var store = new MemoryRunScorePreferenceStore();
            var svc = new RunScorePreferencesService(
                store,
                new RunScoreSopStore(sopPath),
                new NativeRunScorePrefsStore(nativePath),
                vendorStore);

            var pin = svc.EnsureVendorCredentials(overrides: new VendorCredentialOverrides
            {
                RunSignUpLogin = "timer@office.com",
                RunSignUpPassword = "officePass"
            });

            Assert.Equal("timer@office.com", store.TryGetString(RunScorePreferencesService.RunSignUpLogin));
            Assert.Equal("officePass", store.TryGetString(RunScorePreferencesService.RunSignUpPassword));
            Assert.Null(store.TryGetString(RunScorePreferencesService.RaceRosterLogin));
        }
        finally
        {
            try { File.Delete(vendorPath); } catch { }
            try { File.Delete(sopPath); } catch { }
            try { File.Delete(nativePath); } catch { }
        }
    }

    [Fact]
    public void RestoreNativePrefs_RePinsVendorCredentials_DoesNotRestoreStaleSms()
    {
        var vendorPath = Path.Combine(Path.GetTempPath(), "s2f-vendor-" + Guid.NewGuid().ToString("N") + ".json");
        var sopPath = Path.Combine(Path.GetTempPath(), "s2f-sop-" + Guid.NewGuid().ToString("N") + ".json");
        var nativePath = Path.Combine(Path.GetTempPath(), "s2f-native-" + Guid.NewGuid().ToString("N") + ".json");
        var backup = Path.Combine(Path.GetTempPath(), "s2f-rs-prefs-" + Guid.NewGuid().ToString("N"));
        var npp = Path.Combine(Path.GetTempPath(), "s2f-npp-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(npp, "x");
            var vendorStore = new RunScoreVendorSecretsStore(vendorPath);
            vendorStore.Save(new RunScoreVendorSecrets
            {
                SmsLogin = "company-sms",
                SmsPassword = "company-pass"
            });

            var sopStore = new RunScoreSopStore(sopPath);
            sopStore.Save(RunScoreSopProfile.CreateDefaults());
            var store = new MemoryRunScorePreferenceStore();
            store.SetString(RunScorePreferencesService.SaveEntriesDir, @"C:\Office\Entries\");
            store.SetString(RunScorePreferencesService.SaveResultsDir, @"C:\Office\Results\");
            // Stale office SMS that must not win over company cache after Finish
            store.SetString(RunScorePreferencesService.SmsLogin, "old-office");
            store.SetString(RunScorePreferencesService.SmsPassword, "old-pass");

            var nativeStore = new NativeRunScorePrefsStore(nativePath);
            nativeStore.Save(new NativeRunScorePrefsSnapshot
            {
                CapturedAt = DateTimeOffset.UtcNow,
                Strings =
                {
                    [RunScorePreferencesService.SaveEntriesDir] = @"C:\Office\Entries\",
                    [RunScorePreferencesService.SaveResultsDir] = @"C:\Office\Results\",
                    [RunScorePreferencesService.SmsLogin] = "old-office",
                    [RunScorePreferencesService.SmsPassword] = "old-pass"
                }
            });

            var svc = new RunScorePreferencesService(store, sopStore, nativeStore, vendorStore);
            // Simulate race-day apply (sets Backup + company SMS)
            Assert.True(svc.ApplySop(backup).Success);
            Assert.Equal("company-sms", store.TryGetString(RunScorePreferencesService.SmsLogin));

            var restore = svc.RestoreNativePrefs(npp, File.Exists);
            Assert.True(restore.Success);
            Assert.Equal(@"C:\Office\Entries\", store.TryGetString(RunScorePreferencesService.SaveEntriesDir));
            Assert.Equal("company-sms", store.TryGetString(RunScorePreferencesService.SmsLogin));
            Assert.Equal("company-pass", store.TryGetString(RunScorePreferencesService.SmsPassword));
        }
        finally
        {
            try { File.Delete(vendorPath); } catch { }
            try { File.Delete(sopPath); } catch { }
            try { File.Delete(nativePath); } catch { }
            try { File.Delete(npp); } catch { }
            try { Directory.Delete(backup, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SopStore_Save_StripsVendorCredentialKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-sop-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var profile = RunScoreSopProfile.CreateDefaults();
            profile.Strings[RunScorePreferencesService.SmsLogin] = "should-not-persist";
            profile.Strings[RunScorePreferencesService.SmsPassword] = "nope";
            var store = new RunScoreSopStore(path);
            store.Save(profile);

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("SMS login", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("should-not-persist", json, StringComparison.Ordinal);

            var loaded = store.Load();
            Assert.False(loaded.Profile.Strings.ContainsKey(RunScorePreferencesService.SmsLogin));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void BuildSop_IgnoresVendorKeysInProfileStrings()
    {
        var profile = RunScoreSopProfile.CreateDefaults();
        profile.Strings[RunScorePreferencesService.SmsLogin] = "leak";
        var prefs = RunScorePreferencesService.BuildSop(profile, @"C:\Backup");
        Assert.DoesNotContain(prefs, p => p.Name == RunScorePreferencesService.SmsLogin);
    }

    [Fact]
    public async Task NullCloud_SyncVendorSecrets_NotAvailable()
    {
        var cloud = new NullCloudLibrarySync();
        var result = await cloud.SyncRunScoreVendorSecretsAsync("");
        Assert.False(result.Success);
    }
}
