// Surfaces the specific drift items between the live registry
// and the active profile before the user commits a sync. The
// drift status row's click handler opens this dialog; OK = "go
// ahead and save live state into the profile" (MainForm runs
// the actual write), Cancel = "I changed my mind".
//
// Read-only. Acting on individual rows isn't supported in v1 —
// sync is all-or-nothing. The grid is purely informational, so
// the user can decide whether the cumulative changes are
// intentional before they overwrite profile.json.

namespace VostokModManager.Ui;

public class DriftDetailDialog : Form
{
    public DriftDetailDialog(
        string profileName,
        IReadOnlyList<(string Name, string Description)> rows)
    {
        Text = $"Drift — live state vs '{profileName}'";
        MinimumSize = new Size(720, 420);
        Width  = 820;
        Height = 540;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 4,
            Padding = new Padding(16, 14, 16, 14),
            BackColor = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // title
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // summary
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // grid
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // buttons
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text      = "⚠ Live state drifts from active profile",
            AutoSize  = true,
            Font      = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 200, 80),
            Margin    = new Padding(0, 0, 0, 4),
        }, 0, 0);

        root.Controls.Add(new Label
        {
            Text = $"{rows.Count} change(s) below. Clicking Save Live → "
                 + $"Profile writes the current live state into "
                 + $"profile '{profileName}'. Cancel leaves both as-is.",
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = Color.FromArgb(170, 185, 210),
            Margin = new Padding(0, 0, 0, 10),
        }, 0, 1);

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
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
            },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font = new Font("Consolas", 11f),
            },
            GridColor = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 34,
            RowTemplate = { Height = 28 },
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Mod", HeaderText = "Mod", Width = 260,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "What", HeaderText = "What drifted",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 260,
        });
        foreach (DataGridViewColumn c in grid.Columns)
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
        foreach (var (name, desc) in rows)
        {
            var idx = grid.Rows.Add();
            grid.Rows[idx].Cells["Mod"].Value  = name;
            grid.Rows[idx].Cells["What"].Value = desc;
        }
        root.Controls.Add(grid, 0, 2);

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 12, 0, 0),
        };
        var ok = MainForm.ThemedButton("▶ Save Live → Profile");
        ok.Width = 200; ok.Height = 40; ok.AutoSize = false;
        ok.BackColor = Color.FromArgb(45, 90, 55);
        ok.ForeColor = Color.FromArgb(225, 240, 230);
        ok.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        ok.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 115, 70);
        ok.DialogResult = DialogResult.OK;
        ok.Click += (_, _) => Close();
        var cancel = MainForm.ThemedButton("Cancel");
        cancel.Width = 100; cancel.Height = 40; cancel.AutoSize = false;
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Click += (_, _) => Close();
        btnRow.Controls.Add(ok);
        btnRow.Controls.Add(cancel);
        root.Controls.Add(btnRow, 0, 3);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}
