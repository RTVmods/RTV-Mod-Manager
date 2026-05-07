// Top-level window. v0.3.1 — scan + conflict detection wired up.
//
// On show, scans the default mods folder, detects conflicts, and shows
// both lists. ModWorkshop update tracking, AI conflict resolver, and
// the Settings panel come in subsequent commits.

using VostokModManager.Domain;

namespace VostokModManager;

public class MainForm : Form
{
    private const string DefaultModsDir =
        @"C:\Program Files (x86)\Steam\steamapps\common\Road to Vostok\mods";

    private readonly Settings _settings;
    private readonly ModRegistry _registry = new();

    private Label _statusLabel = null!;
    private Label _modsLabel = null!;
    private Label _conflictsLabel = null!;
    private ListBox _modsList = null!;
    private ListBox _conflictsList = null!;

    public MainForm()
    {
        _settings = Settings.Load();
        InitializeWindow();
        BuildLayout();
        Load += (_, _) => RunScan();
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
            RowCount = 5,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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

        _statusLabel = NewStatus("Settings file: " + Settings.Path);
        root.Controls.Add(_statusLabel, 0, 1);

        _modsLabel = NewStatus("Mods: scanning ...");
        root.Controls.Add(_modsLabel, 0, 2);

        _conflictsLabel = NewStatus("Conflicts: —");
        root.Controls.Add(_conflictsLabel, 0, 3);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = Color.Transparent,
            SplitterWidth = 6,
        };
        split.Panel1.Controls.Add(BuildPanel("Installed mods", out _modsList));
        split.Panel2.Controls.Add(BuildPanel("Conflicts", out _conflictsList));
        root.Controls.Add(split, 0, 4);
        // Set the splitter distance after the form has a real size, or
        // we'll get the WinForms default that ignores DesignerSerializationVisibility.
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

    private void RunScan()
    {
        _modsLabel.Text = $"Mods: scanning {DefaultModsDir} ...";
        Application.DoEvents();
        if (!_registry.Scan(DefaultModsDir))
        {
            _modsLabel.Text = $"Mods: cannot read {DefaultModsDir}. " +
                "Is the game installed at the default Steam path?";
            return;
        }
        var enabledCount = _registry.Enabled().Count;
        var total = _registry.Entries.Count;
        _modsLabel.Text =
            $"Mods: {total} found  ({enabledCount} enabled, {total - enabledCount} disabled)";
        PopulateModsList();

        _conflictsLabel.Text = "Conflicts: detecting (deep analysis) ...";
        Application.DoEvents();
        var conflicts = ConflictDetector.DetectAll(_registry.Entries);
        UpdateConflictsStatus(conflicts);
        PopulateConflictsList(conflicts);
    }

    private void PopulateModsList()
    {
        // Sort: enabled first, then by priority asc, then by filename.
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
            var status = e.IsEnabled ? "●" : "○";
            var posStr = e.IsEnabled ? $"[{++pos,2}]" : "  · ";
            var name = string.IsNullOrEmpty(e.DisplayName)
                ? Path.GetFileName(e.Path)
                : e.DisplayName;
            _modsList.Items.Add(
                $"{posStr} {status}  {name}  v{e.Version}   p={e.Priority}   [{e.ModId}]"
            );
        }
        _modsList.EndUpdate();
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

    private void PopulateConflictsList(List<ConflictDetector.Conflict> conflicts)
    {
        _conflictsList.BeginUpdate();
        _conflictsList.Items.Clear();
        foreach (var c in conflicts)
        {
            var ids = string.Join(", ", c.ModIds);
            _conflictsList.Items.Add($"[{c.Type}]  {c.Key}");
            _conflictsList.Items.Add($"    mods: {ids}");
        }
        _conflictsList.EndUpdate();
    }
}
