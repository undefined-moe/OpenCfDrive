using System.Reflection;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Service;

public sealed class SyncWorker(
    ILogger<SyncWorker> logger,
    ActivityLog activity,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly List<RootDrive> _drives = [];
    private RcloneDaemon? _daemon;
    private RcloneStatus? _rcloneStatus;
    private EffectiveSettings? _settings;

    /// <summary>
    /// One drive: its own state database, control pipe, engine and problems.
    /// A drive that fails to start or sync does not stop the others.
    /// </summary>
    private sealed class RootDrive(EffectiveRoot root)
    {
        public string SyncRoot { get; } = root.SyncRoot;

        public string Remote { get; } = root.Remote;

        public SqliteMetadataStore Store = null!;

        public ServiceControlServer? Control;

        public CloudSyncEngine? Engine;

        public SyncRootStatus? Initialized;

        public ProblemLog Problems { get; } = new();

        public (ServiceState State, string Detail) State { get; set; } = (ServiceState.Starting, "Registering and connecting the drive.");

        // The number of the last change a pulse shows; a watching app keeps
        // whichever of a pulse and a status is newer.
        public long Revision = 1;

        /// <summary>Something a pulse shows has changed: it is numbered, and sent to a watching app.</summary>
        public void Changed()
        {
            Interlocked.Increment(ref Revision);
            Control?.NotifyChanged();
        }

        /// <summary>The state to report: a connected drive whose server cannot be reached is offline.</summary>
        public (ServiceState State, string Detail) StateNow() =>
            State.State == ServiceState.Syncing && Engine is { IsOnline: false } engine
                ? (ServiceState.Offline,
                   $"The server cannot be reached ({engine.OfflineReason}). Folders show what is on this PC, and changes " +
                   "are sent once it answers; it is checked again when the network changes, when the drive is used, and every minute or so.")
                : State;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DriveSettings saved;
        try
        {
            saved = DriveSettingsFile.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Ignoring the settings file: {Message}", ex.Message);
            saved = new DriveSettings();
        }

        // Explicit environment values win, which keeps scripted runs deterministic.
        var settings = DriveSettingsFile.Resolve(saved);
        _settings = settings;
        var rcUrl = settings.RcUrl.Value;
        logger.LogInformation(
            "{Count} drive(s) through {Rclone} (settings: {SettingsPath})",
            settings.Roots.Count, settings.ManageRclone ? "an rclone it runs itself" : $"the rclone at {rcUrl}", DriveSettingsFile.Path);
        foreach (var root in settings.Roots)
        {
            logger.LogInformation("Drive '{SyncRoot}' for {Remote}", root.SyncRoot, root.Remote);
        }

        foreach (var (retired, replacement) in DriveSettingsFile.RetiredVariables)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(retired)))
            {
                logger.LogWarning("{Retired} is set but no longer read; {Replacement} replaced it.", retired, replacement);
            }
        }

        if (!settings.HasRemote)
        {
            logger.LogWarning("No rclone remote is configured, so '{Remote}' is used. Choose one in the OpenCfDrive app.", settings.Remote.Value);
        }

        // Engine state never lives in a sync root: everything under that path
        // is user data and is watched/uploaded.
        Directory.CreateDirectory(SyncStatePaths.StateRoot);

        // Each drive's state database and control pipe first: even when
        // rclone below cannot be started, the app can ask every drive why.
        foreach (var root in settings.Roots)
        {
            var drive = new RootDrive(root);
            _drives.Add(drive);
            try
            {
                PrepareDrive(drive, settings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Drive '{SyncRoot}' could not open its state", drive.SyncRoot);
                drive.State = (ServiceState.Failed, ex.Message);
            }
        }

        // One rclone remote control for every drive: its API takes the
        // remote per request.
        var endpoint = await ConnectRcloneAsync(settings, stoppingToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            // Nothing to sync with; stay up so the app can say why.
            await WaitUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        foreach (var drive in _drives)
        {
            if (drive.State.State == ServiceState.Failed)
            {
                continue; // its state or pipe could not be set up above
            }

            try
            {
                await StartEngineAsync(drive, endpoint, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Drive '{SyncRoot}' could not start", drive.SyncRoot);
                drive.State = (ServiceState.Failed, ex.Message);
            }
        }

        await WaitUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>One drive's state database and control pipe, before any syncing.</summary>
    private void PrepareDrive(RootDrive drive, EffectiveSettings settings)
    {
        var syncRoot = drive.SyncRoot;
        var dbPath = SyncStatePaths.DatabasePath(syncRoot);

        SqliteMetadataStore store;
        try
        {
            store = new SqliteMetadataStore(dbPath);
            store.Initialize();
            logger.LogInformation("Using SQLite state at '{DbPath}'", dbPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            dbPath = SyncStatePaths.FallbackDatabasePath(syncRoot);
            logger.LogWarning(ex, "LocalAppData state is not writable; falling back to '{DbPath}'", dbPath);
            store = new SqliteMetadataStore(dbPath);
            store.Initialize();
        }

        // Which remote this folder's state belongs to (the app checks it before
        // pointing the folder at another remote).
        store.SetConfig("OpenCfDriveRoot", syncRoot);
        store.SetConfig("RcloneFs", drive.Remote);
        store.SetConfig("RcloneRcUrl", settings.RcUrl.Value);
        drive.Store = store;

        drive.Control = new ServiceControlServer(
            ServiceControl.PipeName(syncRoot),
            () => BuildStatus(drive),
            lifetime.StopApplication,
            message => logger.LogInformation("{Message}", message),
            syncNow: () => drive.Engine?.SyncNowAsync() ?? Task.CompletedTask,
            pulse: () => BuildPulse(drive));
        drive.Control.Start();
    }

    /// <summary>Connects one drive: its engine, registered with Explorer and watching.</summary>
    private async Task StartEngineAsync(RootDrive drive, RcloneEndpoint endpoint, CancellationToken stoppingToken)
    {
        var syncRoot = drive.SyncRoot;
        var dbPath = SyncStatePaths.DatabasePath(syncRoot);
        var engine = new CloudSyncEngine(syncRoot, drive.Remote, endpoint, dbPath, msg => logger.LogInformation("{Message}", msg));
        drive.Engine = engine;

        // Every failed sync step, for the app to tell the user about.
        engine.ProblemReported += message =>
        {
            logger.LogWarning("{Message}", message);
            drive.Problems.Report(message);
            drive.Changed();
        };

        // Sending, downloading, offline: the app's icon shows it as it happens.
        engine.ActivityChanged += drive.Changed;

        var status = await engine.InitializeAsync(stoppingToken).ConfigureAwait(false);
        drive.Initialized = status;

        logger.LogInformation(
            "Drive '{SyncRoot}': mode: {Mode} | cloudFiles={Supported} ({Reason}) | registered={Registered} | connected={Connected} | items={Items}",
            syncRoot,
            status.CloudFilesSupported ? "cloud" : "unavailable",
            status.CloudFilesSupported,
            status.CapabilityReason,
            status.Registered,
            status.Connected,
            status.RegisteredItems);

        if (status.CloudFilesSupported)
        {
            engine.StartWatcher();
            engine.StartBackgroundSync();
            drive.State = status.Registered && status.Connected
                ? (ServiceState.Syncing, "The drive is registered with Explorer and connected.")
                : (ServiceState.Failed, "The drive could not be registered or connected; see the log.");
        }
        else
        {
            logger.LogError("Cloud Files is not available ({Reason}); '{SyncRoot}' is not synced.", status.CapabilityReason, syncRoot);
            drive.State = (ServiceState.Unavailable, status.CapabilityReason);
        }
    }

    private static async Task WaitUntilStoppedAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>rclone's own log, next to the service's: %LOCALAPPDATA%\OpenCfDrive\logs\rclone.log.</summary>
    private static string RcloneLogPath => Path.Combine(Path.GetDirectoryName(ConsoleOutput.LogPath)!, "rclone.log");

    /// <summary>
    /// The rclone remote control to sync through: one the service starts and
    /// owns (the default), or the one the user runs at the configured
    /// address. Null when there is none; the status then says why.
    /// </summary>
    private async Task<RcloneEndpoint?> ConnectRcloneAsync(EffectiveSettings settings, CancellationToken stoppingToken)
    {
        if (!settings.ManageRclone)
        {
            _rcloneStatus = new RcloneStatus(
                false, settings.RcUrl.Value, null, null, null, null, "External", "rclone runs outside OpenCfDrive.", null);
            return settings.RcEndpoint;
        }

        var program = RcloneLocator.Find(settings.RclonePath);
        if (program is null)
        {
            var why = settings.RclonePath is null
                ? "rclone was not found: it is not on PATH and was not downloaded. Choose rclone.exe, or download it, in OpenCfDrive's Settings."
                : $"rclone was not found at '{settings.RclonePath}'.";
            logger.LogError("{Why}", why);
            _rcloneStatus = new RcloneStatus(true, "", settings.RclonePath, null, null, null, "NotFound", why, null);
            FailEveryDrive(why);
            return null;
        }

        _rcloneStatus = new RcloneStatus(
            true, "", program.Path, program.Source.ToString(), null, null, "Starting", "Starting rclone.", RcloneLogPath);
        try
        {
            _daemon = await RcloneDaemon.StartAsync(
                program,
                settings.RcloneConfig,
                RcloneLogPath,
                message => logger.LogInformation("{Message}", message),
                stoppingToken).ConfigureAwait(false);
            return _daemon.Endpoint;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            var why = $"rclone could not be started: {ex.Message}";
            logger.LogError("{Why}", why);
            _rcloneStatus = _rcloneStatus with { State = "Failed", Detail = why };
            FailEveryDrive(why);
            return null;
        }
    }

    /// <summary>Nothing can sync without rclone: every drive says so.</summary>
    private void FailEveryDrive(string why)
    {
        foreach (var drive in _drives)
        {
            if (drive.State.State != ServiceState.Failed)
            {
                drive.State = (ServiceState.Failed, why);
                drive.Changed();
            }
        }
    }

    private RcloneStatus? RcloneNow() =>
        _daemon is { } daemon
            ? new RcloneStatus(
                true,
                daemon.Endpoint.BaseUrl,
                daemon.Program.Path,
                daemon.Program.Source.ToString(),
                daemon.Version,
                daemon.ProcessId,
                daemon.State.ToString(),
                daemon.Detail,
                daemon.LogPath)
            : _rcloneStatus;

    private ServicePulse BuildPulse(RootDrive drive)
    {
        // The number first: what is read after it is at least that new.
        var revision = Interlocked.Read(ref drive.Revision);
        var (state, detail) = drive.StateNow();
        return new ServicePulse(Environment.ProcessId, revision, state, detail, drive.Engine?.Activity ?? SyncActivity.None, drive.Problems.Count);
    }

    private ServiceStatus BuildStatus(RootDrive drive)
    {
        var revision = Interlocked.Read(ref drive.Revision);
        var items = 0;
        try
        {
            items = drive.Store.Count();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Busy for a moment; the next request counts again.
        }

        var initialized = drive.Initialized;
        var (state, detail) = drive.StateNow();
        return new ServiceStatus(
            Environment.ProcessId,
            _startedAt,
            Version,
            drive.SyncRoot,
            drive.Remote,
            _settings?.RcUrl.Value ?? "",
            state,
            detail,
            initialized?.Registered ?? false,
            initialized?.Connected ?? false,
            items,
            drive.Engine?.BytesFetched ?? 0,
            ConsoleOutput.ActiveLogPath ?? "",
            activity.Recent(100),
            RcloneNow(),
            drive.Problems.Count,
            drive.Problems.Recent(),
            drive.Engine?.Activity ?? SyncActivity.None,
            revision);
    }

    private static string Version =>
        (typeof(SyncWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var drive in _drives)
        {
            if (drive.Control is not null)
            {
                await drive.Control.DisposeAsync().ConfigureAwait(false);
            }
        }

        foreach (var drive in _drives)
        {
            if (drive.Engine is not null)
            {
                await drive.Engine.DisposeAsync().ConfigureAwait(false);
            }
        }

        // After the engines: they may still be finishing a request through it.
        if (_daemon is not null)
        {
            await _daemon.DisposeAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
