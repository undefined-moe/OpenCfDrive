using System.Globalization;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Updates;

namespace OpenCfDrive.App;

/// <summary>
/// The Status page: how the drive is doing, in the notification area icon's
/// picture and words, with what can be done about it; what the drive is; and
/// what it did lately, in a list that takes the rest of the window.
/// </summary>
internal sealed partial class MainForm
{
    private readonly PictureBox _heroIcon = new() { SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 2, 16, 0) };
    private readonly Label _headline = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 2), UseMnemonic = false };
    private readonly Label _headlineDetail = Theme.Text();
    private readonly Button _openFolder = Theme.Button("Open folder");
    private readonly Button _start = Theme.Button("Start syncing");
    private readonly Button _stop = Theme.Button("Stop syncing");
    private readonly Button _restart = Theme.Button("Restart");
    private readonly Button _setUp = Theme.Button("Choose a remote");
    private readonly Card _updateBar = new(2, stretch: 0);
    private readonly Label _updateText = Theme.Text();
    private readonly Button _updateNow = Theme.Button("Restart and update");
    private readonly LinkLabel _folder = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3), UseMnemonic = false };
    private readonly Label _remote = Theme.Text();
    private readonly Label _storage = Theme.Text();
    private readonly Label _space = Theme.Text();
    private readonly Card _activityCard = new() { Padding = new Padding(1, 4, 1, 4) };
    private readonly ListView _activity = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        ShowItemToolTips = true,
        BorderStyle = BorderStyle.None,
        Margin = Padding.Empty,
        AccessibleName = "Recent activity",
    };

    private TableLayoutPanel _statusColumn = null!;
    private (int Count, DateTimeOffset Last) _shownActivity = (-1, default);
    private bool _fittingActivity;

    private Panel BuildStatusPage()
    {
        var (page, column) = Page();
        _statusColumn = column;

        _folder.LinkClicked += (_, _) => _app.OpenFolder();
        _openFolder.Click += (_, _) => _app.OpenFolder();
        _start.Click += async (_, _) => await _app.StartAsync();
        _stop.Click += async (_, _) => await _app.StopAsync();
        _restart.Click += async (_, _) => await _app.RestartAsync();
        _setUp.Click += (_, _) => ShowSettingsPage();
        _updateNow.Click += async (_, _) => await _app.InstallUpdateAsync();

        // An update waiting to be installed, above everything else.
        _updateBar.BackColor = Theme.Blend(Theme.Card, Theme.Accent, 0.08f);
        _updateBar.AddRow(_updateText, _updateNow);
        _updateNow.Anchor = AnchorStyles.Right;
        _updateNow.Margin = new Padding(12, 0, 0, 0);
        _updateText.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _updateText.Dock = DockStyle.None;
        _updateBar.Margin = new Padding(0, 0, 0, 12);
        _updateBar.Visible = false;

        // How the drive is doing, and what can be done about it.
        _headline.Font = _titleFont;
        var hero = new Card(2) { Padding = new Padding(20, 16, 20, 14) };
        hero.AddRow(_heroIcon, _headline);
        hero.SetRowSpan(_heroIcon, 3);
        hero.AddRow(null, _headlineDetail);
        hero.AddRow(null, Theme.ButtonRow(_setUp, _openFolder, _start, _stop, _restart));
        _headlineDetail.Margin = new Padding(0, 0, 0, 6);

        var drive = new Card(2);
        drive.AddRow(Theme.Caption("Folder"), _folder);
        drive.AddRow(Theme.Caption("Remote"), _remote);
        drive.AddRow(Theme.Caption("Storage"), _storage);
        drive.AddRow(Theme.Caption("Space"), _space);

        _activity.Columns.Add("Time");
        _activity.Columns.Add("What happened");
        _activity.SizeChanged += (_, _) => FitActivityColumns();
        UseExplorerLook(_activity);
        _activityCard.AddRow(_activity);

        Add(column, _updateBar, hero, Heading("Drive"), drive, Heading("Recent activity"), _activityCard);

        // The list takes the height the rest leaves, but keeps a few lines:
        // below that, the page scrolls.
        page.Resize += (_, _) => FitActivity();
        column.Layout += (_, _) => FitActivity();
        _statusPage = page;
        return page;
    }

    private void FitActivity()
    {
        if (_fittingActivity || !_statusPage.Visible)
        {
            return;
        }

        _fittingActivity = true;
        try
        {
            var rest = _statusColumn.Padding.Vertical + _activityCard.Margin.Vertical + _activityCard.Padding.Vertical;
            foreach (Control row in _statusColumn.Controls)
            {
                if (row != _activityCard && row.Visible)
                {
                    rest += row.Height + row.Margin.Vertical;
                }
            }

            var line = Font.Height + LogicalToDeviceUnits(4);
            var height = Math.Max(line * 7, _statusPage.ClientSize.Height - rest);
            if (Math.Abs(_activity.Height - height) > 1)
            {
                _activity.Height = height;
            }
        }
        finally
        {
            _fittingActivity = false;
        }
    }

    private void ShowStatus(DriveSnapshot snapshot)
    {
        var (health, headline, detail) = snapshot.Summary;
        if (_busy is not null)
        {
            (health, headline, detail) = (Health.Busy, _busy + "...", "One moment.");
        }

        _headline.Text = headline;
        _headline.ForeColor = ColorOf(health);
        _headlineDetail.Text = detail;

        var idle = _busy is null && !_app.Busy;
        var setUp = snapshot.Settings.HasRemote;

        // What can be done: choose a remote, fix what keeps it from starting, or start, stop and restart it.
        _setUp.Visible = !snapshot.Running && (!setUp || health == Health.Problem);
        _setUp.Text = setUp ? "Open Settings" : "Choose a remote";
        _openFolder.Visible = setUp || snapshot.Running;
        _openFolder.Enabled = snapshot.Roots.Any(root => Directory.Exists(root.SyncRoot));
        _start.Visible = setUp && !snapshot.Running;
        _start.Enabled = idle && snapshot.SettingsProblem is null;
        _stop.Visible = _restart.Visible = snapshot.Running;
        _stop.Enabled = _restart.Enabled = idle;

        // shortcut: several drives show the first, with how many more there
        // are; one list with each drive's own state is not built yet.
        var root = snapshot.Status?.SyncRoot ?? snapshot.Settings.SyncRoot.Value;
        var more = snapshot.Roots.Count - 1;
        _folder.Text = more > 0 ? $"{root} (+{more})" : root;
        _folder.LinkArea = Directory.Exists(root) ? new LinkArea(0, root.Length) : new LinkArea(0, 0);
        _tips.SetToolTip(_folder, more > 0 ? string.Join("\n", snapshot.Roots.Select(r => r.SyncRoot)) : null);
        _remote.Text = setUp || snapshot.Status is not null
            ? (more > 0 ? $"{snapshot.Remote} (+{more})" : snapshot.Remote)
            : "None chosen yet";
        (_storage.Text, _space.Text) = snapshot.RemoteInfo is { } info
            ? (info.Kind ?? info.Problem ?? "-", info.Problem is null ? SpaceText(info) : "-")
            : snapshot.Rclone is { Usable: true } && (setUp || snapshot.Status is not null)
                ? ("Asking rclone...", "Asking the server...")
                : ("-", "-");
        _storage.ForeColor = snapshot.RemoteInfo?.Problem is null ? SystemColors.ControlText : Theme.Problem;

        ShowActivity(snapshot.Status?.RecentActivity ?? []);
    }

    /// <summary>An update ready to install, offered at the top of the Status page.</summary>
    private void ShowUpdateBar(UpdateStatus status)
    {
        var ready = status.Stage == UpdateStage.Ready && status.Available is not null;
        _updateBar.Visible = ready;
        if (ready)
        {
            _updateText.Text = $"OpenCfDrive {GitHubReleases.Format(status.Available!)} is ready to install. " +
                               "Installing it restarts OpenCfDrive; syncing stops for the moment it takes.";
            _updateNow.Text = "Restart and update";
            _updateNow.Enabled = _busy is null && !_app.Busy;
        }
    }

    private void ShowActivity(IReadOnlyList<ActivityEntry> entries)
    {
        var shown = (entries.Count, entries.Count > 0 ? entries[^1].Time : default);
        if (shown == _shownActivity)
        {
            return;
        }

        _shownActivity = shown;
        _activity.BeginUpdate();
        _activity.Items.Clear();
        foreach (var entry in entries.Reverse())
        {
            var item = new ListViewItem([entry.Time.LocalDateTime.ToString("T", CultureInfo.CurrentCulture), entry.Message])
            {
                ToolTipText = entry.Message,
            };
            if (entry.Level is "Warning")
            {
                item.ForeColor = Theme.Busy;
            }
            else if (entry.Level is "Error" or "Critical")
            {
                item.ForeColor = Theme.Problem;
            }

            _activity.Items.Add(item);
        }

        if (entries.Count == 0)
        {
            _activity.Items.Add(new ListViewItem(["", "Nothing yet: uploads, downloads, deletes and problems show here."]) { ForeColor = Theme.Subtle });
        }

        _activity.EndUpdate();
        FitActivityColumns();
    }

    private void FitActivityColumns()
    {
        // The time as wide as a time is, and the rest of the visible width
        // for what happened: long lines end in "..." (their tooltip has all
        // of it) instead of adding a horizontal scroll bar.
        if (_activity.Columns.Count == 2)
        {
            var time = TextRenderer.MeasureText(DateTime.Now.ToString("T", CultureInfo.CurrentCulture), _activity.Font).Width + (Font.Height * 3 / 2);
            _activity.Columns[0].Width = time;
            var scrollBar = SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
            _activity.Columns[1].Width = Math.Max(Font.Height * 6, _activity.Width - time - scrollBar);
        }
    }
}
