using System.Diagnostics;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;
using OpenCfDrive.Core.Updates;

namespace OpenCfDrive.App;

/// <summary>
/// The icon in the notification area (behind the arrow next to the clock,
/// unless the user pins it to the taskbar), its menu, and the window it opens.
/// </summary>
internal sealed class TrayApp : ApplicationContext, IDriveApp
{
    private static readonly TimeSpan VisibleRefresh = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HiddenRefresh = TimeSpan.FromSeconds(15);

    private readonly SingleInstance _instance;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _headline;
    private readonly ToolStripMenuItem _openFolder;
    private readonly ToolStripMenuItem _start;
    private readonly ToolStripMenuItem _stop;
    private readonly ToolStripMenuItem _restart;
    private readonly ToolStripMenuItem _installUpdate;
    private readonly ToolStripMenuItem _exit;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly ProblemNotifier _problems = new();
    private readonly WindowsNotifications _notifications = WindowsNotifications.Create();
    private readonly TrayIcons _icons = new(SystemInformation.SmallIconSize);
    private readonly CancellationTokenSource _closing = new();
    private readonly UpdateNotifier _updateNotices;
    private string? _stateTold;
    private DriveIndicator? _indicatorShown;
    private MainForm? _window;
    private Action? _balloonClicked;
    private readonly HashSet<string> _watching = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private bool _exiting;

    public TrayApp(SingleInstance instance, bool showWindow)
    {
        _instance = instance;

        _headline = new ToolStripMenuItem("OpenCfDrive") { Enabled = false };
        _openFolder = new ToolStripMenuItem("Open the OpenCfDrive folder", null, (_, _) => OpenFolder());
        var openWindow = new ToolStripMenuItem("Status and settings", null, (_, _) => ShowWindow())
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
        };
        _start = new ToolStripMenuItem("Start syncing", null, async (_, _) => await StartAsync());
        _stop = new ToolStripMenuItem("Stop syncing", null, async (_, _) => await StopAsync());
        _restart = new ToolStripMenuItem("Restart syncing", null, async (_, _) => await RestartAsync());
        var openLog = new ToolStripMenuItem("View the log", null, (_, _) => OpenLog());
        _installUpdate = new ToolStripMenuItem("Restart and update", null, async (_, _) => await InstallUpdateAsync()) { Visible = false };
        _exit = new ToolStripMenuItem("Exit", null, (_, _) => Exit());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            _headline, new ToolStripSeparator(),
            _openFolder, openWindow, new ToolStripSeparator(),
            _start, _stop, _restart, new ToolStripSeparator(),
            openLog, _installUpdate, _exit,
        ]);
        _menu.Opening += (_, _) => UpdateMenu();

        // Creating the menu installed the UI thread's synchronization context.
        _ui = SynchronizationContext.Current!;

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Small,
            Text = "OpenCfDrive",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindow();
            }
        };
        _tray.BalloonTipClicked += (_, _) => (_balloonClicked ?? (() => ShowWindow(settings: !(Monitor.Latest?.Settings.HasRemote ?? false))))();

        _instance.OnSignal(_instance.ShowRequested, () => _ui.Post(_ => ShowWindow(), null));
        _instance.OnSignal(_instance.QuitRequested, () => _ui.Post(_ => Exit(), null));

        // Only the copy the installer put in place updates itself: the MSI
        // installs there, not over a build someone runs from elsewhere.
        Updates = new Updater(
            AppVersion.Current,
            AppVersion.UpdateRepository,
            Updater.DefaultFolder,
            automatic: () => DriveSettingsFile.Load().CheckForUpdates != false,
            unavailable: SignInStartup.IsInstalledCopy
                ? null
                : $"Only the installed copy updates itself; this one runs from {Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)}.");
        _updateNotices = new UpdateNotifier(Updates.Status.Stage);
        Updates.Changed += status => _ui.Post(_ => OnUpdateChanged(status), null);

        Monitor.Updated += OnUpdated;
        Monitor.Pulsed += ShowOnTray;
        _timer.Interval = (int)HiddenRefresh.TotalMilliseconds;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ui.Post(async _ => await StartupAsync(showWindow), null);
    }

    public DriveMonitor Monitor { get; } = new();

    public Updater Updates { get; }

    public bool Busy => _busy;

    public DriveSnapshot? Latest => Monitor.Latest;

    public Version CurrentVersion => Updates.Current;

    public UpdateStatus UpdateStatus => Updates.Status;

    public Task CheckForUpdatesAsync() => Updates.CheckNowAsync();

    private async Task StartupAsync(bool showWindow)
    {
        ApplyDefaultSignInStartup();
        await RefreshAsync(probeRclone: true);

        var latest = Monitor.Latest!;
        if (latest.SettingsProblem is null && latest.Settings.HasRemote && !latest.Running)
        {
            await StartAsync();
        }

        if (showWindow)
        {
            ShowWindow(settings: !latest.Settings.HasRemote);
        }
        else if (!latest.Settings.HasRemote)
        {
            ShowBalloon("OpenCfDrive is not set up", "Click here to choose your rclone remote.", ToolTipIcon.Info);
        }

        // After the drive: what an earlier update left, then the checks.
        Updates.Start();
    }

    /// <summary>
    /// The installed app starts at sign-in unless the user turned that off.
    /// Decided once, on its first start; a build someone runs from the
    /// repository never adds itself.
    /// </summary>
    private static void ApplyDefaultSignInStartup()
    {
        try
        {
            var saved = DriveSettingsFile.Load();
            if (saved.StartAtSignIn is null && SignInStartup.IsInstalledCopy)
            {
                SignInStartup.Enable();
                DriveSettingsFile.Save(saved with { StartAtSignIn = true });
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // The window shows the settings problem.
        }
    }

    public async Task RefreshAsync(bool probeRclone = false)
    {
        var visible = _window is { Visible: true };
        _timer.Interval = (int)(visible ? VisibleRefresh : HiddenRefresh).TotalMilliseconds;
        try
        {
            await Monitor.RefreshAsync(
                rcloneMaxAge: probeRclone ? TimeSpan.Zero : visible ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1),
                remoteMaxAge: probeRclone ? TimeSpan.Zero : TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Refresh failed: {ex}");
        }
    }

    private void OnUpdated(DriveSnapshot snapshot)
    {
        ShowOnTray(snapshot);
        _window?.ShowSnapshot(snapshot);
        NotifyProblems(snapshot);
        Watch(snapshot);
    }

    /// <summary>
    /// The icon for the drive's state (see <see cref="TrayIcons"/>), and a
    /// tooltip with the headline and, while it syncs, is offline or has
    /// files it could not sync, what it is doing.
    /// </summary>
    private void ShowOnTray(DriveSnapshot snapshot)
    {
        var indicator = snapshot.Indicator;
        if (indicator != _indicatorShown)
        {
            _tray.Icon = _icons.For(indicator);
            _indicatorShown = indicator;
        }

        var (_, headline, _) = snapshot.Summary;
        var text = $"OpenCfDrive: {headline}";
        if (indicator is DriveIndicator.Syncing or DriveIndicator.Offline or DriveIndicator.Warning &&
            snapshot.Status?.Activity?.Describe() is { Length: > 0 } doing)
        {
            text += "\n" + doing;
        }

        // Windows takes 127 characters.
        _tray.Text = text.Length <= 127 ? text : text[..126] + "…";
    }

    /// <summary>
    /// While the service runs, it tells the app each change as it happens
    /// (see <see cref="ServiceControl.WatchAsync"/>), once per drive, so the
    /// icon shows syncing when it starts and stops, not at the next refresh.
    /// Once the service ends, the app looks again at once, and watches the
    /// next one; one that does not answer is tried again at the next refresh.
    /// </summary>
    private void Watch(DriveSnapshot snapshot)
    {
        if (_exiting)
        {
            return;
        }

        foreach (var root in snapshot.Roots)
        {
            if (root.Running && !_watching.Contains(root.SyncRoot))
            {
                _watching.Add(root.SyncRoot);
                _ = WatchAsync(root.SyncRoot);
            }
        }
    }

    private async Task WatchAsync(string syncRoot)
    {
        bool heard;
        try
        {
            heard = await ServiceControl.WatchAsync(
                syncRoot, pulse => _ui.Post(_ => OnPulse(syncRoot, pulse), null), TimeSpan.FromSeconds(2), _closing.Token);
        }
        finally
        {
            _watching.Remove(syncRoot);
        }

        if (heard && !_exiting)
        {
            await RefreshAsync();
        }
    }

    /// <summary>
    /// A change the service told: the icon follows at once. A new state, or
    /// a new sync problem, also reads the whole status, which says more
    /// (and notifies the user).
    /// </summary>
    private void OnPulse(string syncRoot, ServicePulse pulse)
    {
        if (_exiting)
        {
            return;
        }

        var before = Monitor.Latest?.Roots
            .FirstOrDefault(root => string.Equals(root.SyncRoot, syncRoot, StringComparison.OrdinalIgnoreCase))?.Status;
        Monitor.Apply(syncRoot, pulse);
        if (before is not null && before.ProcessId == pulse.ProcessId &&
            (pulse.State != before.State || pulse.ProblemsReported != before.ProblemsReported))
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Tells the user about every failure: the drive itself in trouble or
    /// offline (once per new state), and each file or folder that could not
    /// be synced (see <see cref="ProblemNotifier"/>). Clicking opens the
    /// window, whose activity list has the details.
    /// </summary>
    private void NotifyProblems(DriveSnapshot snapshot)
    {
        if (_exiting)
        {
            return;
        }

        var (health, headline, detail) = snapshot.Summary;
        var troubled = health == Health.Problem || snapshot.Status?.State == ServiceState.Offline;
        if (!troubled)
        {
            _stateTold = null;
        }
        else if (headline != _stateTold)
        {
            _stateTold = headline;
            ShowBalloon($"OpenCfDrive: {headline}", detail, ToolTipIcon.Warning, () => ShowWindow());
            return;
        }

        if (_problems.Next(snapshot.Status) is { } notice)
        {
            ShowBalloon(notice.Title, notice.Text, ToolTipIcon.Warning, () => ShowWindow());
        }
    }

    private void UpdateMenu()
    {
        var latest = Monitor.Latest;
        var (_, headline, _) = latest?.Summary ?? (Health.Idle, "Checking...", "");
        var roots = latest?.Roots ?? [];
        var several = roots.Count > 1;
        _headline.Text = latest?.Settings.HasRemote == true
            ? (several ? $"{headline}: {roots.Count} drives" : $"{headline}: {latest.Settings.Remote.Value}")
            : headline;
        _openFolder.Enabled = roots.Any(root => Directory.Exists(root.SyncRoot));

        var running = latest?.Running ?? false;
        _start.Enabled = !_busy && latest is { Running: false, SettingsProblem: null } && latest.Settings.HasRemote;
        _stop.Enabled = !_busy && running;
        _restart.Enabled = !_busy && running;
        var update = Updates.Status;
        _installUpdate.Visible = update.Stage == UpdateStage.Ready;
        _installUpdate.Text = update.Available is { } version ? $"Restart and update to {GitHubReleases.Format(version)}" : "Restart and update";
        _installUpdate.Enabled = !_busy;
        _exit.Text = running ? "Exit (syncing continues)" : "Exit";
    }

    private void OnUpdateChanged(UpdateStatus status)
    {
        if (_exiting)
        {
            return;
        }

        _window?.ShowUpdate(status);

        // A download ready to install, or a version out that could not be
        // downloaded (see UpdateNotifier for when each is said).
        if (_updateNotices.Next(status) is { } notice)
        {
            ShowBalloon(
                notice.Title,
                notice.Text,
                ToolTipIcon.Info,
                notice.Install
                    ? async () => await InstallUpdateAsync(ask: true)
                    : notice.Page is { } page ? () => Open(page) : null);
        }
    }

    public void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowProblem($"{target} could not be opened: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops syncing, so nothing the installer replaces is in use, starts the
    /// downloaded installer and exits; the installer opens the app again,
    /// which starts syncing.
    /// </summary>
    public async Task InstallUpdateAsync(bool ask = false)
    {
        var status = Updates.Status;
        if (status.Stage != UpdateStage.Ready || status.Available is not { } version)
        {
            return;
        }

        if (ask && MessageBox.Show(
                $"Install OpenCfDrive {GitHubReleases.Format(version)} now?\n\nOpenCfDrive closes, stops syncing for the moment " +
                "the installer takes, and opens again.",
                "Update OpenCfDrive",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information) != DialogResult.OK)
        {
            return;
        }

        if (Monitor.Latest is { Running: true } latest &&
            await RunAsync("Stopping syncing for the update", () => SyncService.StopAsync(RootPaths(latest))) is not null)
        {
            return;
        }

        if (Updates.BeginInstall() is not { } installer)
        {
            ShowProblem("The downloaded update is gone; check for updates again.");
            return;
        }

        try
        {
            Process.Start(Updater.InstallCommand(installer, Updates.InstallLog, Environment.ProcessPath!))?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowProblem($"The installer could not be started: {ex.Message}");
            return;
        }

        Exit();
    }

    public void ShowWindow(bool settings = false)
    {
        if (_window is null || _window.IsDisposed)
        {
            _window = new MainForm(this);
            _window.VisibleChanged += async (_, _) => await RefreshAsync(probeRclone: _window.Visible);
        }

        // The first time, where the icon was clicked.
        _window.PlaceOn(Screen.FromPoint(Cursor.Position));

        if (Monitor.Latest is { } latest)
        {
            _window.ShowSnapshot(latest);
        }

        _window.ShowUpdate(Updates.Status);
        if (settings)
        {
            _window.ShowSettingsPage();
        }

        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized)
        {
            _window.WindowState = FormWindowState.Normal;
        }

        _window.Activate();
    }

    /// <summary>The folders of every configured drive: what one service process runs.</summary>
    private static IReadOnlyList<string> RootPaths(DriveSnapshot snapshot) =>
        snapshot.Settings.Roots.Select(root => root.SyncRoot).ToArray();

    public async Task StartAsync()
    {
        var latest = Monitor.Latest;
        if (latest is null)
        {
            return;
        }

        await RunAsync("Starting", () => SyncService.StartAsync(RootPaths(latest)));
    }

    public async Task StopAsync()
    {
        var latest = Monitor.Latest;
        if (latest is null)
        {
            return;
        }

        await RunAsync("Stopping", () => SyncService.StopAsync(RootPaths(latest)));
    }

    public async Task RestartAsync()
    {
        var latest = Monitor.Latest;
        if (latest is null)
        {
            return;
        }

        var roots = RootPaths(latest);
        await RunAsync("Restarting", async () => await SyncService.StopAsync(roots) ?? await SyncService.StartAsync(roots));
    }

    /// <summary>
    /// Saves new settings and carries them out: a moved drive is unregistered
    /// from its old folder (left as an ordinary folder), and a running service
    /// restarts with them. One change per drive (see <see cref="DriveChange"/>).
    /// Returns why it could not, or null.
    /// </summary>
    public async Task<string?> ApplySettingsAsync(DriveSettings settings, IReadOnlyList<DriveChange> changes)
    {
        var before = Monitor.Latest!.Settings;
        var after = DriveSettingsFile.Resolve(settings);
        var rcloneChanged =
            before.ManageRclone != after.ManageRclone ||
            (after.ManageRclone
                ? before.RclonePath != after.RclonePath || before.RcloneConfig != after.RcloneConfig
                : before.RcUrl.Value != after.RcUrl.Value || before.RcUser != after.RcUser || before.RcPassword != after.RcPassword);
        // The list of drives itself: a drive added, removed, moved or pointed
        // at another remote needs the service restarted to be picked up; the
        // service reads the settings once, as it starts.
        var rootsChanged =
            after.Roots.Count != before.Roots.Count ||
            changes.Any(change => change.ChangesDrive) ||
            after.Roots.Any(chosen => !before.Roots.Any(current =>
                string.Equals(current.SyncRoot, chosen.SyncRoot, StringComparison.OrdinalIgnoreCase) &&
                current.Remote == chosen.Remote));
        var syncChanged = rootsChanged || rcloneChanged;

        return await RunAsync("Applying the settings", async () =>
        {
            if (syncChanged && await SyncService.StopAsync(RootPaths(Monitor.Latest!)) is { } notStopped)
            {
                return notStopped;
            }

            // First, while nothing else has changed yet: the state of a folder
            // that held another remote's drive no longer applies.
            foreach (var change in changes.Where(change => change.DiscardOldState))
            {
                SqliteMetadataStore.Delete(SyncStatePaths.DatabasePath(change.NewSyncRoot));
            }

            foreach (var oldRoot in changes.Select(change => change.OldSyncRootToUnregister).OfType<string>().Distinct())
            {
                if (SyncRootRegistrar.IsSyncRoot(oldRoot))
                {
                    var result = await Task.Run(() => new SyncRootRegistrar().Unregister(oldRoot));
                    if (!result.Success)
                    {
                        return $"The old drive folder could not be unregistered: {result.Message}";
                    }
                }
            }

            DriveSettingsFile.Save(settings);
            if (settings.StartAtSignIn == true)
            {
                SignInStartup.Enable();
            }
            else
            {
                SignInStartup.Disable();
            }

            return syncChanged && after.HasRemote
                ? await SyncService.StartAsync(after.Roots.Select(root => root.SyncRoot).ToArray())
                : null;
        }, report: false);
    }

    /// <summary>
    /// Downloads rclone from rclone.org into OpenCfDrive's own folder (it is
    /// used when none was chosen and none is on PATH), then lets a service
    /// that was missing it start again. Returns why it failed, or null.
    /// </summary>
    public async Task<string?> DownloadRcloneAsync(IProgress<string> progress)
    {
        return await RunAsync("Downloading rclone", async () =>
        {
            try
            {
                await RcloneDownloader.DownloadAsync(RcloneLocator.DownloadFolder, progress);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException
                                           or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return $"rclone could not be downloaded: {ex.Message}";
            }

            var latest = Monitor.Latest;
            if (latest is null || !latest.Settings.HasRemote || !latest.Settings.ManageRclone)
            {
                return null;
            }

            var roots = RootPaths(latest);
            return latest.Status?.Rclone?.State is "NotFound" or "Failed"
                ? await SyncService.StopAsync(roots) ?? await SyncService.StartAsync(roots)
                : latest.Running ? null : await SyncService.StartAsync(roots);
        }, report: false);
    }

    /// <summary>The log of the rclone the service runs: where it writes, or where it would.</summary>
    public static string RcloneLogPath(DriveSnapshot? snapshot) =>
        snapshot?.Status?.Rclone?.LogPath is { Length: > 0 } log
            ? log
            : Path.Combine(Path.GetDirectoryName(SyncStatePaths.StateRoot)!, "logs", "rclone.log");

    public void OpenRcloneLog()
    {
        var log = RcloneLogPath(Monitor.Latest);
        if (File.Exists(log))
        {
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true })?.Dispose();
        }
        else
        {
            ShowProblem($"There is no rclone log yet ({log}).");
        }
    }

    /// <summary>Runs one start, stop or save at a time; returns why it failed, or null.</summary>
    private async Task<string?> RunAsync(string what, Func<Task<string?>> action, bool report = true)
    {
        if (_busy)
        {
            return "OpenCfDrive is busy with something else; try again in a moment.";
        }

        _busy = true;
        _window?.ShowBusy(what);
        string? problem;
        try
        {
            problem = await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problem = ex.Message;
        }
        finally
        {
            _busy = false;
        }

        await RefreshAsync(probeRclone: true);
        _window?.ShowBusy(null);
        if (problem is not null && report)
        {
            ShowProblem(problem);
        }

        return problem;
    }

    public void ShowProblem(string problem)
    {
        if (_window is { Visible: true })
        {
            MessageBox.Show(_window, problem, "OpenCfDrive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            ShowBalloon("OpenCfDrive", problem, ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// A Windows notification (kept in the notification centre); clicking it
    /// runs <paramref name="clicked"/>, or opens the window. Where Windows
    /// refuses one, the tray icon's balloon.
    /// </summary>
    private void ShowBalloon(string title, string text, ToolTipIcon icon, Action? clicked = null)
    {
        var onClick = clicked ?? (() => ShowWindow(settings: !(Monitor.Latest?.Settings.HasRemote ?? false)));
        if (_notifications.Show(title, text, () => _ui.Post(_ => onClick(), null)))
        {
            return;
        }

        _balloonClicked = clicked;

        // What Windows takes: 63 characters of title, 255 of text.
        title = title.Length <= 63 ? title : title[..62] + "…";
        text = string.IsNullOrWhiteSpace(text) ? " " : text.Length <= 255 ? text : text[..254] + "…";
        _tray.ShowBalloonTip(10_000, title, text, icon);
    }

    public void OpenFolder()
    {
        // With several drives, the first one (which the window headlines).
        var root = Monitor.Latest?.Roots.Select(r => r.SyncRoot).FirstOrDefault(Directory.Exists);
        if (root is not null)
        {
            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true })?.Dispose();
        }
    }

    /// <summary>The service's log file: where the running service writes, or where it would.</summary>
    public static string LogPath(DriveSnapshot? snapshot) =>
        string.IsNullOrEmpty(snapshot?.Status?.LogPath)
            ? Path.Combine(Path.GetDirectoryName(SyncStatePaths.StateRoot)!, "logs", "OpenCfDrive.log")
            : snapshot.Status.LogPath;

    public void OpenLog()
    {
        var log = LogPath(Monitor.Latest);
        if (File.Exists(log))
        {
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true })?.Dispose();
        }
        else
        {
            ShowProblem($"There is no log yet ({log}).");
        }
    }

    private void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _closing.Cancel();
        _timer.Stop();
        Updates.Dispose();
        _window?.CloseForExit();
        _tray.Visible = false;
        _tray.Dispose();
        _icons.Dispose();
        _menu.Dispose();
        ExitThread();
    }
}
