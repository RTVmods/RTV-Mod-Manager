// Modal popup that shows the AI conflict-resolution verdict for a
// file_overlap conflict. v1 is read-only — the user reviews Claude's
// suggestion (verdict word, reason, merged source, suggested order)
// and patches their mods themselves. Apply buttons are deferred until
// we've validated the verdict shape across enough real conflicts.

using VostokModManager.Ai;

namespace VostokModManager.Ui;

public class ResolutionDialog : Form
{
    public ResolutionDialog(ConflictResolver.Verdict v)
    {
        Text = string.IsNullOrEmpty(v.ConflictKey)
            ? "Conflict resolution"
            : $"Conflict resolution: {v.ConflictKey}";
        Width = 960;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(700, 400);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Color.Transparent,
        };
        Controls.Add(root);

        if (!v.Ok)
        {
            BuildErrorView(root, v);
        }
        else
        {
            BuildVerdictView(root, v);
        }

        var closeRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        var close = new Button
        {
            Text = "Close (Esc)",
            Width = 110,
            DialogResult = DialogResult.OK,
        };
        close.Click += (_, _) => Close();
        closeRow.Controls.Add(close);
        Controls.Add(closeRow);
        AcceptButton = close;
        CancelButton = close;
    }

    private static void BuildErrorView(TableLayoutPanel root, ConflictResolver.Verdict v)
    {
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var errLabel = new Label
        {
            Text = $"Error: {v.Error}",
            ForeColor = Color.FromArgb(245, 130, 120),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
        };
        root.Controls.Add(errLabel, 0, 0);

        var rawHeader = new Label
        {
            Text = "Claude's raw response:",
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 4),
        };
        root.Controls.Add(rawHeader, 0, 1);

        var raw = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f),
            Text = string.IsNullOrEmpty(v.RawText) ? "(no output captured)" : v.RawText,
        };
        root.Controls.Add(raw, 0, 2);
    }

    private static void BuildVerdictView(TableLayoutPanel root, ConflictResolver.Verdict v)
    {
        // Layout: verdict header + reason + (conditional body) + cost.
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // verdict
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // reason
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // body header
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // body
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // cost

        var (icon, color) = VerdictIconAndColor(v.Result);
        var header = new Label
        {
            Text = $"{icon}  {v.Result}",
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = color,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(header, 0, 0);

        var reason = new Label
        {
            Text = string.IsNullOrEmpty(v.Reason) ? "(no reason provided)" : v.Reason,
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Margin = new Padding(0, 0, 0, 12),
        };
        root.Controls.Add(reason, 0, 1);

        switch (v.Result)
        {
            case "merge_safe":
                BuildMergeSafeBody(root, v);
                break;
            case "order_resolves":
                BuildOrderResolvesBody(root, v);
                break;
            default:
                // incompatible / unknown: reason already covers it.
                root.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 2);
                root.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 3);
                break;
        }

        var cost = new Label
        {
            Text = $"Cost: ${v.CostUsd:F4}",
            ForeColor = Color.FromArgb(150, 160, 180),
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        root.Controls.Add(cost, 0, 4);
    }

    private static void BuildMergeSafeBody(TableLayoutPanel root, ConflictResolver.Verdict v)
    {
        var hdr = new Label
        {
            Text = "Proposed merged source (read-only — copy and review before applying):",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 4),
        };
        root.Controls.Add(hdr, 0, 2);

        var src = new TextBox
        {
            Text = v.MergedSource,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f),
        };
        root.Controls.Add(src, 0, 3);
    }

    private static void BuildOrderResolvesBody(TableLayoutPanel root, ConflictResolver.Verdict v)
    {
        var hdr = new Label
        {
            Text = "Suggested load order (earlier first):",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 4),
        };
        root.Controls.Add(hdr, 0, 2);

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10f),
            IntegralHeight = false,
        };
        for (var i = 0; i < v.LoadOrder.Count; i++)
            list.Items.Add($"{i + 1}.  {v.LoadOrder[i]}");
        root.Controls.Add(list, 0, 3);
    }

    private static (string icon, Color color) VerdictIconAndColor(string verdict) => verdict switch
    {
        "merge_safe" => ("✓", Color.FromArgb(120, 220, 140)),
        "order_resolves" => ("⚠", Color.FromArgb(255, 200, 80)),
        "incompatible" => ("✗", Color.FromArgb(245, 130, 120)),
        _ => ("?", Color.FromArgb(180, 190, 210)),
    };
}
