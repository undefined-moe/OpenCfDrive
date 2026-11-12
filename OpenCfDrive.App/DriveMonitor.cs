using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;

namespace OpenCfDrive.App;

internal enum Health
{
    Idle,
    Busy,
    Good,
    Problem,
}

/// <summary>One drive at one moment: whether its service runs and what it last said.</summary>
internal sealed record RootStatus(string SyncRoot, string Remote, bool Running, ServiceStatus? Status);

/// <summary>Everything the app shows, read at one moment.</summary>
internal sealed record DriveSnapshot(
    DriveSettings Saved,
    string? SettingsProblem,
    EffectiveSettings Settings,
    bool Running,
    ServiceStatus? Status,
    RcloneProbeResult? Rclone,
    RemoteInfo? RemoteInfo,
    IReadOnlyList<RootStatus>? MoreRoots = null)
{
    /// <summary>Every configured drive; the first is the one the Settings page edits first and the window headlines.</summary>
    public IReadOnlyList<RootStatus> Roots =>
        MoreRoots ?? [new(Settings.SyncRoot.Value, Remote, Running, Status)];

    /// <summary>The remote the service syncs with, or would once started.</summary>
    public string Remote => Status?.Remote ?? Settings.Remote.Value;

    /// <summary>One word for the tray and the window's header, and a sentence on it: the worst of the drives.</summary>
    public (Health Health, string Headline, string Detail) Summary
    {
        get
        {
            if (SettingsProblem is not null)
            {
                return (Health.Problem, "Settings problem", SettingsProblem);
            }

            if (!Running)
            {
                if (!Settings.HasRemote)
                {
                    return (Health.Idle, "Not set up", "Choose your rclone remote in Settings to start.");
                }

                return Settings.ManageRclone && Rclone is { Answers: false }
                    ? (Health.Problem, "rclone not found", $"{Rclone.Problem} Choose rclone.exe or download it in Settings.")
                    : (Health.Idle, "Stopped", "The sync service is not running.");
            }

            if (Status is null)
            {
                return (Health.Busy, "Running", "The sync service runs, but has not reported its status.");
            }

            // Each drive says how it is doing; the worst one is what the one
            // icon and one headline stand for, with its name when there are
            // several and it has something to say.
            var worstRoot = WorstRoot();
            var worst = SummaryOf(worstRoot);
            if (Roots.Count > 1 && worst.Health != Health.Good)
            {
                worst = (worst.Health, worst.Headline, $"{NameOf(worstRoot.SyncRoot)}: {worst.Detail}");
            }

            // The rclone the service runs itself reports through the service;
            // a stopped rclone is every drive's problem, so no drive's name.
            var ownRclone = Status.Rclone is { Managed: true } own ? own : null;
            if (worst.Health != Health.Problem && ownRclone is { State: not "Running" })
            {
                worst = (Health.Problem, "rclone stopped", ownRclone.Detail);
            }
            else if (worst.Health != Health.Problem && ownRclone is null && Rclone is { Usable: false })
            {
                worst = (Health.Problem, "rclone unreachable", Rclone.Problem ?? "");
            }

            return RunsWithOtherSettings
                ? (worst.Health, worst.Headline, $"{worst.Detail} Restart it to use the saved settings.")
                : worst;
        }
    }

    /// <summary>What one drive's own status says about it.</summary>
    private (Health Health, string Headline, string Detail) SummaryOf(RootStatus root)
    {
        if (root.Status is null)
        {
            return (Health.Busy, "Starting", "The drive is registering and connecting.");
        }

        var ownRclone = root.Status.Rclone is { Managed: true } own ? own : null;
        return root.Status.State switch
        {
            ServiceState.Starting => (Health.Busy, "Starting", root.Status.Detail),
            ServiceState.Offline => (Health.Busy, "Offline", $"{root.Status.Detail} {root.Status.Activity?.Describe()}".TrimEnd()),
            ServiceState.Unavailable => (Health.Problem, "Cloud Files unavailable", root.Status.Detail),
            ServiceState.Failed when ownRclone?.State == "NotFound" => (Health.Problem, "rclone not found", root.Status.Detail),
            ServiceState.Failed => (Health.Problem, "Problem", root.Status.Detail),
            _ when root.Status.Activity is not { } activity => (Health.Good, "Running", root.Status.Detail),
            _ when activity.Busy => (Health.Good, "Syncing", activity.Describe()),
            _ => activity.Failing > 0
                ? (Health.Busy, activity.Failing == 1 ? "1 file not synced" : $"{activity.Failing:N0} files not synced", activity.Describe())
                : (Health.Good, "Up to date", root.Status.Detail),
        };
    }

    private static int Rank(Health health) => health switch
    {
        Health.Problem => 3,
        Health.Busy => 2,
        Health.Good => 1,
        _ => 0,
    };

    private static string NameOf(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));

    /// <summary>What the notification area icon shows: the worst of the drives.</summary>
    public DriveIndicator Indicator
    {
        get
        {
            var worst = WorstRoot();
            return DriveIndicators.Of(Summary.Health == Health.Problem, Running, Settings.HasRemote, worst.Status ?? Status);
        }
    }

    /// <summary>The drive in the worst shape: what the icon and the headline stand for.</summary>
    private RootStatus WorstRoot()
    {
        var worst = Roots[0];
        foreach (var root in Roots.Skip(1))
        {
            if (Rank(SummaryOf(root).Health) > Rank(SummaryOf(worst).Health))
            {
                worst = root;
            }
        }

        return worst;
    }

    /// <summary>The service started before the settings in effect now were saved.</summary>
    public bool RunsWithOtherSettings =>
        Status is not null &&
        (!string.Equals(Status.SyncRoot, Settings.SyncRoot.Value, StringComparison.OrdinalIgnoreCase) ||
         !string.Equals(Status.Remote, Settings.Remote.Value, StringComparison.Ordinal) ||
         Roots.Any(root => root.Status is { } status &&
             !string.Equals(status.Remote, root.Remote, StringComparison.Ordinal)) ||
         (Status.Rclone?.Managed ?? true) != Settings.ManageRclone ||
         (!Settings.ManageRclone && !string.Equals(Status.RcUrl, Settings.RcUrl.Value, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// Reads the settings, the service's status and rclone's, whenever asked,
/// and takes in what a watched service says between those reads.
/// </summary>
internal sealed class DriveMonitor
{
    private static readonly TimeSpan ProgramCheckInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, ServicePulse> _pulses = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshing;
    private (TimeSpan Rclone, TimeSpan Remote)? _again;
    private bool _describing;
    private DateTime _rcloneCheckedAt;
    private DateTime _remoteDescribedAt;

    public DriveSnapshot? Latest { get; private set; }

    /// <summary>Raised on the thread that refreshed (the UI thread).</summary>
    public event Action<DriveSnapshot>? Updated;

    /// <summary>
    /// Raised by <see cref="Apply"/> when a pulse changed <see cref="Latest"/>:
    /// only its state and activity, so it may come often.
    /// </summary>
    public event Action<DriveSnapshot>? Pulsed;

    /// <summary>
    /// What the watched service said about one drive since the last read:
    /// kept in <see cref="Latest"/> unless a newer read already has it.
    /// </summary>
    public void Apply(string syncRoot, ServicePulse pulse)
    {
        _pulses[syncRoot] = pulse;
        if (Latest is not { } latest)
        {
            return;
        }

        var changed = false;
        var roots = new List<RootStatus>(latest.Roots.Count);
        foreach (var root in latest.Roots)
        {
            if (root.Status is { } status &&
                string.Equals(root.SyncRoot, syncRoot, StringComparison.OrdinalIgnoreCase) &&
                status.With(pulse) is { } merged && !ReferenceEquals(merged, status))
            {
                roots.Add(root with { Status = merged });
                changed = true;
            }
            else
            {
                roots.Add(root);
            }
        }

        if (!changed)
        {
            return;
        }

        // The headline status is the first running drive's; it follows that
        // drive's pulse too.
        var pulsed = roots.First(root => string.Equals(root.SyncRoot, syncRoot, StringComparison.OrdinalIgnoreCase));
        Latest = latest with
        {
            MoreRoots = roots,
            Status = string.Equals(latest.Status?.SyncRoot, syncRoot, StringComparison.OrdinalIgnoreCase)
                ? pulsed.Status
                : latest.Status,
        };
        Pulsed?.Invoke(Latest);
    }

    /// <param name="rcloneMaxAge">How old rclone's last answer may be before it is asked again.</param>
    /// <param name="remoteMaxAge">How old the remote's description (and space) may be.</param>
    public async Task RefreshAsync(TimeSpan rcloneMaxAge, TimeSpan remoteMaxAge)
    {
        if (_refreshing)
        {
            // Asked again meanwhile: once more when this one is done.
            _again = (rcloneMaxAge, remoteMaxAge);
            return;
        }

        _refreshing = true;
        try
        {
            DriveSettings saved;
            string? problem = null;
            try
            {
                saved = DriveSettingsFile.Load();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                saved = new DriveSettings();
                problem = ex.Message;
            }

            var settings = DriveSettingsFile.Resolve(saved);

            // One status per configured drive, from its own pipe.
            var roots = new List<RootStatus>(settings.Roots.Count);
            foreach (var root in settings.Roots)
            {
                var running = ServiceControl.IsRunning(root.SyncRoot);
                var status = running
                    ? (await ServiceControl.GetStatusAsync(root.SyncRoot, TimeSpan.FromSeconds(2)))?.With(_pulses.GetValueOrDefault(root.SyncRoot))
                    : null;
                roots.Add(new RootStatus(root.SyncRoot, root.Remote, running, status));
            }

            var anyRunning = roots.Exists(root => root.Running);
            var primary = roots.FirstOrDefault(root => root.Running) ?? roots[0];

            // Asking the rclone program means running it a few times; the
            // service reports its own rclone's state live anyway.
            var access = RcloneAccess.From(settings);
            if (access.Managed && rcloneMaxAge > TimeSpan.Zero && rcloneMaxAge < ProgramCheckInterval)
            {
                rcloneMaxAge = ProgramCheckInterval;
            }

            var rclone = Latest?.Rclone;
            if (rclone is null || rclone.Key != access.Key || DateTime.UtcNow - _rcloneCheckedAt >= rcloneMaxAge)
            {
                rclone = await RcloneProbe.CheckAsync(access);
                _rcloneCheckedAt = DateTime.UtcNow;
            }

            var snapshot = new DriveSnapshot(
                saved, problem, settings, anyRunning, primary.Status, rclone, null,
                roots.Count == 1 ? null : roots);
            var described = Latest?.RemoteInfo;
            var known = (settings.HasRemote || anyRunning) && rclone.Usable;
            Latest = snapshot with { RemoteInfo = known && described?.Remote == snapshot.Remote ? described : null };
            Updated?.Invoke(Latest);

            if (known && (Latest.RemoteInfo is null || DateTime.UtcNow - _remoteDescribedAt >= remoteMaxAge))
            {
                _ = DescribeRemoteAsync(access, snapshot.Remote);
            }
        }
        finally
        {
            _refreshing = false;
        }

        if (_again is { } again)
        {
            _again = null;
            await RefreshAsync(again.Rclone, again.Remote);
        }
    }

    /// <summary>Asks rclone about the remote off to the side: the server may take a while to say its space.</summary>
    private async Task DescribeRemoteAsync(RcloneAccess access, string remote)
    {
        if (_describing)
        {
            return;
        }

        _describing = true;
        try
        {
            var info = await RcloneProbe.DescribeAsync(access, remote);
            _remoteDescribedAt = DateTime.UtcNow;
            if (Latest is { } latest && latest.Remote == remote)
            {
                Latest = latest with { RemoteInfo = info };
                Updated?.Invoke(Latest);
            }
        }
        finally
        {
            _describing = false;
        }
    }
}
