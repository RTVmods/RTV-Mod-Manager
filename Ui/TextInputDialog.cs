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
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        // AutoSize so the dialog grows to fit the prompt label
        // (which can be several wrapped lines). MinimumSize keeps a
        // sensible width so the input box doesn't render as a tiny
        // strip when the prompt is short.
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(560, 0);
        Padding = new Padding(16, 14, 16, 14);

        // Stack: prompt → input → button row, all docked Top so the
        // form's AutoSize sums their heights. Add in REVERSE order
        // because Dock.Top stacks newcomers on top of earlier ones —
        // ending order from top to bottom is buttons-row added last
        // ... no wait. Dock.Top stacks first-added at top. So order:
        // promptLabel (Top, added first → at top), input (Top, added
        // second → just below), btnRow (Top, added third → bottom).
        var promptLabel = new Label
        {
            Text = prompt,
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 10),
        };

        var input = new TextBox
        {
            Text = initial,
            Dock = DockStyle.Top,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 13f),
            Margin = new Padding(0, 12, 0, 12),
        };

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 12, 0, 0),
        };
        var ok = MainForm.ThemedButton("OK");
        ok.Width = 100;
        ok.Height = 40;
        ok.AutoSize = false;
        ok.DialogResult = DialogResult.OK;
        ok.Click += (_, _) => { Result = input.Text; Close(); };
        var cancel = MainForm.ThemedButton("Cancel");
        cancel.Width = 100;
        cancel.Height = 40;
        cancel.AutoSize = false;
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Click += (_, _) => { Result = ""; Close(); };
        // RightToLeft flow places the FIRST control rightmost — so
        // OK ends up on the right, which matches Windows convention.
        btnRow.Controls.Add(ok);
        btnRow.Controls.Add(cancel);

        // Add controls in reverse stacking order — Dock.Top puts the
        // FIRST-added at the top, but we want prompt at top so add
        // it first. Subsequent Top-dock children sit below.
        Controls.Add(btnRow);    // becomes bottommost when packed below
        Controls.Add(input);
        Controls.Add(promptLabel);

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
