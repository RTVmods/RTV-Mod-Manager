// Library manager.
//
// The library (<mods>/Library/) is the canonical store of every .vmz the
// user has ever added — one file per (mod_id, version), so multiple
// versions of the same mod can coexist. The live mods folder is just the
// active profile's working copy; the library survives profile switches
// and deletions.
//
// This dialog lists every library entry with its live/enabled state and
// lets the user:
//   • Install a version into the active profile (reuses the standard
//     install pipeline via the callback MainForm supplies).
//   • Delete a version from the library (hard delete, confirmed).
//
// Mirrors ProfileManagerDialog's grid theming + delete-confirm pattern.

using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class LibraryDialog : Form
{
    private readonly ModRegistry _registry;
    private readonly string _modsDir;
    private readonly Func<string, Task> _installCallback;

    private DataGridView _grid = null!;
    private Label _headerLabel = null!;
    private Button _installBtn = null!;
    private Button _deleteBtn = null!;
    private Button _refreshBtn = null!;
    private Button _closeBtn = null!;
    private bool _busy;

    private List<ModLibrary.Entry> _entries = new();
    /// <summary>mod_id → names of saved profiles that include it. Built
    /// once per dialog open from ModProfile.LoadAll().</summary>
    private Dictionary<string, List<string>> _profilesByMod = new(StringComparer.OrdinalIgnoreCase);

    // Current sort state for the manually-populated grid.
    private string _sortCol = "Mod";
    private bool _sortAsc = true;

    /// <summary>True when an install or delete happened, so MainForm
    /// rescans on close.</summary>
    public bool Changed { get; private set; }

    public LibraryDialog(ModRegistry registry, string modsDir, Func<string, Task> installCallback)
    {
        _registry = registry;
        _modsDir = modsDir;
        _installCallback = installCallback;
        InitUi();
        ReloadList();
    }

    private void InitUi()
    {
        Text            = "Mod Library";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize     = new Size(880, 560);
        Width           = 1080;
        Height          = 680;
        ShowInTaskbar   = false;
        DialogSizing.ClampToWorkingArea(this);

        var title = new Label
        {
            Text      = "📚 Mod Library",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 4),
        };

        _headerLabel = new Label
        {
            Dock      = DockStyle.Top,
            AutoSize  = true,
            ForeColor = Color.FromArgb(170, 185, 210),
            Margin    = new Padding(0, 0, 0, 10),
            Text      = "Every .vmz version ever added is archived here. "
                      + "Install a version into your active profile, or delete "
                      + "ones you no longer need.",
        };

        _grid = BuildGrid();

        var content = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 3,
            Padding     = new Padding(16, 12, 16, 12),
            BackColor   = Color.Transparent,
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        content.Controls.Add(title,        0, 0);
        content.Controls.Add(_headerLabel, 0, 1);
        content.Controls.Add(_grid,        0, 2);

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
            ReadOnly = true,
            MultiSelect = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(18, 22, 30),
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 34,
            RowTemplate = { Height = 30 },
            GridColor = Color.FromArgb(40, 46, 58),
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(36, 42, 54);
        g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(210, 220, 235);
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        g.DefaultCellStyle.BackColor = Color.FromArgb(18, 22, 30);
        g.DefaultCellStyle.ForeColor = Color.FromArgb(212, 220, 232);
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(40, 60, 90);
        g.DefaultCellStyle.SelectionForeColor = Color.FromArgb(240, 245, 250);

        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Mod", HeaderText = "Mod",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 240,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Version", HeaderText = "Version", Width = 110,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "State", HeaderText = "State", Width = 150,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Profiles", HeaderText = "In profiles", Width = 200,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Size", HeaderText = "Size", Width = 90,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "MW", HeaderText = "MW id", Width = 90,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        g.SelectionChanged += (_, _) => UpdateButtons();
        // Manual sort: the grid is populated by hand (Tag = Entry), so
        // header clicks sort the backing list and repopulate, toggling
        // ascending/descending on repeat clicks of the same column.
        g.ColumnHeaderMouseClick += (_, e) =>
        {
            var name = g.Columns[e.ColumnIndex].Name;
            if (_sortCol == name) _sortAsc = !_sortAsc;
            else { _sortCol = name; _sortAsc = true; }
            ReloadList();
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

        _installBtn = MainForm.ThemedButton("⬇ Install selected");
        _installBtn.Width = 180; _installBtn.Height = 40; _installBtn.AutoSize = false;
        _installBtn.BackColor = Color.FromArgb(45, 90, 55);
        _installBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _installBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _installBtn.Enabled = false;
        _installBtn.Click += async (_, _) => await InstallSelectedAsync();

        _deleteBtn = MainForm.ThemedButton("🗑 Delete selected");
        _deleteBtn.Width = 170; _deleteBtn.Height = 40; _deleteBtn.AutoSize = false;
        _deleteBtn.ForeColor = Color.FromArgb(245, 130, 120);
        _deleteBtn.Enabled = false;
        _deleteBtn.Click += (_, _) => DeleteSelected();

        _refreshBtn = MainForm.ThemedButton("⟳ Refresh");
        _refreshBtn.Width = 120; _refreshBtn.Height = 40; _refreshBtn.AutoSize = false;
        _refreshBtn.Click += (_, _) => ReloadList();

        _closeBtn = MainForm.ThemedButton("Close");
        _closeBtn.Width = 100; _closeBtn.Height = 40; _closeBtn.AutoSize = false;
        _closeBtn.Click += (_, _) => Close();

        p.Controls.AddRange(new Control[] { _installBtn, _deleteBtn, _refreshBtn, _closeBtn });
        p.Resize += (_, _) =>
        {
            _installBtn.Top = _deleteBtn.Top = _refreshBtn.Top = _closeBtn.Top = 8;
            _installBtn.Left = 16;
            _deleteBtn.Left  = _installBtn.Right + 8;
            _refreshBtn.Left = _deleteBtn.Right + 8;
            _closeBtn.Left   = p.Width - _closeBtn.Width - 16;
        };
        AcceptButton = _closeBtn;
        return p;
    }

    // ── data ──────────────────────────────────────────────────────────

    private void ReloadList()
    {
        try { _entries = ModLibrary.List(_modsDir); }
        catch { _entries = new(); }

        BuildProfilesMap();

        _entries = SortEntries(_entries);

        _grid.Rows.Clear();
        long totalBytes = 0;
        foreach (var e in _entries)
        {
            totalBytes += e.SizeBytes;
            var (state, color) = LiveState(e);
            int idx = _grid.Rows.Add(
                string.IsNullOrEmpty(e.DisplayName) ? e.ModId : e.DisplayName,
                e.Version,
                state,
                ProfilesText(e),
                FormatSize(e.SizeBytes),
                e.ModWorkshopId > 0 ? e.ModWorkshopId.ToString() : "—");
            _grid.Rows[idx].Tag = e;
            _grid.Rows[idx].Cells["State"].Style.ForeColor = color;
            if (ProfileCount(e) == 0)
                _grid.Rows[idx].Cells["Profiles"].Style.ForeColor = Color.FromArgb(150, 160, 180);
        }
        _grid.ClearSelection();
        UpdateSortIndicators();

        _headerLabel.Text =
            $"{_entries.Count} archived version(s)  ·  {FormatSize(totalBytes)} total  ·  "
            + $"in {ModLibrary.LibraryDir(_modsDir)}";
        UpdateButtons();
    }

    /// <summary>Builds the mod_id → profile-names map from all saved
    /// profiles, so the "In profiles" column shows which loadouts include
    /// each library mod (matched by mod_id, version-agnostic).</summary>
    private void BuildProfilesMap()
    {
        _profilesByMod = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in ModProfile.LoadAll())
            {
                foreach (var m in p.Mods)
                {
                    if (string.IsNullOrEmpty(m.ModId)) continue;
                    if (!_profilesByMod.TryGetValue(m.ModId, out var list))
                        _profilesByMod[m.ModId] = list = new List<string>();
                    if (!list.Contains(p.Name)) list.Add(p.Name);
                }
            }
        }
        catch { /* best-effort — empty map just shows "—" */ }
    }

    private int ProfileCount(ModLibrary.Entry e)
        => _profilesByMod.TryGetValue(e.ModId, out var l) ? l.Count : 0;

    private string ProfilesText(ModLibrary.Entry e)
    {
        if (!_profilesByMod.TryGetValue(e.ModId, out var l) || l.Count == 0)
            return "—";
        if (l.Count <= 2) return string.Join(", ", l);
        return $"{l[0]}, {l[1]} +{l.Count - 2}";
    }

    private List<ModLibrary.Entry> SortEntries(List<ModLibrary.Entry> src)
    {
        string Name(ModLibrary.Entry e)
            => string.IsNullOrEmpty(e.DisplayName) ? e.ModId : e.DisplayName;

        IEnumerable<ModLibrary.Entry> q = src;
        switch (_sortCol)
        {
            case "Version":
                q = src.OrderBy(e => e.Version,
                    Comparer<string>.Create((a, b) => ModRegistry.CompareVersions(a, b)));
                break;
            case "State":
                q = src.OrderBy(e => LiveState(e).text, StringComparer.OrdinalIgnoreCase);
                break;
            case "Profiles":
                // Most-used first feels natural; then by name.
                q = src.OrderByDescending(ProfileCount).ThenBy(Name, StringComparer.OrdinalIgnoreCase);
                break;
            case "Size":
                q = src.OrderBy(e => e.SizeBytes);
                break;
            case "MW":
                q = src.OrderBy(e => e.ModWorkshopId);
                break;
            default: // "Mod"
                q = src.OrderBy(Name, StringComparer.OrdinalIgnoreCase)
                       .ThenByDescending(e => e.Version,
                           Comparer<string>.Create((a, b) => ModRegistry.CompareVersions(a, b)));
                break;
        }
        var list = q.ToList();
        if (!_sortAsc) list.Reverse();
        return list;
    }

    /// <summary>Appends a ▲/▼ arrow to the active sort column's header so
    /// the user can see what's sorted and in which direction.</summary>
    private void UpdateSortIndicators()
    {
        foreach (DataGridViewColumn c in _grid.Columns)
        {
            var baseText = c.HeaderText.TrimEnd(' ', '▲', '▼');
            c.HeaderText = c.Name == _sortCol
                ? baseText + (_sortAsc ? "  ▲" : "  ▼")
                : baseText;
        }
    }

    /// <summary>Cross-references the live registry to label a library
    /// entry: live+enabled, live+disabled, an older version while
    /// another is live, or not installed.</summary>
    private (string text, Color color) LiveState(ModLibrary.Entry e)
    {
        var live = _registry.FindById(e.ModId);
        if (live == null)
            return ("Not installed", Color.FromArgb(150, 160, 180));
        var sameVer = ModRegistry.CompareVersions(live.Version, e.Version) == 0;
        if (sameVer)
            return live.IsEnabled
                ? ("✓ Live · enabled", Color.FromArgb(120, 220, 140))
                : ("Live · disabled", Color.FromArgb(255, 200, 80));
        return ($"Other v{live.Version} live", Color.FromArgb(170, 185, 210));
    }

    private void UpdateButtons()
    {
        var n = _grid.SelectedRows.Count;
        _installBtn.Enabled = !_busy && n > 0;
        _deleteBtn.Enabled  = !_busy && n > 0;
        _installBtn.Text = n > 1 ? $"⬇ Install {n} selected" : "⬇ Install selected";
        _deleteBtn.Text  = n > 1 ? $"🗑 Delete {n} selected"  : "🗑 Delete selected";
    }

    private List<ModLibrary.Entry> Selected()
        => _grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.Tag as ModLibrary.Entry)
            .Where(e => e != null)!
            .Cast<ModLibrary.Entry>()
            .ToList();

    // ── actions ───────────────────────────────────────────────────────

    private async Task InstallSelectedAsync()
    {
        if (_busy) return;
        var sel = Selected();
        if (sel.Count == 0) return;

        _busy = true;
        UpdateButtons();
        int installed = 0;
        try
        {
            foreach (var e in sel)
            {
                if (!File.Exists(e.Path)) continue;
                // Reuse the standard install pipeline — it copies into the
                // live folder, writes cfg, adds to the active profile,
                // resolves deps, and rescans. Idempotent on the library.
                await _installCallback(e.Path);
                installed++;
            }
            Changed = installed > 0;
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this,
                "Install failed:\n" + ex.Message,
                "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            ReloadList(); // refresh live-state column
        }
    }

    private void DeleteSelected()
    {
        if (_busy) return;
        var sel = Selected();
        if (sel.Count == 0) return;

        var list = string.Join("\n", sel.Take(10).Select(e =>
            $"   •  {(string.IsNullOrEmpty(e.DisplayName) ? e.ModId : e.DisplayName)}  v{e.Version}"));
        if (sel.Count > 10) list += $"\n   …and {sel.Count - 10} more";

        var dr = ThemedMessageBox.Show(this,
            (sel.Count == 1
                ? "Permanently delete this archived version from the library?"
                : $"Permanently delete these {sel.Count} archived versions from the library?")
            + "\n\n" + list
            + "\n\nThis removes the library copy only. Mods currently installed "
            + "live are NOT affected, but you won't be able to restore these "
            + "versions from the library afterwards.",
            "Delete from library",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (dr != DialogResult.Yes) return;

        int deleted = 0;
        foreach (var e in sel)
        {
            try { ModLibrary.Remove(_modsDir, e.ModId, e.Version); deleted++; }
            catch { /* best-effort, ModLibrary.Remove is already silent */ }
        }
        if (deleted > 0) Changed = true;
        ReloadList();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        double mb = bytes / (1024.0 * 1024.0);
        if (mb >= 1.0) return $"{mb:F1} MB";
        return $"{bytes / 1024.0:F0} KB";
    }
}
