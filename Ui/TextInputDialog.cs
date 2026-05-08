// Themed single-line text input dialog. WinForms doesn't ship one
// (Microsoft.VisualBasic.Interaction.InputBox exists but pulls a
// VB runtime dependency and looks like a 2003 Win XP modal), so we
// roll our own to match the rest of the app's dark theme.

namespace VostokModManager.Ui;

public class TextInputDialog : Form
{
    /// <summary>The text the user typed if they hit OK; empty string
    /// if they cancelled. The caller can also check ShowDialog()'s
    /// return for OK vs Cancel.</summary>
    public string Result { get; private set; } = "";

    public TextInputDialog(string title, string prompt, string initial = "")
    {
        Text = title;
        Width = 520;
        Height = 200;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16, 14, 16, 14),
            BackColor = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var promptLabel = new Label
        {
            Text = prompt,
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            Margin = new Padding(0, 0, 0, 10),
        };
        root.Controls.Add(promptLabel, 0, 0);

        var input = new TextBox
        {
            Text = initial,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 11f),
        };
        root.Controls.Add(input, 0, 1);

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 12, 0, 0),
        };
        var ok = MainForm.ThemedButton("OK");
        ok.Width = 90;
        ok.AutoSize = false;
        ok.DialogResult = DialogResult.OK;
        ok.Click += (_, _) => { Result = input.Text; Close(); };
        var cancel = MainForm.ThemedButton("Cancel");
        cancel.Width = 90;
        cancel.AutoSize = false;
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Click += (_, _) => { Result = ""; Close(); };
        // RightToLeft flow places the FIRST control rightmost — so
        // OK ends up on the right, which matches Windows convention.
        btnRow.Controls.Add(ok);
        btnRow.Controls.Add(cancel);
        root.Controls.Add(btnRow, 0, 3);

        AcceptButton = ok;
        CancelButton = cancel;
        Load += (_, _) => { input.Focus(); input.SelectAll(); };
    }

    /// <summary>Convenience: shows the dialog and returns the typed
    /// string, or null if the user cancelled.</summary>
    public static string? Prompt(IWin32Window? owner, string title, string prompt, string initial = "")
    {
        using var d = new TextInputDialog(title, prompt, initial);
        var dr = owner != null ? d.ShowDialog(owner) : d.ShowDialog();
        return dr == DialogResult.OK ? d.Result : null;
    }
}
