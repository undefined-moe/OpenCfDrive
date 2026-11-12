using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using OpenCfDrive.App;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;
using OpenCfDrive.Core.Updates;

namespace OpenCfDrive.Tests;

/// <summary>
/// The app's window lays itself out whatever the drive's state, at the size
/// it opens at and at its smallest, and with larger text: every text shows
/// whole, every button fits its words, nothing overlaps or sticks out of
/// what holds it, inputs keep room to type, and the activity list keeps some
/// lines. It does on each screen scale the PC has (the windows are created
/// as the app's are, aware of each screen's scale). With
/// OPENCFDRIVE_UI_PICTURES set to a folder, it also saves a picture of each
/// page, for a person to look at.
/// </summary>
public sealed class MainFormLayoutTests
{
    private static readonly string? Pictures = Environment.GetEnvironmentVariable("OPENCFDRIVE_UI_PICTURES");

    public static TheoryData<string> States => new(Samples.Names);

    [Theory]
    [MemberData(nameof(States))]
    public void EveryPage_ShowsAllOfIt_AtEverySize(string state) => Sta.Run(() =>
    {
        var problems = new List<string>();
        foreach (var (screen, largeText) in Scales().SelectMany(s => new[] { (s, false), (s, true) }))
        {
            var app = Samples.App(state);
            using var form = new MainForm(app) { Opacity = 0 };
            form.PlaceOn(screen);
            if (largeText)
            {
                form.Font = new Font(form.Font.FontFamily, form.Font.SizeInPoints * 4 / 3);
            }

            form.ShowSnapshot(app.Latest!);
            form.ShowUpdate(app.UpdateStatus);
            form.Show();
            Application.DoEvents();

            var area = Screen.FromControl(form).WorkingArea;
            Assert.True(area.Contains(form.Bounds), $"{state}: the window ({form.Bounds}) is not within the screen ({area})");
            Assert.True(form.Width >= form.MinimumSize.Width && form.Height >= form.MinimumSize.Height, $"{state}: smaller than its minimum");

            foreach (var (size, name) in new[] { (form.Size, "opened"), (form.MinimumSize, "smallest") })
            {
                form.Size = size;
                foreach (var page in Descendants(form).OfType<NavItem>().ToList())
                {
                    page.Checked = true;
                    Application.DoEvents();
                    var where = $"{state}{(largeText ? ", large text" : "")}, {page.Text} page, {name} " +
                                $"({form.ClientSize.Width}x{form.ClientSize.Height} at {form.DeviceDpi} DPI)";
                    problems.AddRange(LayoutProblems(form).Select(p => $"{where}: {p}"));
                    if (page.Text == "Status")
                    {
                        var list = Descendants(form).OfType<ListView>().Single();
                        if (list.Height < form.Font.Height * 6)
                        {
                            problems.Add($"{where}: the activity list is {list.Height} px high, under 6 lines");
                        }
                    }

                    if (Pictures is { Length: > 0 } && !largeText)
                    {
                        Save(form, Path.Combine(Pictures, $"{state}-{page.Text}-{name}-{form.DeviceDpi}.png".Replace(' ', '-')));
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct()));
    });

    [Fact]
    public void TheWindow_FitsTheScreen_AndIsBiggerThanItsSmallest() => Sta.Run(() =>
    {
        foreach (var screen in Scales())
        {
            var app = Samples.App("up to date");
            using var form = new MainForm(app) { Opacity = 0 };
            form.PlaceOn(screen);
            form.ShowSnapshot(app.Latest!);
            form.Show();
            Application.DoEvents();

            var area = screen.WorkingArea;
            Assert.Equal(area, Screen.FromControl(form).WorkingArea);
            Assert.True(form.Width <= area.Width * 9 / 10 + 1 && form.Height <= area.Height * 9 / 10 + 1);
            Assert.True(form.Width > form.MinimumSize.Width || form.Width >= area.Width * 9 / 10);
            Assert.True(form.Height > form.MinimumSize.Height || form.Height >= area.Height * 9 / 10);

            // Centred.
            Assert.InRange(form.Left - area.Left - (area.Right - form.Right), -2, 2);
            Assert.InRange(form.Top - area.Top - (area.Bottom - form.Bottom), -2, 2);
        }
    });

    /// <summary>
    /// The spacing, given at 96 DPI, comes out at the screen's scale: on the
    /// main screen, and on one with another scale (where the window is
    /// created at the main screen's scale, then moved). Setting the window
    /// to scale before adding what it holds left it all at 96 DPI sizes.
    /// </summary>
    [Fact]
    public void TheSpacing_FollowsTheScreensScale() => Sta.Run(() =>
    {
        foreach (var screen in Scales())
        {
            var app = Samples.App("up to date");
            using var form = new MainForm(app) { Opacity = 0 };
            form.PlaceOn(screen);
            form.ShowSnapshot(app.Latest!);
            form.Show();
            Application.DoEvents();

            var scale = form.DeviceDpi / 96f;
            var caption = Descendants(form).OfType<Label>().First(l => l.Text == "Folder");
            var card = (Card)caption.Parent!;
            Assert.InRange(card.Padding.Left, (int)(16 * scale) - 1, (int)(16 * scale) + 1);
            Assert.InRange(caption.Margin.Right, (int)(16 * scale) - 1, (int)(16 * scale) + 1);
            Assert.InRange(form.Font.Height, (int)(15 * scale), (int)(17 * scale) + 1);
        }
    });

    /// <summary>A screen of each scale the PC has.</summary>
    private static IEnumerable<Screen> Scales() => Screen.AllScreens.DistinctBy(Dpi);

    private static uint Dpi(Screen screen)
    {
        var monitor = MonitorFromPoint(new Point(screen.Bounds.Left + 1, screen.Bounds.Top + 1), 2);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi : 96;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>What a person would see cut off, squeezed or on top of something else.</summary>
    private static IEnumerable<string> LayoutProblems(Control root)
    {
        foreach (var control in Descendants(root).Where(Shown))
        {
            var parent = control.Parent!;
            var name = Describe(control);

            // Within what holds it (a page that scrolls holds more than it shows).
            if (parent is not ScrollableControl { AutoScroll: true } && parent is not Form)
            {
                var room = parent.ClientRectangle;
                room.Inflate(1, 1);
                if (!room.Contains(control.Bounds))
                {
                    yield return $"{name} at {control.Bounds} sticks out of its {Describe(parent)} ({parent.ClientSize.Width}x{parent.ClientSize.Height})";
                }
            }

            // All of its text.
            if (control is Label or ButtonBase && control.Text.Length > 0)
            {
                var needs = control.GetPreferredSize(new Size(control.Width, 0));
                if (needs.Height > control.Height + 1 || (control is ButtonBase && needs.Width > control.Width + 1))
                {
                    yield return $"{name} is {control.Width}x{control.Height} but its text needs {needs.Width}x{needs.Height}";
                }
            }

            if (control is TextBoxBase or ComboBox && control.Width < control.Font.Height * 8)
            {
                yield return $"{name} is {control.Width} px wide: too narrow to type in";
            }

            // No empty space: each row of a card as high as what is in it needs.
            if (control is Card card)
            {
                var heights = card.GetRowHeights();
                for (var row = 0; row < heights.Length; row++)
                {
                    var cells = card.Controls.Cast<Control>()
                        .Where(c => c.Visible && card.GetRow(c) == row && card.GetRowSpan(c) == 1)
                        .ToList();
                    var needs = cells.Count == 0 ? 0 : cells.Max(c => c.GetPreferredSize(new Size(c.Width, 0)).Height + c.Margin.Vertical);
                    if (heights[row] > needs + 2)
                    {
                        yield return $"row {row} of a card is {heights[row]} px high, but what is in it needs {needs} " +
                                     $"({string.Join(", ", cells.Select(Describe))})";
                    }
                }

                if (card.Height > heights.Sum() + card.Padding.Vertical + 2)
                {
                    yield return $"a card is {card.Height} px high, but its rows need {heights.Sum() + card.Padding.Vertical}";
                }
            }
        }

        foreach (var parent in Descendants(root).Prepend(root).Where(c => c == root || Shown(c)))
        {
            var children = parent.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            for (var i = 0; i < children.Count; i++)
            {
                for (var j = i + 1; j < children.Count; j++)
                {
                    if (children[i].Bounds.IntersectsWith(children[j].Bounds))
                    {
                        yield return $"{Describe(children[i])} {children[i].Bounds} overlaps {Describe(children[j])} {children[j].Bounds}";
                    }
                }
            }
        }
    }

    /// <summary>Shown on screen: it and everything holding it are visible.</summary>
    private static bool Shown(Control control)
    {
        for (var c = control; c is not null; c = c.Parent)
        {
            if (!c.Visible)
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(Control control) =>
        $"{control.GetType().Name}{(control.Text is { Length: > 0 } text ? $" \"{(text.Length > 40 ? text[..40] + "..." : text)}\"" : "")}";

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>The window as it shows, and each page whole, scrolled part included.</summary>
    private static void Save(Form form, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var window = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(window, new Rectangle(Point.Empty, form.Size));
            window.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>The drive in each state the window shows differently.</summary>
    private static class Samples
    {
        public static readonly string[] Names =
        [
            "not set up", "up to date", "syncing", "offline", "rclone not found", "own rclone", "update ready", "long names",
        ];

        private const string Root = @"C:\Users\someone\OpenCfDrive";

        public static FakeApp App(string state)
        {
            var remote = state == "long names" ? "a-remote-with-a-long-name:photos/2026/holidays/the-summer-trip/edited" : "box:";
            var root = state == "long names" ? @"C:\Users\someone.with.a.long.name\Documents\Cloud drives\OpenCfDrive for the photos" : Root;
            var own = state == "own rclone";
            var saved = state == "not set up"
                ? new DriveSettings()
                : new DriveSettings { Remote = remote, SyncRoot = root, ManageRclone = !own, RcUser = own ? "opencfdrive" : null };
            var settings = new EffectiveSettings(
                new EffectiveSetting(root, SettingSource.SettingsFile, DriveSettingsFile.SyncRootVariable),
                state == "not set up"
                    ? new EffectiveSetting("", SettingSource.Default, DriveSettingsFile.RemoteVariable)
                    : new EffectiveSetting(remote, SettingSource.SettingsFile, DriveSettingsFile.RemoteVariable),
                new EffectiveSetting(DriveSettingsFile.DefaultRcUrl, SettingSource.Default, DriveSettingsFile.RcUrlVariable),
                ManageRclone: !own,
                RcUser: own ? "opencfdrive" : null,
                RcPassword: own ? "secret" : null);

            var running = state is not ("not set up" or "rclone not found");
            var rclone = state == "rclone not found"
                ? new RcloneProbeResult("run||", false, false, null,
                    "rclone was not found: it is not on PATH, and OpenCfDrive has not downloaded it.", null)
                : new RcloneProbeResult(own ? "url" : "run||", true, true, "v1.75.2", null, ["box", "a-remote-with-a-long-name"],
                    own ? null : new RcloneProgram(@"C:\Users\someone\AppData\Local\OpenCfDrive\rclone\rclone.exe", RcloneSource.Downloaded),
                    own ? null : @"C:\Users\someone\AppData\Roaming\rclone\rclone.conf");
            // What rclone said about the remote (the window asks for it otherwise).
            var info = state == "not set up"
                ? null
                : running
                    ? new RemoteInfo(SyncStatePaths.NormalizeRemote(remote), "webdav (nextcloud) at https://cloud.example.com/remote.php/dav/files/someone",
                        new RemoteSpace(10L << 30, 3L << 30, 7L << 30), null, null)
                    : new RemoteInfo(SyncStatePaths.NormalizeRemote(remote), null, null, null, "rclone was not found.");

            var activity = state switch
            {
                "syncing" => new SyncActivity(Sending: 3, Downloading: 1),
                "offline" => new SyncActivity(Waiting: 2),
                "up to date" or "update ready" or "long names" => SyncActivity.None,
                _ => new SyncActivity(Failing: 1),
            };
            var status = running
                ? new ServiceStatus(
                    ProcessId: 4242,
                    StartedAt: DateTimeOffset.Now.AddHours(-3),
                    Version: "1.3.0",
                    SyncRoot: root,
                    Remote: remote,
                    RcUrl: "http://127.0.0.1:24858",
                    State: state == "offline" ? ServiceState.Offline : ServiceState.Syncing,
                    Detail: state == "offline"
                        ? "The server can't be reached (no such host is known). Files you downloaded still open; your changes wait for it."
                        : "The drive is registered with Explorer and connected.",
                    Registered: true,
                    Connected: true,
                    ItemsTracked: 1234,
                    BytesDownloaded: 52_000_000,
                    LogPath: @"C:\Users\someone\AppData\Local\OpenCfDrive\logs\OpenCfDrive.log",
                    RecentActivity: Activity(state == "long names" ? 0 : 40),
                    Rclone: own
                        ? new RcloneStatus(false, DriveSettingsFile.DefaultRcUrl, null, null, "v1.75.2", null, "Running", "rclone v1.75.2 answers.", null)
                        : new RcloneStatus(true, "http://127.0.0.1:24858", @"C:\Users\someone\AppData\Local\OpenCfDrive\rclone\rclone.exe",
                            nameof(RcloneSource.Downloaded), "v1.75.2", 5151, "Running", "rclone v1.75.2 runs (process 5151).",
                            @"C:\Users\someone\AppData\Local\OpenCfDrive\logs\rclone.log"),
                    Activity: activity,
                    Revision: 3)
                : null;

            var update = state == "update ready"
                ? new UpdateStatus(UpdateStage.Ready, "OpenCfDrive 1.4.0 is downloaded and ready to install.", new Version(1, 4, 0),
                    "https://github.com/owner/name/releases/tag/v1.4.0", DateTimeOffset.Now)
                : new UpdateStatus(UpdateStage.UpToDate, "You have the latest version.", CheckedAt: DateTimeOffset.Now);

            return new FakeApp(new DriveSnapshot(saved, null, settings, running, status, rclone, info), update);
        }

        private static List<ActivityEntry> Activity(int count) => Enumerable.Range(0, count)
            .Select(i => new ActivityEntry(
                DateTimeOffset.Now.AddMinutes(i - count),
                i % 7 == 3 ? "Warning" : i % 11 == 5 ? "Error" : "Information",
                i % 3 == 0
                    ? $"Uploaded 'Documents/Projects/A folder with a long name/report-{i}.docx' (1.2 MB) after the server answered slowly"
                    : $"Downloaded 'photo-{i}.jpg'"))
            .ToList();
    }

    private sealed class FakeApp(DriveSnapshot latest, UpdateStatus update) : IDriveApp
    {
        public DriveSnapshot? Latest { get; } = latest;

        public bool Busy => false;

        public Version CurrentVersion { get; } = new(1, 3, 0);

        public UpdateStatus UpdateStatus { get; } = update;

        public Task CheckForUpdatesAsync() => Task.CompletedTask;

        public Task InstallUpdateAsync(bool ask = false) => Task.CompletedTask;

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public Task RestartAsync() => Task.CompletedTask;

        public Task<string?> ApplySettingsAsync(DriveSettings settings, IReadOnlyList<DriveChange> changes) => Task.FromResult<string?>(null);

        public Task<string?> DownloadRcloneAsync(IProgress<string> progress) => Task.FromResult<string?>(null);

        public void OpenFolder()
        {
        }

        public void OpenLog()
        {
        }

        public void OpenRcloneLog()
        {
        }

        public void Open(string target)
        {
        }
    }

    /// <summary>
    /// Windows Forms needs a single-threaded apartment; and the app is aware
    /// of each screen's scale (PerMonitorV2), so the window is too.
    /// </summary>
    private static class Sta
    {
        public static void Run(Action action)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    // As ApplicationConfiguration.Initialize does in the app; once a
                    // process has chosen, the thread is made aware all the same.
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    SetThreadDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
                    action();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            Application.EnableVisualStyles(); // as the app does: themed buttons, the sizes they really have
            thread.Start();
            thread.Join();
            failure?.Throw();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    }
}
