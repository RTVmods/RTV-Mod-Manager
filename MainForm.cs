// Top-level window. v0.3.2 — Claude Code detection + ModWorkshop
// update tracking now wired up.
//
// On show:
//   1. Detect claude (synchronous; just runs `claude --version`)
//   2. Scan mods folder (synchronous; ZIP read per mod)
//   3. Detect conflicts (synchronous; ~hundreds of ms for 50 mods)
//   4. Async ModWorkshop /mods/versions batch call → re-render the
//      mods list with ✓ current / ⚠ X.Y available / (no MW link)
//      status badges per row.
//
// Settings panel, AI conflict resolver, per-row Enable/Update/Resolve
// buttons come in subsequent commits.

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

    /// <summary>mod_workshop_id → latest version (populated after the
    /// /mods/versions call).</summary>
    private readonly Dictionary<int, string> _latestVersions = new();
    private List<ConflictDetector.Conflict> _lastConflicts = new();

    private Label _claudeLabel = null!;
    private Label _modsLabel = null!;
    private Label _updatesLabel = null!;
    private Label _conflictsLabel = null!;
    private ListBox _modsList = null!;
    private ListBox _conflictsList = null!;

    public MainForm()
    {
        _settings = Settings.Load();
        _claude.OverridePath = _settings.ClaudePath;
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
        split.Panel1.Controls.Add(BuildPanel("Installed mods", out _modsList));
        split.Panel2.Controls.Add(BuildPanel("Conflicts", out _conflictsList));
        root.Controls.Add(split, 0, 5);
        // Center the splitter once the form has a real size.
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

    private static Panel BuildPanel(string headerText, out ListBox list)
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
        list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            Font = new Font("Consolas", 9f),
        };
        p.Controls.Add(list);
        p.Controls.Add(hdr);
        return p;
    }

    private async Task RunStartupAsync()
    {
        // Claude detection — synchronous, fast (one --version call)
        _claude.Detect();
        UpdateClaudeStatus();

        // Scan mods + conflicts — synchronous, runs on the UI thread
        // but is fast enough (~200ms for 50 mods) that DoEvents-style
        // progress isn't necessary.
        _modsLabel.Text = $"Mods: scanning {DefaultModsDir} ...";
        if (!_registry.Scan(DefaultModsDir))
        {
            _modsLabel.Text = $"Mods: cannot read {DefaultModsDir}. " +
                "Is the game installed at the default Steam path?";
            return;
        }
        UpdateModsStatus();
        PopulateModsList();

        _conflictsLabel.Text = "Conflicts: detecting (deep analysis) ...";
        _lastConflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);

        // ModWorkshop update check — async, doesn't block UI
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

    private void PopulateModsList()
    {
        // Sort: enabled first, by priority asc, then by filename.
        // Mirrors the v0.2 sort_for_load_order comparator.
        var sorted = _registry.Entries
            .OrderByDescending(e => e.IsEnabled)
            .ThenBy(e => e.IsEnabled ? e.Priority : 0)
            .ThenBy(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        _modsList.BeginUpdate();
        _modsList.Items.Clear();
        var pos = 0;
        foreach (var e in sorted)
        {
            var posStr = e.IsEnabled ? $"[{++pos,2}]" : "  · ";
            var status = e.IsEnabled ? "●" : "○";
            var name = string.IsNullOrEmpty(e.DisplayName)
                ? Path.GetFileName(e.Path)
                : e.DisplayName;
            var badge = UpdateBadge(e);
            _modsList.Items.Add(
                $"{posStr} {status}  {name}  v{e.Version}   p={e.Priority}   {badge}   [{e.ModId}]"
            );
        }
        _modsList.EndUpdate();
    }

    private string UpdateBadge(ModEntry e)
    {
        var mw = e.ModWorkshopId;
        if (mw <= 0) return "(no MW link)";
        if (!_latestVersions.TryGetValue(mw, out var latest))
            return ""; // check not run yet
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
            // Re-render the mod list so each row gets the badge.
            PopulateModsList();
        }
        catch (Exception ex)
        {
            _updatesLabel.Text = $"Updates: check failed — {ex.Message}";
        }
    }
}
