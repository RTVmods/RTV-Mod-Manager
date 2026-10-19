// Saves view: the Save Slots mod's slots, each tied to a manager profile.
//
// Lists every slot with its name, last map, last played time and state, and
// a profile picker per slot. Picking a profile writes the link into the
// slot's _meta.cfg, the same key the mod's in-game panel edits. "Launch this
// slot" hands the slot to MainForm, which switches to the slot's profile and
// starts the game straight into the slot. "Back up slots" zips the folder.
//
// The dialog never touches save files; the mod does all copying in-game.

using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class SavesDialog : Form
{
    private const string AnyProfile = "(any profile)";

    private readonly string _root;
    private readonly IReadOnlyList<string> _profiles;
    private readonly Func<string> _activeProfile;
    private readonly Func<int, bool> _launchCallback;
    private readonly string? _warning;

    private DataGridView _grid = null!;
    private Label _headerLabel = null!;
    private Button _launchBtn = null!;
    private Button _renameBtn = null!;
    private Button _backupBtn = null!;
    private Button _closeBtn = null!;
    private List<SaveSlots.Slot> _slots = new();
    private bool _loading;

    /// <param name="root">The MultiSaveSlots folder.</param>
    /// <param name="profiles">Names of the manager's profiles.</param>
    /// <param name="activeProfile">The live profile, for the status column.</param>
    /// <param name="launchCallback">Starts the game into slot n; returns
    /// true when the launch went ahead, and the dialog then closes.</param>
    /// <param name="warning">Shown above the list when set, such as the
    /// companion mod not being live.</param>
    public SavesDialog(string root, IReadOnlyList<string> profiles, Func<string> activeProfile,
        Func<int, bool> launchCallback, string? warning)
    {
        _root = root;
        _profiles = profiles;
        _activeProfile = activeProfile;
        _launchCallback = launchCallback;
        _warning = warning;
        InitUi();
        Reload();
    }

    private void InitUi()
    {
        Text            = "Saves";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize     = new Size(900, 460);
        Width           = 1120;
        Height          = 560;
        ShowInTaskbar   = false;
        DialogSizing.ClampToWorkingArea(this);

        var title = new Label
        {
            Text      = "💾 Saves",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 4),
        };

        _headerLabel = new Label
        {
            Dock        = DockStyle.Top,
            AutoSize    = true,
            MaximumSize = new Size(1060, 0),
            ForeColor   = Color.FromArgb(170, 185, 210),
            Margin      = new Padding(0, 0, 0, 10),
        };

        var warn = new Label
        {
            Dock        = DockStyle.Top,
            AutoSize    = true,
            MaximumSize = new Size(1060, 0),
            ForeColor   = Color.FromArgb(255, 200, 80),
            Margin      = new Padding(0, 0, 0, 10),
            Text        = _warning ?? "",
            Visible     = !string.IsNullOrEmpty(_warning),
        };

        _grid = BuildGrid();

        var content = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 4,
            Padding     = new Padding(16, 12, 16, 12),
            BackColor   = Color.Transparent,
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        content.Controls.Add(title,        0, 0);
        content.Controls.Add(_headerLabel, 0, 1);
        content.Controls.Add(warn,         0, 2);
        content.Controls.Add(_grid,        0, 3);

        Controls.Add(content);
        Controls.Add(BuildButtonRow());
    }

    private DataGridView BuildGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(18, 22, 30),
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 34,
            RowTemplate = { Height = 32 },
            GridColor = Color.FromArgb(40, 46, 58),
            EditMode = DataGridViewEditMode.EditOnEnter,
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(36, 42, 54);
        g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(210, 220, 235);
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        g.DefaultCellStyle.BackColor = Color.FromArgb(18, 22, 30);
        g.DefaultCellStyle.ForeColor = Color.FromArgb(212, 220, 232);
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(40, 60, 90);
        g.DefaultCellStyle.SelectionForeColor = Color.FromArgb(240, 245, 250);

        DataGridViewTextBoxColumn Col(string name, string header, int width, bool fill = false) => new()
        {
            Name = name, HeaderText = header, Width = width, ReadOnly = true,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            MinimumWidth = fill ? 180 : 40,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        g.Columns.Add(Col("Slot", "#", 44));
        g.Columns.Add(Col("Name", "Save", 0, fill: true));
        g.Columns.Add(Col("Map", "Last map", 130));
        g.Columns.Add(Col("Played", "Last played", 170));
        g.Columns.Add(Col("State", "State", 150));

        var profile = new DataGridViewComboBoxColumn
        {
            Name = "Profile", HeaderText = "Profile", Width = 230,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        profile.DefaultCellStyle.BackColor = Color.FromArgb(30, 36, 48);
        g.Columns.Add(profile);

        g.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // Commit a combo pick at once instead of on leaving the cell.
            if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewComboBoxCell)
                g.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        g.CellValueChanged += (_, e) =>
        {
            if (_loading || e.RowIndex < 0 || g.Columns[e.ColumnIndex].Name != "Profile") return;
            var n = (int)g.Rows[e.RowIndex].Tag!;
            var picked = g.Rows[e.RowIndex].Cells["Profile"].Value as string ?? AnyProfile;
            var name = picked == AnyProfile ? "" : picked.EndsWith(" (missing)") ? picked[..^10] : picked;
            try { SaveSlots.SetProfile(_root, n, name); }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(this, ex.Message, "Couldn't save the link",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            BeginInvoke(Reload);
        };
        g.DataError += (_, e) => e.ThrowException = false;
        g.SelectionChanged += (_, _) => UpdateButtons();
        g.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && g.Columns[e.ColumnIndex].Name != "Profile") Launch();
        };
        return g;
    }

    private Panel BuildButtonRow()
    {
        var p = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Color.FromArgb(30, 34, 44),
        };

        _launchBtn = MainForm.ThemedButton("▶ Launch this slot");
        _launchBtn.Width = 190; _launchBtn.Height = 40; _launchBtn.AutoSize = false;
        _launchBtn.BackColor = Color.FromArgb(45, 90, 55);
        _launchBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _launchBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _launchBtn.Click += (_, _) => Launch();

        _renameBtn = MainForm.ThemedButton("✎ Rename…");
        _renameBtn.Width = 130; _renameBtn.Height = 40; _renameBtn.AutoSize = false;
        _renameBtn.Click += (_, _) => Rename();

        _backupBtn = MainForm.ThemedButton("🗄 Back up slots");
        _backupBtn.Width = 170; _backupBtn.Height = 40; _backupBtn.AutoSize = false;
        _backupBtn.Click += (_, _) => Backup();

        _closeBtn = MainForm.ThemedButton("Close");
        _closeBtn.Width = 100; _closeBtn.Height = 40; _closeBtn.AutoSize = false;
        _closeBtn.Click += (_, _) => Close();

        p.Controls.AddRange(new Control[] { _launchBtn, _renameBtn, _backupBtn, _closeBtn });
        p.Resize += (_, _) =>
        {
            _launchBtn.Top = _renameBtn.Top = _backupBtn.Top = _closeBtn.Top = 8;
            _launchBtn.Left = 16;
            _renameBtn.Left = _launchBtn.Right + 8;
            _backupBtn.Left = _renameBtn.Right + 8;
            _closeBtn.Left = p.Width - _closeBtn.Width - 16;
        };
        return p;
    }

    private void Reload()
    {
        var live = _activeProfile();
        _headerLabel.Text = "The Save Slots mod's slots. Tie a slot to a profile and it always runs "
            + "with that profile's mods: launching it from here switches profile first, "
            + "and loading it in-game restarts into the profile when another one is live. "
            + $"Live profile: {(live.Length > 0 ? live : "(none)")}.";
        var keep = SelectedSlot();
        _loading = true;
        try
        {
            _slots = SaveSlots.ReadAll(_root);
            var active = SaveSlots.ActiveSlot(_root);
            _grid.Rows.Clear();
            foreach (var s in _slots)
            {
                var i = _grid.Rows.Add();
                var row = _grid.Rows[i];
                row.Tag = s.Number;
                row.Cells["Slot"].Value = s.Number.ToString();
                row.Cells["Name"].Value = s.Occupied ? s.Title : "(empty)";
                row.Cells["Map"].Value = s.Occupied ? (s.LastMap.Length > 0 ? s.LastMap : "?") : "";
                row.Cells["Played"].Value = s.LastPlayed is DateTime t
                    ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
                row.Cells["State"].Value = !s.Occupied ? "" : s.Dead ? "dead" : s.Number == active ? "last played" : "";
                if (s.Dead) row.Cells["State"].Style.ForeColor = Color.FromArgb(245, 120, 110);
                else if (s.Number == active) row.Cells["State"].Style.ForeColor = Color.FromArgb(140, 210, 150);
                if (!s.Occupied) row.DefaultCellStyle.ForeColor = Color.FromArgb(120, 132, 156);

                var combo = (DataGridViewComboBoxCell)row.Cells["Profile"];
                combo.Items.Add(AnyProfile);
                foreach (var p in _profiles) combo.Items.Add(p);
                var value = AnyProfile;
                if (s.Profile.Length > 0)
                {
                    value = _profiles.Contains(s.Profile) ? s.Profile : s.Profile + " (missing)";
                    if (!combo.Items.Contains(value)) combo.Items.Add(value);
                }
                combo.Value = value;
                if (s.Profile.Length > 0 && !_profiles.Contains(s.Profile))
                    combo.Style.ForeColor = Color.FromArgb(255, 200, 80);
            }
            if (keep > 0)
            {
                foreach (DataGridViewRow r in _grid.Rows)
                    if ((int)r.Tag! == keep) { r.Selected = true; _grid.CurrentCell = r.Cells["Name"]; }
            }
        }
        finally { _loading = false; }
        UpdateButtons();
    }

    private int SelectedSlot()
        => _grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Tag is int n ? n : 0;

    private SaveSlots.Slot? Selected()
    {
        var n = SelectedSlot();
        return _slots.FirstOrDefault(s => s.Number == n);
    }

    private void UpdateButtons()
    {
        var s = Selected();
        _launchBtn.Enabled = s is { Occupied: true, Dead: false };
        _renameBtn.Enabled = s is { Occupied: true };
    }

    private void Launch()
    {
        var s = Selected();
        if (s is not { Occupied: true }) return;
        if (s.Dead)
        {
            ThemedMessageBox.Show(this, $"{s.Title} died. Start a new game in that slot from the game's menu.",
                "Slot is dead", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_launchCallback(s.Number)) Close();
        else Reload();
    }

    private void Rename()
    {
        var s = Selected();
        if (s is not { Occupied: true }) return;
        using var dlg = new Form
        {
            Text = $"Rename slot {s.Number}",
            StartPosition = FormStartPosition.CenterParent,
            BackColor = BackColor, ForeColor = ForeColor, Font = Font,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            ClientSize = new Size(440, 120),
        };
        var box = new TextBox
        {
            Text = s.DisplayName, MaxLength = 40,
            Location = new Point(16, 16), Width = 408,
            BackColor = Color.FromArgb(30, 36, 48), ForeColor = ForeColor,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var ok = MainForm.ThemedButton("Save");
        ok.AutoSize = false; ok.Size = new Size(100, 36); ok.Location = new Point(216, 68);
        ok.DialogResult = DialogResult.OK;
        var cancel = MainForm.ThemedButton("Cancel");
        cancel.AutoSize = false; cancel.Size = new Size(100, 36); cancel.Location = new Point(324, 68);
        cancel.DialogResult = DialogResult.Cancel;
        dlg.Controls.AddRange(new Control[] { box, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { SaveSlots.SetDisplayName(_root, s.Number, box.Text); }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this, ex.Message, "Couldn't rename", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Reload();
    }

    private void Backup()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Road to Vostok Mod Manager Backups");
        var zip = Path.Combine(dir, $"saves-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            var n = SaveSlots.Backup(_root, zip);
            ThemedMessageBox.Show(this, $"{n} file(s) written to:\n\n{zip}", "Slots backed up",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this, ex.Message, "Backup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
