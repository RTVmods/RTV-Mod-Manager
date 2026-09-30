// Conflict detail dialog — opened by double-clicking a row in the
// Conflicts grid.
//
// The grid flags a hook_collision at the FILE level ("two mods hook
// res://Scripts/AI.gd"), but MML chains hooks per METHOD: two mods that
// hook the same file on different methods coexist fine — only the same
// path::method is a real clash. This dialog derives the method-level
// truth from the data the detector already captured (Conflict.Details
// ["values"] = each mod's comma-separated method list, parallel to
// ModIds) and flags the methods hooked by 2+ mods.
//
// When a godot.log exists it also shows the RUNTIME-confirmed hook
// clashes for the same script (GodotLogAnalyzer's "Hook declared:"
// lines), including the load order in which the mods stacked.
//
// Non-hook conflicts get a simple generic view: each involved mod and
// its conflicting value (file path / autoload name / override path).

using System.Collections;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ConflictDetailDialog : Form
{
    private readonly ConflictDetector.Conflict _conflict;
    private readonly ModRegistry _registry;
    private readonly string _modsDir;

    public ConflictDetailDialog(
        ConflictDetector.Conflict conflict, ModRegistry registry, string modsDir)
    {
        _conflict = conflict;
        _registry = registry;
        _modsDir  = modsDir;
        InitUi();
    }

    // ── data helpers ──────────────────────────────────────────────────

    private string ModName(string modId)
    {
        var e = _registry.FindById(modId);
        return e != null && !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName : modId;
    }

    /// <summary>Per-mod manifest values parallel to ModIds, from
    /// Details["values"]. Falls back to an empty list.</summary>
    private List<string> Values()
    {
        var list = new List<string>();
        if (_conflict.Details.TryGetValue("values", out var v) && v is IEnumerable seq && v is not string)
            foreach (var item in seq) list.Add(item?.ToString() ?? "");
        return list;
    }

    /// <summary>method → mods (mod_ids) that hook it on this script.
    /// Built from the manifest method lists.</summary>
    private Dictionary<string, List<string>> MethodToMods()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var values = Values();
        for (int i = 0; i < _conflict.ModIds.Count; i++)
        {
            var modId = _conflict.ModIds[i];
            // Prefer the captured value; fall back to the live manifest.
            string raw = i < values.Count ? values[i] : "";
            if (string.IsNullOrEmpty(raw))
            {
                var e = _registry.FindById(modId);
                if (e != null && e.Hooks.TryGetValue(_conflict.Key, out var hv)) raw = hv;
            }
            foreach (var m in raw.Split(','))
            {
                var method = m.Trim();
                if (method.Length == 0) continue;
                if (!map.TryGetValue(method, out var mods)) map[method] = mods = new();
                if (!mods.Contains(modId)) mods.Add(modId);
            }
        }
        return map;
    }

    // ── UI ────────────────────────────────────────────────────────────

    private void InitUi()
    {
        var (title, subtitle) = MainForm.DescribeConflict(_conflict);
        Text            = "Conflict detail";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize     = new Size(680, 460);
        Width           = 860;
        Height          = 620;
        ShowInTaskbar   = false;

        var header = new Label
        {
            Text      = title,
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 17f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 2),
        };
        var sub = new Label
        {
            Text      = subtitle + "   ·   mods: " + string.Join(", ", _conflict.ModIds.Select(ModName)),
            Dock      = DockStyle.Top,
            AutoSize  = true,
            ForeColor = Color.FromArgb(170, 185, 210),
            Margin    = new Padding(0, 0, 0, 10),
        };

        var content = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            Padding     = new Padding(16, 12, 16, 12),
            BackColor   = Color.Transparent,
            AutoScroll  = true,
        };

        Control body = _conflict.Type == ConflictDetector.TYPE_HOOK_COLLISION
            ? BuildHookBody()
            : BuildGenericBody();

        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // header
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // sub
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // body
        content.Controls.Add(header, 0, 0);
        content.Controls.Add(sub,    0, 1);
        content.Controls.Add(body,   0, 2);

        Controls.Add(content);
        Controls.Add(BuildButtonRow());

        DialogSizing.ClampToWorkingArea(this);
    }

    private Control BuildHookBody()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.Transparent,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // legend
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));    // methods grid
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // runtime header
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));    // runtime grid

        var map = MethodToMods();
        int realCollisions = map.Count(kv => kv.Value.Count >= 2);

        panel.Controls.Add(new Label
        {
            Dock = DockStyle.Top, AutoSize = true,
            ForeColor = Color.FromArgb(200, 210, 225),
            Margin = new Padding(0, 0, 0, 6),
            Text = realCollisions > 0
                ? $"{realCollisions} method(s) are hooked by more than one mod — those are the real collisions (the rest only overlap at the file level and coexist via MML's hook chain)."
                : "These mods hook the same script but on DIFFERENT methods — they coexist via MML's hook chain. No method is hooked by more than one mod.",
        }, 0, 0);

        var grid = MakeGrid();
        grid.Columns.Add(Col("Method", "Method", 240));
        grid.Columns.Add(Col("Mods", "Mods hooking it", 360));
        grid.Columns.Add(Col("Status", "Status", 170));
        foreach (var kv in map.OrderByDescending(k => k.Value.Count)
                              .ThenBy(k => k.Key, StringComparer.Ordinal))
        {
            var collision = kv.Value.Count >= 2;
            int idx = grid.Rows.Add(
                kv.Key,
                string.Join(", ", kv.Value.Select(ModName)),
                collision ? $"⚠ COLLISION ({kv.Value.Count} mods)" : "ok — coexists");
            grid.Rows[idx].Cells["Status"].Style.ForeColor = collision
                ? Color.FromArgb(245, 130, 120)
                : Color.FromArgb(140, 165, 140);
            if (collision)
                grid.Rows[idx].Cells["Method"].Style.ForeColor = Color.FromArgb(255, 200, 80);
        }
        panel.Controls.Add(grid, 0, 1);

        // Runtime correlation (best-effort; only when a log exists).
        var (runtimeNote, runtimeGrid) = BuildRuntimeHookSection();
        panel.Controls.Add(runtimeNote, 0, 2);
        panel.Controls.Add(runtimeGrid, 0, 3);
        return panel;
    }

    private (Label note, Control grid) BuildRuntimeHookSection()
    {
        var note = new Label
        {
            Dock = DockStyle.Top, AutoSize = true,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(205, 215, 230),
            Margin = new Padding(0, 10, 0, 4),
            Text = "Runtime (last play session)",
        };
        var grid = MakeGrid();
        grid.Columns.Add(Col("RMethod", "Method", 240));
        grid.Columns.Add(Col("RMods", "Mods that hooked it (load order — last wins)", 460));

        try
        {
            var logPath = GodotLogAnalyzer.LatestLog();
            if (string.IsNullOrEmpty(logPath))
            {
                note.Text = "Runtime (last play session) — no godot.log found";
                grid.Rows.Add("(no log)", "Launch the game once to capture runtime hook data.");
                return (note, grid);
            }
            var a = GodotLogAnalyzer.Analyze(logPath);
            var clashes = a.HookClashes
                .Where(c => string.Equals(c.VanillaPath, _conflict.Key, StringComparison.Ordinal))
                .ToList();
            if (clashes.Count == 0)
            {
                note.Text = "Runtime (last play session) — no multi-mod hook on this script in the last log";
                grid.Rows.Add("(none)", "No method on this script was hooked by 2+ mods at runtime.");
            }
            else
            {
                foreach (var c in clashes)
                    grid.Rows.Add(c.Method, string.Join("  →  ", c.Mods));
            }
        }
        catch
        {
            note.Text = "Runtime (last play session) — could not read the log";
        }
        return (note, grid);
    }

    private Control BuildGenericBody()
    {
        var grid = MakeGrid();
        grid.Columns.Add(Col("Mod", "Mod", 320));
        grid.Columns.Add(Col("Value", "Conflicting value", 460));
        var values = Values();
        for (int i = 0; i < _conflict.ModIds.Count; i++)
        {
            var val = i < values.Count && !string.IsNullOrEmpty(values[i])
                ? values[i] : _conflict.Key;
            grid.Rows.Add(ModName(_conflict.ModIds[i]), val);
        }
        if (_conflict.ModIds.Count == 0)
            grid.Rows.Add(_conflict.Key, "");
        return grid;
    }

    // ── widgets ───────────────────────────────────────────────────────

    private static DataGridView MakeGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.FromArgb(18, 22, 30),
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 32,
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(36, 42, 54);
        g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(210, 220, 235);
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        g.DefaultCellStyle.BackColor = Color.FromArgb(18, 22, 30);
        g.DefaultCellStyle.ForeColor = Color.FromArgb(212, 220, 232);
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(40, 60, 90);
        g.DefaultCellStyle.SelectionForeColor = Color.FromArgb(240, 245, 250);
        g.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        return g;
    }

    private static DataGridViewTextBoxColumn Col(string name, string header, int width) => new()
    {
        Name = name, HeaderText = header, Width = width,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    private Panel BuildButtonRow()
    {
        var p = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(30, 34, 44) };
        var close = MainForm.ThemedButton("Close");
        close.Width = 100; close.Height = 40; close.AutoSize = false;
        close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        close.Click += (_, _) => Close();
        p.Resize += (_, _) => { close.Top = 8; close.Left = p.Width - close.Width - 16; };
        p.Controls.Add(close);
        AcceptButton = close;
        return p;
    }
}
