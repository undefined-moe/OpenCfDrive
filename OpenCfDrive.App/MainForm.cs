using System.Globalization;
using System.Runtime.InteropServices;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Updates;

namespace OpenCfDrive.App;

/// <summary>
/// The window the notification area icon opens, laid out as Windows 11's
/// Settings and OneDrive's settings window are: a navigation pane with the
/// drive's state and remote, and three pages. Status says how the drive is
/// doing and what it did lately; Settings chooses what it syncs with and how
/// rclone runs; About has the version and updates, what the sync service and
/// rclone report, and the logs. Closing it only hides it; the icon stays.
/// </summary>
/// <remarks>
/// No size in it is fixed. Text wraps to the width there is, cards grow with
/// what they hold, pages scroll when the window is short, and the window
/// opens as big as its pages want within the screen it opens on. Spacing is
/// given at 96 DPI; WinForms scales it to each screen's scale, but only when
/// the window is told how to scale after what it holds is added (as the
/// designer writes it), and it takes a size given to the window itself
/// before it opens in the main screen's pixels. So the window's own size is
/// set once it knows its screen (see <see cref="FitToScreen"/>).
/// </remarks>
internal sealed partial class MainForm : Form
{
    private readonly IDriveApp _app;
    private readonly Font _titleFont;
    private readonly Font _headingFont;

    private readonly TableLayoutPanel _nav = new()
    {
        Dock = DockStyle.Left,
        ColumnCount = 1,
        Padding = new Padding(12, 16, 12, 12),
        BackColor = Theme.Page,
    };

    private readonly NavItem _statusNav = new("Status", Theme.SyncGlyph);
    private readonly NavItem _settingsNav = new("Settings", Theme.SettingsGlyph);
    private readonly NavItem _aboutNav = new("About", Theme.InfoGlyph);
    private readonly PictureBox _driveIcon = new() { SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 2, 10, 0) };
    private readonly Label _driveName = new() { Text = "OpenCfDrive", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 1), UseMnemonic = false };
    private readonly Label _driveRemote = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Theme.Subtle, Margin = Padding.Empty, UseMnemonic = false };
    private readonly Panel _pages = new() { Dock = DockStyle.Fill, BackColor = Theme.Page };
    private readonly ToolTip _tips = new();

    private Panel _statusPage = null!;
    private Panel _settingsPage = null!;
    private Panel _aboutPage = null!;
    private DriveSnapshot? _snapshot;
    private string? _busy;
    private bool _exiting;
    private bool _placed;
    private DriveIndicator? _indicator;
    private (int Dpi, int Line) _metrics;

    public MainForm(IDriveApp app)
    {
        _app = app;

        // Nothing is laid out until all of it is built: then once.
        var building = Theme.OneLayout(this);

        Text = "OpenCfDrive";
        Icon = AppIcon.Large;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Page;
        _titleFont = Theme.Title(Font);
        _headingFont = Theme.Heading(Font);
        _driveName.Font = _headingFont;

        _pages.Controls.Add(BuildStatusPage());
        _pages.Controls.Add(BuildSettingsPage());
        _pages.Controls.Add(BuildAboutPage());
        BuildNav();
        Controls.Add(_pages);
        Controls.Add(_nav);
        _nav.TabIndex = 0;
        _pages.TabIndex = 1;

        // Last, as the designer writes it: the sizes above are at 96 DPI, and
        // the window scales them to its screen's once it is created.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        building.Dispose();

        _statusNav.Checked = true;
        FormClosing += (_, e) =>
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    // --- the window ----------------------------------------------------------

    private void BuildNav()
    {
        _nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // The drive at the top, as Settings shows the account.
        var drive = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(6, 0, 0, 18),
        };
        drive.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        drive.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        drive.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        drive.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        drive.Controls.Add(_driveIcon, 0, 0);
        drive.SetRowSpan(_driveIcon, 2);
        drive.Controls.Add(_driveName, 1, 0);
        drive.Controls.Add(_driveRemote, 1, 1);

        Control[] rows = [drive, _statusNav, _settingsNav, _aboutNav];
        _nav.RowCount = rows.Length + 1;
        foreach (var row in rows)
        {
            _nav.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _nav.Controls.Add(row);
        }

        _nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _statusNav.CheckedChanged += (_, _) => ShowPage();
        _settingsNav.CheckedChanged += (_, _) => ShowPage();
        _aboutNav.CheckedChanged += (_, _) => ShowPage();
    }

    private void ShowPage()
    {
        var page = _settingsNav.Checked ? _settingsPage : _aboutNav.Checked ? _aboutPage : _statusPage;
        _pages.SuspendLayout();
        foreach (Control other in _pages.Controls)
        {
            other.Visible = other == page;
        }

        _pages.ResumeLayout(performLayout: true);
    }

    /// <summary>
    /// Sizes that follow the font, and pictures drawn for the screen's scale:
    /// again whenever the window moves to a screen with another scale.
    /// </summary>
    private void ApplyMetrics()
    {
        // A new scale changes the font and the DPI: both say so.
        if ((DeviceDpi, Font.Height) == _metrics)
        {
            return;
        }

        _metrics = (DeviceDpi, Font.Height);
        using var batch = Theme.OneLayout(this);
        var glyphs = Theme.Icons(Font);
        foreach (var item in new[] { _statusNav, _settingsNav, _aboutNav })
        {
            item.GlyphFont?.Dispose();
            item.GlyphFont = glyphs is null ? null : (Font)glyphs.Clone();
            item.Invalidate();
        }

        glyphs?.Dispose();

        // Wide enough for the longest page name, and for a remote's name.
        var items = new[] { _statusNav, _settingsNav, _aboutNav }.Max(i => i.GetPreferredSize(Size.Empty).Width);
        _nav.Width = Math.Max(items, Font.Height * 10) + _nav.Padding.Horizontal;

        // Measured in this screen's pixels, so after the window scaled what it holds.
        foreach (var page in new Control[] { _statusPage, _settingsPage, _aboutPage })
        {
            Theme.LineUpCaptions(page);
        }

        _indicator = null; // drawn again at the new size
        if (_snapshot is not null)
        {
            ShowIndicator(_snapshot);
        }

        using (var icon = AppIcon.Load(Square(48)))
        {
            _aboutIcon.Image = Swap(_aboutIcon.Image, icon.ToBitmap());
        }

        FitActivityColumns();
    }

    private Size Square(int logical) => new(LogicalToDeviceUnits(logical), LogicalToDeviceUnits(logical));

    private static Image Swap(Image? old, Image image)
    {
        old?.Dispose();
        return image;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (IsHandleCreated)
        {
            ApplyMetrics();
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyMetrics();
    }

    /// <summary>Showing it does not take the focus; the app activates it when the user asked for it.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyMetrics();
        FitToScreen();
        _placed = true;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        // Shown again after the screens changed: back onto one.
        if (Visible && _placed && !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Bounds)))
        {
            FitToScreen();
        }
    }

    /// <summary>
    /// Before the window first opens: on the screen with the pointer (where
    /// the notification area icon was clicked), so it opens at that screen's
    /// scale.
    /// </summary>
    public void PlaceOn(Screen screen)
    {
        if (!_placed)
        {
            var area = screen.WorkingArea;
            Location = new Point(area.Left + (area.Width / 4), area.Top + (area.Height / 4));
        }
    }

    /// <summary>
    /// As big as the pages want, in this screen's pixels: the navigation pane
    /// beside a page 40 lines of text wide (640 pixels at 100%), as tall as
    /// the Settings page; never more than nine tenths of the screen, never
    /// less than what keeps every page usable. Centred on the screen.
    /// </summary>
    private void FitToScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var line = Font.Height;
        var page = Theme.PagePadding.Horizontal;

        var smallest = SizeFromClientSize(new Size(_nav.Width + LogicalToDeviceUnits(page) + (line * 26), line * 28));
        var wanted = SizeFromClientSize(new Size(
            _nav.Width + LogicalToDeviceUnits(page) + (line * 40),
            Math.Clamp(SettingsHeight(LogicalToDeviceUnits(page) + (line * 40)), line * 34, line * 44)));

        var most = new Size(area.Width * 9 / 10, area.Height * 9 / 10);
        MinimumSize = new Size(Math.Min(smallest.Width, most.Width), Math.Min(smallest.Height, most.Height));
        var size = new Size(Math.Clamp(wanted.Width, MinimumSize.Width, most.Width), Math.Clamp(wanted.Height, MinimumSize.Height, most.Height));
        Bounds = new Rectangle(
            area.Left + ((area.Width - size.Width) / 2),
            area.Top + ((area.Height - size.Height) / 2),
            size.Width,
            size.Height);
    }

    // --- showing the state ---------------------------------------------------

    public void ShowSettingsPage() => _settingsNav.Checked = true;

    /// <summary>Marks a start, stop or save in progress (or none).</summary>
    public void ShowBusy(string? what)
    {
        _busy = what;
        UseWaitCursor = what is not null;
        if (_snapshot is not null)
        {
            ShowSnapshot(_snapshot);
        }
    }

    public void ShowSnapshot(DriveSnapshot snapshot)
    {
        using var batch = Theme.OneLayout(this);
        _snapshot = snapshot;
        var remote = snapshot.Settings.HasRemote || snapshot.Status is not null ? snapshot.Remote : null;
        var more = snapshot.Roots.Count - 1;
        _driveRemote.Text = remote is null
            ? "Not set up"
            : more > 0 ? $"{Shortened(remote)} +{more}" : Shortened(remote);
        _tips.SetToolTip(_driveRemote, remote is null ? null : string.Join("\n", snapshot.Roots.Select(root => root.Remote)));
        ShowIndicator(snapshot);
        ShowStatus(snapshot);
        ShowAbout(snapshot);
        ShowSettingsState(snapshot);
    }

    /// <summary>A long remote by its name and the last folder of its path: "photos:…/edited".</summary>
    private static string Shortened(string remote)
    {
        const int Most = 28;
        var colon = remote.IndexOf(':');
        if (remote.Length <= Most || colon < 0)
        {
            return remote;
        }

        var last = remote[(colon + 1)..].TrimEnd('/').Split('/')[^1];
        return $"{remote[..(colon + 1)]}…/{last}";
    }

    /// <summary>The notification area icon's picture, in the navigation pane and on the Status page.</summary>
    private void ShowIndicator(DriveSnapshot snapshot)
    {
        var indicator = snapshot.Indicator;
        if (indicator == _indicator)
        {
            return;
        }

        _indicator = indicator;
        _driveIcon.Image = Swap(_driveIcon.Image, TrayIcons.Draw(indicator, Square(32)));
        _heroIcon.Image = Swap(_heroIcon.Image, TrayIcons.Draw(indicator, Square(48)));
    }

    /// <summary>Closes for real: the app is exiting.</summary>
    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    // --- shared pieces ---------------------------------------------------------

    /// <summary>A page: a heading for each card, in a column that scrolls when the window is short.</summary>
    private static (Panel Page, TableLayoutPanel Column) Page()
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Page, Visible = false };
        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = Theme.PagePadding,
            BackColor = Theme.Page,
        };
        Theme.HoldWhileBuilding(page);
        Theme.HoldWhileBuilding(column);
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(column);
        return (page, column);
    }

    private static void Add(TableLayoutPanel column, params Control[] rows)
    {
        foreach (var row in rows)
        {
            column.RowCount++;
            column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            column.Controls.Add(row, 0, column.RowCount - 1);
        }
    }

    private Label Heading(string text) => Theme.Heading(text, _headingFont);

    private static Color ColorOf(Health health) => health switch
    {
        Health.Good => Theme.Good,
        Health.Busy => Theme.Busy,
        Health.Problem => Theme.Problem,
        _ => SystemColors.ControlText,
    };

    private static string SourceText(string? source) => source switch
    {
        nameof(RcloneSource.Chosen) => "chosen in Settings",
        nameof(RcloneSource.Path) => "found on PATH",
        nameof(RcloneSource.Downloaded) => "downloaded from rclone.org",
        _ => "found",
    };

    /// <summary>"3.1 GB used, 6.9 GB free of 10 GB", or why there is no number.</summary>
    private static string SpaceText(RemoteInfo info)
    {
        if (info.Space is not { } space || (space.Total, space.Used, space.Free) == (null, null, null))
        {
            return info.SpaceProblem ?? "Not reported by the server.";
        }

        var parts = new List<string>();
        if (space.Used is { } used)
        {
            parts.Add($"{FormatBytes(used)} used");
        }

        if (space.Free is { } free)
        {
            parts.Add($"{FormatBytes(free)} free");
        }

        var text = string.Join(", ", parts);
        return space.Total is { } total
            ? text.Length == 0 ? $"{FormatBytes(total)} in all" : $"{text} of {FormatBytes(total)}"
            : text;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", bytes)
            : string.Format(CultureInfo.CurrentCulture, "{0:N1} {1}", value, units[unit]);
    }

    /// <summary>Lists in the look Explorer gives its own: a soft highlight, no lines.</summary>
    private static void UseExplorerLook(Control control)
    {
        control.HandleCreated += (_, _) => SetWindowTheme(control.Handle, "Explorer", null);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? subApplication, string? idList);
}
