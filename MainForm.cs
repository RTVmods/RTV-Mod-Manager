// Top-level window. v0.3.3 — interactive mods grid: per-row Toggle
// (enable/disable, moves the .vmz to/from Disabled/) and Update
// (downloads the latest .vmz from ModWorkshop, swaps it in place).
//
// Conflicts column is still a read-only ListBox; AI Resolve buttons +
// Settings panel + resolution dialog land in the next commit.

using VostokModManager.Ai;
using VostokModManager.Api;
using VostokModManager.Domain;

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
    private ListBox _conflictsList = null!;

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
            RowCount = 6,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Color.Transparent,
        };
        for (var i = 0; i < 5; i++)
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

        _claudeLabel = NewStatus("Claude Code: detecting ...");
        root.Controls.Add(_claudeLabel, 0, 1);

        _modsLabel = NewStatus("Mods: scanning ...");
        root.Controls.Add(_modsLabel, 0, 2);

        _updatesLabel = NewStatus("Updates: —");
        root.Controls.Add(_updatesLabel, 0, 3);

        _conflictsLabel = NewStatus("Conflicts: —");
        root.Controls.Add(_conflictsLabel, 0, 4);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = Color.Transparent,
            SplitterWidth = 6,
        };

        // Left: interactive mods grid.
        _modsGrid = BuildModsGrid();
        var modsHost = WrapInPanel("Installed mods", _modsGrid);
        split.Panel1.Controls.Add(modsHost);

        // Right: read-only conflicts list (becomes a grid in the next commit).
        var conflictsHost = WrapInPanelWithList("Conflicts", out _conflictsList);
        split.Panel2.Controls.Add(conflictsHost);

        root.Controls.Add(split, 0, 5);
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
            HeaderText = "Update status",
            Width = 150,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ModId",
            HeaderText = "ID",
            Width = 220,
        });

        grid.CellContentClick += async (_, e) => await OnGridCellClickedAsync(e);
        return grid;
    }

    private static Panel WrapInPanel(string headerText, Control body)
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
        p.Controls.Add(hdr);
        return p;
    }

    private static Panel WrapInPanelWithList(string headerText, out ListBox list)
    {
        list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            Font = new Font("Consolas", 9f),
        };
        return WrapInPanel(headerText, list);
    }

    private async Task RunStartupAsync()
    {
        _claude.Detect();
        UpdateClaudeStatus();

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
        _displayed = _registry.Entries
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
            row.Cells["Update"].Value = "Update";
            ((DataGridViewButtonCell)row.Cells["Update"]).Tag =
                _busy || !IsOutdated(e) ? "disabled" : "";
            row.Cells["Pos"].Value = e.IsEnabled ? $"{++pos}" : "";
            row.Cells["Status"].Value = e.IsEnabled ? "●" : "○";
            row.Cells["Name"].Value = !string.IsNullOrEmpty(e.DisplayName)
                ? e.DisplayName
                : Path.GetFileName(e.Path);
            row.Cells["Version"].Value = e.Version;
            row.Cells["Priority"].Value = e.Priority.ToString();
            row.Cells["UpdateBadge"].Value = UpdateBadge(e);
            row.Cells["ModId"].Value = e.ModId;
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
        _conflictsList.BeginUpdate();
        _conflictsList.Items.Clear();
        foreach (var c in conflicts)
        {
            _conflictsList.Items.Add($"[{c.Type}]  {c.Key}");
            _conflictsList.Items.Add($"    mods: {string.Join(", ", c.ModIds)}");
        }
        _conflictsList.EndUpdate();
    }

    // --- async update check ---------------------------------------

    private async Task CheckUpdatesAsync()
    {
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
            _updatesLabel.Text =
                $"Updates: {outdated} outdated, {current} current, {unknown} unknown";
            PopulateModsGrid();
        }
        catch (Exception ex)
        {
            _updatesLabel.Text = $"Updates: check failed — {ex.Message}";
        }
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
        var fileName = Path.GetFileName(e.Path);
        string dst;
        if (e.IsEnabled)
        {
            var disabledDir = Path.Combine(DefaultModsDir, "Disabled");
            try { Directory.CreateDirectory(disabledDir); }
            catch (Exception ex)
            {
                ShowError("Couldn't create Disabled folder", ex);
                return;
            }
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
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't move {fileName}", ex);
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
}
