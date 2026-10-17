// Asked on a profile switch when the target profile pins a mod to a
// version older than the newest one the Library holds. Lists each
// such mod with the profile's version and the Library's newest, and
// lets the user tick which ones the profile should move to before
// the switch goes ahead. Unticked mods keep the profile's own
// version, so two profiles can still run a mod at different
// versions on purpose; ticked ones stop drifting behind the other
// profiles after an update.
//
// Purely a picker: MainForm applies the chosen versions to the
// profile and saves it. DEV builds (display name carrying "[DEV]")
// are listed but left unticked by default — a DEV build is usually
// newest in the Library, and moving a play profile onto it is
// rarely what the user wants.

using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ProfileVersionPromptDialog : Form
{
    /// <summary>One mod the dialog offers to update.</summary>
    public record Candidate(ProfileMod Mod, string NewestVersion, string NewestDisplayName, bool IsDevBuild);

    /// <summary>The candidates the user ticked. Empty when they chose
    /// to keep every profile version. Only meaningful when the dialog
    /// result is OK; Cancel means "don't switch at all".</summary>
    public List<Candidate> Accepted { get; } = new();

    private readonly string _profileName;
    private readonly List<Candidate> _candidates;
    private DataGridView _grid = null!;
    private Button _useBtn = null!;

    public ProfileVersionPromptDialog(string profileName, List<Candidate> candidates)
    {
        _profileName = profileName;
        _candidates  = candidates;
        InitUi();
        PopulateGrid();
    }

    private void InitUi()
    {
        Text            = $"Newer versions in the Library — {_profileName}";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize     = new Size(760, 380);
        Width           = 1040;
        Height          = 520;
        ShowInTaskbar   = false;

        var title = new Label
        {
            Text      = $"⌘  SWITCHING TO  {_profileName.ToUpperInvariant()}  ⌘",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Consolas", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 50, 60),
            Margin    = new Padding(0, 0, 0, 6),
        };
        var sub = new Label
        {
            Text =
                $"{_candidates.Count} mod(s) in this profile are pinned to a version older than "
                + "the newest one in the Library (usually because another profile updated them).\n"
                + "Tick the ones this profile should move to the newest version. Unticked mods keep "
                + "the profile's own version, so a profile can still run a mod at an older version on purpose.",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            MaximumSize = new Size(1000, 0),
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
        _useBtn = MainForm.ThemedButton("▶ Use newest for ticked");
        _useBtn.Width    = 220;
        _useBtn.Height   = 40;
        _useBtn.AutoSize = false;
        _useBtn.BackColor = Color.FromArgb(35, 75, 50);
        _useBtn.ForeColor = Color.FromArgb(210, 240, 220);
        _useBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 150, 100);
        _useBtn.Click += (_, _) => Commit(useTicked: true);

        var keepBtn = MainForm.ThemedButton("Keep profile versions");
        keepBtn.Width    = 200;
        keepBtn.Height   = 40;
        keepBtn.AutoSize = false;
        keepBtn.Click   += (_, _) => Commit(useTicked: false);

        var allBtn = MainForm.ThemedButton("Tick all");
        allBtn.Width = 90; allBtn.Height = 40; allBtn.AutoSize = false;
        allBtn.Click += (_, _) => SetAll(true);
        var noneBtn = MainForm.ThemedButton("Tick none");
        noneBtn.Width = 100; noneBtn.Height = 40; noneBtn.AutoSize = false;
        noneBtn.Click += (_, _) => SetAll(false);

        var cancelBtn = MainForm.ThemedButton("Cancel switch");
        cancelBtn.Width    = 140;
        cancelBtn.Height   = 40;
        cancelBtn.AutoSize = false;
        cancelBtn.Click   += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        CancelButton = cancelBtn;

        btnRow.Resize += (_, _) =>
        {
            _useBtn.Top   = 6; _useBtn.Left   = 0;
            keepBtn.Top   = 6; keepBtn.Left   = _useBtn.Right + 8;
            allBtn.Top    = 6; allBtn.Left    = keepBtn.Right + 24;
            noneBtn.Top   = 6; noneBtn.Left   = allBtn.Right + 8;
            cancelBtn.Top = 6; cancelBtn.Left = btnRow.Width - cancelBtn.Width - 6;
        };
        btnRow.Controls.Add(_useBtn);
        btnRow.Controls.Add(keepBtn);
        btnRow.Controls.Add(allBtn);
        btnRow.Controls.Add(noneBtn);
        btnRow.Controls.Add(cancelBtn);

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
        g.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Use", HeaderText = "Use", Width = 60, ReadOnly = false,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Mod", HeaderText = "Mod", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Profile", HeaderText = "Profile has", Width = 150, ReadOnly = true,
            DefaultCellStyle = { Font = new Font("Consolas", 12f, FontStyle.Bold) },
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Newest", HeaderText = "Library newest", Width = 170, ReadOnly = true,
            DefaultCellStyle =
            {
                Font = new Font("Consolas", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(120, 220, 140),
            },
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Note", HeaderText = "Note", Width = 160, ReadOnly = true,
            DefaultCellStyle = { ForeColor = Color.FromArgb(255, 200, 80) },
        });
        foreach (DataGridViewColumn col in g.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;
        // A click anywhere on the row flips the tick, so the small
        // checkbox isn't the only target.
        g.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex == 0) return;
            var cell = g.Rows[e.RowIndex].Cells["Use"];
            cell.Value = !(cell.Value is bool b && b);
            g.EndEdit();
        };
        return g;
    }

    private void PopulateGrid()
    {
        _grid.Rows.Clear();
        foreach (var c in _candidates)
        {
            var i = _grid.Rows.Add();
            var r = _grid.Rows[i];
            r.Tag = c;
            r.Cells["Use"].Value     = !c.IsDevBuild;
            r.Cells["Mod"].Value     = string.IsNullOrEmpty(c.Mod.DisplayName) ? c.Mod.ModId : c.Mod.DisplayName;
            r.Cells["Profile"].Value = $"v{c.Mod.Version}";
            r.Cells["Newest"].Value  = $"v{c.NewestVersion}";
            r.Cells["Note"].Value    = c.IsDevBuild ? "DEV build" : "";
            r.Cells["Mod"].ToolTipText = c.NewestDisplayName;
        }
    }

    private void SetAll(bool on)
    {
        foreach (DataGridViewRow r in _grid.Rows) r.Cells["Use"].Value = on;
        _grid.EndEdit();
    }

    private void Commit(bool useTicked)
    {
        _grid.EndEdit();
        Accepted.Clear();
        if (useTicked)
        {
            foreach (DataGridViewRow r in _grid.Rows)
                if (r.Cells["Use"].Value is bool b && b && r.Tag is Candidate c)
                    Accepted.Add(c);
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
