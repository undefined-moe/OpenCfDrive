using Microsoft.Data.Sqlite;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Tests;

public class SqliteMetadataStoreTests
{
    private static string TempDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "opencfdrive-db", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test.db");
    }

    [Fact]
    public void Initialize_CreatesSchema()
    {
        var path = TempDbPath();
        var store = new SqliteMetadataStore(path);
        store.Initialize();

        Assert.True(File.Exists(path));

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('Files');";
        using var reader = command.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Contains("RemotePath", columns);
        Assert.Contains("FileIdentity", columns);
        Assert.Contains("FileId", columns);
        Assert.Contains("IsHydrated", columns);
        Assert.Contains("InSync", columns);
    }

    [Fact]
    public void Delete_RemovesADatabaseThatWasJustRead()
    {
        var path = TempDbPath();
        var store = new SqliteMetadataStore(path);
        store.Initialize();
        store.SetConfig("RcloneFs", "box:");
        Assert.Equal("box:", store.GetConfig("RcloneFs"));
        Assert.Equal(0, store.Count());

        SqliteMetadataStore.Delete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SyncedLocalState_IsTheServersUntilAnUploadRecordsTheFilesOwn()
    {
        var store = new SqliteMetadataStore(TempDbPath());
        store.Initialize();
        var serverTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567);

        // A placeholder made from the listing has the server's size and time.
        store.UpsertFile("a.txt", @"C:\drive\a.txt", "a.txt", 0, true, false, 10, serverTime, inSync: true);
        Assert.Equal<(long, DateTime)?>((10, serverTime.UtcDateTime), store.GetByRemotePath("a.txt")!.SyncedLocalState);

        // An upload records the file's own: the server stamps its time.
        var written = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(42);
        store.UpsertFile("a.txt", @"C:\drive\a.txt", "a.txt", 0, true, false, 12, serverTime.AddMinutes(1), inSync: true,
            localSize: 11, localWriteUtc: written);
        Assert.Equal<(long, DateTime)?>((11, written), store.GetByRemotePath("a.txt")!.SyncedLocalState);

        // A rename keeps it; a server update gives the file the server's again.
        store.RenameRemotePath("a.txt", "b.txt", @"C:\drive\b.txt", "b.txt");
        Assert.Equal<(long, DateTime)?>((11, written), store.GetByRemotePath("b.txt")!.SyncedLocalState);
        store.UpdateRemoteMetadata("b.txt", 20, serverTime.AddMinutes(2), dehydrated: true);
        Assert.Equal<(long, DateTime)?>((20, serverTime.AddMinutes(2).UtcDateTime), store.GetByRemotePath("b.txt")!.SyncedLocalState);

        // Without a server time there is nothing to compare with.
        store.UpsertFile("c.txt", @"C:\drive\c.txt", "c.txt", 0, true, false, 5, null, inSync: true);
        Assert.Null(store.GetByRemotePath("c.txt")!.SyncedLocalState);
    }

    [Fact]
    public void PendingChanges_AreKeptInOrder_OnceEach_AcrossReopening()
    {
        var path = TempDbPath();
        var store = new SqliteMetadataStore(path);
        store.Initialize();

        var delete = store.AddPendingChange(PendingChangeKind.Delete, "a.txt", @"C:\drive\a.txt");
        var rename = store.AddPendingChange(PendingChangeKind.Rename, "old/", @"C:\drive\old", @"C:\drive\new");
        Assert.Equal(delete, store.AddPendingChange(PendingChangeKind.Delete, "a.txt", @"C:\drive\a.txt"));
        Assert.NotEqual(delete, rename);

        var reopened = new SqliteMetadataStore(path);
        reopened.Initialize();
        var changes = reopened.GetPendingChanges();
        Assert.Equal([delete, rename], changes.Select(c => c.Id));
        Assert.Collection(
            changes,
            delete => Assert.Equal((PendingChangeKind.Delete, "a.txt", (string?)null), (delete.Kind, delete.RemotePath, delete.NewLocalPath)),
            rename => Assert.Equal((PendingChangeKind.Rename, "old/", @"C:\drive\new"), (rename.Kind, rename.RemotePath, rename.NewLocalPath)));

        reopened.RemovePendingChange(changes[0].Id);
        Assert.Equal("old/", Assert.Single(reopened.GetPendingChanges()).RemotePath);
    }

    [Fact]
    public void AListedMark_StaysWithTheFolder_ThroughAnUpsertAndARename()
    {
        var store = new SqliteMetadataStore(TempDbPath());
        store.Initialize();
        var now = DateTimeOffset.UtcNow;
        store.UpsertFile("docs/", @"C:\drive\docs", "docs/", 0, isPlaceholder: true, isDirectory: true, size: 0, modifiedUtc: now, inSync: true);
        store.UpsertFile("docs/a.txt", @"C:\drive\docs\a.txt", "docs/a.txt", 0, isPlaceholder: true, isDirectory: false, size: 1, modifiedUtc: now, inSync: true);

        Assert.False(store.WasListed("docs"));
        store.MarkListed("docs/a.txt"); // a file is never marked
        store.MarkListed("docs");
        Assert.True(store.WasListed("docs"));
        Assert.True(store.WasListed("docs/"));
        Assert.False(store.WasListed("docs/a.txt"));

        // Listed again by its parent: the folder row is written again, the mark stays.
        store.UpsertFile("docs/", @"C:\drive\docs", "docs/", 0, isPlaceholder: true, isDirectory: true, size: 0, modifiedUtc: now, inSync: true);
        Assert.True(store.WasListed("docs"));

        // Renamed here: the mark moves with it.
        store.RenameTree("docs", "papers", @"C:\drive\docs", @"C:\drive\papers");
        Assert.True(store.WasListed("papers"));
        Assert.False(store.WasListed("docs"));
    }

    [Fact]
    public void Hashes_AreKept_UpdatedWithTheServersVersion_AndForgotten()
    {
        var store = new SqliteMetadataStore(TempDbPath());
        store.Initialize();

        store.UpsertFile("a.txt", @"C:\drive\a.txt", "a.txt", 0, true, false, 5, DateTimeOffset.UtcNow, hash: "md5:aa");
        store.UpsertFile("b.txt", @"C:\drive\b.txt", "b.txt", 0, true, false, 5, DateTimeOffset.UtcNow);
        Assert.Equal("md5:aa", store.GetByRemotePath("a.txt")?.Hash);
        Assert.Null(store.GetByRemotePath("b.txt")?.Hash);

        store.UpdateRemoteMetadata("a.txt", 6, DateTimeOffset.UtcNow, dehydrated: true, hash: "md5:bb");
        store.SetHash("b.txt", "md5:cc");
        Assert.Equal("md5:bb", store.GetByRemotePath("a.txt")?.Hash);
        Assert.Equal("md5:cc", store.GetByRemotePath("b.txt")?.Hash);

        store.ClearHashes();
        Assert.Null(store.GetByRemotePath("a.txt")?.Hash);
        Assert.Null(store.GetByRemotePath("b.txt")?.Hash);
    }

    [Fact]
    public void GetDirectories_ReturnsOnlyFolders()
    {
        var store = new SqliteMetadataStore(TempDbPath());
        store.Initialize();
        store.UpsertFile("docs/", @"C:\drive\docs", "docs/", 0, true, true, 0, null);
        store.UpsertFile("docs/a.txt", @"C:\drive\docs\a.txt", "docs/a.txt", 0, true, false, 1, null);

        Assert.Equal("docs/", Assert.Single(store.GetDirectories()).RemotePath);
    }

    [Fact]
    public void ListHydratedFiles_ReturnsFilesWithALocalCopy_Only()
    {
        var store = new SqliteMetadataStore(TempDbPath());
        store.Initialize();
        store.UpsertFile("docs/", @"C:\drive\docs", "docs/", 0, true, true, 0, null);
        store.UpsertFile("docs/kept.txt", @"C:\drive\docs\kept.txt", "docs/kept.txt", 0, true, false, 1, null);
        store.UpsertFile("docs/online.txt", @"C:\drive\docs\online.txt", "docs/online.txt", 0, true, false, 1, null);
        store.MarkHydrated("docs/kept.txt");

        var file = Assert.Single(store.ListHydratedFiles());
        Assert.Equal("docs/kept.txt", file.RemotePath);
    }

    [Fact]
    public void Upsert_AndLookups_RoundTrip()
    {
        var store = new SqliteMetadataStore(TempDbPath());
        store.Initialize();

        store.UpsertFile(
            remotePath: "folder/file.txt",
            localPath: @"C:\root\folder\file.txt",
            fileIdentity: "folder/file.txt",
            fileId: 0x1234_5678_9ABC,
            isPlaceholder: true,
            isDirectory: false,
            size: 4096,
            modifiedUtc: DateTimeOffset.UtcNow);

        var byRemote = store.GetByRemotePath("folder/file.txt");
        Assert.NotNull(byRemote);
        Assert.Equal(0x1234_5678_9ABC, byRemote!.FileId);
        Assert.True(byRemote.IsPlaceholder);
        Assert.False(byRemote.IsHydrated);

        var byIdentity = store.GetByFileIdentity("folder/file.txt");
        Assert.NotNull(byIdentity);
        Assert.Equal(byRemote.Id, byIdentity!.Id);

        var byFileId = store.GetByFileId(0x1234_5678_9ABC);
        Assert.NotNull(byFileId);
        Assert.Equal("folder/file.txt", byFileId!.RemotePath);

        var byLocal = store.GetByLocalPath(@"C:\ROOT\folder\file.txt");
        Assert.NotNull(byLocal);

        store.MarkHydrated("folder/file.txt");
        Assert.True(store.GetByRemotePath("folder/file.txt")!.IsHydrated);

        store.MarkInSync("folder/file.txt", 4097);
        var updated = store.GetByRemotePath("folder/file.txt")!;
        Assert.True(updated.InSync);
        Assert.Equal(4097, updated.Size);
    }

    [Fact]
    public void Upsert_IsIdempotentPerRemotePath()
    {
        var store = new SqliteMetadataStore(TempDbPath());
        store.Initialize();

        store.UpsertFile("a.txt", @"C:\root\a.txt", "a.txt", 1, true, false, 10, null);
        store.UpsertFile("a.txt", @"C:\root\a.txt", "a.txt", 2, true, false, 20, null);

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal(2, all[0].FileId);
        Assert.Equal(20, all[0].Size);
    }

    [Fact]
    public void Initialize_MigratesLegacySchema()
    {
        var path = TempDbPath();

        // Old scaffold schema (no FileIdentity/FileId/IsHydrated/InSync).
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE Files (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RemotePath TEXT NOT NULL UNIQUE,
                    LocalPath TEXT NOT NULL,
                    FileId TEXT NOT NULL,
                    IsPlaceholder INTEGER NOT NULL DEFAULT 0,
                    IsDirectory INTEGER NOT NULL DEFAULT 0,
                    Size INTEGER NOT NULL DEFAULT 0,
                    ModifiedUtc TEXT,
                    CreatedUtc TEXT NOT NULL DEFAULT (datetime('now'))
                );";
            command.ExecuteNonQuery();
        }

        var store = new SqliteMetadataStore(path);
        store.Initialize();

        using var verify = new SqliteConnection($"Data Source={path}");
        verify.Open();
        using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Files') WHERE name IN ('FileIdentity','IsHydrated','InSync');";
        var count = Convert.ToInt64(verifyCommand.ExecuteScalar());
        Assert.Equal(3, count);
    }
}
