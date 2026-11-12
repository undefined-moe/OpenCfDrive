using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Tests;

/// <summary>
/// The settings file the app writes and the service reads, and the order in
/// which a value is chosen: environment, then the file, then the default.
/// </summary>
public sealed class DriveSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opencfdrive-settings", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    [Fact]
    public void Load_WithoutAFile_IsEmpty()
    {
        var settings = DriveSettingsFile.Load(SettingsPath);

        Assert.Equal(new DriveSettings(), settings);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var saved = new DriveSettings { SyncRoot = @"D:\Drive", Remote = "box:backup", RcUrl = "http://127.0.0.1:6000", StartAtSignIn = true };

        DriveSettingsFile.Save(saved, SettingsPath);

        Assert.Equal(saved, DriveSettingsFile.Load(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Load_ABrokenFile_SaysWhichFile()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, "{ not json");

        var error = Assert.Throws<InvalidDataException>(() => DriveSettingsFile.Load(SettingsPath));
        Assert.Contains(SettingsPath, error.Message);
    }

    [Fact]
    public void Resolve_PrefersTheEnvironment_ThenTheFile_ThenTheDefault()
    {
        var saved = new DriveSettings { SyncRoot = @"D:\FromFile", Remote = "file:" };
        var environment = new Dictionary<string, string> { [DriveSettingsFile.RemoteVariable] = "env:" };

        var effective = DriveSettingsFile.Resolve(saved, name => environment.GetValueOrDefault(name));

        Assert.Equal(new EffectiveSetting("env:", SettingSource.Environment, "OPENCFDRIVE_REMOTE"), effective.Remote);
        Assert.Equal(new EffectiveSetting(@"D:\FromFile", SettingSource.SettingsFile, "OPENCFDRIVE_ROOT"), effective.SyncRoot);
        Assert.Equal(new EffectiveSetting(DriveSettingsFile.DefaultRcUrl, SettingSource.Default, "OPENCFDRIVE_RC_URL"), effective.RcUrl);
        Assert.True(effective.HasRemote);
    }

    [Fact]
    public void ALoginForAnRcloneTheUserRuns_IsSavedEncrypted_AndReadBack()
    {
        const string password = "correct horse battery staple";
        var saved = new DriveSettings
        {
            ManageRclone = false,
            RcUrl = "http://127.0.0.1:6000",
            RcUser = "me",
            RcPasswordProtected = UserSecret.Protect(password),
        };

        DriveSettingsFile.Save(saved, SettingsPath);
        var effective = DriveSettingsFile.Resolve(DriveSettingsFile.Load(SettingsPath), _ => null);

        // Never in the file, nor in anything printed from the settings.
        Assert.DoesNotContain(password, File.ReadAllText(SettingsPath));
        Assert.Equal(("me", password), (effective.RcUser, effective.RcPassword));
        Assert.DoesNotContain(password, effective.ToString());
        Assert.Equal("Basic", effective.RcEndpoint.Authorization?.Scheme);
    }

    [Fact]
    public void ALoginInTheEnvironment_ComesWholeFromThere()
    {
        var saved = new DriveSettings { RcUser = "file-user", RcPasswordProtected = UserSecret.Protect("file-password") };
        var environment = new Dictionary<string, string>
        {
            [DriveSettingsFile.RcUserVariable] = "env-user",
            [DriveSettingsFile.RcPasswordVariable] = "env-password",
        };

        var effective = DriveSettingsFile.Resolve(saved, name => environment.GetValueOrDefault(name));

        Assert.Equal(("env-user", "env-password"), (effective.RcUser, effective.RcPassword));
    }

    [Fact]
    public void WithoutALogin_NoneIsSent()
    {
        var effective = DriveSettingsFile.Resolve(new DriveSettings { RcUrl = "http://127.0.0.1:6000" }, _ => null);

        Assert.Null(effective.RcUser);
        Assert.Null(effective.RcEndpoint.Authorization);
    }

    [Fact]
    public void AnEncryptedSecret_ThatIsNotOurs_ReadsAsNone()
    {
        Assert.Equal("s3cret", UserSecret.Unprotect(UserSecret.Protect("s3cret")));
        Assert.Null(UserSecret.Unprotect("not base64!"));
        Assert.Null(UserSecret.Unprotect(Convert.ToBase64String("plain text"u8.ToArray())));
        Assert.Null(UserSecret.Unprotect(null));
    }

    [Fact]
    public void Resolve_WithNothingSet_HasNoRemote_AndNormalisesTheDefault()
    {
        var effective = DriveSettingsFile.Resolve(new DriveSettings(), _ => null);

        Assert.False(effective.HasRemote);
        Assert.Equal("e2e:", effective.Remote.Value);
        Assert.Equal(SyncStatePaths.DefaultSyncRoot, effective.SyncRoot.Value);
    }

    [Fact]
    public void Resolve_NormalisesTheRemote()
    {
        var effective = DriveSettingsFile.Resolve(new DriveSettings { Remote = " box:backup/ " }, _ => null);

        Assert.Equal("box:backup", effective.Remote.Value);
    }

    [Fact]
    public void Roots_RoundTrip()
    {
        var saved = new DriveSettings
        {
            Roots = [new DriveRoot(@"D:\One", "box:"), new DriveRoot(@"D:\Two", "s3:photos/")],
        };

        DriveSettingsFile.Save(saved, SettingsPath);

        Assert.Equal(saved.Roots, DriveSettingsFile.Load(SettingsPath).Roots);
    }

    [Fact]
    public void Resolve_WithSeveralRoots_ListsThemAll_AndLeadsWithTheFirst()
    {
        var saved = new DriveSettings
        {
            SyncRoot = @"D:\OldPlace",
            Remote = "old:",
            Roots = [new DriveRoot(@"D:\One", "box:"), new DriveRoot(@"D:\Two", "s3:photos/")],
        };

        var effective = DriveSettingsFile.Resolve(saved, _ => null);

        Assert.Equal(2, effective.Roots.Count);
        Assert.Equal(@"D:\One", effective.Roots[0].SyncRoot);
        Assert.Equal("box:", effective.Roots[0].Remote);
        Assert.Equal(@"D:\Two", effective.Roots[1].SyncRoot);
        Assert.Equal("s3:photos", effective.Roots[1].Remote); // normalised like the first
        // The first root is also what the single-drive places report.
        Assert.Equal(@"D:\One", effective.SyncRoot.Value);
        Assert.Equal("box:", effective.Remote.Value);
    }

    [Fact]
    public void Resolve_WithoutRoots_IsOneDrive_AsBefore()
    {
        var effective = DriveSettingsFile.Resolve(new DriveSettings { SyncRoot = @"D:\One", Remote = "box:" }, _ => null);

        var root = Assert.Single(effective.Roots);
        Assert.Equal(@"D:\One", root.SyncRoot);
        Assert.Equal("box:", root.Remote);
    }

    [Fact]
    public void Resolve_SkipsAnExtraRoot_WithoutARemote()
    {
        var saved = new DriveSettings
        {
            Roots = [new DriveRoot(@"D:\One", "box:"), new DriveRoot(@"D:\Two", null)],
        };

        var effective = DriveSettingsFile.Resolve(saved, _ => null);

        Assert.Single(effective.Roots);
    }

    [Fact]
    public void Resolve_TurnsTtlDays_IntoAFreeAge_PerDrive()
    {
        var saved = new DriveSettings
        {
            Roots = [new DriveRoot(@"D:\One", "box:", TtlDays: 30), new DriveRoot(@"D:\Two", "s3:", TtlDays: 0)],
        };

        var effective = DriveSettingsFile.Resolve(saved, _ => null);

        Assert.Equal(TimeSpan.FromDays(30), effective.Roots[0].FreeUnusedAfter);
        Assert.Null(effective.Roots[1].FreeUnusedAfter); // 0 means never
        Assert.Null(DriveSettingsFile.Resolve(new DriveSettings(), _ => null).Roots[0].FreeUnusedAfter);
    }

    [Fact]
    public void Roots_WithATtl_RoundTrip()
    {
        var saved = new DriveSettings
        {
            Roots = [new DriveRoot(@"D:\One", "box:", TtlDays: 14)],
        };

        DriveSettingsFile.Save(saved, SettingsPath);

        Assert.Equal(saved.Roots, DriveSettingsFile.Load(SettingsPath).Roots);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
