using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OpenCfDrive.Core.Storage;

public sealed class SqliteMetadataStore
{
    private readonly string _dbPath;

    public SqliteMetadataStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    public string DbPath => _dbPath;

    /// <summary>Deletes a state database and its SQLite side files.</summary>
    public static void Delete(string dbPath)
    {
        // A disposed connection stays open in Microsoft.Data.Sqlite's pool and
        // keeps the file in use, so this process's own earlier reads would make
        // the delete fail as if another process held it.
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        foreach (var path in new[] { dbPath, dbPath + "-journal", dbPath + "-wal", dbPath + "-shm" })
        {
            File.Delete(path);
        }
    }

    public void Initialize()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RemotePath TEXT NOT NULL UNIQUE,
                LocalPath TEXT NOT NULL,
                FileIdentity TEXT NOT NULL DEFAULT '',
                FileId INTEGER NOT NULL DEFAULT 0,
                IsPlaceholder INTEGER NOT NULL DEFAULT 0,
                IsHydrated INTEGER NOT NULL DEFAULT 0,
                InSync INTEGER NOT NULL DEFAULT 0,
                IsDirectory INTEGER NOT NULL DEFAULT 0,
                Size INTEGER NOT NULL DEFAULT 0,
                ModifiedUtc TEXT,
                LocalSize INTEGER,
                LocalWriteUtc INTEGER,
                Hash TEXT,
                Listed INTEGER NOT NULL DEFAULT 0,
                CreatedUtc TEXT NOT NULL DEFAULT (datetime('now'))
            );
        ";
        command.ExecuteNonQuery();

        // Columns are added before indexes: a legacy database has the table but
        // not these columns, and indexing a missing column would throw.
        EnsureColumn(connection, "FileIdentity", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "FileId", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "IsHydrated", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "InSync", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "LocalSize", "INTEGER");
        EnsureColumn(connection, "LocalWriteUtc", "INTEGER");
        EnsureColumn(connection, "Hash", "TEXT");
        EnsureColumn(connection, "Listed", "INTEGER NOT NULL DEFAULT 0");

        // Legacy builds stored a GUID string in FileId; normalise before indexing.
        using (var normalise = connection.CreateCommand())
        {
            normalise.CommandText = "UPDATE Files SET FileId = 0 WHERE typeof(FileId) = 'text';";
            normalise.ExecuteNonQuery();
        }

        using var indexes = connection.CreateCommand();
        indexes.CommandText = @"
            CREATE INDEX IF NOT EXISTS idx_files_remote_path ON Files(RemotePath);
            CREATE INDEX IF NOT EXISTS idx_files_local_path ON Files(LocalPath);
            CREATE INDEX IF NOT EXISTS idx_files_file_identity ON Files(FileIdentity);
            CREATE INDEX IF NOT EXISTS idx_files_file_id ON Files(FileId);
        ";
        indexes.ExecuteNonQuery();

        // Config table for engine settings (no machine env needed)
        using var configTable = connection.CreateCommand();
        configTable.CommandText = @"
            CREATE TABLE IF NOT EXISTS Config (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL DEFAULT ''
            );
        ";
        configTable.ExecuteNonQuery();

        // Deletes and renames the user made that could not reach the server
        // (offline), in the order they were made. Kept across restarts, like
        // Nextcloud's journal, until they are sent.
        using var pendingTable = connection.CreateCommand();
        pendingTable.CommandText = @"
            CREATE TABLE IF NOT EXISTS PendingChanges (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Kind TEXT NOT NULL,
                RemotePath TEXT NOT NULL,
                LocalPath TEXT NOT NULL,
                NewLocalPath TEXT
            );
        ";
        pendingTable.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string name, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Files') WHERE name = @name;";
        check.Parameters.AddWithValue("@name", name);
        var exists = Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0;
        if (exists)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE Files ADD COLUMN {name} {definition};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }

    /// <param name="size">The server's size.</param>
    /// <param name="modifiedUtc">The server's modification time.</param>
    /// <param name="localSize">
    /// The local file's size when it was last in sync, with
    /// <paramref name="localWriteUtc"/> its write time; null when it has the
    /// server's (as a placeholder made from the listing does).
    /// </param>
    /// <param name="hash">The server's content hash ("type:value"), null when unknown.</param>
    public void UpsertFile(
        string remotePath,
        string localPath,
        string fileIdentity,
        long fileId,
        bool isPlaceholder,
        bool isDirectory,
        long size,
        DateTimeOffset? modifiedUtc,
        bool inSync = false,
        long? localSize = null,
        DateTime? localWriteUtc = null,
        string? hash = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Files(RemotePath, LocalPath, FileIdentity, FileId, IsPlaceholder, IsDirectory, Size, ModifiedUtc, InSync, LocalSize, LocalWriteUtc, Hash)
            VALUES (@remotePath, @localPath, @fileIdentity, @fileId, @isPlaceholder, @isDirectory, @size, @modifiedUtc, @inSync, @localSize, @localWriteUtc, @hash)
            ON CONFLICT(RemotePath) DO UPDATE SET
                LocalPath = excluded.LocalPath,
                FileIdentity = excluded.FileIdentity,
                FileId = excluded.FileId,
                IsPlaceholder = excluded.IsPlaceholder,
                IsDirectory = excluded.IsDirectory,
                Size = excluded.Size,
                ModifiedUtc = excluded.ModifiedUtc,
                InSync = excluded.InSync,
                LocalSize = excluded.LocalSize,
                LocalWriteUtc = excluded.LocalWriteUtc,
                Hash = excluded.Hash;
        ";
        command.Parameters.AddWithValue("@hash", hash ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.Parameters.AddWithValue("@localPath", localPath);
        command.Parameters.AddWithValue("@fileIdentity", fileIdentity);
        command.Parameters.AddWithValue("@fileId", fileId);
        command.Parameters.AddWithValue("@isPlaceholder", isPlaceholder ? 1 : 0);
        command.Parameters.AddWithValue("@isDirectory", isDirectory ? 1 : 0);
        command.Parameters.AddWithValue("@size", size);
        command.Parameters.AddWithValue("@modifiedUtc", modifiedUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@inSync", inSync ? 1 : 0);
        command.Parameters.AddWithValue("@localSize", localSize ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@localWriteUtc", localWriteUtc?.ToUniversalTime().Ticks ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    public FileRecord? GetByRemotePath(string remotePath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE RemotePath = @remotePath LIMIT 1;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        using var reader = command.ExecuteReader();
        return reader.Read() ? FileRecord.FromReader(reader) : null;
    }

    /// <summary>Primary hydration lookup: the identity blob we attached at creation time.</summary>
    public FileRecord? GetByFileIdentity(string fileIdentity)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE FileIdentity = @identity LIMIT 1;";
        command.Parameters.AddWithValue("@identity", fileIdentity);
        using var reader = command.ExecuteReader();
        return reader.Read() ? FileRecord.FromReader(reader) : null;
    }

    /// <summary>Fallback hydration lookup by the Windows file id reported by cfapi callbacks.</summary>
    public FileRecord? GetByFileId(long fileId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE FileId = @fileId LIMIT 1;";
        command.Parameters.AddWithValue("@fileId", fileId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? FileRecord.FromReader(reader) : null;
    }

    public FileRecord? GetByLocalPath(string localPath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE LocalPath = @localPath COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("@localPath", localPath);
        using var reader = command.ExecuteReader();
        return reader.Read() ? FileRecord.FromReader(reader) : null;
    }

    public void MarkHydrated(string remotePath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Files SET IsHydrated = 1 WHERE RemotePath = @remotePath;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.ExecuteNonQuery();
    }

    public void MarkDehydrated(string remotePath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Files SET IsHydrated = 0 WHERE RemotePath = @remotePath;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A folder was listed from the server. Kept with its row (moved with it
    /// by a rename, gone with it): a folder listed before, empty ones
    /// included, is answered from disk when it cannot be listed now.
    /// </summary>
    /// <param name="directory">Its server path ('/' separated, with or without the trailing '/').</param>
    public void MarkListed(string directory)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Files SET Listed = 1 WHERE RemotePath = @remotePath AND IsDirectory = 1;";
        command.Parameters.AddWithValue("@remotePath", directory.TrimEnd('/') + "/");
        command.ExecuteNonQuery();
    }

    /// <summary>Whether <see cref="MarkListed"/> was done for this folder.</summary>
    public bool WasListed(string directory)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM Files WHERE RemotePath = @remotePath AND IsDirectory = 1 AND Listed = 1;";
        command.Parameters.AddWithValue("@remotePath", directory.TrimEnd('/') + "/");
        return command.ExecuteScalar() is not null;
    }

    public void MarkInSync(string remotePath, long size)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Files SET InSync = 1, Size = @size, IsPlaceholder = 1 WHERE RemotePath = @remotePath;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.Parameters.AddWithValue("@size", size);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records the server's current metadata for a file, which the local copy
    /// now has too. <paramref name="dehydrated"/> is true when the local copy
    /// was dropped because the server content changed.
    /// </summary>
    public void UpdateRemoteMetadata(string remotePath, long size, DateTimeOffset? modifiedUtc, bool dehydrated, string? hash = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Files
               SET Size = @size,
                   ModifiedUtc = @modifiedUtc,
                   Hash = @hash,
                   LocalSize = NULL,
                   LocalWriteUtc = NULL,
                   InSync = 1,
                   IsHydrated = CASE WHEN @dehydrated = 1 THEN 0 ELSE IsHydrated END
             WHERE RemotePath = @remotePath;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.Parameters.AddWithValue("@size", size);
        command.Parameters.AddWithValue("@modifiedUtc", modifiedUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@hash", hash ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@dehydrated", dehydrated ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>Records the server's hash of a file this client knew without one.</summary>
    public void SetHash(string remotePath, string hash)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Files SET Hash = @hash WHERE RemotePath = @remotePath;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.Parameters.AddWithValue("@hash", hash);
        command.ExecuteNonQuery();
    }

    /// <summary>Forgets every hash: the remote now keeps another kind (it was reconfigured).</summary>
    public void ClearHashes()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Files SET Hash = NULL WHERE Hash IS NOT NULL;";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Tracked items directly inside a sync-root-relative directory ('/'
    /// separated, empty for the root). Directory rows end in '/'.
    /// </summary>
    public IReadOnlyList<FileRecord> GetChildren(string relativeDirectory)
    {
        var prefix = relativeDirectory.Length == 0 ? string.Empty : relativeDirectory.TrimEnd('/') + "/";

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE substr(RemotePath, 1, length(@prefix)) = @prefix AND RemotePath <> @prefix;";
        command.Parameters.AddWithValue("@prefix", prefix);
        using var reader = command.ExecuteReader();
        var results = new List<FileRecord>();
        while (reader.Read())
        {
            var record = FileRecord.FromReader(reader);
            if (!record.RemotePath[prefix.Length..].TrimEnd('/').Contains('/'))
            {
                results.Add(record);
            }
        }

        return results;
    }

    /// <summary>Rows at or below a directory (the directory row ends in '/').</summary>
    public IReadOnlyList<FileRecord> GetTree(string directoryRemotePath)
    {
        var prefix = directoryRemotePath.TrimEnd('/') + "/";

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE substr(RemotePath, 1, length(@prefix)) = @prefix;";
        command.Parameters.AddWithValue("@prefix", prefix);
        using var reader = command.ExecuteReader();
        var results = new List<FileRecord>();
        while (reader.Read())
        {
            results.Add(FileRecord.FromReader(reader));
        }

        return results;
    }

    /// <summary>
    /// Re-keys a directory and everything below it after a rename/move: remote
    /// paths, identities (which equal the remote paths) and local paths.
    /// </summary>
    public int RenameTree(string oldDirectory, string newDirectory, string oldLocalDirectory, string newLocalDirectory)
    {
        var oldPrefix = oldDirectory.TrimEnd('/') + "/";
        var newPrefix = newDirectory.TrimEnd('/') + "/";
        var oldLocal = oldLocalDirectory.TrimEnd('\\');
        var newLocal = newLocalDirectory.TrimEnd('\\');

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Files
               SET RemotePath = @newPrefix || substr(RemotePath, length(@oldPrefix) + 1),
                   FileIdentity = @newPrefix || substr(FileIdentity, length(@oldPrefix) + 1),
                   LocalPath = @newLocal || substr(LocalPath, length(@oldLocal) + 1)
             WHERE substr(RemotePath, 1, length(@oldPrefix)) = @oldPrefix;";
        command.Parameters.AddWithValue("@oldPrefix", oldPrefix);
        command.Parameters.AddWithValue("@newPrefix", newPrefix);
        command.Parameters.AddWithValue("@oldLocal", oldLocal);
        command.Parameters.AddWithValue("@newLocal", newLocal);
        return command.ExecuteNonQuery();
    }

    /// <summary>Removes a directory row and every row below it.</summary>
    public int DeleteTree(string directoryRemotePath)
    {
        var prefix = directoryRemotePath.TrimEnd('/') + "/";

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Files WHERE substr(RemotePath, 1, length(@prefix)) = @prefix;";
        command.Parameters.AddWithValue("@prefix", prefix);
        return command.ExecuteNonQuery();
    }

    /// <summary>Removes a tracked item after it was deleted locally and remotely.</summary>
    public bool DeleteByRemotePath(string remotePath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Files WHERE RemotePath = @remotePath;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Re-keys a tracked item after a local rename/move.</summary>
    public bool RenameRemotePath(string oldRemotePath, string newRemotePath, string newLocalPath, string newFileIdentity)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Files
               SET RemotePath = @newRemotePath,
                   LocalPath = @newLocalPath,
                   FileIdentity = @newFileIdentity
             WHERE RemotePath = @oldRemotePath;";
        command.Parameters.AddWithValue("@oldRemotePath", oldRemotePath);
        command.Parameters.AddWithValue("@newRemotePath", newRemotePath);
        command.Parameters.AddWithValue("@newLocalPath", newLocalPath);
        command.Parameters.AddWithValue("@newFileIdentity", newFileIdentity);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>How many files and folders the drive tracks.</summary>
    public int Count()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Files;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>The tracked folders (their rows end in '/').</summary>
    public IReadOnlyList<FileRecord> GetDirectories()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE IsDirectory = 1 ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var results = new List<FileRecord>();
        while (reader.Read())
        {
            results.Add(FileRecord.FromReader(reader));
        }

        return results;
    }

    public IReadOnlyList<FileRecord> GetAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var results = new List<FileRecord>();
        while (reader.Read())
        {
            results.Add(FileRecord.FromReader(reader));
        }

        return results;
    }

    /// <summary>The files with a local copy (see FileRecord.IsHydrated): what a "free up unused" pass looks at.</summary>
    public IReadOnlyList<FileRecord> ListHydratedFiles()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE IsHydrated = 1 AND IsDirectory = 0 ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var results = new List<FileRecord>();
        while (reader.Read())
        {
            results.Add(FileRecord.FromReader(reader));
        }

        return results;
    }

    public void SetConfig(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO Config (Key, Value) VALUES (@key, @value);";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    public string GetConfig(string key, string defaultValue = "")
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Config WHERE Key = @key;";
        command.Parameters.AddWithValue("@key", key);
        var scalar = command.ExecuteScalar();
        return scalar is null ? defaultValue : (string)scalar;
    }

    /// <summary>
    /// Keeps a delete or rename for the server until it can be sent. The same
    /// change of the same item is kept once.
    /// </summary>
    /// <param name="remotePath">The item's server path (folders end in '/').</param>
    /// <param name="localPath">Where it was; for a rename, <paramref name="newLocalPath"/> is where it is now.</param>
    /// <returns>The change's id, the one kept before when it is already there.</returns>
    public long AddPendingChange(PendingChangeKind kind, string remotePath, string localPath, string? newLocalPath = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO PendingChanges(Kind, RemotePath, LocalPath, NewLocalPath)
            SELECT @kind, @remotePath, @localPath, @newLocalPath
             WHERE NOT EXISTS (
                SELECT 1 FROM PendingChanges
                 WHERE Kind = @kind AND RemotePath = @remotePath AND IFNULL(NewLocalPath, '') = IFNULL(@newLocalPath, ''));
            SELECT Id FROM PendingChanges
             WHERE Kind = @kind AND RemotePath = @remotePath AND IFNULL(NewLocalPath, '') = IFNULL(@newLocalPath, '');";
        command.Parameters.AddWithValue("@kind", kind.ToString());
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.Parameters.AddWithValue("@localPath", localPath);
        command.Parameters.AddWithValue("@newLocalPath", newLocalPath ?? (object)DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>The changes waiting for the server, oldest first.</summary>
    public IReadOnlyList<PendingChange> GetPendingChanges()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Kind, RemotePath, LocalPath, NewLocalPath FROM PendingChanges ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var results = new List<PendingChange>();
        while (reader.Read())
        {
            results.Add(new PendingChange(
                reader.GetInt64(0),
                Enum.Parse<PendingChangeKind>(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return results;
    }

    public void RemovePendingChange(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PendingChanges WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }
}

public enum PendingChangeKind
{
    Delete,
    Rename,
}

/// <summary>A delete or rename the user made that has not reached the server yet.</summary>
public sealed record PendingChange(long Id, PendingChangeKind Kind, string RemotePath, string LocalPath, string? NewLocalPath);

public sealed class FileRecord
{
    public long Id { get; init; }
    public string RemotePath { get; init; } = string.Empty;
    public string LocalPath { get; init; } = string.Empty;
    public string FileIdentity { get; init; } = string.Empty;
    public long FileId { get; init; }
    public bool IsPlaceholder { get; init; }
    public bool IsHydrated { get; init; }
    public bool InSync { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public string? ModifiedUtc { get; init; }
    public long? LocalSize { get; init; }
    public DateTime? LocalWriteUtc { get; init; }

    /// <summary>The server's content hash when last seen ("type:value"), null when the remote keeps none.</summary>
    public string? Hash { get; init; }

    /// <summary>
    /// The local file's size and write time when it was last in sync: the
    /// server's unless an upload recorded the file's own (servers usually
    /// stamp their own time), null when the server's time is unknown.
    /// </summary>
    public (long Size, DateTime WriteUtc)? SyncedLocalState =>
        LocalSize is { } size && LocalWriteUtc is { } written
            ? (size, written)
            : DateTime.TryParse(ModifiedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var modified)
                ? (Size, modified.ToUniversalTime())
                : null;

    internal static FileRecord FromReader(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        RemotePath = reader.GetString(reader.GetOrdinal("RemotePath")),
        LocalPath = reader.GetString(reader.GetOrdinal("LocalPath")),
        FileIdentity = reader.GetString(reader.GetOrdinal("FileIdentity")),
        FileId = reader.GetInt64(reader.GetOrdinal("FileId")),
        IsPlaceholder = reader.GetInt64(reader.GetOrdinal("IsPlaceholder")) != 0,
        IsHydrated = reader.GetInt64(reader.GetOrdinal("IsHydrated")) != 0,
        InSync = reader.GetInt64(reader.GetOrdinal("InSync")) != 0,
        IsDirectory = reader.GetInt64(reader.GetOrdinal("IsDirectory")) != 0,
        Size = reader.GetInt64(reader.GetOrdinal("Size")),
        ModifiedUtc = reader.IsDBNull(reader.GetOrdinal("ModifiedUtc")) ? null : reader.GetString(reader.GetOrdinal("ModifiedUtc")),
        LocalSize = reader.IsDBNull(reader.GetOrdinal("LocalSize")) ? null : reader.GetInt64(reader.GetOrdinal("LocalSize")),
        LocalWriteUtc = reader.IsDBNull(reader.GetOrdinal("LocalWriteUtc"))
            ? null
            : new DateTime(reader.GetInt64(reader.GetOrdinal("LocalWriteUtc")), DateTimeKind.Utc),
        Hash = reader.IsDBNull(reader.GetOrdinal("Hash")) ? null : reader.GetString(reader.GetOrdinal("Hash")),
    };
}
