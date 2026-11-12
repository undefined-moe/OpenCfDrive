using System.Collections.Concurrent;
using System.Globalization;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Interop;
using OpenCfDrive.Core.Models;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Core.CloudFiles;

public sealed record SyncRootStatus(
    bool CloudFilesSupported,
    string CapabilityReason,
    bool Registered,
    bool Connected,
    string SyncRootPath,
    string Remote,
    int RegisteredItems);

/// <summary>
/// End-to-end orchestrator: registers the sync root, connects cfapi callbacks,
/// populates placeholders from rclone and uploads local edits back.
/// When the Cloud Files API is unavailable the engine falls back to an explicit
/// simulation mode (plain empty files) and never claims to be a mount.
/// </summary>
public sealed class CloudSyncEngine : IAsyncDisposable, ILocalSide
{
    private readonly string _syncRootPath;
    private readonly string _remote;
    private readonly RcloneEndpoint _rclone;
    private readonly SqliteMetadataStore _store;
    private readonly Action<string>? _log;

    private readonly SyncRootRegistrar _registrar = new();
    private readonly CloudPlaceholderService _placeholders = new();
    private readonly ItemStates _itemStates;

    private RcloneRcClient? _rc;
    private RcloneServeClient? _serve;
    private SyncRootConnection? _connection;
    private FileSystemWatcher? _watcher;

    private readonly Guid _syncRootId = new("6f2a1f1e-6a4b-4f5b-9c2a-0d5d5c1b9e10");
    private static readonly Guid ProviderId = new("d022a580-8ff9-4d6f-9d87-2c0951d08246");

    private readonly bool _forceSimulation;
    private readonly HashSet<string> _stateFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When each listed folder was last listed from the server, by server
    /// path. A folder that holds nothing local is not listed every run: once
    /// it is older than the poll interval it is given on-demand population
    /// again, and Windows lists it when it is next opened. Missing here (a
    /// restart), every online-only folder is marked on the first pass.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _listedAt =
        new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _populating;

    /// <summary>
    /// Local paths the provider itself just wrote: placeholders handed out by
    /// FETCH_PLACEHOLDERS and placeholders updated for a server-side change.
    /// Those writes raise watcher events that are not user edits, so they
    /// must not be queued for upload.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _providerWrites =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ProviderWriteGuard = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Remote paths (files) and directory prefixes (ending in '/') that were
    /// deleted on the server and are now being deleted locally. The local
    /// delete raises NOTIFY_DELETE and watcher events; they must not be
    /// propagated back as remote deletes.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _serverDeletes =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ServerDeleteGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The local changes reported since the last sync run, sent by the next
    /// one as one plan (see <see cref="SendLocalChangesAsync"/>).
    /// </summary>
    private readonly LocalChanges _changes;

    /// <summary>Changes already logged as kept while offline, by sequence number.</summary>
    private readonly HashSet<long> _reportedOffline = [];

    /// <summary>Uploads one run sends at a time.</summary>
    private const int ParallelUploads = 3;

    /// <summary>
    /// When a local change (upload, rename) last reached the server, by remote
    /// path. A listing that started before that moment can predate the change,
    /// so it must not be used to delete or overwrite that file.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _localChangeAt =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the server answers (cloud mode); see <see cref="ServerConnection"/>.</summary>
    private ServerConnection? _server;

    /// <summary>The sync runs; see <see cref="SyncScheduler"/>.</summary>
    private SyncScheduler? _scheduler;

    /// <summary>How long a downloaded file may go unopened before its local copy is freed; null never frees.</summary>
    private readonly TimeSpan? _freeUnusedAfter;

    private ITimer? _freeUnusedTimer;

    /// <summary>
    /// False for remotes that have folders only as parts of file names (S3
    /// and other bucket stores): an empty folder made here cannot be on the
    /// server, so its absence there does not mean it was deleted.
    /// </summary>
    private bool _remoteKeepsEmptyFolders = true;

    /// <summary>What the remote keeps: its hashes and how exact its times are.</summary>
    private RemoteCapabilities _capabilities = RemoteCapabilities.Unknown;

    /// <summary>The store's note of the hash type its hashes are of.</summary>
    private const string HashTypeKey = "HashType";

    /// <summary>
    /// The most a background listing without times may leave for lookups one
    /// by one; past that, the folder is listed again with times, which rclone
    /// reads for several files at once.
    /// </summary>
    private const int MaxTimeLookups = 8;

    /// <summary>Listings and lookups return the remote's hash, when it keeps one.</summary>
    private ListingOptions Listing(bool withTimes = true) => new(_capabilities.HashType, withTimes);

    /// <summary>False while the server cannot be reached; folders then show what is on this PC.</summary>
    public bool IsOnline => _server?.IsOnline ?? true;

    /// <summary>Why the server cannot be reached, while offline.</summary>
    public string? OfflineReason => _server?.OfflineReason;

    /// <summary>
    /// Mock mode only (plain files, no in-sync state): length and write time
    /// of each file when it last matched the server, so a change event with
    /// nothing new is not uploaded again.
    /// </summary>
    private readonly ConcurrentDictionary<string, (long Length, DateTime LastWriteUtc)> _lastSyncedState =
        new(StringComparer.OrdinalIgnoreCase);

    private void RememberSyncedState(string localPath)
    {
        try
        {
            var info = new FileInfo(localPath);
            _lastSyncedState[localPath] = (info.Length, info.LastWriteTimeUtc);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Server-side changes found by a listing, applied after the shell got its reply.</summary>
    private readonly ConcurrentDictionary<string, DirectoryDiff> _pendingDiffs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _applyGate = new(1, 1);

    /// <summary>
    /// Held by a move on the server of something renamed here, and by a first
    /// listing (<see cref="ListFolderForWindowsAsync"/>) or a download
    /// (<see cref="PrepareDownload"/>) of what that move is about: neither
    /// sees the other half done.
    /// </summary>
    private readonly SemaphoreSlim _moveGate = new(1, 1);

    /// <summary>
    /// How long such a listing waits for a move under way, within the
    /// listing's own limit (<see cref="SyncRootConnection.ListingTimeout"/>).
    /// </summary>
    private static readonly TimeSpan FolderMoveWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long such a download waits, well within the 60 s Windows gives a
    /// download before it fails the read.
    /// </summary>
    private static readonly TimeSpan DownloadMoveWait = TimeSpan.FromSeconds(30);

    private sealed record ChangedFile(FileRecord Record, long Size, DateTimeOffset? ModifiedUtc, string? Hash);

    private sealed record DirectoryDiff(
        DateTimeOffset ListedAt,
        IReadOnlyList<FileRecord> Deleted,
        IReadOnlyList<ChangedFile> Changed);

    /// <param name="forceSimulation">
    /// Runs the full remote pipeline (list, materialise, hydrate, watch, upload)
    /// against a plain directory instead of a cfapi sync root. Used by the
    /// mock E2E so the client-side flow can be validated without Cloud Files
    /// registration.
    /// </param>
    public CloudSyncEngine(
        string syncRootPath,
        string remote,
        string rcUrl,
        string dbPath,
        Action<string>? log = null,
        bool forceSimulation = false,
        TimeSpan? freeUnusedAfter = null)
        : this(syncRootPath, remote, new RcloneEndpoint(rcUrl), dbPath, log, forceSimulation, freeUnusedAfter)
    {
    }

    public CloudSyncEngine(
        string syncRootPath,
        string remote,
        RcloneEndpoint rclone,
        string dbPath,
        Action<string>? log = null,
        bool forceSimulation = false,
        TimeSpan? freeUnusedAfter = null)
    {
        _syncRootPath = syncRootPath;
        _remote = remote;
        _rclone = rclone;
        _store = new SqliteMetadataStore(dbPath);
        _log = log;
        _forceSimulation = forceSimulation;
        _freeUnusedAfter = freeUnusedAfter;
        _changes = new LocalChanges(_store);
        _changes.Recorded += () => _scheduler?.RequestLocalRun();
        // Plain files in mock mode are outside a sync root, where the shell
        // has no state column to fill.
        _itemStates = new ItemStates(log, enabled: !forceSimulation);
        _itemStates.FailingChanged += OnActivityChanged;

        // The metadata DB and its SQLite side files are engine state, never
        // user data. If they live inside the sync root they must not be
        // uploaded, deleted or renamed through watcher events.
        foreach (var path in new[] { dbPath, dbPath + "-journal", dbPath + "-wal", dbPath + "-shm" })
        {
            try
            {
                _stateFiles.Add(Path.GetFullPath(path));
            }
            catch (ArgumentException)
            {
                // Ignore unparsable paths; they cannot be watcher events either.
            }
        }
    }

    /// <summary>
    /// A sync step that failed for a file or folder (an upload, rename,
    /// delete, download, a listing, a server change that could not be put in
    /// place), as one line. The service shows each to the user. Without a
    /// handler it goes to the log like any other line.
    /// </summary>
    public event Action<string>? ProblemReported;

    /// <summary>
    /// Raised, on any thread, when <see cref="Activity"/> or
    /// <see cref="IsOnline"/> may have changed. Handlers must not block.
    /// </summary>
    public event Action? ActivityChanged;

    /// <summary>What the drive is doing now: what it sends, downloads, keeps offline, and what failed.</summary>
    public SyncActivity Activity => new(
        Sending: Math.Max(0, Volatile.Read(ref _sending)),
        Downloading: _connection?.ActiveDownloads ?? 0,
        Waiting: Volatile.Read(ref _waiting),
        Failing: _itemStates.Failing);

    // The steps of the run's plan not yet done, and the changes the last
    // plan kept while offline.
    private int _sending;
    private int _waiting;

    private void OnActivityChanged() => ActivityChanged?.Invoke();

    private void SetSending(int steps)
    {
        if (Interlocked.Exchange(ref _sending, steps) != steps)
        {
            OnActivityChanged();
        }
    }

    private void AddSending(int steps)
    {
        Interlocked.Add(ref _sending, steps);
        OnActivityChanged();
    }

    private void SetWaiting(int changes)
    {
        if (Interlocked.Exchange(ref _waiting, changes) != changes)
        {
            OnActivityChanged();
        }
    }

    private void ReportProblem(string message)
    {
        if (ProblemReported is { } report)
        {
            report(message);
        }
        else
        {
            _log?.Invoke(message);
        }
    }

    private bool IsStateFile(string localPath)
    {
        try
        {
            return _stateFiles.Contains(Path.GetFullPath(localPath));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Exposed so hosts/tests can observe cfapi callback activity.</summary>
    public SyncRootConnection? Connection => _connection;

    /// <summary>Payload bytes pulled from the remote so far (hydration telemetry).</summary>
    public long BytesFetched => _serve?.BytesFetched ?? 0;

    /// <summary>Zeroes the hydration byte counter; used by range-hydration tests.</summary>
    public void ResetBytesFetched() => _serve?.ResetBytesFetched();

    public bool IsSimulated { get; private set; }

    private static bool IsRcStartUpTransient(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            var msg = httpEx.Message ?? string.Empty;
            return httpEx.InnerException is System.Net.Sockets.SocketException
                || msg.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("404", StringComparison.Ordinal)
                || msg.Contains("directory not found", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public async Task<SyncRootStatus> InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_syncRootPath);
        _store.Initialize();

        var support = CloudFilesCapability.Current;
        IsSimulated = _forceSimulation || !support.IsSupported;

        var registered = false;
        var connected = false;

        if (_forceSimulation)
        {
            // Mock mode still needs the rclone clients so hydration + upload
            // exercise the same code paths as the real mount.
            _rc = new RcloneRcClient(_rclone);
            _serve = new RcloneServeClient(_rclone, _remote);
            _scheduler = new SyncScheduler(RunAsync, SyncScheduler.ConfiguredInterval());
            _log?.Invoke("MOCK MODE: no sync root registration; using a plain directory.");
        }
        else if (support.IsSupported)
        {
            var registration = _registrar.Register(_syncRootPath, _syncRootId, ProviderId);
            registered = registration.Success;
            _log?.Invoke(registration.Message);

            if (registered)
            {
                _rc = new RcloneRcClient(_rclone);
                _serve = new RcloneServeClient(_rclone, _remote);

                // Checked with a listing of the drive's top folder, given as
                // long as any listing.
                var rc = _rc;
                _server = new ServerConnection(
                    cancellationToken => rc.ListDirectoryAsync(_remote, string.Empty, new ListingOptions(WithTimes: false), cancellationToken),
                    SyncRootConnection.ListingTimeout,
                    _log);
                // Back online, a full run (Nextcloud runs a sync then too):
                // everything kept for the server goes first.
                _server.Changed += online =>
                {
                    OnActivityChanged();
                    if (online)
                    {
                        _ = _scheduler?.SyncNowAsync();
                    }
                };
                _scheduler = new SyncScheduler(RunAsync, SyncScheduler.ConfiguredInterval());

                _connection = new SyncRootConnection(
                    _store,
                    _serve,
                    _log,
                    localDelete: RecordNotifiedDelete,
                    localRename: (oldLocalPath, newLocalPath, remotePath, source) => _changes.Renamed(oldLocalPath, newLocalPath, remotePath, source),
                    listDirectory: ListFolderForWindowsAsync,
                    server: _server,
                    prepareDownload: PrepareDownload,
                    problem: ReportProblem);

                // What a listing found changed on the server is applied by
                // the next run, after its local changes.
                _connection.DirectoryListed += _ => _scheduler.RequestLocalRun();
                _connection.DownloadStarted += _ => OnActivityChanged();

                // A change that waited for a download can go now, whether it
                // was sent or failed.
                _connection.DownloadEnded += _ =>
                {
                    OnActivityChanged();
                    if (!_changes.IsEmpty)
                    {
                        _scheduler.RequestLocalRun();
                    }
                };
                connected = _connection.Connect(_syncRootPath);
            }
        }
        else
        {
            // Without Cloud Files there are no placeholders, and showing the
            // remote would mean downloading all of it into a plain folder.
            // That is never done: nothing is synced.
            _log?.Invoke($"Cloud Files is not available: {support.Reason} Nothing is synced.");
            return new SyncRootStatus(false, support.Reason, false, false, _syncRootPath, _remote, 0);
        }

        var items = 0;
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            try
            {
                items = await PopulateAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (IsRcStartUpTransient(ex) && attempt < 12)
            {
                _log?.Invoke($"Rclone RC not ready yet (attempt {attempt}/12): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }

        if (_rc is not null)
        {
            try
            {
                _capabilities = await _rc.GetCapabilitiesAsync(_remote, cancellationToken).ConfigureAwait(false);
                _remoteKeepsEmptyFolders = _capabilities.CanHaveEmptyDirectories;
                if (!_remoteKeepsEmptyFolders)
                {
                    _log?.Invoke($"'{_remote}' keeps no empty folders (folders are parts of file names there): an empty folder made here stays only here until a file is put in it.");
                }

                _log?.Invoke(_capabilities.Describe(_remote));

                // Another kind of hash than last time (the remote was set up
                // again): the ones kept say nothing about the new kind.
                var hashType = _capabilities.HashType ?? string.Empty;
                if (!string.Equals(_store.GetConfig(HashTypeKey), hashType, StringComparison.Ordinal))
                {
                    _store.ClearHashes();
                    _store.SetConfig(HashTypeKey, hashType);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
            {
                // Assume folders are kept and no hashes, as on most remotes.
            }
        }

        return new SyncRootStatus(
            support.IsSupported,
            support.Reason,
            registered,
            connected,
            _syncRootPath,
            _remote,
            items);
    }

    /// <summary>
    /// Pillar 2: the namespace. In cloud mode the drive is never crawled and
    /// nothing is listed up front: Windows asks for a folder's contents the
    /// first time it is opened (FETCH_PLACEHOLDERS), and that request lists
    /// the one folder. From then on the folder is fully populated and the
    /// background sync keeps it current (see <see cref="SyncRemoteAsync"/>).
    /// A folder that cannot be listed shows what is already local, which is
    /// nothing if it was never opened. Mock mode, a test stand-in, has no such
    /// callback and copies the remote into a plain folder.
    /// </summary>
    public async Task<int> PopulateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSimulated)
        {
            _log?.Invoke($"{ListedDirectories().Count()} folder(s) listed on this PC; the background sync keeps them current.");
            return 0;
        }

        _rc ??= new RcloneRcClient(_rclone);

        var items = await _rc.ListFilesAsync(_remote, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            _log?.Invoke($"Remote '{_remote}' returned no items.");
            return 0;
        }

        // rclone's recursive listing only reports files, so the directory
        // entries that make up the namespace are synthesised from the file
        // paths.
        var all = BuildNamespace(items);
        var files = all.Where(i => !i.IsDirectory).ToList();

        // Plain-directory mode: materialise the real folder tree and file
        // contents. Directories have to be created explicitly because
        // MaterializeFilesAsync only creates the parents of real files.
        foreach (var directory in all.Where(i => i.IsDirectory))
        {
            Directory.CreateDirectory(Path.Combine(
                _syncRootPath,
                directory.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        // The watcher must not see the files we are writing while the
        // namespace is being populated, or it would echo them back.
        _populating = true;
        try
        {
            return await MaterializeFilesAsync(files, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _populating = false;
        }
    }

    /// <summary>
    /// FETCH_PLACEHOLDERS: a folder opened for the first time, listed where
    /// its placeholder says it is on the server. A folder renamed or moved
    /// here (or one inside such a folder) whose move has not reached the
    /// server yet still has its contents under the old path there, and its
    /// placeholder still names that path: they are listed from there into
    /// the folder under its new name, and the move re-keys them with the
    /// rest. Such a listing and the move never overlap: a move under way is
    /// waited for, and then the folder is listed where it went. Answered
    /// with nothing, it would be marked fully populated and stay empty.
    /// </summary>
    private async Task<SyncRootConnection.DirectoryListing?> ListFolderForWindowsAsync(
        string remoteDirectory,
        string localDirectory,
        CancellationToken cancellationToken)
    {
        if (!IsLocalChangePending(localDirectory))
        {
            var entries = await ListNewRemoteEntriesAsync(remoteDirectory, cancellationToken).ConfigureAwait(false);
            return new SyncRootConnection.DirectoryListing(entries, Listed: true);
        }

        if (!await _moveGate.WaitAsync(FolderMoveWait, cancellationToken).ConfigureAwait(false))
        {
            _log?.Invoke($"'{RelativeOf(localDirectory)}' is being moved on the server; it is listed when it is next opened.");
            return null;
        }

        try
        {
            // Moved while this waited: its row says where it is now.
            var current = _store.GetByLocalPath(localDirectory) is { IsDirectory: true } row
                ? row.RemotePath.TrimEnd('/')
                : remoteDirectory;
            var entries = await ListNewRemoteEntriesAsync(current, cancellationToken, landingDirectory: localDirectory).ConfigureAwait(false);
            return new SyncRootConnection.DirectoryListing(entries, Listed: true);
        }
        finally
        {
            _moveGate.Release();
        }
    }

    /// <summary>
    /// FETCH_DATA: a file renamed or moved here, or inside a folder that was,
    /// whose move has not reached the server yet is read from the path its
    /// placeholder names, which the server still has; the move waits until
    /// the read has begun. A read waits for a move under way, then reads from
    /// where the move put the file: its row is gone from the old path once the
    /// move re-keyed it. The move updates the placeholders only after it lets
    /// reads go, because marking a file in sync waits for its download
    /// (RenamedFolderContractTest).
    /// </summary>
    private SyncRootConnection.DownloadSource? PrepareDownload(string remotePath)
    {
        if (_changes.PendingRenameOf(remotePath) is not { NewLocalPath: { } newLocalPath } rename)
        {
            return new SyncRootConnection.DownloadSource(remotePath, null);
        }

        if (!_moveGate.Wait(DownloadMoveWait))
        {
            _log?.Invoke($"'{remotePath}' is being moved on the server; it opens once that is done.");
            return null;
        }

        var current = remotePath;
        if (_store.GetByRemotePath(remotePath) is null)
        {
            var oldPrefix = rename.RemotePath!.TrimEnd('/');
            current = RelativeOf(newLocalPath) + remotePath[oldPrefix.Length..];
        }

        return new SyncRootConnection.DownloadSource(current, new GateHold(_moveGate));
    }

    /// <summary>Lets go of a gate once, however often it is disposed.</summary>
    private sealed class GateHold(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// FETCH_PLACEHOLDERS source: lists one remote directory live and returns
    /// the entries that have no placeholder yet. Their rows are written before
    /// returning, because the platform can issue FETCH_DATA as soon as the
    /// placeholders exist.
    ///
    /// The remote is the source of truth: a known item whose placeholder is
    /// gone (the folder was unregistered and registered again, or handing it
    /// out failed) is handed out again. The one exception is an item the user
    /// deleted, moved or renamed whose change has not reached the server yet:
    /// the server still lists it only because it has not been told.
    ///
    /// The same listing also reveals files changed or deleted on the server.
    /// The reply to the platform can only add entries, so those are recorded
    /// here and applied by <see cref="ApplyServerChangesAsync"/> once the
    /// shell has its reply (see <see cref="SyncRootConnection.DirectoryListed"/>).
    /// </summary>
    /// <param name="landingDirectory">
    /// Set for a folder renamed here whose move has not reached the server:
    /// where its entries land on disk. Their rows keep the server's paths
    /// until the move re-keys them.
    /// </param>
    private async Task<IReadOnlyList<PlaceholderEntry>> ListNewRemoteEntriesAsync(
        string relativeDirectory,
        CancellationToken cancellationToken,
        string? landingDirectory = null)
    {
        _rc ??= new RcloneRcClient(_rclone);
        var listedAt = DateTimeOffset.UtcNow;
        var known = _store.GetChildren(relativeDirectory)
            .ToDictionary(r => r.RemotePath, StringComparer.Ordinal);

        // A folder listed before, on a remote where each file's time costs a
        // request of its own (S3): the hash tells what changed, so only what
        // is new or changed is asked for its time.
        var withoutTimes = _capabilities.ListsWithoutTimes && known.Count > 0;
        var items = await _rc.ListDirectoryAsync(_remote, relativeDirectory, Listing(withTimes: !withoutTimes), cancellationToken).ConfigureAwait(false);
        if (withoutTimes)
        {
            items = await WithTimesWhereNeededAsync(relativeDirectory, items, known, cancellationToken).ConfigureAwait(false);
        }
        var listedPaths = new HashSet<string>(StringComparer.Ordinal);
        var changed = new List<ChangedFile>();

        var entries = new List<PlaceholderEntry>();
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            var relative = item.RelativePath.Replace('\\', '/').Trim('/');
            if (relative.Length == 0)
            {
                continue;
            }

            // Same identity convention as BuildNamespace: directories end in '/'.
            var remotePath = item.IsDirectory ? relative + "/" : relative;
            listedPaths.Add(remotePath);

            var localPath = Path.Combine(_syncRootPath, relative.Replace('/', Path.DirectorySeparatorChar));

            // Listed into a renamed folder, its contents are what is given
            // out: only an item's own delete or rename leaves it alone.
            if (landingDirectory is null ? IsLocalChangePending(localPath) : _changes.IsPendingAt(localPath))
            {
                continue;
            }

            // A server folder that is on disk as a plain folder (unregistering
            // leaves them so) would never be asked for its contents. Made a
            // placeholder again, it is listed when it is opened.
            if (item.IsDirectory && Directory.Exists(localPath) && !_placeholders.QueryState(localPath).IsPlaceholder)
            {
                var converted = _placeholders.ConvertToPlaceholder(
                    localPath,
                    CloudPlaceholderService.BuildFileIdentity(remotePath),
                    markInSync: true,
                    enableOnDemandPopulation: true);
                _log?.Invoke(converted
                    ? $"'{remotePath}' was a plain folder; now a placeholder, listed when opened."
                    : $"'{remotePath}' is a plain folder and could not be made a placeholder.");
                _store.UpsertFile(
                    remotePath,
                    localPath,
                    fileIdentity: remotePath,
                    fileId: 0,
                    isPlaceholder: converted,
                    isDirectory: true,
                    size: 0,
                    modifiedUtc: item.ModifiedTime,
                    inSync: converted);
                continue;
            }

            var isKnown = known.TryGetValue(remotePath, out var record) || (record = _store.GetByRemotePath(remotePath)) is not null;

            // A file made here and not uploaded yet, where the server got one
            // of its own: never taken for the server's. Its upload finds the
            // server's and keeps both.
            if (!isKnown && !item.IsDirectory && File.Exists(localPath) && !_placeholders.QueryState(localPath).IsPlaceholder)
            {
                _changes.Changed(localPath);
                _itemStates.WaitingToUpload(localPath);
                continue;
            }

            if (isKnown)
            {
                var present = item.IsDirectory ? Directory.Exists(localPath) : File.Exists(localPath);
                if (present)
                {
                    if (!item.IsDirectory && !record!.IsDirectory)
                    {
                        if (DiffersFromServer(record, item))
                        {
                            changed.Add(new ChangedFile(record, item.Size, item.ModifiedTime, item.Hash));
                        }
                        else if (record.Hash is null && item.Hash is not null)
                        {
                            // Known from before hashes were kept: from now on the hash tells.
                            _store.SetHash(record.RemotePath, item.Hash);
                        }
                    }

                    continue;
                }

                // Known, but its placeholder is gone: handed out again below.
            }

            var size = item.IsDirectory ? 0 : item.Size;
            _store.UpsertFile(
                remotePath,
                localPath,
                fileIdentity: remotePath,
                fileId: 0,
                isPlaceholder: true,
                isDirectory: item.IsDirectory,
                size: size,
                modifiedUtc: item.ModifiedTime,
                inSync: true,
                hash: item.IsDirectory ? null : item.Hash);

            _providerWrites[landingDirectory is null ? localPath : Path.Combine(landingDirectory, Path.GetFileName(relative))] = now;
            entries.Add(new PlaceholderEntry(
                RelativeName: Path.GetFileName(relative),
                IsDirectory: item.IsDirectory,
                Size: size,
                ModifiedUtc: item.ModifiedTime,
                FileIdentity: remotePath));
        }

        var deleted = known.Values.Where(r => !listedPaths.Contains(r.RemotePath)).ToList();
        if (deleted.Count > 0 || changed.Count > 0)
        {
            _pendingDiffs[relativeDirectory] = new DirectoryDiff(listedAt, deleted, changed);
        }

        // A folder listed from the server is not marked for population again
        // until the interval has passed (see SyncRemoteAsync). A folder
        // renamed here was listed where the server still had it; the folder
        // its entries landed in is the one Windows will next open.
        _listedAt[relativeDirectory] = listedAt;
        if (landingDirectory is not null)
        {
            _listedAt[RelativeOf(landingDirectory)] = listedAt;
        }

        // Also kept in the store, which an empty folder has no other row in:
        // one listed before is answered from disk when it cannot be listed.
        if (relativeDirectory.Length > 0)
        {
            _store.MarkListed(relativeDirectory);
        }

        return entries;
    }

    /// <summary>
    /// A listing without times still needs the time of what is new (its
    /// placeholder shows it), of what changed, and of a file without a hash,
    /// whose time is what tells a change. Those are looked up one by one; past
    /// a few, the folder is listed again with times.
    /// </summary>
    private async Task<IReadOnlyList<CloudItem>> WithTimesWhereNeededAsync(
        string relativeDirectory,
        IReadOnlyList<CloudItem> items,
        IReadOnlyDictionary<string, FileRecord> known,
        CancellationToken cancellationToken)
    {
        bool NeedsTime(CloudItem item)
        {
            var relative = item.RelativePath.Replace('\\', '/').Trim('/');
            var localPath = Path.Combine(_syncRootPath, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!known.TryGetValue(item.IsDirectory ? relative + "/" : relative, out var record) ||
                !(item.IsDirectory ? Directory.Exists(localPath) : File.Exists(localPath)))
            {
                return true; // handed out as a placeholder
            }

            return !item.IsDirectory && FileVersions.SameHash(record.Hash, item.Hash) != true;
        }

        var needed = items.Where(NeedsTime).ToList();
        if (needed.Count == 0)
        {
            return items;
        }

        _rc ??= new RcloneRcClient(_rclone);
        if (needed.Count > MaxTimeLookups)
        {
            return await _rc.ListDirectoryAsync(_remote, relativeDirectory, Listing(), cancellationToken).ConfigureAwait(false);
        }

        var looked = new Dictionary<string, CloudItem>(StringComparer.Ordinal);
        foreach (var item in needed)
        {
            if (await _rc.StatAsync(_remote, item.RelativePath, Listing(), cancellationToken).ConfigureAwait(false) is { } found)
            {
                looked[item.Path] = found;
            }
        }

        return items.Select(item => looked.GetValueOrDefault(item.Path, item)).ToList();
    }

    /// <summary>
    /// Starts what runs beside the drive once it is connected and watched, as
    /// Nextcloud does on start: the deletes and renames the journal kept, and
    /// what changed here while the service was not running, go with the first
    /// run, which starts now; then a full run every interval.
    /// </summary>
    public void StartBackgroundSync()
    {
        if (_scheduler is null)
        {
            return;
        }

        _changes.LoadJournal();

        // The "free up what has not been opened for a while" pass: a minute
        // after start (the first sync run is under way then), then hourly.
        if (_freeUnusedAfter is { } unusedAfter)
        {
            _freeUnusedTimer?.Dispose();
            _freeUnusedTimer = TimeProvider.System.CreateTimer(
                _ => _ = FreeUnusedAsync(unusedAfter), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (!IsSimulated)
                {
                    DiscoverLocalChanges();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ReportProblem($"Looking for changes made while OpenCfDrive was not running failed: {ex.Message}");
            }

            _scheduler.Start();
        });
    }

    /// <summary>
    /// Frees the local copy of every downloaded file that has not been opened
    /// (nor written) for <paramref name="unusedAfter"/>: the same "Free up
    /// space" as the right-click menu, run on files nobody reads. Files with
    /// changes not yet on the server are left for their upload (see
    /// <see cref="ApplyPinStateNow"/>), as are pinned ones.
    /// </summary>
    private async Task FreeUnusedAsync(TimeSpan unusedAfter)
    {
        try
        {
            await Task.Yield();
            foreach (var record in _store.ListHydratedFiles())
            {
                var localPath = record.LocalPath;
                FileAttributes attributes;
                DateTime lastAccess, lastWrite;
                try
                {
                    attributes = File.GetAttributes(localPath);
                    lastAccess = File.GetLastAccessTimeUtc(localPath);
                    lastWrite = File.GetLastWriteTimeUtc(localPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // gone, or being written over; the next pass looks again
                }

                if (!ShouldFreeUnused(attributes, lastAccess, lastWrite, DateTimeOffset.UtcNow.UtcDateTime, unusedAfter) ||
                    InsideKeptDirectory(localPath))
                {
                    continue;
                }

                ApplyPinState(localPath, keepLocal: false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportProblem($"Freeing unused local copies failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether a downloaded file's local copy may be freed: still local (a
    /// placeholder has nothing to free), not pinned by its own attribute, and
    /// neither opened nor written within the age limit. A file downloaded but
    /// never opened keeps the placeholder's old times, so it counts as unused.
    /// </summary>
    internal static bool ShouldFreeUnused(
        FileAttributes attributes, DateTime lastAccessUtc, DateTime lastWriteUtc, DateTime nowUtc, TimeSpan unusedAfter) =>
        (attributes & RecallOnDataAccessAttribute) == 0 &&
        (attributes & PinnedAttribute) == 0 &&
        nowUtc - (lastAccessUtc > lastWriteUtc ? lastAccessUtc : lastWriteUtc) >= unusedAfter;

    /// <summary>Whether the file, or any folder above it up to the sync root, is pinned "Always keep on this device".</summary>
    private bool InsideKeptDirectory(string localPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_syncRootPath));
        for (var dir = Path.GetDirectoryName(localPath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (IsPinned(dir))
            {
                return true;
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(dir), root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Syncs now (the app's or a script's "sync now"): the local changes, then
    /// a look at the server. Completes once that run has finished.
    /// </summary>
    public Task SyncNowAsync() => _scheduler?.SyncNowAsync() ?? Task.CompletedTask;

    /// <summary>
    /// One sync run, as Nextcloud's: the local changes since the last run go
    /// to the server as one plan, then what listings found changed on the
    /// server is applied. A full run then looks at every listed folder too
    /// (cloud mode; mock mode never looks at the server after startup).
    /// One run at a time, so none of this runs beside another part of it.
    /// </summary>
    private async Task RunAsync(bool full, CancellationToken cancellationToken)
    {
        await SendLocalChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var directory in _pendingDiffs.Keys.ToList())
        {
            await ApplyServerChangesAsync(directory).ConfigureAwait(false);
        }

        if (full && !IsSimulated)
        {
            await SyncRemoteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends the local changes reported since the last run as one plan (see
    /// <see cref="LocalChangePlanner"/>): moves in the order they were made,
    /// then deletes (a folder's as one), new folders (parents first), then
    /// uploads, a few at a time. Offline nothing is sent and everything waits;
    /// a server that stops answering midway stops the run, and what is left
    /// waits for the next one. A change that fails otherwise is logged and
    /// dropped, as before.
    /// </summary>
    private async Task SendLocalChangesAsync(CancellationToken cancellationToken)
    {
        if (_changes.IsEmpty)
        {
            SetWaiting(0);
            return;
        }

        var plan = LocalChangePlanner.Plan(_changes.Pending(), this, DateTimeOffset.UtcNow);
        foreach (var dropped in plan.Dropped)
        {
            if (dropped.Reason is not null)
            {
                _log?.Invoke(dropped.Reason);
            }

            // Nothing to send (the client's own write, gone again, or with its
            // folder): no state stays on the file for it.
            ClearStates(dropped.Changes);
            _changes.Complete(dropped.Changes);
        }

        if (_server is { IsOnline: false })
        {
            ReportKeptOffline(plan);
            _server.CheckNow();
            return;
        }

        SetWaiting(0);

        // "Waiting to upload" is shown where the plan has something to send,
        // not for every file event: most of those are the client's own writes,
        // which the plan drops.
        foreach (var move in plan.Moves)
        {
            _itemStates.WaitingToUpload(move.NewLocalPath);
        }

        // A folder made here is no placeholder until the server has it.
        foreach (var folder in plan.Folders.Where(f => Directory.Exists(f.LocalPath) && !_placeholders.QueryState(f.LocalPath).IsPlaceholder))
        {
            _itemStates.WaitingToUpload(folder.LocalPath);
        }

        foreach (var upload in plan.Uploads.Where(u => HasUnsyncedLocalChanges(u.LocalPath)))
        {
            _itemStates.WaitingToUpload(upload.LocalPath);
        }

        // New items found on the way (a folder moved in, or renamed before the
        // server had it) join the plan's later steps.
        var folders = new Queue<PlannedItem>(plan.Folders);
        var uploads = new List<PlannedItem>(plan.Uploads);
        void Follow(string localPath)
        {
            if (Directory.Exists(localPath))
            {
                folders.Enqueue(new PlannedItem(localPath, []));
                AddSending(1);
            }
            else if (File.Exists(localPath) && !uploads.Any(u => LocalChanges.Same(u.LocalPath, localPath)))
            {
                uploads.Add(new PlannedItem(localPath, []));
                AddSending(1);
            }
        }

        var steps = plan.Moves.Count + plan.Deletes.Count + plan.Folders.Count + plan.Uploads.Count;
        if (steps > 0)
        {
            _log?.Invoke(
                $"Local changes: {plan.Moves.Count} move(s), {plan.Deletes.Count} delete(s), " +
                $"{plan.Folders.Count} new folder(s), {plan.Uploads.Count} upload(s).");
        }

        SetSending(steps);
        try
        {
            foreach (var move in plan.Moves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await StepAsync(move.From, $"Rename propagation failed for '{move.RemotePath}'", async () =>
                {
                    if (!await RenameRemoteAsync(move.RemotePath, move.NewLocalPath, move.Source).ConfigureAwait(false))
                    {
                        Follow(move.NewLocalPath);
                    }
                    else if (!IsSimulated && File.Exists(move.NewLocalPath) && !_placeholders.QueryState(move.NewLocalPath).IsPlaceholder)
                    {
                        // Renamed just as its upload finished, before it was
                        // made a placeholder: the upload step makes it one
                        // (and sends it only if it changed since).
                        Follow(move.NewLocalPath);
                    }
                }).ConfigureAwait(false);
            }

            foreach (var delete in plan.Deletes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remotePath = delete.IsDirectory ? delete.RemotePath.TrimEnd('/') + "/" : delete.RemotePath;
                await StepAsync(delete.From, $"Delete propagation failed for '{remotePath}'", () => HandleRemoteDeleteAsync(remotePath)).ConfigureAwait(false);
            }

            while (folders.TryDequeue(out var folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await StepAsync(folder.From, $"Upload failed for folder '{RelativeOf(folder.LocalPath)}'", async () =>
                {
                    foreach (var inside in await CreateRemoteFolderAsync(folder.LocalPath).ConfigureAwait(false))
                    {
                        Follow(inside);
                    }
                }).ConfigureAwait(false);
            }

            using var slots = new SemaphoreSlim(ParallelUploads);
            await Task.WhenAll(uploads.Select(async upload =>
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await StepAsync(upload.From, $"Upload failed for '{upload.LocalPath}'", async () =>
                    {
                        try
                        {
                            await UploadAsync(upload.LocalPath, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && !File.Exists(upload.LocalPath))
                        {
                            // Renamed or deleted here while it was sent: that
                            // change was recorded, and the next run sends it.
                            _log?.Invoke($"'{RelativeOf(upload.LocalPath)}' was renamed or deleted here while it was sent; the next run sends that change.");
                        }
                    }).ConfigureAwait(false);
                }
                finally
                {
                    slots.Release();
                }
            })).ConfigureAwait(false);
        }
        catch (Exception ex) when (_server is not null && ServerConnection.IsUnreachable(ex))
        {
            _server.Failed(ex.Message);
            ReportKeptOffline(LocalChangePlanner.Plan(_changes.Pending(), this, DateTimeOffset.UtcNow));
        }
        finally
        {
            SetSending(0);
        }
    }

    /// <summary>
    /// One step of the plan; its changes are done with once it has run, or
    /// failed for any reason but an unreachable server. A failure is shown on
    /// the file (except while offline, when it is still waiting to upload).
    /// </summary>
    private async Task StepAsync(IReadOnlyList<LocalChange> from, string failure, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            ClearStates(from);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !(_server is not null && ServerConnection.IsUnreachable(ex)))
        {
            ReportProblem($"{failure}: {ex.Message}");
            foreach (var localPath in ChangedPaths(from))
            {
                if (File.Exists(localPath) || Directory.Exists(localPath))
                {
                    _itemStates.CouldNotSync(localPath, ex.Message);
                }
            }
        }

        _changes.Complete(from);
        AddSending(-1);
    }

    /// <summary>The local paths a plan step touched (a rename has both names).</summary>
    private static IEnumerable<string> ChangedPaths(IReadOnlyList<LocalChange> from)
    {
        foreach (var change in from)
        {
            yield return change.LocalPath;
            if (change.NewLocalPath is not null)
            {
                yield return change.NewLocalPath;
            }
        }
    }

    /// <summary>
    /// Done with <paramref name="from"/>: their files show no state, unless
    /// another change of the file still waits (one reported again while this
    /// was sent, or put back because the file was still downloading).
    /// </summary>
    private void ClearStates(IReadOnlyList<LocalChange> from)
    {
        foreach (var localPath in ChangedPaths(from).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_changes.HasChangeFor(localPath, from))
            {
                _itemStates.Clear(localPath);
            }
        }
    }

    /// <summary>
    /// The file reached the server, or has nothing to send: it shows no
    /// state, unless a change of it still waits. Inside a plan step that is
    /// the step's own change, so the step clears it once done (ClearStates).
    /// </summary>
    private void ClearState(string localPath)
    {
        if (!_changes.HasChangeFor(localPath))
        {
            _itemStates.Clear(localPath);
        }
    }

    /// <summary>Says once per change what waits for the server, as before, and counts it.</summary>
    private void ReportKeptOffline(LocalChangePlan plan)
    {
        SetWaiting(plan.Moves.Count + plan.Deletes.Count + plan.Folders.Count +
                   plan.Uploads.Count(u => HasUnsyncedLocalChanges(u.LocalPath)));

        bool First(IReadOnlyList<LocalChange> from)
        {
            lock (_reportedOffline)
            {
                return from.Count(c => _reportedOffline.Add(c.Seq)) > 0;
            }
        }

        foreach (var move in plan.Moves.Where(m => First(m.From)))
        {
            _log?.Invoke($"Offline: '{move.RemotePath}' is renamed to '{RelativeOf(move.NewLocalPath)}' on the server once it answers.");
        }

        foreach (var delete in plan.Deletes.Where(d => First(d.From)))
        {
            _log?.Invoke($"Offline: '{delete.RemotePath}' is deleted on the server once it answers.");
        }

        foreach (var folder in plan.Folders.Where(f => First(f.From)))
        {
            _log?.Invoke($"Offline: folder '{RelativeOf(folder.LocalPath)}' is created on the server once it answers.");
        }

        foreach (var upload in plan.Uploads.Where(u => HasUnsyncedLocalChanges(u.LocalPath) && First(u.From)))
        {
            // FileSystemWatcher is not guaranteed to report edits made through
            // a hydrated Cloud Files placeholder. The offline plan is the
            // authoritative point at which we know the upload is waiting, so
            // also set the Explorer state here.
            _itemStates.WaitingToUpload(upload.LocalPath);
            _log?.Invoke($"Offline: '{RelativeOf(upload.LocalPath)}' is uploaded once the server answers.");
        }
    }

    LocalItem? ILocalSide.Find(string localPath)
    {
        if (Directory.Exists(localPath))
        {
            return new LocalItem(true, _placeholders.QueryState(localPath).FileId, 0, default);
        }

        var info = new FileInfo(localPath);
        return info.Exists
            ? new LocalItem(false, _placeholders.QueryState(localPath).FileId, info.Length, info.LastWriteTimeUtc)
            : null;
    }

    bool ILocalSide.IsHydrating(string localPath) => _connection?.IsHydratingUnder(localPath) == true;

    bool ILocalSide.IsOwnWrite(string localPath) => IsProviderWrite(localPath) && !HasUnsyncedLocalChanges(localPath);

    /// <summary>
    /// A delete Cloud Files reported. One the server made, carried out here,
    /// is not sent back.
    /// </summary>
    private void RecordNotifiedDelete(string localPath, string remotePath, long fileId, bool isDirectory)
    {
        if (!IsServerDelete(remotePath))
        {
            _changes.Deleted(localPath, remotePath, fileId, isDirectory);
            ClearState(localPath);
        }
    }

    /// <summary>
    /// One background sync run, the counterpart of a Nextcloud sync run's
    /// remote discovery for the folders this PC has listed. A folder that
    /// holds something local (the root, a pinned folder, a downloaded, kept or
    /// edited file) is listed every run, parents first, one at a time, never
    /// recursively, and what changed on the server is applied: new items
    /// appear, changed files take the new version (keeping a local edit as a
    /// conflict copy), and deleted ones go. A folder that holds nothing local
    /// is online-only: it is not listed; once it is older than the poll
    /// interval it is given on-demand population again, and Windows lists it
    /// from the server when it is next opened. Folders never opened are left
    /// to be listed when they are. Offline, nothing is asked; the connection
    /// check runs instead.
    /// </summary>
    private async Task SyncRemoteAsync(CancellationToken cancellationToken)
    {
        if (_server is { IsOnline: false })
        {
            _server.CheckNow();
            return;
        }

        // The run sent the local changes before this, so no listing takes
        // what they change for the server's current state.
        var relistAge = SyncScheduler.ConfiguredRelistAge();
        var local = LocalDirectories();
        foreach (var directory in ListedDirectories().ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (local.Contains(directory) || IsPinnedFolder(directory))
            {
                if (!await RefreshDirectoryAsync(directory, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            // Online-only: nothing here would be lost by asking the server
            // only when it is opened. Leave it fully populated until it is
            // older than the relist age, then let the next opening list it.
            if (_listedAt.TryGetValue(directory, out var listedAt) &&
                DateTimeOffset.UtcNow - listedAt < relistAge)
            {
                continue;
            }

            if (_placeholders.EnableOnDemandPopulation(LocalPathOf(directory)))
            {
                _log?.Invoke($"'{RelativeOf(LocalPathOf(directory))}' holds nothing local; it is listed from the server when it is next opened.");
            }
        }
    }

    /// <summary>
    /// The folders a sync run keeps listing, as server paths ("" for the
    /// root): each holds, at any depth below it, something whose local copy a
    /// server change could not simply replace or drop: a downloaded, kept or
    /// edited file, a plain local file, or a change made here not sent yet.
    /// Any depth, so a server delete or rename of a folder above a downloaded
    /// file is still seen. Worked out once per run (see
    /// <see cref="LocalDirectories(SqliteMetadataStore, string, bool, Func{string, bool})"/>).
    /// A pinned folder counts too; that is checked per listed folder
    /// (<see cref="IsPinnedFolder"/>).
    /// </summary>
    private HashSet<string> LocalDirectories()
    {
        var local = LocalDirectories(_store, _syncRootPath, IsSimulated, CloudPlaceholderService.HasLocalData);
        foreach (var change in _changes.Pending())
        {
            foreach (var path in change.NewLocalPath is null ? [change.LocalPath] : new[] { change.LocalPath, change.NewLocalPath })
            {
                var relative = RelativeOf(path);
                if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                {
                    // The item itself, in case it is a folder, and every folder above it.
                    AddWithParents(local, relative);
                }
            }
        }

        return local;
    }

    /// <summary>
    /// The folders holding local data at any depth, from one pass over the
    /// store: a file the provider downloaded (also "Always keep"), or one
    /// whose data is on this PC by its attributes (one lookup each, no handle
    /// opened), which also takes in edits and plain files the store knows.
    /// In mock mode also plain files the store does not know yet.
    /// </summary>
    internal static HashSet<string> LocalDirectories(
        SqliteMetadataStore store,
        string syncRootPath,
        bool simulated,
        Func<string, bool> hasLocalData)
    {
        // The root is always kept current: it is what Explorer opens first.
        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in store.GetAll())
        {
            if (row.IsDirectory)
            {
                continue;
            }

            known.Add(row.LocalPath);
            if ((row.IsHydrated && File.Exists(row.LocalPath)) || hasLocalData(row.LocalPath))
            {
                AddWithParents(local, ParentOf(row.RemotePath));
            }
        }

        if (simulated && Directory.Exists(syncRootPath))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(syncRootPath, "*", SearchOption.AllDirectories).Where(f => !known.Contains(f)))
                {
                    AddWithParents(local, ParentOf(Path.GetRelativePath(syncRootPath, file).Replace('\\', '/')));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: the folders found so far stand.
            }
        }

        return local;
    }

    /// <summary>For tests: whether a sync run keeps listing this folder.</summary>
    internal static bool HoldsLocalFiles(
        SqliteMetadataStore store,
        string syncRootPath,
        string directory,
        bool simulated = false,
        Func<string, bool>? hasLocalData = null)
    {
        var relative = directory.Trim('/');
        return relative.Length == 0 ||
               IsPinned(Path.Combine(syncRootPath, relative.Replace('/', Path.DirectorySeparatorChar))) ||
               LocalDirectories(store, syncRootPath, simulated, hasLocalData ?? CloudPlaceholderService.HasLocalData).Contains(relative);
    }

    /// <summary>"Always keep on this device" on the folder itself.</summary>
    private bool IsPinnedFolder(string directory) => directory.Length > 0 && IsPinned(LocalPathOf(directory));

    private static bool IsPinned(string localDirectory)
    {
        try
        {
            return Directory.Exists(localDirectory) && (File.GetAttributes(localDirectory) & PinnedAttribute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: treat it as local rather than risk missing a change.
            return true;
        }
    }

    /// <summary>Adds a folder and every folder above it, up to the root.</summary>
    private static void AddWithParents(HashSet<string> folders, string relative)
    {
        // A folder already there came with its parents.
        for (var folder = relative.Trim('/'); folders.Add(folder) && folder.Length > 0; folder = ParentOf(folder))
        {
        }
    }

    /// <summary>The folder a server path is in: "a/b" for "a/b/c.txt", "" at the top.</summary>
    private static string ParentOf(string remotePath)
    {
        var path = remotePath.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>
    /// The folders Windows has had listed (fully populated directory
    /// placeholders; the platform keeps that state on disk), parents first.
    /// </summary>
    private IEnumerable<string> ListedDirectories() =>
        _store.GetDirectories()
            .Select(row => row.RemotePath.TrimEnd('/'))
            .Prepend(string.Empty)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(directory => directory.Length == 0 ? 0 : directory.Count(c => c == '/') + 1)
            .Where(directory => IsListed(LocalPathOf(directory)));

    private bool IsListed(string localDirectory) =>
        Directory.Exists(localDirectory) &&
        _placeholders.QueryState(localDirectory) is { IsPlaceholder: true, Partial: false };

    private string LocalPathOf(string relative) =>
        relative.Length == 0
            ? _syncRootPath
            : Path.Combine(_syncRootPath, relative.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Lists one listed folder and applies what changed. False when the
    /// server could not be reached: the drive is offline and the run stops.
    /// </summary>
    private async Task<bool> RefreshDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(LocalPathOf(directory)))
        {
            return true; // removed by its parent's changes in this run
        }

        IReadOnlyList<PlaceholderEntry> entries;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(SyncRootConnection.ListingTimeout);
            try
            {
                entries = await ListNewRemoteEntriesAsync(directory, timeout.Token).ConfigureAwait(false);
                _server?.Succeeded();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var reason = timeout.IsCancellationRequested
                    ? $"no answer within {SyncRootConnection.ListingTimeout.TotalSeconds:0} s"
                    : ex.Message;
                if (timeout.IsCancellationRequested || ServerConnection.IsUnreachable(ex))
                {
                    _server?.Failed(reason);
                    return false;
                }

                // Typically a folder the server removed since its parent was
                // listed; the parent's next listing removes it here.
                ReportProblem($"Background sync could not list '{directory}' ({reason}); the next run tries again.");
                return true;
            }
        }

        if (entries.Count > 0)
        {
            var items = entries
                .Select(entry => new RcloneItemMetadata(
                    entry.FileIdentity,
                    directory.Length == 0 ? entry.RelativeName : $"{directory}/{entry.RelativeName}",
                    entry.Size,
                    entry.ModifiedUtc,
                    entry.IsDirectory))
                .ToList();
            var result = _placeholders.CreatePlaceholders(_syncRootPath, items);
            _log?.Invoke(
                $"Server added in '{directory}': {string.Join(", ", entries.Select(e => e.RelativeName))}" +
                (result.Failed > 0 ? $" ({result.Failed} could not be shown: {string.Join("; ", result.Errors)})." : "."));
        }

        await ApplyServerChangesAsync(directory).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// True when the server's copy is not the one this client last saw: a
    /// known file changed or gone, or a file at a path this client never saw.
    /// </summary>
    private static bool ServerMovedOn(FileRecord? record, CloudItem? server) =>
        record is null
            ? server is { IsDirectory: false }
            : server is null || server.IsDirectory || DiffersFromServer(record, server);

    private static bool DiffersFromServer(FileRecord record, CloudItem item) =>
        FileVersions.Differs(record.Size, record.ModifiedUtc, record.Hash, item.Size, item.ModifiedTime, item.Hash);

    /// <summary>
    /// Applies what the last listing of a directory found, as Nextcloud does:
    /// files deleted on the server are deleted locally, files changed on the
    /// server get their new size/time and drop their local copy. A local
    /// change that has not reached the server is never lost: an edited file
    /// the server changed too is kept as a conflict copy, and one the server
    /// deleted is uploaded again. Only the client's own upload or rename still
    /// on its way is waited for (the listing may predate it); the upload then
    /// checks the server itself.
    /// </summary>
    private async Task ApplyServerChangesAsync(string relativeDirectory)
    {
        if (!_pendingDiffs.TryRemove(relativeDirectory, out var diff))
        {
            return;
        }

        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var record in diff.Deleted)
            {
                if (record.IsDirectory)
                {
                    ApplyServerDirectoryDelete(record, diff.ListedAt);
                }
                else if (!IsOwnChangeInFlight(record.LocalPath, record.RemotePath, diff.ListedAt))
                {
                    ApplyServerFileDelete(record);
                }
            }

            foreach (var change in diff.Changed)
            {
                if (!IsOwnChangeInFlight(change.Record.LocalPath, change.Record.RemotePath, diff.ListedAt))
                {
                    await ApplyServerVersionAsync(change.Record.LocalPath, change.Record.RemotePath, change.Size, change.ModifiedUtc, change.Hash).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            ReportProblem($"Applying server changes in '{relativeDirectory}' failed: {ex.Message}");
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// The file is gone from the server, so it goes locally too; the local
    /// delete is not sent back. One with local changes the server never got
    /// stays and is uploaded again instead (Nextcloud restores it the same way).
    /// </summary>
    private void ApplyServerFileDelete(FileRecord record)
    {
        if (HasUnsyncedLocalChanges(record.LocalPath))
        {
            RestoreToServer(record, "deleted it");
            return;
        }

        _serverDeletes[record.RemotePath] = DateTimeOffset.UtcNow;
        try
        {
            if (File.Exists(record.LocalPath))
            {
                File.Delete(record.LocalPath);
            }

            _store.DeleteByRemotePath(record.RemotePath);
            _log?.Invoke($"Server deleted '{record.RemotePath}'; removed the local file.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Typically open in another program. The row stays, so the next
            // listing of this directory tries again.
            _serverDeletes.TryRemove(record.RemotePath, out _);
            ReportProblem($"Server deleted '{record.RemotePath}', but the local file could not be removed: {ex.Message}");
        }
    }

    /// <summary>
    /// The folder is gone from the server, so it goes locally too. Files in
    /// it with local changes the server never got stay, with the folders
    /// they are in, and are uploaded again (as Nextcloud keeps them); only
    /// the rest goes. The client's own upload or rename still on its way is
    /// waited for.
    /// </summary>
    private void ApplyServerDirectoryDelete(FileRecord record, DateTimeOffset listedAt)
    {
        var prefix = record.RemotePath.TrimEnd('/') + "/";

        // A remote without empty folders never lists one: absent is not deleted.
        if (!_remoteKeepsEmptyFolders && Directory.Exists(record.LocalPath) &&
            !Directory.EnumerateFileSystemEntries(record.LocalPath).Any())
        {
            return;
        }

        var edited = new List<string>();
        if (Directory.Exists(record.LocalPath))
        {
            // Only enumerations by other processes are forwarded to the
            // provider, so this sees exactly what is on disk.
            foreach (var file in Directory.EnumerateFiles(record.LocalPath, "*", SearchOption.AllDirectories))
            {
                if (IsOwnChangeInFlight(file, RelativeOf(file), listedAt))
                {
                    _log?.Invoke($"Server deleted folder '{prefix}', but '{RelativeOf(file)}' is still being sent; the next listing deletes it.");
                    return;
                }

                if (HasUnsyncedLocalChanges(file))
                {
                    edited.Add(file);
                }
            }
        }

        if (edited.Count > 0)
        {
            KeepEditedInDeletedFolder(record, prefix, edited);
            return;
        }

        _serverDeletes[prefix] = DateTimeOffset.UtcNow;
        try
        {
            if (Directory.Exists(record.LocalPath))
            {
                Directory.Delete(record.LocalPath, recursive: true);
            }

            _store.DeleteTree(prefix);
            _store.DeleteByRemotePath(record.RemotePath);
            _log?.Invoke($"Server deleted folder '{prefix}'; removed the local folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _serverDeletes.TryRemove(prefix, out _);
            ReportProblem($"Server deleted folder '{prefix}', but the local folder could not be removed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deleted folder, edited files: those files and their folders stay and
    /// are uploaded again; every other file goes, and so do folders left
    /// empty. Rows go for what is deleted and for the kept files (which are
    /// new to the server now); the folders kept keep theirs.
    /// </summary>
    private void KeepEditedInDeletedFolder(FileRecord record, string prefix, IReadOnlyList<string> edited)
    {
        var keep = new HashSet<string>(edited, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(record.LocalPath, "*", SearchOption.AllDirectories).ToList())
        {
            if (keep.Contains(file))
            {
                continue;
            }

            var remotePath = RelativeOf(file);
            _serverDeletes[remotePath] = DateTimeOffset.UtcNow;
            try
            {
                File.Delete(file);
                _store.DeleteByRemotePath(remotePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _serverDeletes.TryRemove(remotePath, out _);
                ReportProblem($"Server deleted '{remotePath}', but the local file could not be removed: {ex.Message}");
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(record.LocalPath, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length)
                     .ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                var remotePath = RelativeOf(directory) + "/";
                _serverDeletes[remotePath] = DateTimeOffset.UtcNow;
                try
                {
                    Directory.Delete(directory);
                    _store.DeleteByRemotePath(remotePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _serverDeletes.TryRemove(remotePath, out _);
                }
            }
        }

        // What stays is new to the server now: no rows, so no listing takes
        // it for deleted again; the uploads bring the folders back with them.
        _store.DeleteTree(prefix);
        _store.DeleteByRemotePath(record.RemotePath);
        foreach (var file in edited)
        {
            _log?.Invoke($"Server deleted folder '{prefix}', but '{RelativeOf(file)}' has local changes: it is kept and uploaded again.");
            _changes.Changed(file);
            _itemStates.WaitingToUpload(file);
        }
    }

    /// <summary>
    /// A file with local changes the server lost track of (it deleted the
    /// file, or its folder): the local version stays and is uploaded as new.
    /// </summary>
    private void RestoreToServer(FileRecord record, string what)
    {
        _store.DeleteByRemotePath(record.RemotePath);
        _log?.Invoke($"Server {what} '{record.RemotePath}', which has local changes: they are kept and uploaded again.");
        _changes.Changed(record.LocalPath);
        _itemStates.WaitingToUpload(record.LocalPath);
    }

    /// <summary>
    /// Keeps the local version of a file the server changed too: a copy of
    /// it next to it, named like Nextcloud's conflict copies, which is
    /// uploaded as a file of its own (OneDrive and Nextcloud keep both).
    /// A copy kept already of the file as it is now is used again: the
    /// server's version could not be put in place then (the file was in
    /// use), and this is the next try. Returns the copy's path, or null if it
    /// could not be made.
    /// </summary>
    private string? KeepConflictCopy(string localPath, string remotePath)
    {
        try
        {
            if (ConflictCopies.KeptCopyOf(localPath) is { } kept)
            {
                _log?.Invoke($"Conflict: '{remotePath}' changed here and on the server; this PC's version is kept as '{RelativeOf(kept)}' already.");
                return kept;
            }

            var copy = ConflictCopies.PathFor(localPath, File.GetLastWriteTime(localPath));
            File.Copy(localPath, copy);
            _itemStates.ConflictCopy(copy);
            _log?.Invoke($"Conflict: '{remotePath}' changed here and on the server; the server's version keeps the name, this PC's is kept as '{RelativeOf(copy)}'.");
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportProblem($"Conflict: '{remotePath}' changed here and on the server, and the local version could not be copied aside ({ex.Message}); it is kept as it is for now.");
            return null;
        }
    }

    /// <summary>
    /// The local copy is replaced by the server's version. A local change
    /// not yet sent is kept first as a conflict copy, and if it cannot be,
    /// nothing is replaced; unless it is the server's content already, which
    /// is no conflict (Nextcloud compares content hashes the same way). In
    /// cloud mode the file becomes a dehydrated placeholder with the server's
    /// size and time (a plain local file is made a placeholder first); in mock
    /// mode the server's bytes are written over it.
    /// What the file holds goes only if it is still as it was when this was
    /// decided: the step that replaces it checks the file's size and write
    /// time while no other program can open it, so a save made meanwhile is
    /// never thrown away. Such a file is left as it is and looked at again.
    /// </summary>
    /// <param name="hash">The server version's hash, when the remote keeps one.</param>
    /// <param name="compareContent">False when the caller has just found the content differs.</param>
    private async Task<bool> ApplyServerVersionAsync(
        string localPath,
        string remotePath,
        long size,
        DateTimeOffset? modifiedUtc,
        string? hash,
        bool compareContent = true)
    {
        // The file as it is when what to do with it is decided.
        if (CloudPlaceholderService.LocalVersion(localPath) is not { } seen)
        {
            return false;
        }

        if (HasUnsyncedLocalChanges(localPath))
        {
            if (compareContent && await IsServerContentAsync(localPath, hash).ConfigureAwait(false))
            {
                TakeAsInSync(localPath, remotePath, size, modifiedUtc, hash, seen);
                ClearState(localPath);
                return true;
            }

            if (KeepConflictCopy(localPath, remotePath) is null)
            {
                return false;
            }
        }

        _providerWrites[localPath] = DateTimeOffset.UtcNow;
        Replacement replaced;
        if (IsSimulated)
        {
            replaced = DownloadOver(localPath, remotePath, seen);
        }
        else
        {
            if (!_placeholders.QueryState(localPath).IsPlaceholder &&
                !_placeholders.ConvertToPlaceholder(localPath, CloudPlaceholderService.BuildFileIdentity(remotePath), markInSync: false))
            {
                ReportProblem($"Server changed '{remotePath}', but the local file could not be made a placeholder to take it.");
                return false;
            }

            var hr = _placeholders.UpdateFromRemote(localPath, size, modifiedUtc, discardLocalChanges: true, unchangedFrom: seen);
            replaced = CfApi.Succeeded(hr) ? Replacement.Done
                : hr == CloudPlaceholderService.ChangedSinceLookedAt ? Replacement.ChangedMeanwhile
                : Replacement.Failed;
            if (replaced == Replacement.Failed)
            {
                // Typically open in another program. The row keeps the old
                // metadata, so the next listing retries.
                ReportProblem($"Server changed '{remotePath}', but the local copy could not be updated: {HResult.Describe(hr)}.");
            }
        }

        if (replaced == Replacement.ChangedMeanwhile)
        {
            // Saved again meanwhile: that is a change of its own, which the
            // check before its upload compares with the server again (and,
            // the server's version being newer, keeps as a conflict copy).
            _log?.Invoke($"Server changed '{remotePath}', but the local file changed again meanwhile; it is left as it is and looked at again.");
            _changes.Changed(localPath);
            _itemStates.WaitingToUpload(localPath);
            return false;
        }

        if (replaced != Replacement.Done)
        {
            return false;
        }

        _store.UpdateRemoteMetadata(remotePath, size, modifiedUtc, dehydrated: !IsSimulated, hash);
        ClearState(localPath);
        _log?.Invoke($"Server changed '{remotePath}' ({size} bytes); local copy will be re-downloaded on next open.");
        return true;
    }

    /// <summary>
    /// Whether a local file holds the server's content: its hash, of the
    /// type the remote keeps, made by rclone, equals the server's. False when
    /// the remote keeps no hash or this version has none, and for a file not
    /// all here, which reading would download.
    /// </summary>
    private async Task<bool> IsServerContentAsync(string localPath, string? serverHash)
    {
        if (_capabilities.HashType is not { } hashType || serverHash is null ||
            (!IsSimulated && (File.GetAttributes(localPath) & RecallOnDataAccessAttribute) != 0))
        {
            return false;
        }

        try
        {
            _rc ??= new RcloneRcClient(_rclone);
            var local = await _rc.HashLocalFileAsync(localPath, hashType).ConfigureAwait(false);
            return local is not null && FileVersions.SameHash($"{hashType}:{local}", serverHash) == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            _log?.Invoke($"Could not hash '{RelativeOf(localPath)}' to compare it with the server's: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The local file is the server's version already, though it changed on
    /// both sides (the same edit made twice, or saved again): it is recorded
    /// as in sync with no copy and nothing sent.
    /// </summary>
    /// <param name="compared">
    /// The file's size and write time from before its content was compared.
    /// They are what is recorded as in sync, not the file's as it is now: if
    /// it was saved again meanwhile, it no longer matches them and still
    /// counts as edited.
    /// </param>
    private void TakeAsInSync(string localPath, string remotePath, long size, DateTimeOffset? modifiedUtc, string? hash, (long Length, DateTime WriteUtc) compared)
    {
        var state = _placeholders.QueryState(localPath);
        _store.UpsertFile(
            remotePath: remotePath,
            localPath: localPath,
            fileIdentity: remotePath,
            fileId: state.FileId != 0 ? state.FileId : _store.GetByRemotePath(remotePath)?.FileId ?? 0,
            isPlaceholder: !IsSimulated,
            isDirectory: false,
            size: size,
            modifiedUtc: modifiedUtc,
            inSync: true,
            localSize: compared.Length,
            localWriteUtc: compared.WriteUtc,
            hash: hash);
        _localChangeAt[remotePath] = DateTimeOffset.UtcNow;
        _lastSyncedState[localPath] = compared;

        if (!IsSimulated)
        {
            var identity = CloudPlaceholderService.BuildFileIdentity(remotePath);
            if (state.IsPlaceholder)
            {
                _placeholders.MarkInSync(localPath, identity, compared.Length);
            }
            else
            {
                _placeholders.ConvertToPlaceholder(localPath, identity, markInSync: true);
            }
        }

        _log?.Invoke($"'{remotePath}' changed here and on the server to the same content: no conflict, nothing to send.");
    }

    /// <summary>How putting the server's version in place of a local file went.</summary>
    private enum Replacement
    {
        Done,
        Failed,

        /// <summary>The file changed since it was looked at, and was left as it is.</summary>
        ChangedMeanwhile,
    }

    /// <summary>
    /// Mock mode: writes the server's current bytes over the local file, if
    /// it still has the size and write time it had when this was decided. It
    /// is opened sharing nothing, so no other program has it open or can open
    /// it while it is checked and written.
    /// </summary>
    private Replacement DownloadOver(string localPath, string remotePath, (long Length, DateTime WriteUtc) unchangedFrom)
    {
        if (_serve is null)
        {
            return Replacement.Failed;
        }

        try
        {
            using (var source = _serve.OpenRangeAsync(remotePath, 0, -1).GetAwaiter().GetResult())
            using (var target = new FileStream(localPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (target.Length != unchangedFrom.Length || File.GetLastWriteTimeUtc(target.SafeFileHandle) != unchangedFrom.WriteUtc)
                {
                    return Replacement.ChangedMeanwhile;
                }

                target.SetLength(0);
                source.CopyTo(target);
            }

            RememberSyncedState(localPath);
            return Replacement.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            ReportProblem($"Server changed '{remotePath}', but the local file could not be replaced: {ex.Message}");
            return Replacement.Failed;
        }
    }

    private async Task<(long? Size, DateTimeOffset? ModifiedUtc, string? Hash)> GetServerMetadataAsync(
        string relative,
        CancellationToken cancellationToken)
    {
        try
        {
            // One lookup of the file, not a listing of its whole folder.
            _rc ??= new RcloneRcClient(_rclone);
            var item = await _rc.StatAsync(_remote, relative, Listing(), cancellationToken).ConfigureAwait(false);
            return item is { IsDirectory: false } ? (item.Size, item.ModifiedTime, item.Hash) : (null, null, null);
        }
        catch (Exception ex)
        {
            ReportProblem($"Could not read server metadata for '{relative}': {ex.Message}");
            return (null, null, null);
        }
    }

    /// <summary>
    /// True while the client's own change of the file may not show in a
    /// listing yet: a change of it waiting for a run or being sent (an upload
    /// checks the server itself before sending, and the server wins there
    /// too), or an upload or rename that landed around when the listing was
    /// taken. That is not a conflict, just a listing that may predate the change.
    /// </summary>
    private bool IsOwnChangeInFlight(string localPath, string remotePath, DateTimeOffset listedAt) =>
        _changes.HasChangeFor(localPath) ||
        (_localChangeAt.TryGetValue(remotePath, out var changedAt) && changedAt >= listedAt - TimeSpan.FromSeconds(2));

    /// <summary>
    /// True when the local file holds something the server does not have: a
    /// placeholder edited since it was last in sync, or a plain local file
    /// (mock mode: a file that differs from when it last matched the server).
    /// </summary>
    private bool HasUnsyncedLocalChanges(string localPath)
    {
        if (!File.Exists(localPath))
        {
            return false;
        }

        if (IsSimulated)
        {
            var info = new FileInfo(localPath);
            return !(_lastSyncedState.TryGetValue(localPath, out var synced) &&
                     synced == (info.Length, info.LastWriteTimeUtc));
        }

        var state = _placeholders.QueryState(localPath);
        return !state.IsPlaceholder || !state.InSync || !MatchesLastSync(localPath, _store.GetByLocalPath(localPath));
    }

    /// <summary>
    /// Whether a file still has the size and write time it had when it was
    /// last in sync. In-sync alone does not prove a file unchanged: Windows
    /// does not clear it for every write (an append that starts on a page
    /// boundary leaves it set). So, like other sync clients (Nextcloud
    /// compares both with its journal), an edit is also told by these. True
    /// when the record cannot say, which leaves it to in-sync.
    /// </summary>
    private static bool MatchesLastSync(string localPath, FileRecord? record)
    {
        if (record is null || record.IsDirectory || record.SyncedLocalState is not { } synced)
        {
            return true;
        }

        var info = new FileInfo(localPath);
        return info.Exists && info.Length == synced.Size && info.LastWriteTimeUtc == synced.WriteUtc;
    }

    private bool IsProviderWrite(string localPath)
    {
        if (!_providerWrites.TryGetValue(localPath, out var at))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - at <= ProviderWriteGuard)
        {
            return true;
        }

        _providerWrites.TryRemove(localPath, out _);
        return false;
    }

    private bool IsServerDelete(string remotePath)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, at) in _serverDeletes)
        {
            if (now - at > ServerDeleteGuard)
            {
                _serverDeletes.TryRemove(key, out _);
                continue;
            }

            // A folder is guarded as "dir/". Its own local delete may come as
            // "dir" too: once its row is gone, nothing says it was a folder.
            if (string.Equals(key.TrimEnd('/'), remotePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                (key.EndsWith('/') && remotePath.StartsWith(key, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Turns a flat remote listing into the full namespace: every file plus
    /// every ancestor directory, parents before children. Directories reported
    /// by the listing are kept as-is; missing ones are synthesised because
    /// rclone's recursive listing omits them.
    /// </summary>
    public static IReadOnlyList<RcloneItemMetadata> BuildNamespace(IReadOnlyList<CloudItem> items)
    {
        var directories = new Dictionary<string, RcloneItemMetadata>(StringComparer.OrdinalIgnoreCase);
        var files = new List<RcloneItemMetadata>();

        foreach (var item in items)
        {
            var relative = item.RelativePath.Replace('\\', '/').Trim('/');
            if (relative.Length == 0)
            {
                continue;
            }

            if (item.IsDirectory)
            {
                directories[relative] = new RcloneItemMetadata(
                    item.Path.TrimEnd('/') + "/",
                    relative,
                    0,
                    item.ModifiedTime,
                    IsDirectory: true);
                continue;
            }

            files.Add(new RcloneItemMetadata(
                item.Path,
                relative,
                item.Size,
                item.ModifiedTime,
                IsDirectory: false));

            // Add every ancestor so CfCreatePlaceholders can create the file
            // inside a directory placeholder instead of a plain directory.
            var separator = relative.LastIndexOf('/');
            while (separator > 0)
            {
                var parent = relative[..separator];
                directories.TryAdd(parent, new RcloneItemMetadata(
                    parent + "/",
                    parent,
                    0,
                    ModifiedUtc: null,
                    IsDirectory: true));
                separator = parent.LastIndexOf('/');
            }
        }

        return directories.Values
            .OrderBy(d => d.RelativePath.Count(c => c == '/'))
            .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Concat(files)
            .ToList();
    }

    /// <summary>
    /// Mock-mode materialisation: writes the remote bytes to a real file so the
    /// E2E can read them back, instead of creating dehydrated placeholders.
    /// </summary>
    private async Task<int> MaterializeFilesAsync(
        IReadOnlyList<RcloneItemMetadata> files,
        CancellationToken cancellationToken)
    {
        var created = 0;
        foreach (var file in files)
        {
            var localPath = Path.Combine(
                _syncRootPath,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            var parent = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var downloaded = false;
            if (_serve is not null && file.Size > 0)
            {
                try
                {
                    await using var source = await _serve
                        .OpenRangeAsync(file.RelativePath, 0, -1, cancellationToken)
                        .ConfigureAwait(false);
                    await using var target = File.Create(localPath);
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    downloaded = true;
                }
                catch (Exception ex)
                {
                    ReportProblem($"Mock download failed for '{file.RelativePath}': {ex.Message}");
                }
            }

            if (!downloaded && !File.Exists(localPath))
            {
                using var stream = File.Create(localPath);
                stream.SetLength(0);
            }

            _store.UpsertFile(
                file.RemotePath,
                localPath,
                file.RemotePath,
                fileId: 0,
                isPlaceholder: false,
                isDirectory: false,
                size: file.Size,
                modifiedUtc: file.ModifiedUtc);

            _store.MarkHydrated(file.RemotePath);
            RememberSyncedState(localPath);
            created++;
        }

        _log?.Invoke($"MOCK: materialised {created} file(s).");
        return created;
    }

    /// <summary>Pillar 4: watch the sync root and push local edits back to rclone.</summary>
    public void StartWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        _watcher = new FileSystemWatcher(_syncRootPath)
        {
            IncludeSubdirectories = true,
            // Attributes: "Always keep on this device" / "Free up space" only
            // set the pinned / unpinned attribute; acting on it is up to us.
            // DirectoryName: folder deletes and renames (in mock mode the
            // watcher is the only source of them).
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                           NotifyFilters.Size | NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true,
        };

        // Like Nextcloud's folder watcher, it only marks what changed for the
        // next sync run; the run reads the drive and decides.
        _watcher.Created += (_, e) => RecordChanged(e.FullPath);
        _watcher.Changed += (_, e) =>
        {
            QueuePinChange(e.FullPath);

            // A folder's write time moves with what is inside it; that is not
            // a folder made here.
            if (!Directory.Exists(e.FullPath))
            {
                RecordChanged(e.FullPath);
            }
        };

        // Cloud Files reports deletes and renames of placeholders itself; the
        // watcher covers mock mode and items that are not placeholders yet.
        _watcher.Deleted += (_, e) => RecordWatcherDelete(e.FullPath);
        _watcher.Renamed += (_, e) => RecordWatcherRename(e.OldFullPath, e.FullPath);

        _log?.Invoke($"Watching '{_syncRootPath}' for local changes.");
    }

    private const FileAttributes PinnedAttribute = (FileAttributes)0x00080000;
    private const FileAttributes UnpinnedAttribute = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnDataAccessAttribute = (FileAttributes)0x00400000;

    /// <summary>
    /// Explorer's "Always keep on this device" sets the pinned attribute and
    /// "Free up space" sets unpinned (on a folder, also on what is inside);
    /// Storage Sense frees space the same way. The platform only records the
    /// request: the provider downloads (hydrates) or drops (dehydrates) the
    /// local copy. Both are idempotent, so the watcher events our own
    /// hydrate/dehydrate raise end here as no-ops.
    /// </summary>
    private void QueuePinChange(string localPath)
    {
        if (IsSimulated || _populating || IsStateFile(localPath))
        {
            return;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(localPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var pinned = (attributes & PinnedAttribute) != 0;
        var unpinned = (attributes & UnpinnedAttribute) != 0;
        if (pinned == unpinned)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var files = (attributes & FileAttributes.Directory) != 0
                    ? Directory.EnumerateFiles(localPath, "*", SearchOption.AllDirectories)
                    : [localPath];

                foreach (var file in files)
                {
                    ApplyPinState(file, keepLocal: pinned);
                }
            }
            catch (Exception ex)
            {
                ReportProblem($"Pin/unpin handling failed for '{localPath}': {ex.Message}");
            }
        });
    }

    /// <summary>
    /// One pin or unpin at a time per file: the second of two requests (the
    /// watcher's, and the one an upload makes once a file waiting to be freed
    /// is sent) then finds the work done. Two at once would open a file the
    /// other just freed with write access, which downloads it again.
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _pinLocks = new(StringComparer.OrdinalIgnoreCase);

    private void ApplyPinState(string localPath, bool keepLocal)
    {
        lock (_pinLocks.GetOrAdd(localPath, _ => new object()))
        {
            ApplyPinStateNow(localPath, keepLocal);
        }
    }

    private void ApplyPinStateNow(string localPath, bool keepLocal)
    {
        var attributes = File.GetAttributes(localPath);
        var isLocal = (attributes & RecallOnDataAccessAttribute) == 0;
        var relative = RelativeOf(localPath);

        // A file inside a pinned folder can be unpinned on its own, and the
        // other way round; the file's own attribute wins.
        if ((attributes & (keepLocal ? UnpinnedAttribute : PinnedAttribute)) != 0)
        {
            return;
        }

        if (keepLocal && !isLocal)
        {
            _providerWrites[localPath] = DateTimeOffset.UtcNow;
            var hr = _placeholders.Hydrate(localPath);
            _log?.Invoke(CfApi.Succeeded(hr)
                ? $"Always keep on this device: downloaded '{relative}'."
                : $"Always keep on this device: could not download '{relative}': {HResult.Describe(hr)}.");
        }
        else if (!keepLocal && isLocal)
        {
            // Never the only copy of a change: a file with local changes the
            // server does not have yet is uploaded first and freed after
            // (OneDrive does the same). The platform's own refusal covers
            // only what in-sync tells, which is not every edit.
            if (HasUnsyncedLocalChanges(localPath))
            {
                _log?.Invoke($"Free up space: '{relative}' has changes that are not on the server yet; it is freed once they are uploaded.");
                _changes.Changed(localPath);
                return;
            }

            _providerWrites[localPath] = DateTimeOffset.UtcNow;
            if (_placeholders.Dehydrate(localPath, new FileInfo(localPath).Length))
            {
                _store.MarkDehydrated(relative);
                _log?.Invoke($"Free up space: removed the local copy of '{relative}'.");
            }
            else
            {
                _log?.Invoke($"Free up space: kept '{relative}' (in use, or local changes not uploaded yet).");
            }
        }
    }

    /// <summary>A file written or made, or a folder made: for the next run to look at.</summary>
    private void RecordChanged(string localPath)
    {
        // Namespace population writes local files; never echo those back.
        // Its state is set by the run that plans it (see SendLocalChangesAsync):
        // many events are the client's own writes, which carry no state.
        if (!_populating && !IsStateFile(localPath))
        {
            _changes.Changed(localPath);
        }
    }

    private void RecordWatcherDelete(string localPath)
    {
        if (IsStateFile(localPath))
        {
            return;
        }

        // Directory rows carry a trailing '/', so a folder is handled as one.
        var record = _store.GetByLocalPath(localPath);

        // Cloud Files reports every delete of a placeholder (NOTIFY_DELETE),
        // and reports a move to another folder as a rename. The watcher sees
        // that same move as delete-old + create-new. It only matters for
        // files that are not placeholders yet, and in mock mode; there the
        // run tells a move by the size and write time the file had when last
        // in sync (a move keeps both; the time is exact to 100 ns).
        if (!IsSimulated && record is { IsPlaceholder: true })
        {
            return;
        }

        var remotePath = record?.RemotePath ?? RelativeOf(localPath);
        if (!IsServerDelete(remotePath))
        {
            var synced = _lastSyncedState.TryGetValue(localPath, out var state) ? state : ((long, DateTime)?)null;
            _changes.Deleted(localPath, remotePath, 0, record?.IsDirectory == true, synced);
            ClearState(localPath);
        }
    }

    private void RecordWatcherRename(string oldLocalPath, string newLocalPath)
    {
        if (IsStateFile(oldLocalPath) || IsStateFile(newLocalPath))
        {
            return;
        }

        // Cloud Files reports every rename of a placeholder (NOTIFY_RENAME,
        // with its state from before the rename). Files that are not
        // placeholders yet, and mock mode, are the watcher's.
        if (!IsSimulated && _placeholders.QueryState(newLocalPath).IsPlaceholder)
        {
            return;
        }

        var record = _store.GetByLocalPath(oldLocalPath);
        _changes.Renamed(oldLocalPath, newLocalPath, record?.RemotePath ?? RelativeOf(oldLocalPath), null);
    }

    /// <summary>
    /// Deletes a remote file, or a remote folder (path ending in '/', or one
    /// the store knows files under), and forgets it locally.
    /// </summary>
    private async Task HandleRemoteDeleteAsync(string remoteRelativePath)
    {
        // The local delete came from the server in the first place.
        if (IsServerDelete(remoteRelativePath))
        {
            return;
        }

        var relative = remoteRelativePath.TrimEnd('/');
        var record = _store.GetByRemotePath(remoteRelativePath) ?? _store.GetByRemotePath(relative);
        var isDirectory = remoteRelativePath.EndsWith('/') || record?.IsDirectory == true || _store.GetTree(relative).Count > 0;
        if (isDirectory)
        {
            await DeleteRemoteDirectoryAsync(relative).ConfigureAwait(false);
        }
        else
        {
            await DeleteRemoteFileAsync(relative, record).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The server version of a file deleted here changed since this client
    /// last saw it: it is not deleted, and comes back here with the next
    /// listing (Nextcloud restores it too, rather than lose the change).
    /// </summary>
    private async Task<bool> ChangedOnServerSinceAsync(string relative, FileRecord? record)
    {
        if (record is null || record.IsDirectory)
        {
            return false;
        }

        _rc ??= new RcloneRcClient(_rclone);
        var server = await _rc.StatAsync(_remote, relative, Listing()).ConfigureAwait(false);
        if (server is { IsDirectory: false } && DiffersFromServer(record, server))
        {
            _log?.Invoke($"'{relative}' was deleted here, but it changed on the server since: the server's version is kept and comes back.");
            return true;
        }

        return false;
    }

    private async Task DeleteRemoteFileAsync(string relative, FileRecord? record)
    {
        // Its row is claimed first; one already gone was deleted before.
        if (record is not null && !_store.DeleteByRemotePath(relative))
        {
            return;
        }

        var (fs, remoteFile) = ResolveRemoteFile(relative);
        _rc ??= new RcloneRcClient(_rclone);

        if (await ChangedOnServerSinceAsync(relative, record).ConfigureAwait(false))
        {
            return;
        }

        if (record is null && IsSimulated)
        {
            // Mock mode tracks files only, so an unknown path is a folder or a
            // file that never reached the server. The run has deleted what it
            // knows below it (deletes come before this in the order the watcher
            // reports them); now it goes if it is empty, unless a folder above
            // it is going too and takes it along.
            var slash = relative.LastIndexOf('/');
            if (slash > 0 && !Directory.Exists(Path.Combine(_syncRootPath, relative[..slash].Replace('/', Path.DirectorySeparatorChar))))
            {
                return;
            }

            await _rc.RemoveEmptyDirectoriesAsync(fs, remoteFile).ConfigureAwait(false);
            _log?.Invoke($"Removed remote folder '{fs}{remoteFile}' if it was one and is empty.");
            return;
        }

        var deleted = false;
        try
        {
            deleted = await _rc.TryDeleteFileAsync(fs, remoteFile).ConfigureAwait(false);
        }
        catch (Exception) when (record is null)
        {
            // Unknown path: it may have been a folder that was never tracked
            // (mock mode keeps no folder rows); handled below.
        }

        if (record is null && !deleted)
        {
            await _rc.RemoveEmptyDirectoriesAsync(fs, remoteFile).ConfigureAwait(false);
        }

        _log?.Invoke(deleted
            ? $"Deleted remote '{fs}{remoteFile}'."
            : $"Remote delete reported failure for '{fs}{remoteFile}'.");
    }

    /// <summary>
    /// A folder deleted locally: deletes the files this client knows under
    /// it, then removes the remote folder only if it is empty. A file added
    /// on the server that this client never listed survives, together with
    /// its folder; the next listing brings the folder back with it.
    /// </summary>
    private async Task DeleteRemoteDirectoryAsync(string relativeDirectory)
    {
        var (fs, remoteDirectory) = ResolveRemoteFile(relativeDirectory);
        _rc ??= new RcloneRcClient(_rclone);

        // A subfolder of a folder that is being deleted too: the parent's
        // delete covers everything below it, including this folder (this
        // folder's own rows are left for the parent to find). Mock mode keeps
        // no folder rows, but the watcher reports the parent's delete too, so
        // a parent that is gone is enough; its rows are no sign, since the
        // files' own deletes may have claimed them all by now. (A run plans a
        // folder deleted with what is inside as one delete; this is for one
        // whose parent's delete came in an earlier run.)
        var slash = relativeDirectory.LastIndexOf('/');
        if (slash > 0)
        {
            var parent = relativeDirectory[..slash];
            var parentLocal = Path.Combine(_syncRootPath, parent.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(parentLocal) &&
                (IsSimulated || _store.GetByRemotePath(parent + "/") is { IsDirectory: true } || _store.GetTree(parent).Count > 0))
            {
                return;
            }
        }

        foreach (var row in _store.GetTree(relativeDirectory).Where(r => !r.IsDirectory))
        {
            // Claimed here unless the file's own delete got to it first.
            if (!_store.DeleteByRemotePath(row.RemotePath))
            {
                continue;
            }

            var (_, remoteFile) = ResolveRemoteFile(row.RemotePath);
            try
            {
                // Changed on the server since: kept there, with its folder.
                if (!await ChangedOnServerSinceAsync(row.RemotePath, row).ConfigureAwait(false))
                {
                    await _rc.TryDeleteFileAsync(fs, remoteFile).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ServerConnection.IsUnreachable(ex))
            {
                // One file must not stop the rest; the folder then stays.
                ReportProblem($"Could not delete remote '{fs}{remoteFile}': {ex.Message}");
            }
        }

        _store.DeleteTree(relativeDirectory);
        _store.DeleteByRemotePath(relativeDirectory + "/");
        await _rc.RemoveEmptyDirectoriesAsync(fs, remoteDirectory).ConfigureAwait(false);

        _log?.Invoke($"Deleted remote folder '{fs}{remoteDirectory}' (only if it held nothing this client has not seen).");
    }

    /// <summary>
    /// True while the user's own delete, move or rename of the item at
    /// <paramref name="localPath"/> (or of a folder above it) waits for the
    /// server or is being sent: either its old path or, for a rename, its new
    /// one. A listing must not hand such an item out again: the server still
    /// has it, but only because it has not been told yet.
    /// </summary>
    private bool IsLocalChangePending(string localPath) => _changes.IsPending(localPath);

    /// <summary>
    /// Mirrors a local rename/move on the server, as one step of a run's plan.
    /// Moving out of the drive is a delete. False when the server never had
    /// the item (a file or folder made here, renamed before it was sent):
    /// the run then sends it under its new name.
    /// </summary>
    private async Task<bool> RenameRemoteAsync(
        string oldRemoteRelative,
        string newLocalPath,
        SyncRootConnection.RenameSource? source)
    {
        var oldRelative = oldRemoteRelative.TrimEnd('/');
        var newRelative = RelativeOf(newLocalPath);
        var record = _store.GetByRemotePath(oldRemoteRelative) ?? _store.GetByRemotePath(oldRelative);
        var isTrackedDirectory = oldRemoteRelative.EndsWith('/') ||
                                 record?.IsDirectory == true ||
                                 _store.GetTree(oldRelative).Count > 0;

        if (newRelative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(newRelative))
        {
            await HandleRemoteDeleteAsync(isTrackedDirectory ? oldRelative + "/" : oldRelative).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(newRelative, oldRelative, StringComparison.Ordinal))
        {
            return true;
        }

        // A folder the server does not have yet (Explorer's "New folder",
        // renamed before it was created there): made under the new name.
        if (Directory.Exists(newLocalPath) && !isTrackedDirectory)
        {
            return false;
        }

        return await RenameTrackedAsync(oldRelative, newRelative, newLocalPath, record, isTrackedDirectory, source).ConfigureAwait(false);
    }

    private async Task<bool> RenameTrackedAsync(
        string oldRelative,
        string newRelative,
        string newLocalPath,
        FileRecord? record,
        bool isTrackedDirectory,
        SyncRootConnection.RenameSource? source)
    {
        if (isTrackedDirectory)
        {
            await RenameRemoteDirectoryAsync(oldRelative, newRelative, newLocalPath, source).ConfigureAwait(false);
            return true;
        }

        // Never sent: whether there is anything to send under the new name
        // is the upload's call.
        if (record is null)
        {
            return false;
        }

        var (fs, oldRemote) = ResolveRemoteFile(oldRelative);
        var (_, newRemote) = ResolveRemoteFile(newRelative);

        _rc ??= new RcloneRcClient(_rclone);

        // A download of it meanwhile waits, and one starting is waited for
        // (see PrepareDownload). The placeholder is updated after: marking
        // it in sync waits for a download of it.
        await _moveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _rc.MoveFileAsync(fs, oldRemote, newRemote).ConfigureAwait(false);

            _store.RenameRemotePath(oldRelative, newRelative, newLocalPath, newRelative);
            _localChangeAt[newRelative] = DateTimeOffset.UtcNow;
            if (_lastSyncedState.TryRemove(Path.Combine(_syncRootPath, oldRelative.Replace('/', Path.DirectorySeparatorChar)), out var state))
            {
                _lastSyncedState[newLocalPath] = state;
            }
        }
        finally
        {
            _moveGate.Release();
        }

        _log?.Invoke($"Renamed remote '{fs}{oldRemote}' -> '{newRemote}'.");

        ClearState(newLocalPath);

        if (!IsSimulated)
        {
            UpdateRenamedPlaceholder(newLocalPath, newRelative, source);
        }

        return true;
    }

    /// <summary>
    /// After a rename has reached the server: gives the placeholder its new
    /// identity, and marks it in sync again if it was unchanged just before
    /// the rename (in sync and as it was synced, see MatchesLastSync) and has
    /// not been written since (the platform clears in-sync on every rename,
    /// which would otherwise make it look edited and get it downloaded and
    /// uploaded again). An item that had local changes keeps waiting for its
    /// upload.
    /// </summary>
    private void UpdateRenamedPlaceholder(string localPath, string identityPath, SyncRootConnection.RenameSource? source)
    {
        var identity = CloudPlaceholderService.BuildFileIdentity(identityPath);
        var record = _store.GetByRemotePath(identityPath);

        // A rename sent later (it was kept while offline) has no state from
        // before it; the file's size and write time against its last sync
        // tell instead. A folder has no content to compare.
        var unchanged = source is null
            ? Directory.Exists(localPath) || (record?.SyncedLocalState is not null && MatchesLastSync(localPath, record))
            : source.InSync &&
              File.GetLastWriteTimeUtc(localPath) == source.LastWriteUtc &&
              (!File.Exists(localPath) || new FileInfo(localPath).Length == source.Length) &&
              (!File.Exists(localPath) || MatchesLastSync(localPath, record));

        if (unchanged)
        {
            _placeholders.MarkInSync(localPath, identity);
        }
        else
        {
            _placeholders.UpdateFileIdentity(localPath, identity);
        }
    }

    /// <summary>
    /// A folder renamed/moved locally: moves it on the server, then re-keys
    /// every row below it and gives every placeholder below it its new
    /// identity, so opening a file there still downloads from the right path.
    /// A first listing of the folder or a download in it meanwhile waits (see
    /// <see cref="ListFolderForWindowsAsync"/> and <see cref="PrepareDownload"/>),
    /// and one under way is waited for. The placeholders are updated after
    /// they may go: marking one in sync waits for a download of it.
    /// </summary>
    private async Task RenameRemoteDirectoryAsync(
        string oldRelative,
        string newRelative,
        string newLocalPath,
        SyncRootConnection.RenameSource? source)
    {
        // Cloud Files reports the rename before it happens; the run that
        // carries it out comes after it did.
        var (fs, oldRemote) = ResolveRemoteFile(oldRelative);
        var (_, newRemote) = ResolveRemoteFile(newRelative);
        _rc ??= new RcloneRcClient(_rclone);
        var oldLocalPath = Path.Combine(_syncRootPath, oldRelative.Replace('/', Path.DirectorySeparatorChar));

        await _moveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _rc.MoveDirectoryAsync(fs + oldRemote, fs + newRemote).ConfigureAwait(false);
            await _rc.RemoveEmptyDirectoriesAsync(fs, oldRemote).ConfigureAwait(false);
            _store.RenameTree(oldRelative, newRelative, oldLocalPath, newLocalPath);
        }
        finally
        {
            _moveGate.Release();
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var row in _store.GetTree(newRelative))
        {
            _localChangeAt[row.RemotePath] = now;
            if (IsSimulated || !(File.Exists(row.LocalPath) || Directory.Exists(row.LocalPath)))
            {
                continue;
            }

            // Only the folder itself was renamed; what is inside keeps its
            // in-sync state and just needs the new identity.
            if (string.Equals(row.RemotePath, newRelative + "/", StringComparison.Ordinal))
            {
                UpdateRenamedPlaceholder(row.LocalPath, row.RemotePath, source);
            }
            else
            {
                _placeholders.UpdateFileIdentity(row.LocalPath, CloudPlaceholderService.BuildFileIdentity(row.RemotePath));
            }
        }

        foreach (var key in _lastSyncedState.Keys.Where(k => k.StartsWith(oldLocalPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (_lastSyncedState.TryRemove(key, out var state))
            {
                _lastSyncedState[newLocalPath + key[oldLocalPath.Length..]] = state;
            }
        }

        _log?.Invoke($"Renamed remote folder '{fs}{oldRemote}' -> '{newRemote}'.");
        ClearState(newLocalPath);
    }

    private string RelativeOf(string localPath) =>
        Path.GetRelativePath(_syncRootPath, localPath).Replace('\\', '/');

    /// <summary>
    /// Maps a sync-root-relative path to the real rclone target. The DB stores
    /// paths relative to the configured remote, which is not a valid rclone
    /// spec on its own: uploadfile/deletefile would treat it as a local path.
    /// </summary>
    private (string Fs, string RemoteFile) ResolveRemoteFile(string relative)
    {
        var (fs, baseRemote) = RcloneRcClient.SplitRemote(_remote);
        var remoteFile = string.IsNullOrEmpty(baseRemote) ? relative : $"{baseRemote}/{relative}";
        return (fs, remoteFile);
    }

    /// <summary>
    /// A folder made here is made on the server too, as Nextcloud and OneDrive
    /// do (uploading a file creates the folders above it, but an empty folder
    /// has nothing to upload). Returns what is in a folder it made: a folder
    /// moved or copied in is reported alone, without what it holds.
    /// </summary>
    private async Task<IReadOnlyList<string>> CreateRemoteFolderAsync(string localPath)
    {
        if (!Directory.Exists(localPath))
        {
            return []; // renamed or deleted meanwhile
        }

        var relative = RelativeOf(localPath);
        var remotePath = relative + "/";
        if (_store.GetByRemotePath(remotePath) is not null && (IsSimulated || _placeholders.QueryState(localPath).IsPlaceholder))
        {
            return []; // a folder the server already has
        }

        var (fs, remoteDirectory) = ResolveRemoteFile(relative);
        _rc ??= new RcloneRcClient(_rclone);
        await _rc.MakeDirectoryAsync(fs, remoteDirectory).ConfigureAwait(false);
        _store.UpsertFile(
            remotePath: remotePath,
            localPath: localPath,
            fileIdentity: remotePath,
            fileId: 0,
            isPlaceholder: !IsSimulated,
            isDirectory: true,
            size: 0,
            modifiedUtc: null,
            inSync: true);
        _localChangeAt[remotePath] = DateTimeOffset.UtcNow;

        // Everything in it is here, so it is fully populated: listed, and
        // kept current by the background sync from now on.
        if (!IsSimulated && !_placeholders.QueryState(localPath).IsPlaceholder)
        {
            _placeholders.ConvertToPlaceholder(localPath, CloudPlaceholderService.BuildFileIdentity(remotePath), markInSync: true);
        }

        _log?.Invoke($"Created folder '{relative}' on {fs}.");
        return Directory.EnumerateFileSystemEntries(localPath).ToList();
    }

    /// <summary>
    /// Changes made here while the service was not running, found when it
    /// starts (Nextcloud's local discovery does the same): edited or new
    /// files are uploaded, new folders created on the server, with the first
    /// run. Deletes are not taken from it: unregistering the drive removes
    /// every online-only file, and that must never delete them on the server.
    /// What is missing comes back from the server instead; the deletes made
    /// before the service stopped are in the journal.
    /// </summary>
    private void DiscoverLocalChanges()
    {
        int files = 0, folders = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var folder in Directory.EnumerateDirectories(_syncRootPath, "*", options))
        {
            if (_store.GetByRemotePath(RelativeOf(folder) + "/") is null && !_placeholders.QueryState(folder).IsPlaceholder)
            {
                _changes.Changed(folder);
                _itemStates.WaitingToUpload(folder);
                folders++;
            }
        }

        foreach (var file in Directory.EnumerateFiles(_syncRootPath, "*", options))
        {
            if (!IsStateFile(file) && HasUnsyncedLocalChanges(file))
            {
                _changes.Changed(file);
                _itemStates.WaitingToUpload(file);
                files++;
            }
        }

        if (files + folders > 0)
        {
            _log?.Invoke($"Changes made while OpenCfDrive was not running: {files} file(s) and {folders} folder(s) are sent to the server.");
        }
    }

    public async Task<bool> UploadAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
        {
            ClearState(localPath);
            return false;
        }

        // Never read while a download is still writing it: the change waits
        // for the run that the download's end asks for.
        if (_connection?.IsHydrating(localPath) == true)
        {
            _changes.Changed(localPath);
            _itemStates.WaitingToUpload(localPath);
            return false;
        }

        var info = new FileInfo(localPath);
        var sent = (Length: info.Length, WriteUtc: info.LastWriteTimeUtc);
        var relative = Path.GetRelativePath(_syncRootPath, localPath).Replace('\\', '/');
        var record = _store.GetByLocalPath(localPath);

        // Nothing new to send? Size alone cannot tell (a same-size edit is
        // still an edit).
        var state = _placeholders.QueryState(localPath);
        if (!IsSimulated)
        {
            // Unchanged means in sync and still as it was synced (see
            // MatchesLastSync): downloading, freeing space and server updates
            // keep both, and a rename is marked in sync again once it reaches
            // the server. A dehydrated placeholder is unchanged, so its empty
            // local copy is never sent over good server data.
            if (state is { IsPlaceholder: true, InSync: true } && MatchesLastSync(localPath, record))
            {
                ClearState(localPath);
                return false;
            }

            // A plain file still as it was last synced (unregistering leaves
            // downloaded files so): made a placeholder again, not sent again.
            if (!state.IsPlaceholder && record is { IsDirectory: false, SyncedLocalState: not null } && MatchesLastSync(localPath, record))
            {
                _placeholders.ConvertToPlaceholder(localPath, CloudPlaceholderService.BuildFileIdentity(record.RemotePath), markInSync: true);
                ClearState(localPath);
                return false;
            }
        }
        else if (_lastSyncedState.TryGetValue(localPath, out var synced) &&
                 synced == (info.Length, info.LastWriteTimeUtc))
        {
            ClearState(localPath);
            return false;
        }

        // A local change is only sent over the version this client last saw.
        // If the server has moved on since, nothing is lost, as in Nextcloud:
        // a file it changed, or one it got from elsewhere at this path, takes
        // the server's version and the local one is kept as a conflict copy;
        // a file it deleted is uploaded again.
        _rc ??= new RcloneRcClient(_rclone);
        var server = await _rc.StatAsync(_remote, relative, Listing(), cancellationToken).ConfigureAwait(false);
        if (ServerMovedOn(record, server))
        {
            if (server is null)
            {
                _store.DeleteByRemotePath(record!.RemotePath);
                _log?.Invoke($"Server deleted '{relative}', which has local changes: they are kept and uploaded again.");
                record = null;
            }
            else if (server.IsDirectory)
            {
                // A folder took the name on the server: the file is kept
                // under its conflict name, and the folder comes with the
                // next listing.
                if (KeepConflictCopy(localPath, relative) is not null)
                {
                    File.Delete(localPath);
                    ClearState(localPath);
                    if (record is not null)
                    {
                        _store.DeleteByRemotePath(record.RemotePath);
                    }
                }

                return false;
            }
            else if (record is null && (SameAsServer(info, server) || await IsServerContentAsync(localPath, server.Hash).ConfigureAwait(false)))
            {
                // Added on both sides, the same file (Nextcloud takes equal
                // size and time for equal, or the content hash): nothing to send.
                _store.UpsertFile(
                    remotePath: relative,
                    localPath: localPath,
                    fileIdentity: relative,
                    fileId: state.FileId,
                    isPlaceholder: true,
                    isDirectory: false,
                    size: server.Size,
                    modifiedUtc: server.ModifiedTime,
                    inSync: true,
                    localSize: sent.Length,
                    localWriteUtc: sent.WriteUtc,
                    hash: server.Hash);
                if (!IsSimulated)
                {
                    _placeholders.ConvertToPlaceholder(localPath, CloudPlaceholderService.BuildFileIdentity(relative), markInSync: true);
                }

                RememberSyncedState(localPath);
                ClearState(localPath);
                return false;
            }
            else
            {
                // A file this client never saw needs a row to take its version.
                if (record is null)
                {
                    _store.UpsertFile(
                        remotePath: relative,
                        localPath: localPath,
                        fileIdentity: relative,
                        fileId: state.FileId,
                        isPlaceholder: state.IsPlaceholder,
                        isDirectory: false,
                        size: server.Size,
                        modifiedUtc: server.ModifiedTime,
                        inSync: false);
                }

                // A file new on both sides was compared just above.
                await ApplyServerVersionAsync(localPath, relative, server.Size, server.ModifiedTime, server.Hash, compareContent: record is not null).ConfigureAwait(false);
                return false;
            }
        }

        var (fs, remoteFile) = ResolveRemoteFile(relative);
        var remoteDirectory = remoteFile.Contains('/')
            ? remoteFile[..remoteFile.LastIndexOf('/')]
            : string.Empty;

        await _rc.UploadFileAsync(fs, remoteDirectory, localPath, cancellationToken).ConfigureAwait(false);

        // The store holds the server's metadata, which is what later listings
        // are compared against. Most remotes stamp their own modification
        // time on upload, so the local one would read as a server-side change.
        // The file's own size and time as sent are kept beside it: what an
        // edit is told by from now on. Taken before the upload, so a write
        // during it still counts as an edit.
        var (serverSize, serverTime, serverHash) = await GetServerMetadataAsync(relative, cancellationToken).ConfigureAwait(false);

        _store.UpsertFile(
            remotePath: relative,
            localPath: localPath,
            fileIdentity: relative,
            fileId: state.FileId != 0 ? state.FileId : record?.FileId ?? 0,
            isPlaceholder: true,
            isDirectory: false,
            size: serverSize ?? sent.Length,
            modifiedUtc: serverTime ?? new DateTimeOffset(sent.WriteUtc, TimeSpan.Zero),
            inSync: true,
            localSize: sent.Length,
            localWriteUtc: sent.WriteUtc,
            hash: serverHash);
        _localChangeAt[relative] = DateTimeOffset.UtcNow;
        RememberSyncedState(localPath);
        ClearState(localPath);

        _log?.Invoke($"Uploaded '{relative}' ({info.Length} bytes) to {fs}{remoteDirectory}.");

        if (!IsSimulated)
        {
            // Decided by the file, not the store: a plain file can take a
            // known placeholder's place (an editor saving through a temp file).
            var identity = CloudPlaceholderService.BuildFileIdentity(relative);
            if (state.IsPlaceholder)
            {
                // With the file's size: the platform's own record of it is
                // what Free up space later downloads back, and an edit in
                // place does not change it (EditedPlaceholderContractTest).
                // The size it has now, not as sent: a write since then is
                // uploaded next, and must not be given a size it no longer has.
                _placeholders.MarkInSync(localPath, identity, new FileInfo(localPath).Length);
            }
            else
            {
                _placeholders.ConvertToPlaceholder(localPath, identity, markInSync: true);
            }

            // Free up space was asked for while this was still to be sent.
            if ((File.GetAttributes(localPath) & UnpinnedAttribute) != 0)
            {
                ApplyPinState(localPath, keepLocal: false);
            }
        }

        return true;
    }

    /// <summary>
    /// The same file on both sides, by Nextcloud's test: equal size and
    /// modification time, to within the remote's precision. Never on a
    /// remote that keeps no file times: its times are its own upload times.
    /// </summary>
    private bool SameAsServer(FileInfo local, CloudItem server) =>
        _capabilities.KeepsFileTimes &&
        local.Length == server.Size &&
        server.ModifiedTime is { } serverTime &&
        (serverTime.UtcDateTime - local.LastWriteTimeUtc).Duration() < _capabilities.TimeTolerance;


    public async ValueTask DisposeAsync()
    {
        _scheduler?.Dispose();
        _scheduler = null;

        _freeUnusedTimer?.Dispose();
        _freeUnusedTimer = null;

        _watcher?.Dispose();
        _watcher = null;

        _connection?.Dispose();
        _connection = null;

        _server?.Dispose();
        _server = null;

        _serve?.Dispose();
        _serve = null;

        _rc?.Dispose();
        _rc = null;

        _itemStates.Dispose();

        // The drive stays registered: stopping the service (the app's Stop,
        // a restart to apply settings) is not removing the drive. Until the
        // service is back, Explorer still lists it and downloaded files open;
        // online-only ones wait for it. Unregistering is the uninstaller's
        // and --unregister's job, and the app's when the drive moves.
        await Task.CompletedTask;
    }
}
