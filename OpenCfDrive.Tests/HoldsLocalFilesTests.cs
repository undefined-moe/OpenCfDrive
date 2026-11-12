using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Tests;

/// <summary>
/// Which listed folders the background sync keeps listing every run: those
/// with something local to protect (a downloaded file, a pinned folder, or an
/// edited file), and the root; the rest are online-only and are listed when
/// they are opened.
/// </summary>
public class HoldsLocalFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opencfdrive-holds-local", Guid.NewGuid().ToString("N"));
    private readonly SqliteMetadataStore _store;

    public HoldsLocalFilesTests()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteMetadataStore(Path.Combine(_root, "state.db"));
        _store.Initialize();
    }

    // What the attributes say about a file's data: all on the server
    // (RECALL_ON_DATA_ACCESS set), or here (downloaded, edited or plain).
    private static bool OnlineOnly(string path) => false;

    private static bool Edited(string path) => true;

    [Fact]
    public void TheRootIsAlwaysLocal() =>
        Assert.True(CloudSyncEngine.HoldsLocalFiles(_store, _root, string.Empty));

    [Fact]
    public void AHydratedFileMakesItsFolderLocal()
    {
        var directory = Path.Combine(_root, "hydrated");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "a.txt");
        File.WriteAllText(file, "z");
        _store.UpsertFile("hydrated/a.txt", file, "hydrated/a.txt", 0, isPlaceholder: true, isDirectory: false, size: 1, modifiedUtc: DateTimeOffset.UtcNow, inSync: true);
        _store.MarkHydrated("hydrated/a.txt");

        Assert.True(CloudSyncEngine.HoldsLocalFiles(_store, _root, "hydrated", hasLocalData: OnlineOnly));
    }

    [Fact]
    public void APinnedFolderIsLocal()
    {
        var directory = Path.Combine(_root, "pinned");
        Directory.CreateDirectory(directory);
        File.SetAttributes(directory, File.GetAttributes(directory) | (FileAttributes)0x00080000);

        Assert.True(CloudSyncEngine.HoldsLocalFiles(_store, _root, "pinned", hasLocalData: OnlineOnly));
    }

    [Fact]
    public void AFileEditedSinceItsLastSyncMakesItsFolderLocal()
    {
        var directory = Path.Combine(_root, "edited");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "a.txt");
        File.WriteAllText(file, "z");
        _store.UpsertFile("edited/a.txt", file, "edited/a.txt", 0, isPlaceholder: true, isDirectory: false, size: 1, modifiedUtc: DateTimeOffset.UtcNow, inSync: true);

        Assert.True(CloudSyncEngine.HoldsLocalFiles(_store, _root, "edited", hasLocalData: Edited));
    }

    [Fact]
    public void AFolderOfOnlineOnlyPlaceholdersIsNotLocal()
    {
        var directory = Path.Combine(_root, "online");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "a.txt");
        File.WriteAllText(file, "z");
        var modified = DateTimeOffset.UtcNow;
        var info = new FileInfo(file);
        _store.UpsertFile("online/a.txt", file, "online/a.txt", 0, isPlaceholder: true, isDirectory: false, size: info.Length, modifiedUtc: modified, inSync: true, localSize: info.Length, localWriteUtc: info.LastWriteTimeUtc);

        Assert.False(CloudSyncEngine.HoldsLocalFiles(_store, _root, "online", hasLocalData: OnlineOnly));
    }

    [Fact]
    public void ADownloadedFileDeepBelow_MakesEveryFolderAboveItLocal_ButNotItsSibling()
    {
        var deep = Path.Combine(_root, "a", "b", "c");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(_root, "a", "sibling"));
        var file = Path.Combine(deep, "f.txt");
        File.WriteAllText(file, "z");
        var other = Path.Combine(_root, "a", "sibling", "g.txt");
        File.WriteAllText(other, "z");
        _store.UpsertFile("a/b/c/f.txt", file, "a/b/c/f.txt", 0, isPlaceholder: true, isDirectory: false, size: 1, modifiedUtc: DateTimeOffset.UtcNow, inSync: true);
        _store.UpsertFile("a/sibling/g.txt", other, "a/sibling/g.txt", 0, isPlaceholder: true, isDirectory: false, size: 1, modifiedUtc: DateTimeOffset.UtcNow, inSync: true);

        // Only f.txt's data is here.
        var local = CloudSyncEngine.LocalDirectories(_store, _root, simulated: false, hasLocalData: path => path == file);

        Assert.Equal(["", "a", "a/b", "a/b/c"], local.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ByItsAttributes_APlainFileHoldsLocalData_AndAMissingOneDoesNot()
    {
        var file = Path.Combine(_root, "plain.txt");
        File.WriteAllText(file, "z");

        Assert.True(CloudPlaceholderService.HasLocalData(file));
        Assert.False(CloudPlaceholderService.HasLocalData(Path.Combine(_root, "missing.txt")));
        Assert.False(CloudPlaceholderService.HasLocalData(_root));
    }

    [Fact]
    public void APlainFileTheStoreDoesNotKnowMakesItsFolderLocal()
    {
        var directory = Path.Combine(_root, "plain");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "new.txt"), "z");

        Assert.True(CloudSyncEngine.HoldsLocalFiles(_store, _root, "plain", simulated: true));
    }

    // --- what the "free up unused" pass may free ----------------------------

    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    [Fact]
    public void ALocalFileUnopenedForTheConfiguredDays_MayBeFreed()
    {
        var now = DateTime.UtcNow;
        Assert.True(CloudSyncEngine.ShouldFreeUnused(
            FileAttributes.Archive, now.AddDays(-31), now.AddDays(-40), now, TimeSpan.FromDays(30)));
        // The write, not the open, is the more recent use here.
        Assert.False(CloudSyncEngine.ShouldFreeUnused(
            FileAttributes.Archive, now.AddDays(-31), now.AddDays(-3), now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void APlaceholderOrAPinnedFile_IsNeverFreed()
    {
        var now = DateTime.UtcNow;
        var old = now.AddDays(-365);
        Assert.False(CloudSyncEngine.ShouldFreeUnused(
            RecallOnDataAccess | FileAttributes.Archive, old, old, now, TimeSpan.FromDays(30)));
        Assert.False(CloudSyncEngine.ShouldFreeUnused(
            Pinned | FileAttributes.Archive, old, old, now, TimeSpan.FromDays(30)));
    }

    public void Dispose()
    {
        SqliteMetadataStore.Delete(_store.DbPath);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // ignored
        }
    }
}
