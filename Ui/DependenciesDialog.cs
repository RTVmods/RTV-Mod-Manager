// Modal that lists a mod's declared [dependencies] required/optional
// entries with each one's current state (✓ enabled / ⚠ disabled but
// installed / ✗ not installed). Read-only in v1 — fixing missing
// deps is a one-click checkbox toggle in the main grid (or a
// download for "not installed").

using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class DependenciesDialog : Form
{
    public DependenciesDialog(ModEntry subject, IReadOnlyDictionary<string, ModEntry> registry)
    {
        Text = $"Dependencies — {DisplayLabel(subject)}";
        MinimumSize = new Size(540, 320);
        Width = 620;
        Height = 460;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16, 14, 16, 14),
            BackColor = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // header
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // close
        Controls.Add(root);

        var required = subject.RequiredDependencies;
        var optional = subject.OptionalDependencies;

        var header = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 0, 0, 10),
        };
        if (required.Count == 0 && optional.Count == 0)
            header.Text =
                $"`{DisplayLabel(subject)}` doesn't declare any dependencies in "
                + "its mod.txt.\n\n"
                + "Mod authors can add a `[dependencies]` section with "
                + "`required = a, b, c` to opt in.";
        else
            header.Text =
                $"`{DisplayLabel(subject)}` declares "
                + $"{required.Count} required and {optional.Count} optional "
                + "dependency entries:";
        root.Controls.Add(header, 0, 0);

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10f),
            OwnerDraw = false,
        };
        list.Columns.Add("State", 70);
        list.Columns.Add("Mod ID", 200);
        list.Columns.Add("Kind", 80);
        list.Columns.Add("Status", 220);
        AddRows(list, required, kind: "required", registry);
        AddRows(list, optional, kind: "optional", registry);
        root.Controls.Add(list, 0, 1);

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 12, 0, 0),
        };
        var close = MainForm.ThemedButton("Close (Esc)");
        close.Width = 110;
        close.AutoSize = false;
        close.DialogResult = DialogResult.OK;
        close.Click += (_, _) => Close();
        btnRow.Controls.Add(close);
        root.Controls.Add(btnRow, 0, 2);

        AcceptButton = close;
        CancelButton = close;
    }

    private static void AddRows(
        ListView list,
        List<string> deps,
        string kind,
        IReadOnlyDictionary<string, ModEntry> registry)
    {
        foreach (var depId in deps)
        {
            string state, status;
            Color color;
            if (registry.TryGetValue(depId, out var dep))
            {
                if (dep.IsEnabled)
                {
                    state = "✓";
                    status = "Installed and enabled";
                    color = Color.FromArgb(120, 220, 140);
                }
                else
                {
                    state = "⚠";
                    status = "Installed but currently disabled";
                    color = Color.FromArgb(255, 200, 80);
                }
            }
            else
            {
                state = "✗";
                status = kind == "required"
                    ? "Not installed — required, install or disable the dependent"
                    : "Not installed (optional)";
                color = kind == "required"
                    ? Color.FromArgb(245, 130, 120)
                    : Color.FromArgb(150, 160, 180);
            }
            var row = new ListViewItem(new[] { state, depId, kind, status })
            {
                ForeColor = color,
            };
            list.Items.Add(row);
        }
    }

    private static string DisplayLabel(ModEntry e)
        => !string.IsNullOrEmpty(e.DisplayName)
            ? e.DisplayName
            : !string.IsNullOrEmpty(e.ModId)
                ? e.ModId
                : Path.GetFileName(e.Path);
}
