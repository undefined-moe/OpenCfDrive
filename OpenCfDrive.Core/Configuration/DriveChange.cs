using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Core.Configuration;

/// <summary>What a folder's state database says: the remote it belongs to and how many items it tracks.</summary>
public sealed record DriveState(string? Remote, int Items);

/// <summary>
/// What saving a new drive folder or remote involves, or why it is refused.
/// A drive folder belongs to one remote: its placeholders and state describe
/// that remote's files. Pointing it at another remote would make those look
/// deleted on the server (and remove them locally), and make the downloaded
/// ones look like new local files (and upload them). So a new folder or a new
/// remote needs a folder that is empty, or that was this same remote's drive
/// before.
/// </summary>
public sealed record DriveChange(
    bool Allowed,
    string? Problem,
    string NewSyncRoot,
    string NewRemote,
    string? OldSyncRootToUnregister,
    bool DiscardOldState)
{
    public bool ChangesDrive => OldSyncRootToUnregister is not null || DiscardOldState;

    /// <param name="isSyncRoot">Whether a folder is a registered drive (see SyncRootRegistrar.IsSyncRoot).</param>
    /// <param name="stateOf">A folder's state database, or null if it has none.</param>
    public static DriveChange Check(
        string currentSyncRoot,
        string? currentRemote,
        string newSyncRoot,
        string newRemote,
        Func<string, bool> isSyncRoot,
        Func<string, DriveState?> stateOf)
    {
        var oldRoot = Normalize(currentSyncRoot);
        var root = Normalize(newSyncRoot);
        var remote = SyncStatePaths.NormalizeRemote(newRemote);
        var sameRoot = string.Equals(oldRoot, root, StringComparison.OrdinalIgnoreCase);
        var sameRemote = currentRemote is not null &&
                         string.Equals(SyncStatePaths.NormalizeRemote(currentRemote), remote, StringComparison.Ordinal);

        if (sameRoot && sameRemote)
        {
            return new DriveChange(true, null, root, remote, null, false);
        }

        var state = stateOf(root);
        var formerDriveOfThisRemote = state is not null && string.Equals(state.Remote, remote, StringComparison.Ordinal);

        // A registered drive is not listed (that would ask its provider to fill
        // it): it is empty when its state tracks nothing, so it holds no
        // placeholders either.
        var empty = !Directory.Exists(root) ||
                    (isSyncRoot(root)
                        ? state is null || state.Items == 0
                        : !Directory.EnumerateFileSystemEntries(root).Any());

        if (!empty && !(formerDriveOfThisRemote && !sameRoot))
        {
            var problem = sameRoot && currentRemote is not null
                ? $"To use another remote, choose a new, empty drive folder. '{root}' is already the drive " +
                  $"for {SyncStatePaths.NormalizeRemote(currentRemote)}."
                : $"'{root}' is not empty. Choose a new or empty folder for the drive.";
            return new DriveChange(false, problem, root, remote, null, false);
        }

        return new DriveChange(
            true,
            null,
            root,
            remote,
            sameRoot ? null : oldRoot,
            DiscardOldState: state is not null && !formerDriveOfThisRemote);
    }

    /// <summary>A folder's state database, or null if it has none (or it cannot be read).</summary>
    public static DriveState? StateOf(string syncRoot)
    {
        var path = SyncStatePaths.DatabasePath(syncRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var store = new SqliteMetadataStore(path);
            var remote = store.GetConfig("RcloneFs");
            return new DriveState(string.IsNullOrWhiteSpace(remote) ? null : remote, store.Count());
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    /// <summary>For the app: the real checks against this machine.</summary>
    public static DriveChange Check(string currentSyncRoot, string? currentRemote, string newSyncRoot, string newRemote) =>
        Check(currentSyncRoot, currentRemote, newSyncRoot, newRemote, SyncRootRegistrar.IsSyncRoot, StateOf);

    private static string Normalize(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
}
