using System.Text.Json;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Core.Configuration;

/// <summary>One drive: the folder it lives in and the remote it syncs with.</summary>
/// <param name="SyncRoot">The drive folder.</param>
/// <param name="Remote">The rclone remote spec, e.g. "myremote:" or "myremote:backup".</param>
public sealed record DriveRoot(string SyncRoot, string? Remote);

/// <summary>
/// What the user chose in the OpenCfDrive app, kept in
/// %LOCALAPPDATA%\OpenCfDrive\settings.json. A null value was never set and
/// falls back to its default. The service reads this at startup; environment
/// variables still win over it (scripts and tests rely on that).
/// </summary>
public sealed record DriveSettings
{
    /// <summary>The drive folder.</summary>
    public string? SyncRoot { get; init; }

    /// <summary>The rclone remote spec, e.g. "myremote:" or "myremote:backup".</summary>
    public string? Remote { get; init; }

    /// <summary>
    /// Every drive when there is more than one; null and an empty list mean
    /// the one <see cref="SyncRoot"/> and <see cref="Remote"/> above describe.
    /// </summary>
    public IReadOnlyList<DriveRoot>? Roots { get; init; }

    /// <summary>
    /// Whether OpenCfDrive runs rclone's remote control itself (the default),
    /// or uses one the user runs at <see cref="RcUrl"/>.
    /// </summary>
    public bool? ManageRclone { get; init; }

    /// <summary>The rclone program OpenCfDrive runs; null finds one (see RcloneLocator).</summary>
    public string? RclonePath { get; init; }

    /// <summary>The rclone config file it runs with; null is rclone's own default.</summary>
    public string? RcloneConfig { get; init; }

    /// <summary>The base URL of an rclone remote control the user runs (rclone rcd).</summary>
    public string? RcUrl { get; init; }

    /// <summary>The login that remote control was started with (--rc-user).</summary>
    public string? RcUser { get; init; }

    /// <summary>Its password (--rc-pass), encrypted for this Windows user (see <see cref="UserSecret"/>).</summary>
    public string? RcPasswordProtected { get; init; }

    /// <summary>Whether the app starts when the user signs in to Windows.</summary>
    public bool? StartAtSignIn { get; init; }

    /// <summary>Whether the app checks for new versions on its own (the default) and downloads them.</summary>
    public bool? CheckForUpdates { get; init; }
}

public enum SettingSource
{
    Default,
    SettingsFile,
    Environment,
}

/// <summary>A setting's value in effect, and where it came from.</summary>
public sealed record EffectiveSetting(string Value, SettingSource Source, string EnvironmentVariable);

/// <summary>One drive the service runs with, in effect.</summary>
public sealed record EffectiveRoot(string SyncRoot, string Remote);

/// <summary>The settings the service runs with.</summary>
/// <param name="ManageRclone">OpenCfDrive runs rclone itself; otherwise it uses the one at <paramref name="RcUrl"/>.</param>
/// <param name="RclonePath">The rclone program chosen (environment or settings), or null to find one.</param>
/// <param name="RcloneConfig">The rclone config file chosen, or null for rclone's default.</param>
/// <param name="RcUser">The login for the remote control at <paramref name="RcUrl"/>, or null for none.</param>
/// <param name="RcPassword">Its password, in plain text: never logged or shown.</param>
/// <param name="MoreRoots">The drives after the first, when the user configured several; they always come from the settings file.</param>
public sealed record EffectiveSettings(
    EffectiveSetting SyncRoot,
    EffectiveSetting Remote,
    EffectiveSetting RcUrl,
    bool ManageRclone = true,
    string? RclonePath = null,
    string? RcloneConfig = null,
    string? RcUser = null,
    string? RcPassword = null,
    IReadOnlyList<EffectiveRoot>? MoreRoots = null)
{
    /// <summary>Every drive in effect, first the one <see cref="SyncRoot"/> and <see cref="Remote"/> describe.</summary>
    public IReadOnlyList<EffectiveRoot> Roots =>
        MoreRoots is { Count: > 0 } more ? [new(SyncRoot.Value, Remote.Value), .. more] : [new(SyncRoot.Value, Remote.Value)];

    /// <summary>A remote was chosen; without one the service has nothing to sync.</summary>
    public bool HasRemote => Remote.Source != SettingSource.Default;

    /// <summary>The remote control at <see cref="RcUrl"/>, with its login.</summary>
    public Services.RcloneEndpoint RcEndpoint => new(RcUrl.Value, RcUser, RcUser is null ? null : RcPassword ?? "");

    /// <summary>Never the password.</summary>
    public override string ToString() =>
        $"{nameof(EffectiveSettings)} {{ SyncRoot = {SyncRoot}, Remote = {Remote}, RcUrl = {RcUrl}, ManageRclone = {ManageRclone}, " +
        $"RclonePath = {RclonePath}, RcloneConfig = {RcloneConfig}, RcUser = {RcUser}, Roots = {Roots.Count} }}";
}

public static class DriveSettingsFile
{
    // None of these is RCLONE_...: rclone itself reads every RCLONE_<NAME>
    // variable as its --<name> flag, and the rclone the service runs inherits them.
    public const string SyncRootVariable = "OPENCFDRIVE_ROOT";
    public const string RemoteVariable = "OPENCFDRIVE_REMOTE";
    public const string RcUrlVariable = "OPENCFDRIVE_RC_URL";

    /// <summary>The login for the remote control at <see cref="RcUrlVariable"/> (scripts, tests).</summary>
    public const string RcUserVariable = "OPENCFDRIVE_RC_USER";
    public const string RcPasswordVariable = "OPENCFDRIVE_RC_PASS";

    /// <summary>The names these had before, no longer read, and what replaced them.</summary>
    public static readonly IReadOnlyDictionary<string, string> RetiredVariables = new Dictionary<string, string>
    {
        ["RCLONE_FS"] = RemoteVariable,
        ["RCLONE_RC_URL"] = RcUrlVariable,
    };

    /// <summary>Points the service and the app at another settings file (tests, side-by-side runs).</summary>
    public const string PathVariable = "OPENCFDRIVE_SETTINGS";

    /// <summary>The rclone program to run, ahead of the settings file.</summary>
    public const string RclonePathVariable = "OPENCFDRIVE_RCLONE";

    /// <summary>Used when no remote is configured (the name the tests give their remote).</summary>
    public const string DefaultRemote = "e2e:";

    public const string DefaultRcUrl = "http://127.0.0.1:5572";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>%LOCALAPPDATA%\OpenCfDrive\settings.json, unless OPENCFDRIVE_SETTINGS says otherwise.</summary>
    public static string Path
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(PathVariable);
            return string.IsNullOrWhiteSpace(overridden)
                ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SyncStatePaths.StateRoot)!, "settings.json")
                : overridden;
        }
    }

    /// <summary>The saved settings; empty when there is no file yet.</summary>
    /// <exception cref="InvalidDataException">The file is not valid settings JSON.</exception>
    public static DriveSettings Load(string? path = null)
    {
        path ??= Path;
        if (!File.Exists(path))
        {
            return new DriveSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<DriveSettings>(File.ReadAllText(path), Json) ?? new DriveSettings();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{path}' is not a valid settings file: {ex.Message}", ex);
        }
    }

    /// <summary>Writes the settings in one step, so the service never reads half a file.</summary>
    public static void Save(DriveSettings settings, string? path = null)
    {
        path ??= Path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Each setting from the environment, else the settings file, else its default.</summary>
    public static EffectiveSettings Resolve(DriveSettings saved, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        EffectiveSetting Pick(string variable, string? fromFile, string fallback)
        {
            var fromEnvironment = environment(variable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return new EffectiveSetting(fromEnvironment.Trim(), SettingSource.Environment, variable);
            }

            return string.IsNullOrWhiteSpace(fromFile)
                ? new EffectiveSetting(fallback, SettingSource.Default, variable)
                : new EffectiveSetting(fromFile.Trim(), SettingSource.SettingsFile, variable);
        }

        var remote = Pick(RemoteVariable, FirstOf(saved.Roots)?.Remote ?? saved.Remote, DefaultRemote);
        var rcUrl = Pick(RcUrlVariable, saved.RcUrl, DefaultRcUrl);
        var rclonePath = environment(RclonePathVariable);

        // A login set in the environment comes whole from there: never one
        // part from the environment and the other from the file.
        var (rcUser, rcPassword) = Blank(environment(RcUserVariable)) is { } envUser
            ? (envUser, environment(RcPasswordVariable))
            : (Blank(saved.RcUser), UserSecret.Unprotect(saved.RcPasswordProtected));

        // The drives after the first come from the file only: the environment
        // names one drive (the first), and a root without a folder or a
        // remote cannot sync, so it is left out rather than guessed at.
        var moreRoots = saved.Roots is { Count: > 1 } roots
            ? roots.Skip(1)
                .Where(root => !string.IsNullOrWhiteSpace(root.SyncRoot) && !string.IsNullOrWhiteSpace(root.Remote))
                .Select(root => new EffectiveRoot(root.SyncRoot.Trim(), SyncStatePaths.NormalizeRemote(root.Remote)))
                .ToArray()
            : null;
        return new EffectiveSettings(
            Pick(SyncRootVariable, FirstOf(saved.Roots)?.SyncRoot ?? saved.SyncRoot, SyncStatePaths.DefaultSyncRoot),
            remote with { Value = SyncStatePaths.NormalizeRemote(remote.Value) },
            rcUrl,
            // A remote control address in the environment (scripts, tests)
            // means that rclone, not one of OpenCfDrive's own.
            ManageRclone: rcUrl.Source != SettingSource.Environment && saved.ManageRclone != false,
            RclonePath: string.IsNullOrWhiteSpace(rclonePath) ? Blank(saved.RclonePath) : rclonePath.Trim(),
            RcloneConfig: Blank(saved.RcloneConfig),
            RcUser: rcUser,
            RcPassword: rcUser is null ? null : rcPassword,
            MoreRoots: moreRoots);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DriveRoot? FirstOf(IReadOnlyList<DriveRoot>? roots) =>
        roots is { Count: > 0 } ? roots[0] : null;

    /// <summary>The settings the service runs with right now (a broken file counts as empty).</summary>
    public static EffectiveSettings Current()
    {
        try
        {
            return Resolve(Load());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Resolve(new DriveSettings());
        }
    }
}
