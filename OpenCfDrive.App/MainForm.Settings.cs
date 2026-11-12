using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.App;

/// <summary>
/// The Settings page: the drive (its remote and folder), how rclone runs,
/// and the app's own choices. Changes wait for Save, which checks them and
/// carries them out; the fields only show what rclone has to say about a
/// choice before it is saved.
/// </summary>
internal sealed partial class MainForm
{
    private readonly ComboBox _rootBox = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "drive", Margin = new Padding(0, 4, 0, 4) };
    private readonly Button _addRoot = Theme.Button("Add drive");
    private readonly Button _removeRoot = Theme.Button("Remove");
    private readonly ComboBox _remoteBox = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, DropDownStyle = ComboBoxStyle.DropDown, AccessibleName = "rclone remote", Margin = new Padding(0, 4, 8, 4) };
    private readonly Label _remoteDetails = Theme.Hint();
    private readonly TextBox _folderBox = Input("Drive folder");
    private readonly RadioButton _manageRclone = new() { Text = "OpenCfDrive runs rclone", AutoSize = true, Checked = true, Margin = new Padding(0, 2, 24, 6) };
    private readonly RadioButton _ownRclone = new() { Text = "I run rclone rcd myself", AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
    private readonly TextBox _programBox = Input("rclone program", "On PATH, else the copy downloaded from rclone.org");
    private readonly TextBox _configBox = Input("rclone config file", "rclone's own default");
    private readonly Label _programNote = Theme.Hint();
    private readonly Label _configNote = Theme.Hint();
    private readonly Button _download = Theme.Button("Download rclone from rclone.org");
    private readonly Label _downloadProgress = Theme.Hint();
    private readonly TextBox _rcUrlBox = Input("rclone remote control");
    private readonly TextBox _rcUserBox = Input("rclone remote control user");
    private readonly TextBox _rcPasswordBox = Input("rclone remote control password");
    private readonly Button _test = Theme.Button("Test");
    private readonly Label _rcTest = Theme.Hint();
    private readonly Label _rcdHint = Theme.Hint(
        "Start your rclone rcd with --rc-serve and a login, for example as below. Never --rc-no-auth: it lets any program " +
        "on this PC use your remotes.");
    private readonly TextBox _rcdCommand = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, ReadOnly = true, Margin = new Padding(0, 0, 8, 4), AccessibleName = "rclone rcd command" };
    private readonly Button _copyCommand = Theme.Button("Copy");
    private readonly CheckBox _signIn = new() { Text = "Start OpenCfDrive when I sign in to Windows", AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
    private readonly CheckBox _autoUpdate = new() { Text = "Check for new versions and download them", AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
    private readonly Label _autoUpdateHint = Theme.Hint("Every 10 hours, from the project's releases on GitHub. You choose when to install.");
    private readonly Label _overrides = Theme.Text(color: Theme.Busy);
    private readonly Button _save = Theme.Button("Save");
    private readonly Button _discard = Theme.Button("Discard changes");
    private readonly Label _saveResult = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 16, 6), UseMnemonic = false };
    private readonly System.Windows.Forms.Timer _checkSoon = new() { Interval = 800 };
    private readonly System.Windows.Forms.Timer _describeSoon = new() { Interval = 600 };
    private readonly List<Control> _managedRows = [];
    private readonly List<Control> _ownRows = [];
    private TableLayoutPanel _settingsColumn = null!;
    private TableLayoutPanel _footer = null!;

    private FieldSet? _loaded;
    private bool _loading;
    // The drives being edited, as (folder, remote) pairs; one of them is in
    // the fields below the picker at a time.
    private List<(string Folder, string Remote)> _draftRoots = [];
    private int _rootIndex;
    private bool _fillingRootBox;

    private static TextBox Input(string name, string? placeholder = null) => new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        AccessibleName = name,
        PlaceholderText = placeholder ?? "",
        Margin = new Padding(0, 4, 8, 4),
    };

    private Panel BuildSettingsPage()
    {
        var (page, column) = Page();
        _settingsColumn = column;

        var browse = Theme.Button("Browse...");
        browse.Click += (_, _) => BrowseForFolder();
        var refresh = Theme.Button("Refresh list");
        refresh.Click += async (_, _) => await TestRcloneAsync(fillRemotes: true);
        var browseProgram = Theme.Button("Browse...");
        browseProgram.Click += (_, _) => BrowseForFile(_programBox, "Choose rclone.exe", "rclone|rclone.exe|Programs|*.exe");
        var browseConfig = Theme.Button("Browse...");
        browseConfig.Click += (_, _) => BrowseForFile(_configBox, "Choose rclone's config file", "rclone config|*.conf|All files|*.*");
        _download.Click += async (_, _) => await DownloadRcloneAsync();
        _test.Click += async (_, _) => await TestRcloneAsync(fillRemotes: true);
        _copyCommand.Click += (_, _) => Clipboard.SetText(_rcdCommand.Text);
        _rcPasswordBox.UseSystemPasswordChar = true;
        _rcdCommand.Font = new Font(FontFamily.GenericMonospace, Font.SizeInPoints);
        _rcdCommand.BackColor = Theme.Card;
        _save.Click += async (_, _) => await SaveAsync();
        _discard.Click += (_, _) =>
        {
            if (_snapshot is not null)
            {
                LoadSettings(_snapshot);
                ShowSaveResult("", SystemColors.ControlText);
            }
        };

        _addRoot.Click += (_, _) => AddRoot();
        _removeRoot.Click += (_, _) => RemoveRoot();
        _rootBox.SelectedIndexChanged += (_, _) =>
        {
            if (!_fillingRootBox && !_loading)
            {
                SwitchRoot();
            }
        };

        // The drive(s): what each shows, and where.
        var drive = new Card(3);
        var rootTools = new WrapRow();
        rootTools.Controls.AddRange([_addRoot, _removeRoot]);
        drive.AddRow(Theme.FieldCaption("Drive"), _rootBox);
        drive.AddIndented(rootTools);
        drive.AddRow(Theme.FieldCaption("Remote"), _remoteBox, refresh);
        drive.AddIndented(_remoteDetails);
        drive.AddIndented(Theme.Hint(
            "A remote in rclone's config, optionally with a path inside it: myremote: or myremote:backup. " +
            "Create remotes with rclone config."));
        drive.AddRow(Theme.FieldCaption("Folder"), _folderBox, browse);
        drive.SameWidthButtons();
        drive.AddIndented(Theme.Hint(
            "Where the drive appears in Explorer. Moving it, or choosing another remote, needs a new or empty folder; " +
            "the old one stays as an ordinary folder with the files already downloaded."));
        drive.AddIndented(Theme.Hint(
            "Add another drive to sync a second folder with its own remote; each gets its own place in Explorer."));

        // rclone: the program OpenCfDrive runs, or the rclone rcd the user runs.
        var rclone = new Card(3);
        var mode = new WrapRow();
        mode.Controls.AddRange([_manageRclone, _ownRclone]);
        rclone.AddRow(mode);
        Managed(rclone, Theme.FieldCaption("Program"), _programBox, browseProgram);
        Managed(rclone, null, _programNote);
        Managed(rclone, null, Theme.ButtonRow(_download));
        Managed(rclone, null, _downloadProgress);
        Managed(rclone, Theme.FieldCaption("Config file"), _configBox, browseConfig);
        Managed(rclone, null, _configNote);
        Own(rclone, Theme.FieldCaption("Address"), _rcUrlBox, _test);
        Own(rclone, Theme.FieldCaption("User"), _rcUserBox, null);
        Own(rclone, Theme.FieldCaption("Password"), _rcPasswordBox, null);
        Own(rclone, null, _rcTest);
        Own(rclone, null, _rcdHint);
        Own(rclone, null, _rcdCommand, _copyCommand);
        rclone.SameWidthButtons();

        // The app.
        var general = new Card();
        general.AddRow(_signIn);
        general.AddRow(_autoUpdate);
        general.AddRow(_autoUpdateHint);
        _autoUpdateHint.Margin = new Padding(20, 0, 0, 2); // under the box's text
        _overrides.Visible = false;

        Add(column, Heading("Drive"), drive, Heading("rclone"), rclone, Heading("General"), general, _overrides);

        // Save stays in view below the fields.
        _footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Padding = new Padding(24, 12, 16, 12),
            BackColor = Theme.Page,
        };
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footer.Controls.Add(_saveResult, 0, 0);
        _footer.Controls.Add(_save, 1, 0);
        _footer.Controls.Add(_discard, 2, 0);
        _save.Anchor = _discard.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _footer.Paint += (_, e) =>
        {
            using var line = new Pen(Theme.CardBorder);
            e.Graphics.DrawLine(line, 0, 0, _footer.Width, 0);
        };

        var wrapper = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Page, Visible = false };
        page.Visible = true;
        wrapper.Controls.Add(page);
        wrapper.Controls.Add(_footer);
        WatchFields();
        ShowRcloneMode();
        _remoteDetails.Visible = false;
        _remoteDetails.TextChanged += (_, _) => _remoteDetails.Visible = _remoteDetails.Text.Length > 0;
        _settingsPage = wrapper;
        return wrapper;
    }

    // A null last cell keeps an input to its column, so it ends where the
    // inputs with a button beside them end.
    private void Managed(Card card, Control? caption, params Control?[] cells) => Rows(_managedRows, card, caption, cells);

    private void Own(Card card, Control? caption, params Control?[] cells) => Rows(_ownRows, card, caption, cells);

    private static void Rows(List<Control> rows, Card card, Control? caption, Control?[] cells)
    {
        card.AddRow([caption, .. cells]);
        rows.AddRange(new[] { caption }.Concat(cells).OfType<Control>());
    }

    /// <summary>How tall the Settings page is at <paramref name="width"/>, with its Save row.</summary>
    private int SettingsHeight(int width) =>
        _settingsColumn.GetPreferredSize(new Size(width, 0)).Height + _footer.GetPreferredSize(new Size(width, 0)).Height;

    private void WatchFields()
    {
        foreach (var input in new Control[] { _folderBox, _remoteBox, _rcUrlBox, _rcUserBox, _rcPasswordBox, _programBox, _configBox })
        {
            input.TextChanged += (_, _) => FieldsChanged();
        }

        // The fields describe the drive picked in the box above them.
        foreach (var input in new Control[] { _folderBox, _remoteBox })
        {
            input.TextChanged += (_, _) => KeepRootDraft();
        }

        _rcUrlBox.TextChanged += (_, _) => ShowRcdCommand();
        _rcUserBox.TextChanged += (_, _) => ShowRcdCommand();
        _signIn.CheckedChanged += (_, _) => FieldsChanged();
        _autoUpdate.CheckedChanged += (_, _) => FieldsChanged();
        _manageRclone.CheckedChanged += (_, _) =>
        {
            FieldsChanged();
            ShowRcloneMode();
        };

        // The chosen remote's details, and the chosen rclone, once typing pauses.
        foreach (var input in new Control[] { _remoteBox, _rcUrlBox, _rcUserBox, _rcPasswordBox, _programBox, _configBox })
        {
            input.TextChanged += (_, _) => DescribeSoon();
        }

        foreach (var input in new Control[] { _rcUrlBox, _rcUserBox, _rcPasswordBox, _programBox, _configBox })
        {
            input.TextChanged += (_, _) => CheckSoon();
        }

        _manageRclone.CheckedChanged += (_, _) => CheckSoon();
        _remoteBox.SelectedIndexChanged += (_, _) => DescribeSoon();
        _describeSoon.Tick += async (_, _) =>
        {
            _describeSoon.Stop();
            await DescribeTypedRemoteAsync();
        };
        _checkSoon.Tick += async (_, _) =>
        {
            _checkSoon.Stop();
            await TestRcloneAsync(fillRemotes: true);
        };
    }

    /// <summary>Only the fields for the chosen way of running rclone.</summary>
    private void ShowRcloneMode()
    {
        var managed = _manageRclone.Checked;
        _settingsColumn.SuspendLayout();
        foreach (var control in _managedRows)
        {
            control.Visible = managed && Says(control);
        }

        foreach (var control in _ownRows)
        {
            control.Visible = !managed && Says(control);
        }

        _settingsColumn.ResumeLayout(performLayout: true);
        _download.Enabled = managed && _busy is null && !_app.Busy;
    }

    /// <summary>A note with nothing to say takes no room.</summary>
    private static bool Says(Control control) => control is not Label label || label.Text.Length > 0;

    /// <summary>The page's part of a new snapshot: the fields, unless the user is editing them.</summary>
    private void ShowSettingsState(DriveSnapshot snapshot)
    {
        var idle = _busy is null && !_app.Busy;
        _download.Enabled = idle && _manageRclone.Checked;
        if (!Edited)
        {
            LoadSettings(snapshot);
        }

        ShowOverrides(snapshot.Settings);
        ShowSaveButtons();
    }

    private void ShowSaveButtons()
    {
        var idle = _busy is null && !_app.Busy;
        var neverSetUp = _snapshot?.Saved.Remote is null && _snapshot?.Saved.Roots is not { Count: > 0 };
        _save.Enabled = idle && (Edited || neverSetUp);
        _discard.Enabled = idle && Edited;
    }

    private void FieldsChanged()
    {
        if (_loading)
        {
            return;
        }

        _saveResult.Text = "";
        ShowSaveButtons();
    }

    private void DescribeSoon()
    {
        if (!_loading)
        {
            _describeSoon.Stop();
            _describeSoon.Start();
        }
    }

    private void CheckSoon()
    {
        if (!_loading)
        {
            _checkSoon.Stop();
            _checkSoon.Start();
        }
    }

    /// <summary>The rclone the Settings fields describe, before they are saved.</summary>
    private RcloneAccess FormAccess() => new(
        _manageRclone.Checked,
        Blank(_programBox.Text),
        Blank(_configBox.Text),
        _rcUrlBox.Text.Trim(),
        Blank(_rcUserBox.Text),
        Blank(_rcUserBox.Text) is null ? null : _rcPasswordBox.Text);

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Describes the remote typed in Settings, so it can be checked before saving.</summary>
    private async Task DescribeTypedRemoteAsync()
    {
        var remote = _remoteBox.Text.Trim();
        var access = FormAccess();
        if (remote.Length == 0)
        {
            _remoteDetails.Text = "";
            return;
        }

        var normalized = SyncStatePaths.NormalizeRemote(remote);
        if (_snapshot?.RemoteInfo is { } known && known.Remote == normalized &&
            access == RcloneAccess.From(_snapshot.Settings))
        {
            ShowRemoteDetails(known);
            return;
        }

        _remoteDetails.Text = "Asking rclone about it...";
        _remoteDetails.ForeColor = Theme.Subtle;
        var info = await RcloneProbe.DescribeAsync(access, normalized);
        if (_remoteBox.Text.Trim() == remote && FormAccess() == access)
        {
            ShowRemoteDetails(info);
        }
    }

    private void ShowRemoteDetails(RemoteInfo info)
    {
        _remoteDetails.Text = info.Problem ?? $"{info.Kind ?? info.Remote}. Space: {SpaceText(info)}";
        _remoteDetails.ForeColor = info.Problem is null ? SystemColors.ControlText : Theme.Problem;
    }

    private void ShowOverrides(EffectiveSettings settings)
    {
        var overridden = new[] { settings.SyncRoot, settings.Remote, settings.RcUrl }
            .Where(s => s.Source == SettingSource.Environment)
            .Select(s => s.EnvironmentVariable)
            .ToList();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DriveSettingsFile.RcUserVariable)))
        {
            overridden.Add($"{DriveSettingsFile.RcUserVariable} and {DriveSettingsFile.RcPasswordVariable}");
        }

        _overrides.Visible = overridden.Count > 0;
        _overrides.Text = overridden.Count == 0
            ? ""
            : $"Set in your environment, so the service uses those values instead of these: {string.Join(", ", overridden)}.";
    }

    // --- the drive list --------------------------------------------------------

    /// <summary>The picker's text for a drive: its folder's name and its remote.</summary>
    private static string RootItemText((string Folder, string Remote) root)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(
            string.IsNullOrWhiteSpace(root.Folder) ? "?" : root.Folder.Trim()));
        return string.IsNullOrWhiteSpace(root.Remote) ? $"{name} (no remote)" : $"{name} — {root.Remote}";
    }

    /// <summary>Fills the picker from <see cref="_draftRoots"/> and selects the edited drive.</summary>
    private void FillRootBox()
    {
        _fillingRootBox = true;
        try
        {
            _rootBox.Items.Clear();
            foreach (var root in _draftRoots)
            {
                _rootBox.Items.Add(RootItemText(root));
            }

            _rootBox.SelectedIndex = Math.Clamp(_rootIndex, 0, Math.Max(0, _draftRoots.Count - 1));
        }
        finally
        {
            _fillingRootBox = false;
        }
    }

    /// <summary>The fields now describe another drive: the old one keeps what they held.</summary>
    private void SwitchRoot()
    {
        if (_rootIndex < _draftRoots.Count)
        {
            _draftRoots[_rootIndex] = (_folderBox.Text, _remoteBox.Text);
        }

        _rootIndex = _rootBox.SelectedIndex;
        LoadRootFields();
    }

    /// <summary>Shows the edited drive in the fields, and whether it can be removed.</summary>
    private void LoadRootFields()
    {
        _loading = true;
        try
        {
            var (folder, remote) = _draftRoots[Math.Clamp(_rootIndex, 0, _draftRoots.Count - 1)];
            _folderBox.Text = folder;
            _remoteBox.Text = remote;
        }
        finally
        {
            _loading = false;
        }

        _removeRoot.Enabled = _draftRoots.Count > 1;
    }

    /// <summary>What the fields say goes back into the edited drive (and its line in the picker).</summary>
    private void KeepRootDraft()
    {
        if (_loading || _rootIndex >= _draftRoots.Count)
        {
            return;
        }

        _draftRoots[_rootIndex] = (_folderBox.Text, _remoteBox.Text);
        var text = RootItemText(_draftRoots[_rootIndex]);
        if (_rootBox.SelectedIndex == _rootIndex && _rootBox.Items[_rootIndex] is { } item && (string)item != text)
        {
            _fillingRootBox = true;
            try
            {
                _rootBox.Items[_rootIndex] = text;
            }
            finally
            {
                _fillingRootBox = false;
            }
        }
    }

    /// <summary>A folder no other drive has, under the user's profile.</summary>
    private string DefaultFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(profile, n == 1 ? "OpenCfDrive" : $"OpenCfDrive {n}");
            if (_draftRoots.All(root => !string.Equals(root.Folder.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private void AddRoot()
    {
        KeepRootDraft();
        _draftRoots.Add((DefaultFolder(), ""));
        _rootIndex = _draftRoots.Count - 1;
        FillRootBox();
        LoadRootFields();
        FieldsChanged();
    }

    private void RemoveRoot()
    {
        if (_draftRoots.Count <= 1)
        {
            return;
        }

        var folder = _draftRoots[_rootIndex].Folder;
        if (MessageBox.Show(
                this,
                $"'{folder}' is removed when you save: it is taken out of Explorer's navigation pane and stays as an " +
                "ordinary folder with the files already downloaded.",
                "Remove the drive",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information) != DialogResult.OK)
        {
            return;
        }

        _draftRoots.RemoveAt(_rootIndex);
        _rootIndex = Math.Min(_rootIndex, _draftRoots.Count - 1);
        FillRootBox();
        LoadRootFields();
        FieldsChanged();
    }

    // --- loading and saving ------------------------------------------------------

    private void LoadSettings(DriveSnapshot snapshot)
    {
        _loading = true;
        try
        {
            var saved = snapshot.Saved;
            if (saved.Roots is { Count: > 0 } list)
            {
                _draftRoots = list.Select(root => (root.SyncRoot, root.Remote ?? "")).ToList();
            }
            else
            {
                _draftRoots = [(saved.SyncRoot ?? snapshot.Settings.SyncRoot.Value, saved.Remote ?? "")];
            }

            _rootIndex = 0;
            FillRootBox();
            var first = _draftRoots[0];
            _folderBox.Text = first.Folder;
            _remoteBox.Text = first.Remote;
            _rcUrlBox.Text = saved.RcUrl ?? DriveSettingsFile.DefaultRcUrl;
            _rcUserBox.Text = saved.RcUser ?? "";
            _rcPasswordBox.Text = UserSecret.Unprotect(saved.RcPasswordProtected) ?? "";
            _manageRclone.Checked = saved.ManageRclone != false;
            _ownRclone.Checked = saved.ManageRclone == false;
            _programBox.Text = saved.RclonePath ?? "";
            _configBox.Text = saved.RcloneConfig ?? "";
            _signIn.Checked = SignInStartup.IsEnabled;
            _autoUpdate.Checked = saved.CheckForUpdates != false;
            if (snapshot.Rclone?.Remotes is { } remotes)
            {
                FillRemotes(remotes);
            }

            ShowRcdCommand();
            if (snapshot.Rclone is { } probe && probe.Key == FormAccess().Key)
            {
                ShowRcloneCheck(probe);
            }

            ShowRcloneMode();
            _removeRoot.Enabled = _draftRoots.Count > 1;
            _remoteBox.Select(_remoteBox.Text.Length, 0); // not all of it selected, as setting it leaves it
            _loaded = FieldValues;

            // What the monitor already knows about this remote, or ask once.
            var remote = _remoteBox.Text.Trim();
            if (remote.Length == 0)
            {
                _remoteDetails.Text = "";
            }
            else if (snapshot.RemoteInfo is { } info && info.Remote == SyncStatePaths.NormalizeRemote(remote))
            {
                ShowRemoteDetails(info);
            }
            else if (_remoteDetails.Text.Length == 0)
            {
                _describeSoon.Start();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>What the Settings fields hold, to tell whether the user changed any.</summary>
    private sealed record FieldSet(
        string Roots, string Folder, string Remote, string RcUrl, string RcUser, string RcPassword, bool SignIn, bool AutoUpdate,
        bool ManageRclone, string Program, string Config)
    {
        /// <summary>Never the password.</summary>
        public override string ToString() => $"{nameof(FieldSet)} {{ Roots = {Roots}, Folder = {Folder}, Remote = {Remote}, RcUrl = {RcUrl}, RcUser = {RcUser} }}";
    }

    /// <summary>Every drive in one string, so a change to any of them counts.</summary>
    private string RootsText => string.Join("\n", _draftRoots.Select(root => $"{root.Folder}|{root.Remote}"));

    private FieldSet FieldValues => new(
        RootsText, _folderBox.Text, _remoteBox.Text, _rcUrlBox.Text, _rcUserBox.Text, _rcPasswordBox.Text, _signIn.Checked,
        _autoUpdate.Checked, _manageRclone.Checked, _programBox.Text, _configBox.Text);

    /// <summary>The fields differ from what was loaded into them: the user is editing.</summary>
    private bool Edited => _loaded is { } loaded && loaded != FieldValues;

    private void FillRemotes(IReadOnlyList<string> remotes)
    {
        var text = _remoteBox.Text;
        var items = remotes.Select(r => r + ":").ToArray();
        if (!_remoteBox.Items.Cast<string>().SequenceEqual(items))
        {
            _remoteBox.Items.Clear();
            _remoteBox.Items.AddRange(items);
            _remoteBox.Text = text;
        }
    }

    private void ShowRcdCommand()
    {
        var address = Uri.TryCreate(_rcUrlBox.Text.Trim(), UriKind.Absolute, out var uri)
            ? $"{uri.Host}:{uri.Port}"
            : "127.0.0.1:5572";
        var user = Blank(_rcUserBox.Text) ?? "opencfdrive";
        _rcdCommand.Text = $"rclone rcd --rc-serve --rc-addr {address} --rc-user {user} --rc-pass <your password>";
    }

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the drive folder: a new or empty one, or the current drive",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(_folderBox.Text) ? _folderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folderBox.Text = dialog.SelectedPath;
        }
    }

    private void BrowseForFile(TextBox box, string title, string filter)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (File.Exists(box.Text.Trim()))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(box.Text.Trim());
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            box.Text = dialog.FileName;
        }
    }

    /// <summary>Checks the rclone the Settings fields describe, and lists its remotes.</summary>
    private async Task TestRcloneAsync(bool fillRemotes)
    {
        var access = FormAccess();
        var note = access.Managed ? _programNote : _rcTest;
        note.Text = access.Managed ? "Looking for rclone..." : "Asking rclone...";
        note.ForeColor = Theme.Subtle;
        ShowRcloneMode();
        var result = await RcloneProbe.CheckAsync(access);
        if (FormAccess() != access)
        {
            return; // the fields changed meanwhile; their own check follows
        }

        ShowRcloneCheck(result);
        if (fillRemotes && result.Remotes is { } remotes)
        {
            FillRemotes(remotes);
        }
    }

    private void ShowRcloneCheck(RcloneProbeResult result)
    {
        if (_manageRclone.Checked)
        {
            _programNote.Text = result.Program is { } program
                ? result.Usable
                    ? $"Runs {program.Path} ({SourceText(program.Source.ToString())}, {result.Version}), with " +
                      $"{Remotes(result.Remotes!.Count)} in its config."
                    : result.Problem
                : $"{result.Problem} Install rclone, choose rclone.exe, or download it here.";
            _programNote.ForeColor = result.Usable ? Theme.Good : Theme.Problem;
            _configNote.Text = result.ConfigFile is { } file ? $"rclone reads {file}" : "";
        }
        else
        {
            _rcTest.Text = result.Usable
                ? $"rclone {result.Version} answers, with {Remotes(result.Remotes!.Count)} in its config."
                : result.Problem;
            _rcTest.ForeColor = result.Usable ? Theme.Good : Theme.Problem;
        }

        ShowRcloneMode();
    }

    private static string Remotes(int count) => count == 1 ? "1 remote" : $"{count} remotes";

    private async Task DownloadRcloneAsync()
    {
        _download.Enabled = false;
        _downloadProgress.ForeColor = Theme.Subtle;
        _downloadProgress.Text = "Downloading...";
        ShowRcloneMode();
        var progress = new Progress<string>(line => _downloadProgress.Text = line);
        var problem = await _app.DownloadRcloneAsync(progress);
        _downloadProgress.Text = problem ?? $"Downloaded into {RcloneLocator.DownloadFolder}.";
        _downloadProgress.ForeColor = problem is null ? Theme.Good : Theme.Problem;
        _download.Enabled = _manageRclone.Checked;
        await TestRcloneAsync(fillRemotes: true);
    }

    private async Task SaveAsync()
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        KeepRootDraft();
        var roots = _draftRoots.Select(root => (Folder: root.Folder.Trim(), Remote: root.Remote.Trim())).ToList();
        var rcUrl = _rcUrlBox.Text.Trim();
        var rcUser = Blank(_rcUserBox.Text);
        var rcPassword = _rcPasswordBox.Text;
        var managed = _manageRclone.Checked;
        var program = Blank(_programBox.Text);
        var config = Blank(_configBox.Text);

        string? invalid = null;
        foreach (var (folder, remote) in roots)
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                invalid = $"The drive folder must be a full path, like C:\\Users\\you\\OpenCfDrive: '{folder}'.";
            }
            else if (remote.Length == 0 || remote.StartsWith(':'))
            {
                invalid = $"Choose an rclone remote for '{folder}', like myremote:.";
            }

            if (invalid is not null)
            {
                break;
            }
        }

        var twice = roots
            .GroupBy(root => root.Folder, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (invalid is null && twice is not null)
        {
            invalid = $"Each drive needs its own folder; '{twice}' is chosen twice.";
        }
        else if (invalid is null && managed && program is not null && !File.Exists(program))
        {
            invalid = $"There is no rclone program at {program}.";
        }
        else if (invalid is null && managed && config is not null && !File.Exists(config))
        {
            invalid = $"There is no rclone config file at {config}.";
        }
        else if (invalid is null && !managed && (!Uri.TryCreate(rcUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            invalid = "The remote control address must be an http:// address, like http://127.0.0.1:5572.";
        }
        else if (invalid is null && !managed && (rcUser is null || rcPassword.Length == 0))
        {
            invalid = "Enter the user and password your rclone rcd was started with (--rc-user and --rc-pass).";
        }

        if (invalid is not null)
        {
            ShowSaveResult(invalid, Theme.Problem);
            return;
        }

        var settings = snapshot.Saved with
        {
            Roots = roots.Select(root => new DriveRoot(root.Folder, root.Remote)).ToList(),
            // The first drive also in the old single-drive places, so a copy
            // from before several drives were a thing still syncs it.
            SyncRoot = roots[0].Folder,
            Remote = roots[0].Remote,
            RcUrl = rcUrl,
            RcUser = rcUser,
            RcPasswordProtected = rcUser is null || rcPassword.Length == 0 ? null : UserSecret.Protect(rcPassword),
            ManageRclone = managed,
            RclonePath = program,
            RcloneConfig = config,
            StartAtSignIn = _signIn.Checked,
            CheckForUpdates = _autoUpdate.Checked,
        };
        var before = snapshot.Settings;
        var after = DriveSettingsFile.Resolve(settings);

        // One check per drive: a folder that was another remote's drive
        // before, or a new folder that is not empty, is refused. A drive the
        // list no longer has only needs unregistering.
        var changes = new List<DriveChange>();
        foreach (var (folder, remote) in roots)
        {
            var current = before.Roots.FirstOrDefault(root =>
                string.Equals(root.SyncRoot, folder, StringComparison.OrdinalIgnoreCase));
            changes.Add(DriveChange.Check(folder, current?.Remote, folder, remote));
        }

        foreach (var gone in before.Roots.Where(root =>
                     roots.All(chosen => !string.Equals(chosen.Folder, root.SyncRoot, StringComparison.OrdinalIgnoreCase))))
        {
            changes.Add(new DriveChange(true, null, gone.SyncRoot, gone.Remote, gone.SyncRoot, DiscardOldState: false));
        }

        var refused = changes.FirstOrDefault(change => !change.Allowed);
        if (refused is not null)
        {
            ShowSaveResult(refused.Problem!, Theme.Problem);
            return;
        }

        var moved = changes.Select(change => change.OldSyncRootToUnregister).OfType<string>().Distinct().ToList();
        if (moved.Count > 0)
        {
            var list = string.Join("\n", moved);
            var said = MessageBox.Show(
                this,
                (moved.Count == 1 ? $"{moved[0]} stops being a drive" : "These folders stop being drives") +
                $":\n\n{list}\n\n" +
                "Each is removed from Explorer's navigation pane and stays as an ordinary folder with the files already downloaded.",
                "Move the drive",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);
            if (said != DialogResult.OK)
            {
                return;
            }
        }

        var problem = await _app.ApplySettingsAsync(settings, changes);
        if (problem is null)
        {
            if (_app.Latest is { } latest)
            {
                LoadSettings(latest);
            }

            ShowSaveResult(after.HasRemote && _app.Latest?.Running == true ? "Saved; the drive is syncing." : "Saved.", Theme.Good);
        }
        else
        {
            ShowSaveResult(problem, Theme.Problem);
        }

        ShowSaveButtons();
    }

    private void ShowSaveResult(string text, Color color)
    {
        _saveResult.Text = text;
        _saveResult.ForeColor = color;
    }
}
