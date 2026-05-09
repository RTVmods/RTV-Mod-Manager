// Top-level window. v0.3.3 — interactive mods grid: per-row Toggle
// (enable/disable, moves the .vmz to/from Disabled/) and Update
// (downloads the latest .vmz from ModWorkshop, swaps it in place).
//
// Conflicts column is still a read-only ListBox; AI Resolve buttons +
// Settings panel + resolution dialog land in the next commit.

using VostokModManager.Ai;
using VostokModManager.Api;
using VostokModManager.Domain;
using VostokModManager.Ui;

namespace VostokModManager;

public class MainForm : Form
{
    /// <summary>Hardcoded fallback when the user hasn't set a custom
    /// ModsDir. Default Steam install layout for Road to Vostok.</summary>
    private const string SteamFallbackModsDir =
        @"C:\Program Files (x86)\Steam\steamapps\common\Road to Vostok\mods";

    /// <summary>The active mods directory. Reads from the user's
    /// settings if set; otherwise falls back to the Steam install
    /// path. This is the path scan/toggle/install operations target.</summary>
    private string ModsDir =>
        !string.IsNullOrEmpty(_settings.ModsDir)
            ? _settings.ModsDir
            : SteamFallbackModsDir;

    private readonly Settings _settings;
    private readonly ModRegistry _registry = new();
    private readonly ClaudeCodeRunner _claude = new();
    private readonly ModWorkshopClient _mw = new();
    private readonly ConflictResolver _resolver;

    /// <summary>mod_workshop_id → latest version (populated after the
    /// /mods/versions call).</summary>
    private readonly Dictionary<int, string> _latestVersions = new();
    private List<ConflictDetector.Conflict> _lastConflicts = new();

    /// <summary>Backing list for the mods grid, in display order. The
    /// grid binds to row indices into this list, so click handlers
    /// look up entries by row number.</summary>
    private List<ModEntry> _displayed = new();

    /// <summary>True while a long-running per-row action is in flight
    /// (download / etc.). Disables the action buttons across the grid.</summary>
    private bool _busy;

    /// <summary>True while we're rewriting the mods grid programmatically
    /// — guards the CellValueChanged handler so it doesn't react to
    /// our own Value assignments and try to "toggle" each row.</summary>
    private bool _populatingMods;

    private Label _claudeLabel = null!;
    private Label _modsLabel = null!;
    private Label _updatesLabel = null!;
    private Label _conflictsLabel = null!;
    private DataGridView _modsGrid = null!;
    private DataGridView _conflictsGrid = null!;
    private Panel _setupBanner = null!;
    private Label _setupBannerLabel = null!;
    private TextBox _modsPathInput = null!;
    private TextBox _claudePathInput = null!;
    private TextBox _decompPathInput = null!;
    private TextBox _filterBox = null!;

    /// <summary>Backing list for the conflicts grid, in display order.
    /// Click handlers look up by row index.</summary>
    private List<ConflictDetector.Conflict> _displayedConflicts = new();

    public MainForm()
    {
        _settings = Settings.Load();
        _claude.OverridePath = _settings.ClaudePath;
        _resolver = new ConflictResolver(_claude, _registry)
        {
            GameSourcePath = _settings.GameSourcePath,
        };
        InitializeWindow();
        BuildLayout();
        Shown += async (_, _) => await RunStartupAsync();
    }

    private void InitializeWindow()
    {
        Text = "Vostok Mod Manager";
        MinimumSize = new Size(900, 600);
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 10f);
        // ExtractAssociatedIcon pulls the .exe's own embedded icon
        // (set via <ApplicationIcon> in the .csproj). Wrapped — older
        // Win10 builds occasionally throw IOException on this call.
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { /* form falls back to the default WinForms icon */ }
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };

        // Restore last-session size + position if we have one. Validate
        // the saved bounds intersect a current screen so a multi-monitor
        // disconnect can't park us offscreen.
        var saved = new Rectangle(
            _settings.WindowLeft, _settings.WindowTop,
            _settings.WindowWidth, _settings.WindowHeight);
        if (saved.Width >= 600 && saved.Height >= 400
            && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)))
        {
            StartPosition = FormStartPosition.Manual;
            Location = saved.Location;
            Size = saved.Size;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            Width = 1280;
            Height = 800;
        }
        if (_settings.WindowMaximized)
            WindowState = FormWindowState.Maximized;

        // Persist the new bounds on close. Skip the save when
        // minimised — RestoreBounds gives us the un-minimised values
        // so we don't get stuck restoring a tiny "minimised" rect.
        FormClosing += (_, _) =>
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settings.WindowLeft = b.Left;
            _settings.WindowTop = b.Top;
            _settings.WindowWidth = b.Width;
            _settings.WindowHeight = b.Height;
            _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
            // Snapshot per-grid column widths. Wrapped null-checks
            // because BuildLayout assigns the grid fields, but if a
            // construction error fired FormClosing before that ran
            // we don't want to NRE on the way out.
            if (_modsGrid != null) SaveColumnWidths(_modsGrid, "mods");
            if (_conflictsGrid != null) SaveColumnWidths(_conflictsGrid, "conflicts");
            try { _settings.Save(); }
            catch { /* best-effort; don't block app close on a write error */ }
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 10,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Color.Transparent,
        };
        for (var i = 0; i < 9; i++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Vostok Mod Manager",
            Font = new Font(Font.FontFamily, 20f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(title, 0, 0);

        _setupBanner = BuildSetupBanner();
        root.Controls.Add(_setupBanner, 0, 1);

        _claudeLabel = NewStatus("Claude Code: detecting ...");
        root.Controls.Add(_claudeLabel, 0, 2);

        _modsLabel = NewStatus("Mods: scanning ...");
        root.Controls.Add(_modsLabel, 0, 3);

        _updatesLabel = NewStatus("Updates: —");
        root.Controls.Add(_updatesLabel, 0, 4);

        _conflictsLabel = NewStatus("Conflicts: —");
        root.Controls.Add(_conflictsLabel, 0, 5);

        // Inline settings rows — Mods folder + Claude path + Decomp
        // path, each with Browse / Save buttons. Mods folder is
        // first because it's the most fundamental path: empty
        // settings + missing default Steam folder = nothing else
        // works.
        _modsPathInput = BuildPathRow(
            root, 6, "Mods folder:",
            placeholder: "(default Steam install — change if you moved the game)",
            onBrowse: BrowseMods,
            onSave: SaveModsPath);
        _claudePathInput = BuildPathRow(
            root, 7, "Claude Code path:",
            placeholder: "(auto-detect — fill if not found above)",
            onBrowse: BrowseClaude,
            onSave: SaveClaudePath);
        _decompPathInput = BuildPathRow(
            root, 8, "Game source (Decomp/):",
            placeholder: "path to the decompiled game source folder",
            onBrowse: BrowseDecomp,
            onSave: SaveDecompPath);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = Color.Transparent,
            SplitterWidth = 6,
        };

        // Left: interactive mods grid + toolbar (filter, bulk, refresh).
        _modsGrid = BuildModsGrid();
        ApplyColumnWidths(_modsGrid, "mods");
        split.Panel1.Controls.Add(
            WrapInPanel("Installed mods", _modsGrid, BuildModsToolbar()));

        // Right: interactive conflicts grid with Resolve button.
        _conflictsGrid = BuildConflictsGrid();
        ApplyColumnWidths(_conflictsGrid, "conflicts");
        split.Panel2.Controls.Add(WrapInPanel("Conflicts", _conflictsGrid));

        // Mods is the primary view — give it ~62% of the width by
        // default, or whatever ratio the user dragged it to last
        // session. Subscribe to Resize BEFORE adding the container to
        // its parent — the Add triggers the initial layout pass, and
        // a later subscription would miss that first event entirely.
        // Shown fires after the form has its real client size, so it
        // also guarantees a correct distance regardless of when the
        // SplitContainer first landed at its final width.
        // Use any positive saved ratio with a safety clamp at apply
        // time. Earlier we used `is > 0.1 and < 0.9` for the load
        // check, which silently rejected saved values outside that
        // band — including reasonable user preferences like 0.95
        // (very wide mods) or 0.05 (very narrow). The fallback to
        // 0.62 only kicks in for the "fresh install, no saved value"
        // case (ratio = 0).
        var ratio = _settings.SplitterRatio > 0
            ? Math.Clamp(_settings.SplitterRatio, 0.05, 0.95)
            : 0.62;
        // Guard so SplitterMoved doesn't write to settings during
        // OUR programmatic SplitterDistance assignments — those
        // happen on every window resize and would clobber the saved
        // ratio with a slightly-drifted value (int truncation, or
        // worse, a clamp on a small window).
        var applyingSplit = false;
        void ApplySplit()
        {
            if (split.Width > 100)
            {
                var dist = (int)(split.Width * ratio);
                // Clamp inside the SplitContainer's allowed range so
                // a small window can't crash with an out-of-range
                // SplitterDistance.
                var min = split.Panel1MinSize;
                var max = split.Width - split.Panel2MinSize - split.SplitterWidth;
                if (max > min)
                {
                    applyingSplit = true;
                    try { split.SplitterDistance = Math.Clamp(dist, min, max); }
                    finally { applyingSplit = false; }
                }
            }
        }
        split.Resize += (_, _) => ApplySplit();
        // Only USER drags update the persisted ratio — programmatic
        // SplitterDistance writes from ApplySplit are suppressed by
        // the applyingSplit flag.
        split.SplitterMoved += (_, _) =>
        {
            if (applyingSplit) return;
            if (split.Width > 100)
            {
                ratio = (double)split.SplitterDistance / split.Width;
                _settings.SplitterRatio = ratio;
            }
        };
        root.Controls.Add(split, 0, 9);
        Shown += (_, _) => ApplySplit();
    }

    private static Label NewStatus(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 2, 0, 2),
        ForeColor = Color.FromArgb(180, 190, 210),
    };

    /// <summary>Themed Button factory — by default WinForms paints
    /// Button controls in the system-light scheme regardless of the
    /// parent form's BackColor/ForeColor, which renders our text in
    /// near-white on near-white. FlatStyle=Flat with explicit colors
    /// keeps everything readable against the dark slate panel.</summary>
    public static Button ThemedButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 55, 70),
            ForeColor = Color.FromArgb(225, 230, 240),
            AutoSize = true,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(85, 100, 120);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(65, 80, 105);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(35, 45, 60);
        return b;
    }

    private Panel BuildSetupBanner()
    {
        var p = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Visible = false,
            BackColor = Color.FromArgb(140, 100, 30),
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 4, 0, 8),
        };
        _setupBannerLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(255, 240, 220),
            MaximumSize = new Size(1200, 0),
            Font = new Font("Segoe UI", 10.5f),
        };
        p.Controls.Add(_setupBannerLabel);
        return p;
    }

    private TextBox BuildPathRow(
        TableLayoutPanel root, int rowIdx,
        string labelText, string placeholder,
        Action onBrowse, Action onSave)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 4,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 2),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        row.Controls.Add(new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(180, 190, 210),
            Margin = new Padding(0, 6, 0, 0),
        }, 0, 0);

        var input = new TextBox
        {
            PlaceholderText = placeholder,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(30, 36, 48),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 4, 0, 4),
        };
        row.Controls.Add(input, 1, 0);

        var browseBtn = ThemedButton("Browse...");
        browseBtn.Margin = new Padding(4, 2, 0, 2);
        browseBtn.Click += (_, _) => onBrowse();
        row.Controls.Add(browseBtn, 2, 0);

        var saveBtn = ThemedButton("Save");
        saveBtn.Margin = new Padding(4, 2, 0, 2);
        saveBtn.Click += (_, _) => onSave();
        row.Controls.Add(saveBtn, 3, 0);

        root.Controls.Add(row, 0, rowIdx);
        return input;
    }

    private DataGridView BuildModsGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(18, 22, 30),
            BorderStyle = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle =
            {
                BackColor = Color.FromArgb(36, 42, 54),
                ForeColor = Color.FromArgb(220, 225, 235),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
                SelectionForeColor = Color.FromArgb(220, 225, 235),
            },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font = new Font("Consolas", 10f),
            },
            GridColor = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 32,
            RowTemplate = { Height = 28 },
        };

        // Columns. AutoGenerateColumns = false so we control the layout.
        // The grid is read-only by default, but the Enabled checkbox
        // column overrides that so the user can flip mods on/off.
        grid.ReadOnly = false;
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Enabled",
            HeaderText = "On",
            Width = 40,
            ReadOnly = false,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        // Single icon-style Update column. Three cell states:
        //   "⬆"  in orange — outdated, click to update
        //   "✓"  in green  — current
        //   "—"  muted     — no MW link / unknown
        // Using a LinkColumn gets the hand cursor + visited-color
        // semantics for free; non-link states are styled per-cell in
        // PopulateModsGrid. Font is bumped so the single-char icons
        // read at a glance even though the column itself is narrow;
        // the version number lives in the tooltip rather than the
        // cell to keep the column slim.
        grid.Columns.Add(new DataGridViewLinkColumn
        {
            Name = "Update",
            HeaderText = "Upd",
            Width = 50,
            ReadOnly = true,
            TrackVisitedState = false,
            LinkBehavior = LinkBehavior.HoverUnderline,
            ActiveLinkColor = Color.FromArgb(255, 220, 120),
            LinkColor = Color.FromArgb(255, 200, 80),
            VisitedLinkColor = Color.FromArgb(255, 200, 80),
            DefaultCellStyle =
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Symbol", 14f, FontStyle.Bold),
            },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Pos",
            HeaderText = "#",
            Width = 36,
            ReadOnly = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = "Mod",
            // Used to be AutoSizeMode = Fill, but Fill columns can't
            // be drag-resized in any meaningful way — dragging the
            // boundary just resizes the adjacent fixed column. Now
            // it's a regular sized column so the user can drag it
            // and the width persists across launches like every
            // other column. Width is generous by default so a fresh
            // install still shows long mod names without truncation.
            Width = 600,
            ReadOnly = true,
            MinimumWidth = 220,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Version",
            HeaderText = "Version",
            Width = 80,
            ReadOnly = true,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Priority",
            HeaderText = "Prio",
            Width = 50,
            ReadOnly = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        // (Update + version status now collapse into the single
        // "Update" link column at the start of the row.)
        // Mod ID intentionally not a column — it lives on the row's
        // ToolTipText (hover the Name cell) since it's rarely needed
        // visually and wastes space.

        // Lock the order. The grid renders mods in load-order priority;
        // letting the user click a column header to re-sort would
        // silently reshuffle the list out of load order, which is the
        // ONE invariant we never want broken. Toggling a mod also
        // shouldn't move it, which the priority-only sort in
        // PopulateModsGrid already takes care of.
        foreach (DataGridViewColumn col in grid.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;

        grid.CellContentClick += async (_, e) => await OnGridCellClickedAsync(e);

        // Right-click on a row → select that row + show the context
        // menu with Open page / Set ID. Header right-clicks (RowIndex
        // < 0) get no menu.
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
            grid.ClearSelection();
            grid.Rows[e.RowIndex].Selected = true;
            grid.CurrentCell = grid.Rows[e.RowIndex].Cells["Name"];
        };
        grid.CellContextMenuStripNeeded += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            e.ContextMenuStrip = BuildModsContextMenu(e.RowIndex);
        };

        // Checkbox-cell plumbing: by default DataGridView only fires
        // CellValueChanged after the cell loses focus. CommitEdit on
        // dirty-state-change makes it fire immediately on click,
        // which is what users expect from a checkbox.
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty
                && grid.CurrentCell is DataGridViewCheckBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, e) => OnGridCellValueChanged(e);
        return grid;
    }

    /// <summary>Builds a row-specific context menu for the mods grid.
    /// Item enable-state and labels depend on whether the mod has a
    /// ModWorkshop ID — Open is greyed when there's nothing to open;
    /// Set toggles between "Set…" and "Change…" based on presence.</summary>
    private ContextMenuStrip BuildModsContextMenu(int rowIndex)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(36, 42, 54),
            ForeColor = Color.FromArgb(220, 225, 235),
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
        };
        if (rowIndex < 0 || rowIndex >= _displayed.Count) return menu;
        var entry = _displayed[rowIndex];
        var hasMw = entry.ModWorkshopId > 0;

        var open = new ToolStripMenuItem("Open ModWorkshop page")
        {
            Enabled = hasMw,
            ToolTipText = hasMw
                ? $"Open https://modworkshop.net/mod/{entry.ModWorkshopId} in your browser."
                : "No ModWorkshop ID linked — use Set ModWorkshop ID first.",
        };
        open.Click += (_, _) => OpenModPage(entry);
        menu.Items.Add(open);

        var setLabel = hasMw ? "Change ModWorkshop ID…" : "Set ModWorkshop ID…";
        var setItem = new ToolStripMenuItem(setLabel)
        {
            ToolTipText = "Edit mod.txt to add or update the [updates] modworkshop = N "
                + "field. Lets the manager track this mod for updates.",
        };
        setItem.Click += async (_, _) => await SetModWorkshopIdAsync(entry);
        menu.Items.Add(setItem);

        return menu;
    }

    /// <summary>Custom palette for the context menu so the dropdown
    /// doesn't pop up as a bright Windows-Aero white-on-blue strip
    /// against the rest of the dark form.</summary>
    private class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(65, 80, 105);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(65, 80, 105);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(65, 80, 105);
        public override Color MenuItemBorder => Color.FromArgb(85, 100, 120);
        public override Color MenuBorder => Color.FromArgb(85, 100, 120);
        public override Color ToolStripDropDownBackground => Color.FromArgb(36, 42, 54);
        public override Color ImageMarginGradientBegin => Color.FromArgb(36, 42, 54);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(36, 42, 54);
        public override Color ImageMarginGradientEnd => Color.FromArgb(36, 42, 54);
    }


    private DataGridView BuildConflictsGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(18, 22, 30),
            BorderStyle = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle =
            {
                BackColor = Color.FromArgb(36, 42, 54),
                ForeColor = Color.FromArgb(220, 225, 235),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
                SelectionForeColor = Color.FromArgb(220, 225, 235),
            },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font = new Font("Consolas", 10f),
                WrapMode = DataGridViewTriState.True,
            },
            GridColor = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 32,
            RowTemplate = { Height = 28 },
        };
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Resolve",
            HeaderText = "",
            Width = 80,
            FlatStyle = FlatStyle.System,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Type",
            HeaderText = "Type",
            Width = 180,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Key",
            HeaderText = "Key",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 100,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Mods",
            HeaderText = "Mods",
            Width = 220,
        });
        grid.CellContentClick += async (_, e) => await OnConflictsCellClickedAsync(e);
        return grid;
    }

    /// <summary>Reads `_settings.ColumnWidths` and applies any saved
    /// width to a non-Fill column whose name matches. Fill columns
    /// are skipped — they auto-compute and pinning them would defeat
    /// the leftover-space behaviour. The lower bound (20px) blocks a
    /// corrupted settings file from rendering an unfindable 1px
    /// column.</summary>
    private void ApplyColumnWidths(DataGridView grid, string prefix)
    {
        foreach (DataGridViewColumn col in grid.Columns)
        {
            if (col.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill) continue;
            var key = $"{prefix}.{col.Name}";
            if (_settings.ColumnWidths.TryGetValue(key, out var w) && w >= 20)
                col.Width = w;
        }
    }

    /// <summary>Snapshots the current widths of every non-Fill column
    /// into `_settings.ColumnWidths`. Caller is responsible for
    /// _settings.Save() afterwards (FormClosing batches this with the
    /// rest of the persisted UI state).</summary>
    private void SaveColumnWidths(DataGridView grid, string prefix)
    {
        foreach (DataGridViewColumn col in grid.Columns)
        {
            if (col.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill) continue;
            _settings.ColumnWidths[$"{prefix}.{col.Name}"] = col.Width;
        }
    }

    /// <summary>Wraps a content control in a panel with a bold header
    /// label at the top and an optional toolbar between the header
    /// and the content. WinForms docks children in reverse order, so
    /// add: body (Fill) → toolbar (Top) → header (Top), and the
    /// header lands at the very top with the toolbar just below it.</summary>
    private static Panel WrapInPanel(string headerText, Control body, Control? toolbar = null)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var hdr = new Label
        {
            Text = headerText,
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
        };
        body.Dock = DockStyle.Fill;
        p.Controls.Add(body);
        if (toolbar != null)
        {
            toolbar.Dock = DockStyle.Top;
            p.Controls.Add(toolbar);
        }
        p.Controls.Add(hdr);
        return p;
    }

    private Control BuildModsToolbar()
    {
        var bar = new TableLayoutPanel
        {
            Height = 32,
            Dock = DockStyle.Top,
            ColumnCount = 5,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 2, 0, 4),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        bar.Controls.Add(new Label
        {
            Text = "Filter:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(180, 190, 210),
            Margin = new Padding(0, 6, 4, 0),
        }, 0, 0);

        _filterBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, 36, 48),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "filter by name, id, or filename",
            Margin = new Padding(0, 4, 8, 4),
        };
        _filterBox.TextChanged += (_, _) => PopulateModsGrid();
        bar.Controls.Add(_filterBox, 1, 0);

        var enableAll = ThemedButton("Enable all");
        enableAll.Margin = new Padding(0, 2, 4, 2);
        enableAll.Click += (_, _) => BulkToggle(enable: true);
        bar.Controls.Add(enableAll, 2, 0);

        var disableAll = ThemedButton("Disable all");
        disableAll.Margin = new Padding(0, 2, 4, 2);
        disableAll.Click += (_, _) => BulkToggle(enable: false);
        bar.Controls.Add(disableAll, 3, 0);

        var refresh = ThemedButton("Refresh");
        refresh.Margin = new Padding(0, 2, 0, 2);
        refresh.Click += async (_, _) => await RefreshAllAsync();
        bar.Controls.Add(refresh, 4, 0);

        return bar;
    }

    private async Task RunStartupAsync()
    {
        // Hydrate the path inputs from saved settings so users can see
        // and edit what's currently in effect.
        _modsPathInput.Text = _settings.ModsDir;
        _claudePathInput.Text = _settings.ClaudePath;
        _decompPathInput.Text = _settings.GameSourcePath;

        // First-run convenience: try to auto-detect the Decomp/ folder
        // if the user hasn't set one yet. We only check known locations
        // (next to the game install, repo Desktop layout, Documents)
        // and only when the candidate looks like a real Decomp (Scripts/
        // Loader.gd + Interface.gd present).
        if (string.IsNullOrEmpty(_settings.GameSourcePath))
        {
            var detected = AutodetectDecomp();
            if (!string.IsNullOrEmpty(detected))
            {
                _settings.GameSourcePath = detected;
                _settings.Save();
                _resolver.GameSourcePath = detected;
                _decompPathInput.Text = detected;
            }
        }

        _claude.Detect();
        UpdateClaudeStatus();
        RefreshSetupBanner();

        _modsLabel.Text = $"Mods: scanning {ModsDir} ...";
        if (!_registry.Scan(ModsDir))
        {
            _modsLabel.Text = $"Mods: cannot read {ModsDir}. " +
                "Is the game installed at the default Steam path?";
            return;
        }
        UpdateModsStatus();
        PopulateModsGrid();

        _conflictsLabel.Text = "Conflicts: detecting (deep analysis) ...";
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);

        await CheckUpdatesAsync();
    }

    // --- status / list rendering ----------------------------------

    private void UpdateClaudeStatus()
    {
        if (_claude.IsAvailable)
        {
            _claudeLabel.Text =
                $"Claude Code: ✓  {_claude.Version}   ({_claude.ResolvedPath})";
        }
        else if (ClaudeCodeRunner.HasMsixInstall())
        {
            _claudeLabel.Text =
                "Claude Code: ✗  Detected Claude Desktop (Microsoft Store), " +
                "but its CLI is sandboxed and unreachable from outside the " +
                "package. Install the standalone CLI: " +
                "`npm install -g @anthropic-ai/claude-code`.";
        }
        else
        {
            _claudeLabel.Text =
                "Claude Code: ✗ not found — AI conflict resolution disabled. " +
                "Install Node.js (nodejs.org), then run " +
                "`npm install -g @anthropic-ai/claude-code`.";
        }
    }

    private void UpdateModsStatus()
    {
        var enabled = _registry.Enabled().Count;
        var total = _registry.Entries.Count;
        _modsLabel.Text =
            $"Mods: {total} found  ({enabled} enabled, {total - enabled} disabled)";
    }

    private void UpdateConflictsStatus(List<ConflictDetector.Conflict> conflicts)
    {
        if (conflicts.Count == 0)
        {
            _conflictsLabel.Text = "Conflicts: none detected ✓";
            return;
        }
        var byType = conflicts
            .GroupBy(c => c.Type)
            .Select(g => $"{g.Count()} {g.Key}");
        _conflictsLabel.Text = "Conflicts: " + string.Join(", ", byType);
    }

    private void PopulateModsGrid()
    {
        // Sort: load order (priority asc) then filename. The same
        // ordering applies regardless of IsEnabled — toggling a mod
        // shouldn't shuffle its row, just flip its checkbox. The grid
        // itself is locked (every column has SortMode = NotSortable in
        // BuildModsGrid) so the user can't accidentally re-sort by
        // clicking a header either.
        // Filter: case-insensitive substring on display name, mod_id,
        // or filename. Empty filter = no filtering.
        var filter = (_filterBox?.Text ?? "").Trim().ToLowerInvariant();
        bool Matches(ModEntry e)
        {
            if (filter.Length == 0) return true;
            if (e.DisplayName.ToLowerInvariant().Contains(filter)) return true;
            if (e.ModId.ToLowerInvariant().Contains(filter)) return true;
            if (Path.GetFileName(e.Path).ToLowerInvariant().Contains(filter)) return true;
            return false;
        }

        _displayed = _registry.Entries
            .Where(Matches)
            .OrderBy(e => e.Priority)
            .ThenBy(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Guard the CellValueChanged handler — setting the checkbox
        // value programmatically would otherwise be indistinguishable
        // from a user click and would re-toggle the mod.
        _populatingMods = true;
        _modsGrid.SuspendLayout();
        try
        {
            _modsGrid.Rows.Clear();
            var pos = 0;
            foreach (var e in _displayed)
            {
                var rowIdx = _modsGrid.Rows.Add();
                var row = _modsGrid.Rows[rowIdx];
                row.Cells["Enabled"].Value = e.IsEnabled;
                StyleUpdateCell(row.Cells["Update"], e);
                row.Cells["Pos"].Value = e.IsEnabled ? $"{++pos}" : "";
                var name = !string.IsNullOrEmpty(e.DisplayName)
                    ? e.DisplayName
                    : Path.GetFileName(e.Path);
                row.Cells["Name"].Value = name;
                row.Cells["Name"].ToolTipText = $"id: {e.ModId}\npath: {e.Path}";
                row.Cells["Version"].Value = e.Version;
                row.Cells["Priority"].Value = e.Priority.ToString();
            }
        }
        finally
        {
            _modsGrid.ResumeLayout();
            _populatingMods = false;
        }
    }

    private bool IsOutdated(ModEntry e)
    {
        var mw = e.ModWorkshopId;
        if (mw <= 0) return false;
        if (!_latestVersions.TryGetValue(mw, out var latest)) return false;
        if (string.IsNullOrEmpty(latest)) return false;
        return latest != e.Version;
    }

    /// <summary>Writes the Update column's text + per-cell style for a
    /// row. The DataGridViewLinkCell renders link-blue by default; we
    /// override LinkColor per-cell to get green (current) / orange
    /// (outdated) / muted-grey (no info) variants. Only outdated rows
    /// keep an underline-on-hover and the hand cursor — the others are
    /// styled to look static even though they're still link cells.</summary>
    private void StyleUpdateCell(DataGridViewCell cell, ModEntry e)
    {
        var mw = e.ModWorkshopId;
        var muted = Color.FromArgb(120, 130, 150);
        var orange = Color.FromArgb(255, 200, 80);
        var green = Color.FromArgb(120, 220, 140);

        string text;
        Color color;
        string tip;

        if (mw <= 0)
        {
            text = "—";
            color = muted;
            tip = "No ModWorkshop link in mod.txt — can't check for updates.";
        }
        else if (!_latestVersions.TryGetValue(mw, out var latest)
                 || string.IsNullOrEmpty(latest))
        {
            text = "—";
            color = muted;
            tip = "Update status unknown (cache miss). Click Refresh to check.";
        }
        else if (latest == e.Version)
        {
            text = "✓";
            color = green;
            tip = $"Up to date (v{latest}).";
        }
        else
        {
            text = "⬆";
            color = orange;
            tip = $"Update available: v{e.Version} → v{latest}. Click to install.";
        }

        cell.Value = text;
        cell.ToolTipText = tip;
        cell.Style.ForeColor = color;
        cell.Style.SelectionForeColor = color;
        if (cell is DataGridViewLinkCell link)
        {
            link.LinkColor = color;
            link.ActiveLinkColor = color;
            link.VisitedLinkColor = color;
        }
    }

    private void PopulateConflictsList(List<ConflictDetector.Conflict> conflicts)
    {
        _displayedConflicts = conflicts.ToList();
        _conflictsGrid.SuspendLayout();
        _conflictsGrid.Rows.Clear();
        foreach (var c in _displayedConflicts)
        {
            var rowIdx = _conflictsGrid.Rows.Add();
            var row = _conflictsGrid.Rows[rowIdx];
            row.Cells["Resolve"].Value = IsButtonRow(c) ? "Resolve" : "";
            row.Cells["Resolve"].ToolTipText = ResolveButtonTooltip(c);
            row.Cells["Type"].Value = c.Type;
            row.Cells["Key"].Value = c.Key;
            row.Cells["Mods"].Value = string.Join(", ", c.ModIds);
        }
        _conflictsGrid.ResumeLayout();
    }

    private string ResolveButtonTooltip(ConflictDetector.Conflict c)
    {
        if (c.Type != ConflictDetector.TYPE_FILE_OVERLAP)
            return "AI resolve only handles file_overlap conflicts in v1.";
        if (!_claude.IsAvailable)
            return "Claude Code not detected — install it to enable AI resolve.";
        return "Send this conflict to Claude Code for analysis.";
    }

    /// <summary>True if the conflict can be sent to Claude. v1 covers
    /// every file_overlap (Claude can merge any text file — README,
    /// INSTRUCTIONS, .gd, etc. — though .gd is where real merges
    /// happen). Other conflict types (autoload / hook / script_extend
    /// / class_name / take_over / super_chain) require either custom
    /// resolution prompts or aren't really merge candidates; we'll
    /// add them iteratively.</summary>
    private bool IsResolvable(ConflictDetector.Conflict c)
    {
        if (c.Type != ConflictDetector.TYPE_FILE_OVERLAP) return false;
        return _claude.IsAvailable;
    }

    /// <summary>True for any file_overlap regardless of Claude state —
    /// drives whether the button cell shows "Resolve" or stays blank.
    /// Click handler still re-checks IsResolvable() and surfaces a
    /// clear hint if Claude isn't available.</summary>
    private static bool IsButtonRow(ConflictDetector.Conflict c)
        => c.Type == ConflictDetector.TYPE_FILE_OVERLAP;

    // --- async update check ---------------------------------------

    private async Task CheckUpdatesAsync(bool forceFresh = false)
    {
        // Use the persisted cache if it's still inside the 1h TTL —
        // saves a round-trip to ModWorkshop on every launch and
        // respects their no-spam policy. The Refresh toolbar button
        // forces a re-fetch.
        if (!forceFresh && _settings.IsCacheFresh)
        {
            _latestVersions.Clear();
            foreach (var (k, v) in _settings.CachedVersions)
                if (int.TryParse(k, out var id))
                    _latestVersions[id] = v;
            var ageMin = (int)Math.Round(_settings.CacheAge.TotalMinutes);
            _updatesLabel.Text =
                $"Updates: {SummarizeUpdates()}  (cached ~{ageMin} min ago — Refresh to recheck)";
            PopulateModsGrid();
            return;
        }

        var ids = _registry.Enabled()
            .Select(e => e.ModWorkshopId)
            .Where(i => i > 0)
            .ToList();
        if (ids.Count == 0)
        {
            _updatesLabel.Text = "Updates: no enabled mods have a ModWorkshop link";
            return;
        }
        _updatesLabel.Text = $"Updates: checking {ids.Count} mods on ModWorkshop ...";
        try
        {
            var versions = await _mw.CheckVersionsAsync(ids);
            _latestVersions.Clear();
            foreach (var (k, v) in versions) _latestVersions[k] = v;
            // Persist for the next launch.
            _settings.CachedVersions = _latestVersions
                .ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
            _settings.CacheTimestamp = DateTime.UtcNow.ToString("o");
            _settings.Save();
            _updatesLabel.Text = $"Updates: {SummarizeUpdates()}";
            PopulateModsGrid();
        }
        catch (Exception ex)
        {
            _updatesLabel.Text = $"Updates: check failed — {ex.Message}";
        }
    }

    private string SummarizeUpdates()
    {
        int outdated = 0, current = 0, unknown = 0;
        foreach (var e in _registry.Enabled())
        {
            var mw = e.ModWorkshopId;
            if (mw <= 0) continue;
            if (!_latestVersions.TryGetValue(mw, out var latest))
            { unknown++; continue; }
            if (latest == e.Version) current++;
            else outdated++;
        }
        return $"{outdated} outdated, {current} current, {unknown} unknown";
    }

    // --- toolbar actions ------------------------------------------

    private void BulkToggle(bool enable)
    {
        var targets = _registry.Entries.Where(e => e.IsEnabled != enable).ToList();
        if (targets.Count == 0)
        {
            _modsLabel.Text = enable
                ? "All mods are already enabled."
                : "All mods are already disabled.";
            return;
        }
        var verb = enable ? "Enable" : "Disable";
        // Show up to N mod names in the prompt so the user can spot
        // anything they didn't mean to touch. Bigger N = more noise;
        // 8 fits comfortably in the default MessageBox width.
        const int previewCount = 8;
        var preview = string.Join("\n",
            targets.Take(previewCount)
                   .Select(e => "  • " + (string.IsNullOrEmpty(e.DisplayName)
                       ? Path.GetFileName(e.Path)
                       : e.DisplayName)));
        if (targets.Count > previewCount)
            preview += $"\n  …and {targets.Count - previewCount} more";
        var dr = MessageBox.Show(this,
            $"{verb} all {targets.Count} {(enable ? "disabled" : "enabled")} mods?\n\n"
            + preview + "\n\n"
            + "Each mod's .vmz will be moved between <mods>/ and <mods>/Disabled/. "
            + "You can undo with the opposite bulk action.",
            $"{verb} all — confirm",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            // Default to No so a stray Enter cancels instead of
            // bulk-toggling the entire mod list.
            MessageBoxDefaultButton.Button2);
        if (dr != DialogResult.Yes) return;

        var failed = 0;
        foreach (var e in targets)
        {
            if (!ToggleModFiles(e)) failed++;
        }
        // One rescan + one redetect at the end — far cheaper than per-mod.
        _registry.Scan(ModsDir);
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
        if (failed > 0)
        {
            MessageBox.Show(this,
                $"{failed} of {targets.Count} mods couldn't be moved. "
                + "(Usually because the .vmz file is locked by something — "
                + "Steam, antivirus, or a running game.)",
                "Bulk toggle finished with errors",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RefreshAllAsync()
    {
        _registry.Scan(ModsDir);
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
        await CheckUpdatesAsync(forceFresh: true);
    }

    // --- decomp auto-detect ---------------------------------------

    private static string AutodetectDecomp()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(home))
        {
            candidates.Add(Path.Combine(home, "Desktop", "RoadToVostok Dev", "Decomp"));
            candidates.Add(Path.Combine(home, "Desktop", "Decomp"));
        }
        if (!string.IsNullOrEmpty(docs))
            candidates.Add(Path.Combine(docs, "RoadToVostok_Decomp"));
        foreach (var c in candidates)
            if (LooksLikeDecomp(c)) return c;
        return "";
    }

    /// <summary>True if `path` contains the landmark files we expect
    /// in a real Decomp/ — Scripts/Loader.gd and Scripts/Interface.gd
    /// are both shipped by Road to Vostok and not by anything else.</summary>
    private static bool LooksLikeDecomp(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
        return File.Exists(Path.Combine(path, "Scripts", "Loader.gd"))
            && File.Exists(Path.Combine(path, "Scripts", "Interface.gd"));
    }

    // --- per-row actions -------------------------------------------

    private async Task OnGridCellClickedAsync(DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _displayed.Count) return;
        if (_busy) return;
        var col = _modsGrid.Columns[e.ColumnIndex].Name;
        var entry = _displayed[e.RowIndex];
        switch (col)
        {
            case "Update":
                if (IsOutdated(entry))
                    await UpdateModAsync(entry);
                // The muted "—" state means we have no ModWorkshop ID
                // for this mod. Repurpose the click to "tell me the
                // ID" — most discoverable place to fix the missing
                // link.
                else if (entry.ModWorkshopId <= 0)
                    await SetModWorkshopIdAsync(entry);
                break;
        }
    }

    /// <summary>Fires when the user toggles the Enabled checkbox.
    /// CommitEdit on dirty-state-change makes this fire immediately
    /// rather than on focus-loss. We diff the new bool against the
    /// entry's current state so we don't double-toggle if the value is
    /// already in sync (e.g. just after a programmatic populate).</summary>
    private void OnGridCellValueChanged(DataGridViewCellEventArgs e)
    {
        if (_populatingMods) return;
        if (e.RowIndex < 0 || e.RowIndex >= _displayed.Count) return;
        if (e.ColumnIndex < 0 || e.ColumnIndex >= _modsGrid.Columns.Count) return;
        if (_modsGrid.Columns[e.ColumnIndex].Name != "Enabled") return;
        if (_busy) return;
        var entry = _displayed[e.RowIndex];
        var cellValue = _modsGrid.Rows[e.RowIndex].Cells["Enabled"].Value;
        var nowChecked = cellValue is bool b && b;
        if (nowChecked == entry.IsEnabled) return;
        ToggleMod(entry);
    }

    private void ToggleMod(ModEntry e)
    {
        if (!ToggleModFiles(e))
        {
            ShowError("Toggle failed",
                new Exception($"Couldn't move {Path.GetFileName(e.Path)} — "
                    + "see Output for details."));
            return;
        }
        // Rescan + redetect conflicts (different enabled set may have
        // a different conflict set).
        _registry.Scan(ModsDir);
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
    }

    /// <summary>Filesystem-only mod toggle — moves the .vmz / dir
    /// between &lt;mods&gt;/ and &lt;mods&gt;/Disabled/ without rescanning the
    /// registry. Used by both ToggleMod (single, with rescan) and
    /// BulkToggle (many, single rescan at the end). Returns false on
    /// failure so the caller can count errors.</summary>
    private bool ToggleModFiles(ModEntry e)
    {
        var fileName = Path.GetFileName(e.Path);
        string dst;
        if (e.IsEnabled)
        {
            var disabledDir = Path.Combine(ModsDir, "Disabled");
            try { Directory.CreateDirectory(disabledDir); }
            catch { return false; }
            dst = Path.Combine(disabledDir, fileName);
        }
        else
        {
            dst = Path.Combine(ModsDir, fileName);
        }
        try
        {
            if (e.IsArchive) File.Move(e.Path, dst);
            else Directory.Move(e.Path, dst);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Open the mod's ModWorkshop page in the user's default
    /// browser. No-op (with a status message) if the mod has no MW ID
    /// linked — the context menu's Open item is greyed in that case,
    /// so this only triggers if something else called it.</summary>
    private void OpenModPage(ModEntry e)
    {
        if (e.ModWorkshopId <= 0)
        {
            _modsLabel.Text = $"`{e.DisplayName}` has no ModWorkshop ID linked.";
            return;
        }
        var url = $"https://modworkshop.net/mod/{e.ModWorkshopId}";
        try
        {
            // UseShellExecute = true so the OS resolves the default
            // browser. ProcessStartInfo with a bare URL would fail on
            // .NET Core without this flag.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowError("Couldn't open browser", ex);
        }
    }

    /// <summary>Prompts for a ModWorkshop ID (or URL — we parse either)
    /// and rewrites the mod's mod.txt to set [updates] modworkshop = N.
    /// Backs up archive mods to a .bak first; for directory mods we
    /// just overwrite mod.txt in place. Refreshes the registry on
    /// success so the Update column re-renders with the new state.</summary>
    private async Task SetModWorkshopIdAsync(ModEntry e)
    {
        var current = e.ModWorkshopId > 0 ? e.ModWorkshopId.ToString() : "";
        var prompt =
            $"Enter the ModWorkshop ID for `{e.DisplayName}`.\n\n"
            + "Accepts either a numeric ID (e.g. 56398) or the full mod URL "
            + "(e.g. https://modworkshop.net/mod/56398/optional-slug).\n\n"
            + "This rewrites the mod's mod.txt to add or update its "
            + "[updates] modworkshop = N field.";
        var input = TextInputDialog.Prompt(this, "Set ModWorkshop ID", prompt, current);
        if (input == null) return;
        var newId = ManifestEditor.ParseModWorkshopIdInput(input);
        if (newId <= 0)
        {
            MessageBox.Show(this,
                $"Couldn't parse a ModWorkshop ID from:\n  {input}\n\n"
                + "Expected a positive integer or a URL like "
                + "https://modworkshop.net/mod/56398.",
                "Invalid input",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (newId == e.ModWorkshopId)
        {
            _modsLabel.Text = $"`{e.DisplayName}` already linked to ModWorkshop {newId}.";
            return;
        }

        try
        {
            if (e.IsArchive)
            {
                // Read existing mod.txt from inside the .vmz, splice
                // the new ID, back up the archive, then write the
                // updated entry.
                string oldText;
                using (var arch = new ModArchive())
                {
                    if (!arch.Open(e.Path))
                        throw new IOException("Failed to open the .vmz archive for reading.");
                    oldText = arch.ReadText("mod.txt");
                    if (string.IsNullOrEmpty(oldText))
                        throw new InvalidDataException("mod.txt is missing or empty inside the archive.");
                }
                var newText = ManifestEditor.SetUpdatesModworkshop(oldText, newId);
                var backup = ZipPatcher.CreateBackup(e.Path);
                ZipPatcher.ReplaceEntry(e.Path, "mod.txt", newText);
                _modsLabel.Text =
                    $"Linked `{e.DisplayName}` → ModWorkshop {newId}. "
                    + $"Backup: {Path.GetFileName(backup)}";
            }
            else
            {
                // Directory mod — mod.txt is on disk. Backup the file,
                // then overwrite.
                var modTxtPath = Path.Combine(e.Path, "mod.txt");
                if (!File.Exists(modTxtPath))
                    throw new FileNotFoundException("mod.txt not found in the mod folder.", modTxtPath);
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var bakPath = $"{modTxtPath}.{stamp}.bak";
                File.Copy(modTxtPath, bakPath, overwrite: false);
                var newText = ManifestEditor.SetUpdatesModworkshop(
                    await File.ReadAllTextAsync(modTxtPath),
                    newId);
                await File.WriteAllTextAsync(modTxtPath, newText);
                _modsLabel.Text =
                    $"Linked `{e.DisplayName}` → ModWorkshop {newId}. "
                    + $"Backup: {Path.GetFileName(bakPath)}";
            }
        }
        catch (Exception ex)
        {
            ShowError("Couldn't update mod.txt", ex);
            return;
        }

        // Re-scan + re-check updates so the new link kicks in.
        _registry.Scan(ModsDir);
        UpdateModsStatus();
        PopulateModsGrid();
        await CheckUpdatesAsync(forceFresh: true);
    }

    private async Task UpdateModAsync(ModEntry e)
    {
        var mw = e.ModWorkshopId;
        if (mw <= 0) return;
        var label = string.IsNullOrEmpty(e.DisplayName)
            ? Path.GetFileName(e.Path)
            : e.DisplayName;
        var finalPath = e.Path;
        var tempPath = finalPath + ".download";

        _busy = true;
        _updatesLabel.Text = $"Downloading {label} ...";
        PopulateModsGrid();  // re-render to disable buttons

        try
        {
            // Clean up any leftover from a prior failed attempt.
            if (File.Exists(tempPath)) File.Delete(tempPath);

            await _mw.DownloadLatestAsync(mw, tempPath);

            // Swap the file. If the original is locked (game running
            // and reading it), File.Delete throws — we leave the temp
            // file in place and tell the user to rename manually.
            if (File.Exists(finalPath))
            {
                try { File.Delete(finalPath); }
                catch (Exception ex)
                {
                    _updatesLabel.Text =
                        $"Downloaded {label} to {Path.GetFileName(tempPath)}, but " +
                        $"couldn't replace existing file ({ex.Message}). " +
                        "Quit the game and rename the .download file manually.";
                    return;
                }
            }
            File.Move(tempPath, finalPath);

            _updatesLabel.Text =
                $"Updated {label}. (Mod's internal version may still " +
                "lag the ModWorkshop reported version — that's a mod-author quirk.)";

            _registry.Scan(ModsDir);
            UpdateModsStatus();
            PopulateModsGrid();
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
            { try { File.Delete(tempPath); } catch { } }
            ShowError($"Update failed for {label}", ex);
        }
        finally
        {
            _busy = false;
            PopulateModsGrid();
        }
    }

    private void ShowError(string title, Exception ex)
    {
        MessageBox.Show(this, ex.Message, title,
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // --- settings + setup banner ----------------------------------

    private void BrowseMods()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Locate the Road to Vostok mods folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        // Open the picker at whatever path is currently in the input
        // (or the live ModsDir if the input's empty), so the user can
        // step up one level to find the right one.
        var seed = !string.IsNullOrWhiteSpace(_modsPathInput.Text)
            ? _modsPathInput.Text
            : ModsDir;
        if (Directory.Exists(seed)) dlg.SelectedPath = seed;
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _modsPathInput.Text = dlg.SelectedPath;
            SaveModsPath();
        }
    }

    private void SaveModsPath()
    {
        var path = _modsPathInput.Text.Trim();
        // Empty input = "use default Steam path" — preserve that
        // explicit choice so the user can clear the box to revert.
        // For non-empty paths we sanity-check existence; bail with a
        // clear message rather than silently scanning a nonexistent
        // folder.
        if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
        {
            MessageBox.Show(this,
                $"`{path}` doesn't exist. The mods folder must be a real "
                + "directory — pick the `mods/` folder inside the Road to "
                + "Vostok install.",
                "Mods folder not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _settings.ModsDir = path;
        _settings.Save();
        // Re-scan from the new path immediately so the grid refreshes.
        _registry.Scan(ModsDir);
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
        RefreshSetupBanner();
    }

    private void BrowseClaude()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Locate claude.exe / claude.cmd",
            Filter = "Claude Code (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*",
        };
        // Default-open in %APPDATA%/npm where the npm-global install lives.
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appdata))
            dlg.InitialDirectory = Path.Combine(appdata, "npm");
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _claudePathInput.Text = dlg.FileName;
            SaveClaudePath();
        }
    }

    private void BrowseDecomp()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Locate the decompiled game source folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (Directory.Exists(_decompPathInput.Text))
            dlg.SelectedPath = _decompPathInput.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _decompPathInput.Text = dlg.SelectedPath;
            SaveDecompPath();
        }
    }

    private void SaveClaudePath()
    {
        _settings.ClaudePath = _claudePathInput.Text.Trim();
        _settings.Save();
        _claude.OverridePath = _settings.ClaudePath;
        _claude.Detect();
        UpdateClaudeStatus();
        // Re-render conflicts grid so Resolve buttons reflect the new
        // Claude availability state.
        PopulateConflictsList(_lastConflicts);
        RefreshSetupBanner();
    }

    private void SaveDecompPath()
    {
        _settings.GameSourcePath = _decompPathInput.Text.Trim();
        _settings.Save();
        _resolver.GameSourcePath = _settings.GameSourcePath;
        RefreshSetupBanner();
    }

    private void RefreshSetupBanner()
    {
        var msgs = new List<string>();
        if (!_claude.IsAvailable)
        {
            if (ClaudeCodeRunner.HasMsixInstall())
            {
                msgs.Add(
                    "• Claude Desktop (Microsoft Store) detected — that " +
                    "install is sandboxed and unreachable from outside the " +
                    "package. Install the standalone CLI: install Node.js " +
                    "from nodejs.org, then in a NEW terminal run " +
                    "`npm install -g @anthropic-ai/claude-code`. Restart " +
                    "the Manager when done.");
            }
            else
            {
                msgs.Add(
                    "• Claude Code not detected. Install Node.js from " +
                    "nodejs.org, then in a NEW terminal run " +
                    "`npm install -g @anthropic-ai/claude-code`. Or paste " +
                    "a known claude.exe path into the input below and " +
                    "click Save.");
            }
        }
        if (!Directory.Exists(ModsDir))
        {
            msgs.Add(
                $"• Mods folder `{ModsDir}` doesn't exist. The default " +
                "Steam install path doesn't apply on your machine — set " +
                "the Mods folder below to wherever Road to Vostok lives.");
        }
        if (string.IsNullOrEmpty(_settings.GameSourcePath)
            || !Directory.Exists(_settings.GameSourcePath))
        {
            msgs.Add(
                "• Game source (Decomp/) not configured. AI conflict " +
                "resolution still works without it, but produces better " +
                "merges when given the original game script as context. " +
                "Paste your Decomp/ path into the input below or click " +
                "Browse... to pick the folder.");
        }
        if (msgs.Count == 0)
        {
            _setupBanner.Visible = false;
            return;
        }
        _setupBannerLabel.Text = "⚙ Setup needed:\n\n" + string.Join("\n\n", msgs);
        _setupBanner.Visible = true;
    }


    // --- conflict resolve ----------------------------------------

    private async Task OnConflictsCellClickedAsync(DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _displayedConflicts.Count) return;
        if (_busy)
        {
            _conflictsLabel.Text = "(Busy — wait for the current operation to finish.)";
            return;
        }
        var col = _conflictsGrid.Columns[e.ColumnIndex].Name;
        if (col != "Resolve") return;
        var conflict = _displayedConflicts[e.RowIndex];
        if (!IsButtonRow(conflict))
        {
            _conflictsLabel.Text =
                $"Resolve doesn't handle [{conflict.Type}] conflicts in v1 — "
                + "only file_overlap.";
            return;
        }
        if (!_claude.IsAvailable)
        {
            _conflictsLabel.Text =
                "Resolve needs Claude Code. "
                + "Install Node.js then `npm install -g @anthropic-ai/claude-code`, "
                + "then restart the manager.";
            return;
        }
        await ResolveAsync(conflict);
    }

    private async Task ResolveAsync(ConflictDetector.Conflict conflict)
    {
        _busy = true;
        var prevConflictsLabel = _conflictsLabel.Text;
        _conflictsLabel.Text =
            $"Resolving {conflict.Key} with Claude Code ...";
        // Re-render grids to disable buttons during the call.
        PopulateModsGrid();

        ConflictResolver.Verdict verdict;
        try
        {
            verdict = await _resolver.ResolveFileOverlapAsync(conflict);
        }
        catch (Exception ex)
        {
            verdict = new ConflictResolver.Verdict
            {
                Ok = false,
                ConflictKey = conflict.Key,
                Error = ex.Message,
            };
        }
        finally
        {
            _busy = false;
            _conflictsLabel.Text = prevConflictsLabel;
            PopulateModsGrid();
        }

        using var dlg = new ResolutionDialog(verdict);
        dlg.ShowDialog(this);
        // If the user applied the merge inside the dialog, the .vmz
        // contents changed — rescan + redetect so the conflict (and
        // any related ones) drop off the list cleanly.
        if (dlg.Applied)
        {
            _registry.Scan(ModsDir);
            UpdateModsStatus();
            PopulateModsGrid();
            _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
            UpdateConflictsStatus(_lastConflicts);
            PopulateConflictsList(_lastConflicts);
        }
    }
}
