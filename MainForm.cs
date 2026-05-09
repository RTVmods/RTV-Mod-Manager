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
    /// <summary>The in-game mod loader's mod_config.cfg state under
    /// %APPDATA%\Road to Vostok\. Reloaded on every Rescan so we
    /// pick up any changes the loader made between our scans;
    /// written back via SaveModConfigSafely whenever the user
    /// toggles or re-prioritises a mod.</summary>
    private ModConfig _modConfig = new();

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

    /// <summary>Single ContextMenuStrip instance assigned to the
    /// mods grid. Items are rebuilt each time it opens, based on
    /// the row index captured by MouseDown.</summary>
    private ContextMenuStrip _modsContextMenu = null!;
    private int _modsContextRow = -1;

    /// <summary>Backing list for the conflicts grid, in display order.
    /// Click handlers look up by row index.</summary>
    private List<ConflictDetector.Conflict> _displayedConflicts = new();

    public MainForm()
    {
        _settings = Settings.Load();
        _modConfig = ModConfig.Load(ModConfig.DefaultPath);
        _claude.OverridePath = _settings.ClaudePath;
        _resolver = new ConflictResolver(_claude, _registry)
        {
            GameSourcePath = _settings.GameSourcePath,
        };
        InitializeWindow();
        BuildLayout();
        Shown += async (_, _) => await RunStartupAsync();
    }

    /// <summary>Reloads mod_config.cfg from disk and rescans the
    /// mods folder, applying cfg as the source of truth for
    /// per-mod enabled state and priority. Wraps `_registry.Scan`
    /// so every refresh path (RefreshAllAsync, after-toggle, etc.)
    /// picks up cfg changes the in-game loader may have written
    /// since our last scan.</summary>
    private bool Rescan()
    {
        _modConfig = ModConfig.Load(ModConfig.DefaultPath);
        return _registry.Scan(ModsDir, _modConfig);
    }

    /// <summary>Saves mod_config.cfg, surfacing failures in a
    /// dialog. Returns true on success so callers can short-circuit
    /// (revert UI state, abort batched flows). Save-failure is rare
    /// — APPDATA is user-writable — but file-locking by the in-game
    /// loader during simultaneous use is the most likely real
    /// cause.</summary>
    private bool SaveModConfigSafely()
    {
        try { _modConfig.Save(); return true; }
        catch (Exception ex)
        {
            ShowError("Couldn't save mod_config.cfg", ex);
            return false;
        }
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

        // Drag-and-drop install: drop one or more .vmz files anywhere
        // on the window to copy them into the mods folder.
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (files.Any(f => f.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase)))
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            var vmz = files
                .Where(f => f.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (vmz.Count > 0) await InstallModFilesAsync(vmz);
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
        // Layout-time SplitterMoved events fire DURING the form's
        // initial layout pass, with whatever transient sizes the
        // SplitContainer happens to be in (default SplitterDistance
        // 50, intermediate parent widths, etc.). Those events
        // shouldn't be persisted — they don't represent user intent.
        // Only honor SplitterMoved after Form.Shown completes.
        var formShown = false;
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
                    split.SplitterDistance = Math.Clamp(dist, min, max);
            }
        }
        split.Resize += (_, _) => ApplySplit();
        split.SplitterMoved += (_, _) =>
        {
            if (!formShown) return;
            if (split.Width <= 100) return;
            var observed = (double)split.SplitterDistance / split.Width;
            // 0.005 = half a percent — well above float drift /
            // int-truncation noise, well below any deliberate drag.
            if (Math.Abs(observed - ratio) < 0.005) return;
            ratio = observed;
            _settings.SplitterRatio = ratio;
        };
        root.Controls.Add(split, 0, 9);
        Shown += (_, _) =>
        {
            ApplySplit();
            // Flip the flag AFTER ApplySplit so its own induced
            // SplitterMoved (still mid-Shown handler) is also
            // ignored. The first event we treat as user-intent is
            // whatever fires after Shown returns — i.e. an actual
            // drag from the user.
            BeginInvoke(() => formShown = true);
        };
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
            // Editable in-place — commits write the new value to
            // mod_config.cfg's [profile.<active>.priority] block.
            ReadOnly = false,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        // Mod (Fill) goes LAST so every other column has a draggable
        // right edge. With Mod in the middle, Prio (last) had no
        // right boundary to drag, and dragging "Mod's right edge"
        // actually resized the next fixed column (Version) — the
        // Fill column auto-recomputes, so what looks like resizing
        // Mod is really resizing its neighbour. Putting Mod at the
        // end inverts that: Prio gets a right boundary, every fixed
        // column is drag-resizable, and Mod absorbs leftover width
        // without needing a drag handle of its own.
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = "Mod",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 100,
            ReadOnly = true,
            MinimumWidth = 220,
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

        // Right-click → context menu. Use the standard pattern of
        // attaching a ContextMenuStrip directly to the grid + capturing
        // the row in MouseDown, instead of CellContextMenuStripNeeded.
        // The latter doesn't fire reliably for every DataGridView cell
        // type — link cells and checkbox cells in particular swallow
        // right-clicks before the event reaches our handler.
        _modsContextMenu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(36, 42, 54),
            ForeColor = Color.FromArgb(220, 225, 235),
            ShowImageMargin = false,
        };
        _modsContextMenu.Opening += (_, e) =>
        {
            if (_modsContextRow < 0 || _modsContextRow >= _displayed.Count)
            {
                e.Cancel = true;
                return;
            }
            PopulateModsContextMenu(_modsContextMenu, _modsContextRow);
        };
        grid.ContextMenuStrip = _modsContextMenu;
        // MouseDown on the grid surface (NOT CellMouseDown) so we
        // capture even when the click lands in a checkbox/link cell.
        grid.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = grid.HitTest(e.X, e.Y);
            _modsContextRow = hit.RowIndex;
            if (hit.RowIndex >= 0 && hit.RowIndex < grid.Rows.Count)
            {
                grid.ClearSelection();
                grid.Rows[hit.RowIndex].Selected = true;
            }
        };
        // Bonus discoverability — double-click a row's Mod cell to
        // open the mod page directly (skip the right-click menu).
        // No-op for mods without a ModWorkshop ID.
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _displayed.Count) return;
            if (grid.Columns[e.ColumnIndex].Name != "Name") return;
            var entry = _displayed[e.RowIndex];
            if (entry.ModWorkshopId > 0) OpenModPage(entry);
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
    /// <summary>Rebuilds the mods context menu's items for the given
    /// row index. Called from the menu's Opening event so the items
    /// always reflect the right-clicked row's current state (which
    /// could differ from a stale captured row if the grid was
    /// repopulated between the right-click and the menu open).</summary>
    private void PopulateModsContextMenu(ContextMenuStrip menu, int rowIndex)
    {
        menu.Items.Clear();
        if (rowIndex < 0 || rowIndex >= _displayed.Count) return;
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

        menu.Items.Add(new ToolStripSeparator());

        var depsCount = entry.RequiredDependencies.Count
            + entry.OptionalDependencies.Count;
        var depsLabel = depsCount > 0
            ? $"Show dependencies ({depsCount})…"
            : "Show dependencies…";
        var depsItem = new ToolStripMenuItem(depsLabel)
        {
            ToolTipText = depsCount > 0
                ? $"List the {depsCount} declared dependencies and their "
                  + "current state (enabled / disabled / not installed)."
                : "This mod doesn't declare any dependencies. The dialog "
                  + "will explain how mod authors can add them.",
        };
        depsItem.Click += (_, _) => ShowDependenciesDialog(entry);
        menu.Items.Add(depsItem);

        var editDepsItem = new ToolStripMenuItem("Edit required dependencies…")
        {
            ToolTipText = "Rewrite [dependencies] required = ... in mod.txt. "
                + "Comma-separated list of mod IDs; leave empty to clear.",
        };
        editDepsItem.Click += async (_, _) => await SetModDependenciesAsync(entry);
        menu.Items.Add(editDepsItem);

        var setPrioItem = new ToolStripMenuItem(
            $"Set priority… (current: {entry.Priority})")
        {
            ToolTipText = "Rewrite [mod] priority = N in mod.txt. Lower "
                + "numbers load earlier; default is 0; negatives are fine.",
        };
        setPrioItem.Click += async (_, _) => await SetModPriorityAsync(entry);
        menu.Items.Add(setPrioItem);

        menu.Items.Add(new ToolStripSeparator());
        var locked = IsLocked(entry);
        var lockItem = new ToolStripMenuItem(locked ? "Unlock mod" : "Lock mod")
        {
            Checked = locked,
            ToolTipText = locked
                ? "Currently locked — Enable all / Disable all skip this mod. "
                + "Click to unlock so bulk toggles include it again."
                : "Lock this mod so Enable all / Disable all skip it. The "
                + "checkbox still works for individual toggling.",
        };
        lockItem.Click += (_, _) => ToggleLock(entry);
        menu.Items.Add(lockItem);

        menu.Items.Add(new ToolStripSeparator());
        var deleteItem = new ToolStripMenuItem("Delete mod…")
        {
            ToolTipText = "Send the .vmz file (or directory mod's folder) "
                + "to the Recycle Bin and remove its entries from "
                + "mod_config.cfg. Recoverable — restore from the Recycle "
                + "Bin if you change your mind.",
            ForeColor = Color.FromArgb(245, 130, 120),
        };
        deleteItem.Click += (_, _) => DeleteMod(entry);
        menu.Items.Add(deleteItem);
    }

    /// <summary>Sends the mod's .vmz / directory to the Recycle Bin
    /// (NOT hard-deleted — recoverable) and clears its
    /// mod-id@version entries from mod_config.cfg's active profile.
    /// Strong confirmation dialog with No as default. Lock state is
    /// also cleared from settings since the mod no longer exists.</summary>
    private void DeleteMod(ModEntry e)
    {
        var fileName = Path.GetFileName(e.Path);
        var sizeNote = "";
        try
        {
            if (e.IsArchive && File.Exists(e.Path))
            {
                var bytes = new FileInfo(e.Path).Length;
                sizeNote = $" ({bytes / 1024} KiB)";
            }
        }
        catch { /* size is decorative */ }

        var dr = MessageBox.Show(this,
            $"Delete `{e.DisplayName}`{sizeNote}?\n\n"
            + $"  • Sends `{fileName}` to the Recycle Bin\n"
            + "  • Removes its entries from mod_config.cfg\n"
            + (IsLocked(e) ? "  • Removes its lock\n" : "")
            + "\nRecoverable from the Recycle Bin if you change your mind. "
            + "The .bak files (if any) are NOT deleted.",
            "Delete mod — confirm",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            // Default to No so a stray Enter cancels.
            MessageBoxDefaultButton.Button2);
        if (dr != DialogResult.Yes) return;

        try
        {
            // Microsoft.VisualBasic ships in the .NET Windows Forms
            // SDK; FileSystem.DeleteFile/Directory with the
            // SendToRecycleBin option is the simplest reliable way
            // to recycle-bin from .NET. Avoids P/Invoke.
            if (File.Exists(e.Path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    e.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else if (Directory.Exists(e.Path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    e.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                MessageBox.Show(this,
                    $"Couldn't find `{e.Path}` on disk — nothing to "
                    + "recycle. Refreshing the registry to clean up "
                    + "the stale grid entry.",
                    "File missing",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            ShowError("Couldn't delete mod", ex);
            return;
        }

        // Clear cfg + lock state for this mod so we don't leave
        // orphaned entries behind.
        if (!string.IsNullOrEmpty(e.ModId))
        {
            _modConfig.RemoveEntry(e.ModId, e.Version);
            SaveModConfigSafely();
            if (_settings.LockedMods.Remove(e.ModId))
            {
                try { _settings.Save(); }
                catch { /* lock-list cleanup is best-effort */ }
            }
        }

        _modsLabel.Text = $"Deleted `{e.DisplayName}` (sent to Recycle Bin).";
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
    }

    /// <summary>Opens the read-only dependencies dialog for a mod.
    /// Builds an id-keyed view of the registry so the dialog can
    /// classify each dep as enabled / disabled / not installed
    /// without re-querying.</summary>
    private void ShowDependenciesDialog(ModEntry e)
    {
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _registry.Entries)
        {
            if (string.IsNullOrEmpty(entry.ModId)) continue;
            // First-wins on duplicate IDs — registry shouldn't have
            // any, but if a user manually copied a .vmz to two
            // places we don't want a TryAdd-style throw here.
            byId.TryAdd(entry.ModId, entry);
        }
        using var dlg = new DependenciesDialog(e, byId);
        dlg.ShowDialog(this);
    }

    /// <summary>Whether this mod is currently in the user's lock list.
    /// Locked mods are skipped by BulkToggle but can still be toggled
    /// individually via the checkbox.</summary>
    private bool IsLocked(ModEntry e)
        => !string.IsNullOrEmpty(e.ModId)
            && _settings.LockedMods.Contains(e.ModId);

    /// <summary>Adds or removes the mod from the lock list and
    /// persists. Triggers a grid repopulate so the 🔒 indicator on
    /// the Mod column updates.</summary>
    private void ToggleLock(ModEntry e)
    {
        if (string.IsNullOrEmpty(e.ModId))
        {
            // No mod_id in mod.txt — we'd have nothing to key the
            // lock state on. Surface the issue rather than silently
            // doing nothing.
            MessageBox.Show(this,
                $"`{Path.GetFileName(e.Path)}` has no mod_id in its "
                + "mod.txt — can't lock without an ID to key off.",
                "Can't lock",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_settings.LockedMods.Contains(e.ModId))
            _settings.LockedMods.Remove(e.ModId);
        else
            _settings.LockedMods.Add(e.ModId);
        _settings.Save();
        PopulateModsGrid();
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
            // FlatStyle.System → Windows light-theme button chrome,
            // which against our dark grid renders as bright white
            // tiles on every row (including the inert non-Resolve
            // ones). Flat + dark cell colors blend in; empty rows
            // get suppressed entirely via CellPainting below.
            FlatStyle = FlatStyle.Flat,
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(45, 55, 70),
                ForeColor = Color.FromArgb(225, 230, 240),
                SelectionBackColor = Color.FromArgb(65, 80, 105),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Type",
            HeaderText = "Type",
            Width = 180,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Mods",
            HeaderText = "Mods",
            Width = 220,
        });
        // Key (Fill) goes last for the same reason Mod is last in
        // the mods grid: gives Mods a draggable right edge and lets
        // Key absorb leftover width without needing a drag handle.
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Key",
            HeaderText = "Key",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 100,
        });
        // Suppress the button chrome on empty Resolve cells so
        // non-resolvable conflict rows (everything except
        // file_overlap) don't render as a column of empty white
        // tiles against the dark grid.
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "Resolve") return;
            var s = e.Value as string;
            if (!string.IsNullOrEmpty(s)) return;
            e.PaintBackground(e.ClipBounds, true);
            e.Handled = true;
        };
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
            ColumnCount = 6,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 2, 0, 4),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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

        var install = ThemedButton("Install mod…");
        install.Margin = new Padding(0, 2, 4, 2);
        install.Click += async (_, _) => await InstallModFromFilePickerAsync();
        bar.Controls.Add(install, 2, 0);

        var enableAll = ThemedButton("Enable all");
        enableAll.Margin = new Padding(0, 2, 4, 2);
        enableAll.Click += (_, _) => BulkToggle(enable: true);
        bar.Controls.Add(enableAll, 3, 0);

        var disableAll = ThemedButton("Disable all");
        disableAll.Margin = new Padding(0, 2, 4, 2);
        disableAll.Click += (_, _) => BulkToggle(enable: false);
        bar.Controls.Add(disableAll, 4, 0);

        var refresh = ThemedButton("Refresh");
        refresh.Margin = new Padding(0, 2, 0, 2);
        refresh.Click += async (_, _) => await RefreshAllAsync();
        bar.Controls.Add(refresh, 5, 0);

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
        if (!Rescan())
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
                var locked = IsLocked(e);
                // 🔒 prefix on the cell text gives an at-a-glance
                // signal — same as the Lock/Unlock context-menu state,
                // but visible without right-clicking each row.
                row.Cells["Name"].Value = locked ? $"🔒 {name}" : name;
                row.Cells["Name"].ToolTipText = locked
                    ? $"id: {e.ModId}\npath: {e.Path}\n"
                      + "🔒 Locked — Enable all / Disable all skip this mod."
                    : $"id: {e.ModId}\npath: {e.Path}";
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
        if (c.Type == ConflictDetector.TYPE_DUPLICATE_MOD_ID)
            return "Two installed mods declare the same mod_id. The Mods "
                + "column lists the colliding files — delete or move one "
                + "out of the mods folder (and its Disabled subfolder) so "
                + "only one remains. Often this is a leftover .vmz from a "
                + "previous version sitting alongside the current copy.";
        if (c.Type == ConflictDetector.TYPE_MISSING_DEPENDENCY)
            return "An enabled mod requires another mod that's either "
                + "disabled or not installed. The Mods column lists the "
                + "dependent and the missing one — enable / install the "
                + "required mod, or disable the dependent.";
        if (c.Type == ConflictDetector.TYPE_DEPENDENCY_ORDER)
        {
            var min = c.Details.TryGetValue("suggested_dependent_min", out var v)
                ? v?.ToString() ?? "?" : "?";
            return $"A mod loads before its declared dependency. Raise the "
                + $"dependent's priority to at least {min} (right-click → "
                + "Set priority…) so it loads after the dependency.";
        }
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
        // Mods that need toggling, partitioned into "free" (will be
        // toggled) and "locked" (skipped). Locked mods are surfaced
        // in the confirmation prompt so the user knows the bulk
        // action isn't fully comprehensive.
        var candidates = _registry.Entries.Where(e => e.IsEnabled != enable).ToList();
        var targets = candidates.Where(e => !IsLocked(e)).ToList();
        var skipped = candidates.Where(e => IsLocked(e)).ToList();
        if (targets.Count == 0)
        {
            if (skipped.Count > 0)
                _modsLabel.Text = enable
                    ? $"All non-locked mods are already enabled "
                      + $"({skipped.Count} locked, skipped)."
                    : $"All non-locked mods are already disabled "
                      + $"({skipped.Count} locked, skipped).";
            else
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
        var skippedNote = skipped.Count > 0
            ? $"\n\n🔒 Skipping {skipped.Count} locked mod"
              + (skipped.Count == 1 ? "" : "s")
              + " — they'll keep their current state."
            : "";
        var dr = MessageBox.Show(this,
            $"{verb} all {targets.Count} {(enable ? "disabled" : "enabled")} mods?\n\n"
            + preview + skippedNote + "\n\n"
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
        // Single cfg save for the whole batch — one .bak rotation,
        // one disk hit even for 50+ toggles.
        SaveModConfigSafely();
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
        if (failed > 0)
        {
            MessageBox.Show(this,
                $"{failed} of {targets.Count} mods couldn't be toggled. "
                + "(Likely because the .vmz file in mods/Disabled/ is "
                + "locked, or a mod has no mod_id in its mod.txt.)",
                "Bulk toggle finished with errors",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RefreshAllAsync()
    {
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
        await CheckUpdatesAsync(forceFresh: true);
    }

    /// <summary>Opens an OpenFileDialog seeded at the user's Downloads
    /// folder (where browsers drop ModWorkshop downloads by default)
    /// and installs every selected .vmz via InstallModFilesAsync.</summary>
    private async Task InstallModFromFilePickerAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Install mod from .vmz",
            Filter = "Vostok mod (*.vmz)|*.vmz|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var downloads = Path.Combine(home, "Downloads");
            if (Directory.Exists(downloads)) dlg.InitialDirectory = downloads;
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await InstallModFilesAsync(dlg.FileNames);
    }

    /// <summary>Copies one or more .vmz files into the mods folder,
    /// rejecting anything that doesn't look like a real Vostok mod
    /// archive. Skip-or-overwrite prompt for filename collisions.
    /// Single registry rescan + conflict redetect at the end so a
    /// drop of 20 files doesn't trigger 20 full refreshes.</summary>
    private async Task InstallModFilesAsync(IEnumerable<string> sources)
    {
        var modsDir = ModsDir;
        if (!Directory.Exists(modsDir))
        {
            MessageBox.Show(this,
                $"Mods folder `{modsDir}` doesn't exist. Set a valid "
                + "Mods folder above before installing.",
                "Mods folder not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var installed = new List<string>();
        var skipped = new List<(string path, string reason)>();
        // Compute full path of mods dir (case-insensitive comparable)
        // so we can detect "drop a file already in the mods folder."
        var modsDirFull = Path.GetFullPath(modsDir);

        foreach (var src in sources)
        {
            try
            {
                if (!File.Exists(src))
                    throw new FileNotFoundException("File not found.", src);
                if (!src.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Not a .vmz file.");

                // Validate: opens as a zip and contains mod.txt.
                using (var arch = new ModArchive())
                {
                    if (!arch.Open(src))
                        throw new InvalidDataException(
                            "Couldn't open as a zip archive — probably "
                            + "corrupt or wrong file type.");
                    if (!arch.HasFile("mod.txt"))
                        throw new InvalidDataException(
                            "No mod.txt inside the archive — doesn't "
                            + "look like a Vostok mod.");
                }

                var dst = Path.Combine(modsDir, Path.GetFileName(src));
                // Source already inside mods/? Treat as no-op.
                if (string.Equals(
                        Path.GetFullPath(src),
                        Path.GetFullPath(dst),
                        StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add((src, "already in the mods folder"));
                    continue;
                }

                if (File.Exists(dst))
                {
                    var dr = MessageBox.Show(this,
                        $"`{Path.GetFileName(src)}` already exists in the "
                        + "mods folder. Overwrite the existing file?",
                        "File exists",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (dr != DialogResult.Yes)
                    {
                        skipped.Add((src, "exists, user declined overwrite"));
                        continue;
                    }
                }
                // File.Copy is fast for typical mod sizes (<10MB) but
                // run on a thread pool so a slow disk doesn't freeze
                // the UI mid-batch.
                await Task.Run(() => File.Copy(src, dst, overwrite: true));
                installed.Add(Path.GetFileName(src));
            }
            catch (Exception ex)
            {
                skipped.Add((src, ex.Message));
            }
        }

        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);

        if (installed.Count > 0)
            _modsLabel.Text =
                $"Installed {installed.Count} mod"
                + (installed.Count == 1 ? "" : "s")
                + (skipped.Count > 0 ? $" ({skipped.Count} skipped)" : "")
                + ".";
        else if (skipped.Count > 0)
            _modsLabel.Text =
                $"No mods installed ({skipped.Count} skipped).";

        if (skipped.Count > 0)
        {
            var preview = string.Join("\n",
                skipped.Take(8).Select(s => $"  • {Path.GetFileName(s.path)}: {s.reason}"));
            if (skipped.Count > 8)
                preview += $"\n  …and {skipped.Count - 8} more";
            MessageBox.Show(this,
                $"{installed.Count} installed; {skipped.Count} skipped.\n\n"
                + preview,
                "Install report",
                MessageBoxButtons.OK,
                installed.Count > 0
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning);
        }
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

    /// <summary>Fires when the user edits a cell in-place. Two
    /// columns are editable: the Enabled checkbox (toggle-mod flow,
    /// writes mod_config.cfg) and the Priority text cell (writes
    /// the cfg's [profile.&lt;active&gt;.priority] block).</summary>
    private void OnGridCellValueChanged(DataGridViewCellEventArgs e)
    {
        if (_populatingMods) return;
        if (e.RowIndex < 0 || e.RowIndex >= _displayed.Count) return;
        if (e.ColumnIndex < 0 || e.ColumnIndex >= _modsGrid.Columns.Count) return;
        if (_busy) return;
        var col = _modsGrid.Columns[e.ColumnIndex].Name;
        var entry = _displayed[e.RowIndex];

        if (col == "Enabled")
        {
            var cellValue = _modsGrid.Rows[e.RowIndex].Cells["Enabled"].Value;
            var nowChecked = cellValue is bool b && b;
            if (nowChecked == entry.IsEnabled) return;
            ToggleMod(entry);
            return;
        }

        if (col == "Priority")
        {
            var raw = _modsGrid.Rows[e.RowIndex].Cells["Priority"]
                .Value?.ToString()?.Trim() ?? "";
            if (!int.TryParse(raw, out var newPriority))
            {
                MessageBox.Show(this,
                    $"`{raw}` isn't a valid priority. Must be a whole "
                    + "integer (negative is fine). Reverting.",
                    "Invalid priority",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _populatingMods = true;
                try { _modsGrid.Rows[e.RowIndex].Cells["Priority"].Value
                    = entry.Priority.ToString(); }
                finally { _populatingMods = false; }
                return;
            }
            if (newPriority == entry.Priority) return;
            // Priority lives in mod_config.cfg now — the in-game
            // loader's [profile.<active>.priority] section overrides
            // any [mod] priority in mod.txt. Editing mod.txt would
            // be a no-op against the running game.
            if (string.IsNullOrEmpty(entry.ModId))
            {
                MessageBox.Show(this,
                    $"`{Path.GetFileName(entry.Path)}` has no mod_id in "
                    + "its mod.txt — can't write a priority entry without "
                    + "an ID to key off.",
                    "Can't set priority",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _populatingMods = true;
                try { _modsGrid.Rows[e.RowIndex].Cells["Priority"].Value
                    = entry.Priority.ToString(); }
                finally { _populatingMods = false; }
                return;
            }
            var oldPriority = entry.Priority;
            _modConfig.SetPriority(entry.ModId, entry.Version, newPriority);
            if (!SaveModConfigSafely())
            {
                _populatingMods = true;
                try { _modsGrid.Rows[e.RowIndex].Cells["Priority"].Value
                    = oldPriority.ToString(); }
                finally { _populatingMods = false; }
                return;
            }
            _modsLabel.Text =
                $"`{entry.DisplayName}` priority {oldPriority} → {newPriority}.";
            Rescan();
            UpdateModsStatus();
            PopulateModsGrid();
            return;
        }
    }

    private void ToggleMod(ModEntry e)
    {
        if (!ToggleModFiles(e))
        {
            ShowError("Toggle failed",
                new Exception($"Couldn't toggle `{e.DisplayName}` — "
                    + "the file is locked or the mod has no mod_id. "
                    + "Quit Road to Vostok and any antivirus scan, "
                    + "then try again."));
            return;
        }
        if (!SaveModConfigSafely()) return;
        // Rescan + redetect conflicts (different enabled set may have
        // a different conflict set).
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
    }

    /// <summary>Toggles a mod's enabled state via mod_config.cfg
    /// (the in-game loader's source of truth) — and, when enabling
    /// a mod that's currently sitting in &lt;mods&gt;/Disabled/, also
    /// moves the file back to &lt;mods&gt;/ so the loader can discover
    /// it. Disabling never moves files anymore: cfg=false is enough,
    /// and leaving the file in &lt;mods&gt;/ matches what the in-game
    /// loader UI does. mods/Disabled/ shrinks naturally as the
    /// user re-enables legacy disables.
    ///
    /// Doesn't save the cfg here — caller batches that via
    /// SaveModConfigSafely so a 50-file BulkToggle is one rotation,
    /// not 50. Returns false when the move fails so the caller can
    /// count errors.</summary>
    private bool ToggleModFiles(ModEntry e)
    {
        if (string.IsNullOrEmpty(e.ModId))
        {
            // No mod_id → can't key cfg state. Surface the issue
            // instead of silently doing nothing.
            return false;
        }
        var enabling = !e.IsEnabled;
        if (enabling)
        {
            // Move out of Disabled/ if necessary so the loader can
            // discover it. Files already in mods/ stay put.
            if (IsUnderDisabledFolder(e.Path))
            {
                var fileName = Path.GetFileName(e.Path);
                var dst = Path.Combine(ModsDir, fileName);
                try
                {
                    if (e.IsArchive) File.Move(e.Path, dst);
                    else Directory.Move(e.Path, dst);
                    e.Path = dst;
                }
                catch
                {
                    return false;
                }
            }
            _modConfig.SetEnabled(e.ModId, e.Version, true);
            // Refresh priority entry too if missing — declared
            // priority becomes the cfg priority on first enable so
            // future cfg-only flows see a stable value.
            if (!_modConfig.HasEntry(e.ModId, e.Version))
                _modConfig.SetPriority(e.ModId, e.Version, e.DeclaredPriority);
        }
        else
        {
            _modConfig.SetEnabled(e.ModId, e.Version, false);
        }
        return true;
    }

    private bool IsUnderDisabledFolder(string entryPath)
    {
        var disabled = Path.Combine(ModsDir, "Disabled");
        try
        {
            var entryFull = Path.GetFullPath(entryPath);
            var disabledFull = Path.GetFullPath(disabled);
            return entryFull.StartsWith(
                disabledFull + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
    /// <summary>Reads the mod's mod.txt, applies `transform` to the
    /// content, and writes it back — handling the archive vs
    /// directory-mod split, .bak backups, and error reporting in one
    /// place. Returns the success message line on success (so the
    /// caller can decide what to do with it) or null on failure.</summary>
    private async Task<string?> EditModTxtAsync(ModEntry e, Func<string, string> transform)
    {
        try
        {
            if (e.IsArchive)
            {
                string oldText;
                using (var arch = new ModArchive())
                {
                    if (!arch.Open(e.Path))
                        throw new IOException("Failed to open the .vmz archive for reading.");
                    oldText = arch.ReadText("mod.txt");
                    if (string.IsNullOrEmpty(oldText))
                        throw new InvalidDataException("mod.txt is missing or empty inside the archive.");
                }
                var newText = transform(oldText);
                var backup = ZipPatcher.CreateBackup(e.Path);
                ZipPatcher.ReplaceEntry(e.Path, "mod.txt", newText);
                return $"Backup: {Path.GetFileName(backup)}";
            }
            else
            {
                var modTxtPath = Path.Combine(e.Path, "mod.txt");
                if (!File.Exists(modTxtPath))
                    throw new FileNotFoundException("mod.txt not found in the mod folder.", modTxtPath);
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var bakPath = $"{modTxtPath}.{stamp}.bak";
                File.Copy(modTxtPath, bakPath, overwrite: false);
                var newText = transform(await File.ReadAllTextAsync(modTxtPath));
                await File.WriteAllTextAsync(modTxtPath, newText);
                return $"Backup: {Path.GetFileName(bakPath)}";
            }
        }
        catch (Exception ex)
        {
            ShowError("Couldn't update mod.txt", ex);
            return null;
        }
    }

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
        var bak = await EditModTxtAsync(e, txt => ManifestEditor.SetUpdatesModworkshop(txt, newId));
        if (bak == null) return;
        _modsLabel.Text = $"Linked `{e.DisplayName}` → ModWorkshop {newId}. {bak}";

        // Re-scan + re-check updates so the new link kicks in.
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        await CheckUpdatesAsync(forceFresh: true);
    }

    /// <summary>Prompts for a new load-order priority and rewrites
    /// `[mod] priority = N` in mod.txt. Lower numbers load earlier.
    /// Negative values are allowed — useful for pinning a mod above
    /// the default-0 priority of stock mods.</summary>
    /// <summary>Writes a new load-order priority for the mod into
    /// mod_config.cfg's [profile.&lt;active&gt;.priority] section. We
    /// do NOT write to mod.txt here — the in-game loader's cfg
    /// overrides any [mod] priority in mod.txt, so a mod.txt edit
    /// would be silent against the running game. The mod-author's
    /// declared priority remains as a fallback when the cfg has
    /// no entry.</summary>
    private Task SetModPriorityAsync(ModEntry e)
    {
        var current = e.Priority.ToString();
        var prompt =
            $"Enter the load-order priority for `{e.DisplayName}`.\n\n"
            + "Lower numbers load earlier; default is 0. Negative "
            + "values are fine (e.g. -100 to pin above everything "
            + "else).\n\n"
            + "Writes to mod_config.cfg's "
            + $"[profile.{_modConfig.ActiveProfile}.priority] block — "
            + "the same key the in-game loader UI edits.";
        var input = TextInputDialog.Prompt(this, "Set priority", prompt, current);
        if (input == null) return Task.CompletedTask;
        if (!int.TryParse(input.Trim(), out var newPriority))
        {
            MessageBox.Show(this,
                $"Couldn't parse `{input}` as an integer. Priority must "
                + "be a whole number (positive, negative, or zero).",
                "Invalid input",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Task.CompletedTask;
        }
        if (newPriority == e.Priority)
        {
            _modsLabel.Text = $"`{e.DisplayName}` priority is already {newPriority}.";
            return Task.CompletedTask;
        }
        if (string.IsNullOrEmpty(e.ModId))
        {
            MessageBox.Show(this,
                $"`{Path.GetFileName(e.Path)}` has no mod_id in mod.txt — "
                + "can't write a priority entry without an ID to key off.",
                "Can't set priority",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Task.CompletedTask;
        }
        var oldPriority = e.Priority;
        _modConfig.SetPriority(e.ModId, e.Version, newPriority);
        if (!SaveModConfigSafely()) return Task.CompletedTask;
        _modsLabel.Text =
            $"`{e.DisplayName}` priority {oldPriority} → {newPriority}.";

        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        // Re-detect — priority changes can resolve dependency_order
        // conflicts.
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
        return Task.CompletedTask;
    }

    /// <summary>Picks required dependencies from a checklist of
    /// installed mods (with a manual-add box for not-yet-installed
    /// mod IDs), then rewrites `[dependencies] required = ...` in
    /// mod.txt. An empty selection clears the field. Optional deps
    /// aren't editable here yet — the "user wants to declare a hard
    /// dependency" use case is much more common.</summary>
    private async Task SetModDependenciesAsync(ModEntry e)
    {
        var newDeps = DependencyPickerDialog.Pick(
            this, e, _registry.Entries, e.RequiredDependencies);
        if (newDeps == null) return;
        // Set comparison — the picker returns ticks in click order,
        // not the order they were declared in mod.txt, so positional
        // diff would falsely report a change after every dialog
        // close. Order doesn't matter semantically for deps anyway.
        var existing = new HashSet<string>(
            e.RequiredDependencies, StringComparer.OrdinalIgnoreCase);
        var picked = new HashSet<string>(
            newDeps, StringComparer.OrdinalIgnoreCase);
        if (existing.SetEquals(picked))
        {
            _modsLabel.Text = $"`{e.DisplayName}` dependencies unchanged.";
            return;
        }

        // Order check — among the deps the user JUST added, find any
        // installed dep whose priority is >= the subject's. If found,
        // offer to bump the subject above the highest-priority new
        // dep so the load order will satisfy the new declaration.
        // Only newly-added deps matter here: pre-existing violations
        // would already be flagged in the conflicts panel.
        int? bumpTo = null;
        var added = picked.Except(existing, StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count > 0)
        {
            var registryById = new Dictionary<string, ModEntry>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _registry.Entries)
            {
                if (string.IsNullOrEmpty(entry.ModId)) continue;
                registryById.TryAdd(entry.ModId, entry);
            }
            var blockers = added
                .Where(id => registryById.ContainsKey(id))
                .Select(id => registryById[id])
                .Where(d => d.Priority >= e.Priority)
                .ToList();
            if (blockers.Count > 0)
            {
                var maxBlocker = blockers.OrderByDescending(d => d.Priority).First();
                var newPrio = maxBlocker.Priority + 1;
                var blockerNames = string.Join(", ",
                    blockers
                        .OrderByDescending(d => d.Priority)
                        .Select(d => $"{d.ModId} (prio {d.Priority})"));
                var dr = MessageBox.Show(this,
                    $"`{e.DisplayName}` is at priority {e.Priority}, but "
                    + $"now requires:\n  {blockerNames}\n\n"
                    + "Dependents must load AFTER their dependencies, so "
                    + $"`{e.DisplayName}`'s priority needs to be > "
                    + $"{maxBlocker.Priority}.\n\n"
                    + $"Bump `{e.DisplayName}`'s priority "
                    + $"{e.Priority} → {newPrio} as part of this edit?",
                    "Bump priority?",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (dr == DialogResult.Cancel) return;
                if (dr == DialogResult.Yes) bumpTo = newPrio;
                // No → write deps only; the conflicts panel will
                // surface the dependency_order violation so the user
                // can fix it later.
            }
        }

        // Deps still live in mod.txt (no cfg equivalent), but
        // priority moved to mod_config.cfg. So we do TWO writes:
        // mod.txt for the new dep list, cfg for the optional bump.
        var bak = await EditModTxtAsync(e, txt =>
            ManifestEditor.SetDependencyList(txt, "required", newDeps));
        if (bak == null) return;
        if (bumpTo.HasValue && !string.IsNullOrEmpty(e.ModId))
        {
            _modConfig.SetPriority(e.ModId, e.Version, bumpTo.Value);
            SaveModConfigSafely();
        }
        var depsMsg = newDeps.Count == 0
            ? $"Cleared `{e.DisplayName}` required dependencies."
            : $"Set `{e.DisplayName}` required dependencies "
              + $"({newDeps.Count}).";
        var prioMsg = bumpTo.HasValue
            ? $" Priority {e.Priority} → {bumpTo.Value}."
            : "";
        _modsLabel.Text = $"{depsMsg}{prioMsg} {bak}";

        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        // Re-detect since dependency edits can satisfy or break
        // missing_dependency / dependency_order conflicts elsewhere.
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
    }

    private async Task UpdateModAsync(ModEntry e)
    {
        var mw = e.ModWorkshopId;
        if (mw <= 0) return;
        var label = string.IsNullOrEmpty(e.DisplayName)
            ? Path.GetFileName(e.Path)
            : e.DisplayName;
        var modId = e.ModId;
        var oldVersion = e.Version;
        // Capture pre-update cfg state so we can carry the user's
        // enable + priority customisations forward when the new mod
        // version's mod.txt declares a different version string —
        // cfg is keyed by mod-id@version, so the new version starts
        // with no cfg entries unless we migrate.
        var oldEnabled = _modConfig.IsEnabled(modId, oldVersion, fallback: true);
        var hadCfgPriority = _modConfig.HasEntry(modId, oldVersion);
        var oldCfgPriority = _modConfig.Priority(modId, oldVersion, e.DeclaredPriority);

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

            // First rescan so we can see the new mod.txt's version.
            Rescan();

            // Migrate cfg state if the mod.txt-declared version
            // changed. Without this, the user's previous priority
            // override would silently revert to the declared default
            // and the new key would default to enabled — close to
            // right but losing customisations.
            var migrated = false;
            var newEntry = _registry.FindById(modId);
            var newVersion = newEntry?.Version ?? oldVersion;
            if (!string.IsNullOrEmpty(modId)
                && !string.Equals(newVersion, oldVersion, StringComparison.Ordinal))
            {
                _modConfig.SetEnabled(modId, newVersion, oldEnabled);
                if (hadCfgPriority)
                    _modConfig.SetPriority(modId, newVersion, oldCfgPriority);
                _modConfig.RemoveEntry(modId, oldVersion);
                SaveModConfigSafely();
                Rescan();
                migrated = true;
            }

            // Force-refresh the ModWorkshop /mods/versions cache —
            // we just downloaded a new file, and the cached API
            // value on _our_ side is likely stale (the cached
            // "outdated" check is what told us to update in the
            // first place). Without this refresh, the comparison
            // below would always look like a version mismatch even
            // when the new mod.txt + the latest API value agree.
            // Best-effort: a network failure here just leaves the
            // cache as-is and the message falls through to a
            // neutral "versions disagree" wording.
            try { await CheckUpdatesAsync(forceFresh: true); }
            catch { /* keep going with the stale cache */ }

            UpdateModsStatus();
            PopulateModsGrid();

            var apiVersion = _latestVersions.TryGetValue(mw, out var av) ? av : "";
            if (string.IsNullOrEmpty(apiVersion))
            {
                _updatesLabel.Text = $"Updated {label} → v{newVersion}"
                    + (migrated ? " (cfg state carried over)." : ".");
            }
            else if (string.Equals(newVersion, apiVersion, StringComparison.Ordinal))
            {
                _updatesLabel.Text = $"Updated {label} → v{newVersion}"
                    + (migrated ? " (cfg state carried over)." : ".");
            }
            else
            {
                // Could be either side lagging — author hasn't
                // bumped mod.txt yet, OR ModWorkshop's CDN hasn't
                // caught up to a brand-new release. Don't take
                // sides; just describe what we see.
                _updatesLabel.Text =
                    $"Updated {label}: file's mod.txt is v{newVersion}, "
                    + $"ModWorkshop API reports v{apiVersion}. The Update "
                    + "column may still show ⬆ until both sides agree — "
                    + "click Refresh in a couple of minutes.";
            }
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
        Rescan();
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
            Rescan();
            UpdateModsStatus();
            PopulateModsGrid();
            _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
            UpdateConflictsStatus(_lastConflicts);
            PopulateConflictsList(_lastConflicts);
        }
    }
}
