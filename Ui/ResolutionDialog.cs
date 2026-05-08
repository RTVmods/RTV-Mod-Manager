// Modal popup that shows the AI conflict-resolution verdict for a
// file_overlap conflict. v1 is read-only — the user reviews Claude's
// suggestion (verdict word, reason, merged source, suggested order)
// and patches their mods themselves. Apply buttons are deferred until
// we've validated the verdict shape across enough real conflicts.

using VostokModManager.Ai;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ResolutionDialog : Form
{
    /// <summary>True if the user clicked an Apply button and the
    /// merge was written successfully. The caller (MainForm) reads
    /// this after ShowDialog returns to decide whether to rescan.</summary>
    public bool Applied { get; private set; }

    private readonly ConflictResolver.Verdict _verdict;

    public ResolutionDialog(ConflictResolver.Verdict v)
    {
        _verdict = v;
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

    private void BuildVerdictView(TableLayoutPanel root, ConflictResolver.Verdict v)
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

    private void BuildMergeSafeBody(TableLayoutPanel root, ConflictResolver.Verdict v)
    {
        // The body cell at row 3 is the Fill row — we put the source
        // editor there. Apply buttons go in the row above (row 2)
        // alongside the "Proposed merged source" header.
        var headerRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 4),
        };
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        headerRow.Controls.Add(new Label
        {
            Text = "Proposed merged source (review, then Apply to a mod or copy out manually):",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 0, 0),
        }, 0, 0);

        // Per-mod Apply buttons. v.Mods carries display name + archive
        // path so we don't have to ferry the registry through.
        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Right,
        };
        foreach (var m in v.Mods)
        {
            var btn = new Button
            {
                Text = $"Apply to {m.DisplayName}…",
                AutoSize = true,
                Margin = new Padding(4, 0, 0, 0),
            };
            btn.Click += (_, _) => ApplyMergeToMod(m);
            btnRow.Controls.Add(btn);
        }
        headerRow.Controls.Add(btnRow, 1, 0);
        root.Controls.Add(headerRow, 0, 2);

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

    /// <summary>Confirms with the user, backs up the .vmz, replaces
    /// the contested entry with the merged source. On success sets
    /// `Applied = true` so the caller knows to rescan.</summary>
    private void ApplyMergeToMod(ConflictResolver.ConflictMod m)
    {
        if (!m.IsArchive)
        {
            MessageBox.Show(this,
                $"Apply only supports .vmz archive mods at the moment. "
                + $"`{m.ModId}` is a directory mod — edit it manually for now.",
                "Not supported",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrEmpty(_verdict.ConflictKey)
            || string.IsNullOrEmpty(_verdict.MergedSource))
        {
            MessageBox.Show(this,
                "Verdict is missing the conflict path or the merged "
                + "source — can't apply.",
                "Not enough data",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var entryName = _verdict.ConflictKey;
        var fileName = Path.GetFileName(m.ArchivePath);
        var dr = MessageBox.Show(this,
            $"Replace `{entryName}` inside `{fileName}` with Claude's merged source?\n\n"
            + "A timestamped .bak backup of the .vmz will be saved alongside the "
            + "original first, so you can roll back if anything goes wrong.\n\n"
            + "Tip: quit Road to Vostok before applying — running games sometimes "
            + "hold .vmz files open and the write will fail.",
            $"Apply to {m.DisplayName}",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (dr != DialogResult.Yes) return;

        string backup;
        try
        {
            backup = ZipPatcher.CreateBackup(m.ArchivePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Backup failed: {ex.Message}\n\nNothing was changed.",
                "Apply failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try
        {
            ZipPatcher.ReplaceEntry(m.ArchivePath, entryName, _verdict.MergedSource);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Patch failed: {ex.Message}\n\n"
                + $"Backup at: {Path.GetFileName(backup)}\n"
                + "The original may be partially modified — restore from the "
                + ".bak file if the mod misbehaves.",
                "Apply failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Applied = true;
        MessageBox.Show(this,
            $"Applied to `{m.DisplayName}`.\n\n"
            + $"Backup saved as `{Path.GetFileName(backup)}` (delete it once "
            + "you've confirmed the mod still works).",
            "Apply succeeded",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BuildOrderResolvesBody(TableLayoutPanel root, ConflictResolver.Verdict v)
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
