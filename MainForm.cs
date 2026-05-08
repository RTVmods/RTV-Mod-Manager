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
    private const string DefaultModsDir =
        @"C:\Program Files (x86)\Steam\steamapps\common\Road to Vostok\mods";

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

    private Label _claudeLabel = null!;
    private Label _modsLabel = null!;
    private Label _updatesLabel = null!;
    private Label _conflictsLabel = null!;
    private DataGridView _modsGrid = null!;
    private DataGridView _conflictsGrid = null!;
    private Panel _setupBanner = null!;
    private Label _setupBannerLabel = null!;
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
        Width = 1280;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 9f);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Color.Transparent,
        };
        for (var i = 0; i < 8; i++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Vostok Mod Manager",
            Font = new Font(Font.FontFamily, 18f, FontStyle.Bold),
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

        // Inline settings rows — Claude path + Decomp path with
        // Browse / Save buttons.
        _claudePathInput = BuildPathRow(
            root, 6, "Claude Code path:",
            placeholder: "(auto-detect — fill if not found above)",
            onBrowse: BrowseClaude,
            onSave: SaveClaudePath);
        _decompPathInput = BuildPathRow(
            root, 7, "Game source (Decomp/):",
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
        split.Panel1.Controls.Add(
            WrapInPanel("Installed mods", _modsGrid, BuildModsToolbar()));

        // Right: interactive conflicts grid with Resolve button.
        _conflictsGrid = BuildConflictsGrid();
        split.Panel2.Controls.Add(WrapInPanel("Conflicts", _conflictsGrid));

        root.Controls.Add(split, 0, 8);
        split.Resize += (_, _) =>
        {
            if (split.Width > 100)
                split.SplitterDistance = split.Width / 2;
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
            Font = new Font("Segoe UI", 9.5f),
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
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
                SelectionForeColor = Color.FromArgb(220, 225, 235),
            },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font = new Font("Consolas", 9f),
            },
            GridColor = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 28,
            RowTemplate = { Height = 24 },
        };

        // Columns. AutoGenerateColumns = false so we control the layout.
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Toggle",
            HeaderText = "",
            Width = 80,
            UseColumnTextForButtonValue = false,
            FlatStyle = FlatStyle.System,
        });
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Update",
            HeaderText = "",
            Width = 80,
            UseColumnTextForButtonValue = false,
            FlatStyle = FlatStyle.System,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Pos",
            HeaderText = "#",
            Width = 36,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Status",
            HeaderText = "",
            Width = 24,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = "Mod",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 100,
            // Don't let a narrow split-panel collapse this column —
            // the user reported the Name being invisible when Fill
            // had no minimum.
            MinimumWidth = 220,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Version",
            HeaderText = "Version",
            Width = 80,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Priority",
            HeaderText = "Prio",
            Width = 50,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "UpdateBadge",
            HeaderText = "Update",
            Width = 110,
        });
        // Mod ID intentionally not a column — it lives on the row's
        // ToolTipText (hover the Name cell) since it's rarely needed
        // visually and wastes space.

        grid.CellContentClick += async (_, e) => await OnGridCellClickedAsync(e);
        return grid;
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
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
                SelectionForeColor = Color.FromArgb(220, 225, 235),
            },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font = new Font("Consolas", 9f),
                WrapMode = DataGridViewTriState.True,
            },
            GridColor = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 28,
            RowTemplate = { Height = 24 },
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
            Height = 24,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
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

        _modsLabel.Text = $"Mods: scanning {DefaultModsDir} ...";
        if (!_registry.Scan(DefaultModsDir))
        {
            _modsLabel.Text = $"Mods: cannot read {DefaultModsDir}. " +
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
        // Sort: enabled first, by priority asc, then by filename.
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
            .OrderByDescending(e => e.IsEnabled)
            .ThenBy(e => e.IsEnabled ? e.Priority : 0)
            .ThenBy(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        _modsGrid.SuspendLayout();
        _modsGrid.Rows.Clear();
        var pos = 0;
        foreach (var e in _displayed)
        {
            var rowIdx = _modsGrid.Rows.Add();
            var row = _modsGrid.Rows[rowIdx];
            row.Cells["Toggle"].Value = e.IsEnabled ? "Disable" : "Enable";
            // Empty button-cell value renders as a blank button — the
            // user can't act on it (handler checks IsOutdated before
            // doing anything).
            row.Cells["Update"].Value = IsOutdated(e) ? "Update" : "";
            row.Cells["Pos"].Value = e.IsEnabled ? $"{++pos}" : "";
            row.Cells["Status"].Value = e.IsEnabled ? "●" : "○";
            var name = !string.IsNullOrEmpty(e.DisplayName)
                ? e.DisplayName
                : Path.GetFileName(e.Path);
            row.Cells["Name"].Value = name;
            row.Cells["Name"].ToolTipText = $"id: {e.ModId}\npath: {e.Path}";
            row.Cells["Version"].Value = e.Version;
            row.Cells["Priority"].Value = e.Priority.ToString();
            row.Cells["UpdateBadge"].Value = UpdateBadge(e);
        }
        _modsGrid.ResumeLayout();
    }

    private bool IsOutdated(ModEntry e)
    {
        var mw = e.ModWorkshopId;
        if (mw <= 0) return false;
        if (!_latestVersions.TryGetValue(mw, out var latest)) return false;
        if (string.IsNullOrEmpty(latest)) return false;
        return latest != e.Version;
    }

    private string UpdateBadge(ModEntry e)
    {
        var mw = e.ModWorkshopId;
        if (mw <= 0) return "(no MW link)";
        if (!_latestVersions.TryGetValue(mw, out var latest)) return "";
        if (string.IsNullOrEmpty(latest)) return "(unknown)";
        if (latest == e.Version) return "✓ current";
        return $"⚠ {latest} available";
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
        var dr = MessageBox.Show(this,
            $"{verb} all {targets.Count} {(enable ? "disabled" : "enabled")} mods?\n\n"
            + "Each mod's .vmz will be moved between <mods>/ and <mods>/Disabled/.",
            $"{verb} all",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (dr != DialogResult.Yes) return;

        var failed = 0;
        foreach (var e in targets)
        {
            if (!ToggleModFiles(e)) failed++;
        }
        // One rescan + one redetect at the end — far cheaper than per-mod.
        _registry.Scan(DefaultModsDir);
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
        _registry.Scan(DefaultModsDir);
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
            case "Toggle":
                ToggleMod(entry);
                break;
            case "Update":
                if (IsOutdated(entry))
                    await UpdateModAsync(entry);
                break;
        }
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
        _registry.Scan(DefaultModsDir);
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
            var disabledDir = Path.Combine(DefaultModsDir, "Disabled");
            try { Directory.CreateDirectory(disabledDir); }
            catch { return false; }
            dst = Path.Combine(disabledDir, fileName);
        }
        else
        {
            dst = Path.Combine(DefaultModsDir, fileName);
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

            _registry.Scan(DefaultModsDir);
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
            _registry.Scan(DefaultModsDir);
            UpdateModsStatus();
            PopulateModsGrid();
            _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
            UpdateConflictsStatus(_lastConflicts);
            PopulateConflictsList(_lastConflicts);
        }
    }
}
