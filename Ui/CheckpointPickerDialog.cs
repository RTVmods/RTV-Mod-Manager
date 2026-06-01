// Tiny modal for picking which checkpoint slot to restore when
// both LastLaunch and LastKnownGood snapshots exist on disk.
// Shows each slot's timestamp + the active profile name at the
// time of capture, so the user can read "yes that's the state
// I want to roll back to" before committing.
//
// One-shot: OK applies the chosen slot, Cancel does nothing.
// The actual restore is done by MainForm.DoRestore.

using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class CheckpointPickerDialog : Form
{
    public CheckpointSlot Choice { get; private set; }

    public CheckpointPickerDialog(
        CrashCheckpoint.Marker? lastLaunch,
        CrashCheckpoint.Marker? lastKnownGood)
    {
        Text = "Restore checkpoint — pick a slot";
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(520, 320);
        Width = 560; Height = 360;
        Padding = new Padding(16, 14, 16, 14);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 4,
            BackColor = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text     = "Pick which checkpoint to restore:",
            AutoSize = true,
            Font     = new Font("Segoe UI", 14f, FontStyle.Bold),
            Margin   = new Padding(0, 0, 0, 8),
        }, 0, 0);

        root.Controls.Add(BuildSlotRow(
            "▶ Last-launch",
            "Undo the change you made right before the most "
            + "recent Launch click.",
            lastLaunch,
            CheckpointSlot.LastLaunch), 0, 1);

        root.Controls.Add(BuildSlotRow(
            "▶ Last-known-good",
            "Roll back to the state from the most recent session "
            + "that ended cleanly (zero exit code, ≥ 60s).",
            lastKnownGood,
            CheckpointSlot.LastKnownGood), 0, 2);

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 12, 0, 0),
        };
        var cancel = MainForm.ThemedButton("Cancel");
        cancel.Width = 100; cancel.Height = 40; cancel.AutoSize = false;
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Click += (_, _) => Close();
        btnRow.Controls.Add(cancel);
        root.Controls.Add(btnRow, 0, 3);
        CancelButton = cancel;
    }

    /// <summary>One clickable row per slot. Big button at left
    /// labelled with the slot's name, body text on the right
    /// describing the snapshot's age and profile context.</summary>
    private Control BuildSlotRow(string title, string blurb,
        CrashCheckpoint.Marker? marker, CheckpointSlot slot)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2, RowCount = 1,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var btn = MainForm.ThemedButton(title);
        btn.Width = 170; btn.Height = 56; btn.AutoSize = false;
        btn.TextAlign = ContentAlignment.MiddleLeft;
        btn.BackColor = Color.FromArgb(45, 70, 100);
        btn.ForeColor = Color.FromArgb(225, 240, 255);
        btn.FlatAppearance.BorderColor = Color.FromArgb(90, 140, 200);
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 90, 130);
        btn.Margin = new Padding(0, 0, 12, 0);
        btn.Click += (_, _) =>
        {
            Choice = slot;
            DialogResult = DialogResult.OK;
            Close();
        };
        panel.Controls.Add(btn, 0, 0);

        var label = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            ForeColor = Color.FromArgb(170, 185, 210),
            Margin = new Padding(0, 4, 0, 0),
        };
        if (marker != null)
        {
            var age = DateTime.UtcNow - marker.CreatedAt;
            var ageStr = age.TotalMinutes < 60
                ? $"{(int)age.TotalMinutes}m ago"
                : age.TotalHours < 48
                    ? $"{(int)age.TotalHours}h ago"
                    : $"{(int)age.TotalDays}d ago";
            label.Text =
                $"Taken: {marker.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} ({ageStr}).\n"
                + $"Active profile: '{marker.ActiveProfileName}'.\n\n"
                + blurb;
        }
        else
        {
            label.Text = $"(no snapshot yet)\n\n{blurb}";
            btn.Enabled = false;
        }
        panel.Controls.Add(label, 1, 0);

        return panel;
    }
}
