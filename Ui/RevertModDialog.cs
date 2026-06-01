// Per-mod version rollback dialog. Lists every snapshot ModBackup
// has on disk for the right-clicked mod (newest first) and lets the
// user either Revert to one or Delete an unwanted entry to free disk
// space. The actual file swap is performed by MainForm — this dialog
// is purely a picker, returning the chosen backup's path via
// SelectedBackupPath.
//
// Layout: header + DataGridView (Version / When / Size) + button row.
// Mirrors the styling of the main mods grid so the dialog feels like
// it's part of the same UI surface.

using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class RevertModDialog : Form
{
    /// <summary>Path of the backup the user picked, or null if they
    /// cancelled / closed the dialog without choosing.</summary>
    public string? SelectedBackupPath { get; private set; }

    private readonly ModEntry _entry;
    private readonly string _modsDir;
    private List<ModBackup.BackupEntry> _backups;

    private DataGridView _grid = null!;
    private Button       _revertBtn = null!;
    private Button       _deleteBtn = null!;

    public RevertModDialog(
        ModEntry entry,
        List<ModBackup.BackupEntry> backups,
        string modsDir)
    {
        _entry   = entry;
        _backups = backups;
        _modsDir = modsDir;
        InitUi();
        PopulateGrid();
    }

    private void InitUi()
    {
        Text            = $"Revert — {_entry.DisplayName}";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        // Width chosen so the bottom button row (Revert to
        // selected · Delete backup · Close) sits with comfortable
        // breathing room rather than crammed against the edges.
        MinimumSize     = new Size(720, 360);
        Width           = 1000;
        Height          = 460;
        ShowInTaskbar   = false;

        var title = new Label
        {
            Text      = $"★  REVERT  {_entry.DisplayName.ToUpperInvariant()}  ★",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Consolas", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 50, 60),
            Margin    = new Padding(0, 0, 0, 6),
        };
        var sub = new Label
        {
            Text =
                $"Current: v{_entry.Version}.   "
                + $"{_backups.Count} backup(s) on disk.\n"
                + "Reverting copies the chosen snapshot over the live "
                + ".vmz; the current version is auto-backed-up first so "
                + "this is reversible.",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            ForeColor = Color.FromArgb(170, 185, 210),
            Margin    = new Padding(0, 0, 0, 10),
        };

        _grid = BuildGrid();

        var btnRow = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 52,
            BackColor = Color.Transparent,
        };
        _revertBtn = MainForm.ThemedButton("▶ Revert to selected");
        _revertBtn.Width    = 180;
        _revertBtn.Height   = 40;
        _revertBtn.AutoSize = false;
        _revertBtn.Enabled  = false;
        _revertBtn.BackColor = Color.FromArgb(85, 55, 35);
        _revertBtn.ForeColor = Color.FromArgb(240, 220, 200);
        _revertBtn.FlatAppearance.BorderColor = Color.FromArgb(160, 100, 70);
        _revertBtn.Click += (_, _) => OnRevert();

        _deleteBtn = MainForm.ThemedButton("🗑 Delete backup");
        _deleteBtn.Width    = 140;
        _deleteBtn.Height   = 40;
        _deleteBtn.AutoSize = false;
        _deleteBtn.Enabled  = false;
        _deleteBtn.ForeColor = Color.FromArgb(245, 130, 120);
        _deleteBtn.Click += (_, _) => OnDelete();

        var closeBtn = MainForm.ThemedButton("Close");
        closeBtn.Width    = 100;
        closeBtn.Height   = 40;
        closeBtn.AutoSize = false;
        closeBtn.Click   += (_, _) => Close();
        CancelButton = closeBtn;

        btnRow.Resize += (_, _) =>
        {
            _revertBtn.Top = 6; _revertBtn.Left = 0;
            _deleteBtn.Top = 6; _deleteBtn.Left = _revertBtn.Right + 8;
            closeBtn.Top   = 6; closeBtn.Left   = btnRow.Width - closeBtn.Width - 6;
        };
        btnRow.Controls.Add(_revertBtn);
        btnRow.Controls.Add(_deleteBtn);
        btnRow.Controls.Add(closeBtn);

        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 4,
            Padding     = new Padding(14, 10, 14, 10),
            BackColor   = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(title,  0, 0);
        root.Controls.Add(sub,    0, 1);
        root.Controls.Add(_grid,  0, 2);
        root.Controls.Add(btnRow, 0, 3);
        Controls.Add(root);
    }

    private DataGridView BuildGrid()
    {
        var g = new DataGridView
        {
            Dock                  = DockStyle.Fill,
            AutoGenerateColumns   = false,
            AllowUserToAddRows    = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly              = true,
            SelectionMode         = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect           = false,
            RowHeadersVisible     = false,
            BackgroundColor       = Color.FromArgb(18, 22, 30),
            BorderStyle           = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle =
            {
                BackColor = Color.FromArgb(36, 42, 54),
                ForeColor = Color.FromArgb(220, 225, 235),
                Font      = new Font("Segoe UI", 12f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
                SelectionForeColor = Color.FromArgb(220, 225, 235),
            },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font      = new Font("Consolas", 12f),
            },
            GridColor           = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 36,
            RowTemplate         = { Height = 32 },
        };
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Version", HeaderText = "Version", Width = 120,
            Resizable = DataGridViewTriState.True,
            DefaultCellStyle = { Font = new Font("Consolas", 12f, FontStyle.Bold) },
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "When", HeaderText = "Backed up",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 50,
        });
        // Size: bumped 80 → 120 so values up to "9999.9 MB" fit
        // without ellipsis. Right-aligned + monospace so the
        // numbers line up across rows; with the old 80px width
        // any value past "99.9 MB" clipped to "116...".
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Size", HeaderText = "Size", Width = 120,
            Resizable = DataGridViewTriState.True,
            DefaultCellStyle =
            {
                Alignment = DataGridViewContentAlignment.MiddleRight,
                Font      = new Font("Consolas", 12f),
            },
        });
        foreach (DataGridViewColumn col in g.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;

        g.SelectionChanged += (_, _) =>
        {
            var sel = g.SelectedRows.Count > 0;
            _revertBtn.Enabled = sel;
            _deleteBtn.Enabled = sel;
        };
        // Double-click commits the revert — same as the button.
        g.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            OnRevert();
        };
        return g;
    }

    private void PopulateGrid()
    {
        _grid.Rows.Clear();
        foreach (var b in _backups)
        {
            var i = _grid.Rows.Add();
            var r = _grid.Rows[i];
            r.Tag = b;
            r.Cells["Version"].Value = string.IsNullOrEmpty(b.Version)
                ? "?"
                : $"v{b.Version}";
            r.Cells["When"].Value = FormatWhen(b.CreatedAt);
            r.Cells["Size"].Value = FormatBytes(b.SizeBytes);
            r.Cells["When"].ToolTipText = b.Path;
        }
        if (_backups.Count == 0)
        {
            _revertBtn.Enabled = false;
            _deleteBtn.Enabled = false;
        }
    }

    private void OnRevert()
    {
        if (_grid.SelectedRows.Count == 0) return;
        var row = _grid.SelectedRows[0];
        if (row.Tag is not ModBackup.BackupEntry b) return;
        SelectedBackupPath = b.Path;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnDelete()
    {
        if (_grid.SelectedRows.Count == 0) return;
        var row = _grid.SelectedRows[0];
        if (row.Tag is not ModBackup.BackupEntry b) return;
        var dr = ThemedMessageBox.Show(this,
            $"Delete backup v{b.Version}  ({FormatBytes(b.SizeBytes)})?\n\n"
            + "This removes the snapshot file from disk. You will no "
            + "longer be able to revert to this version unless you "
            + "have another copy elsewhere.",
            "Delete backup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (dr != DialogResult.Yes) return;
        ModBackup.DeleteBackup(b.Path);
        _backups = ModBackup.ListBackups(_modsDir, _entry.ModId);
        PopulateGrid();
    }

    private static string FormatWhen(DateTime t)
    {
        if (t == DateTime.MinValue) return "unknown";
        var ago = DateTime.Now - t;
        string rel;
        if (ago.TotalMinutes < 1)   rel = "just now";
        else if (ago.TotalMinutes < 60)
            rel = $"{(int)ago.TotalMinutes} min ago";
        else if (ago.TotalHours < 24)
            rel = $"{(int)ago.TotalHours} h ago";
        else if (ago.TotalDays < 30)
            rel = $"{(int)ago.TotalDays} d ago";
        else
            rel = t.ToString("yyyy-MM-dd");
        return $"{t:yyyy-MM-dd HH:mm}  ·  {rel}";
    }

    private static string FormatBytes(long n)
    {
        if (n < 1024) return $"{n} B";
        if (n < 1024 * 1024) return $"{n / 1024.0:F1} KB";
        return $"{n / 1024.0 / 1024.0:F1} MB";
    }
}
