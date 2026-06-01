// Read-only dialog for showing a mod's CHANGELOG.md (or README.md /
// CHANGES.md fallback) extracted from its .vmz. No markdown rendering
// — the text appears verbatim in a monospace box. Mod CHANGELOGs are
// typically short release-note paragraphs; raw text is honest and
// trivially readable.
//
// Triggered from the row's right-click → Show changelog… item. The
// menu disables the item when the mod doesn't ship any of the
// recognised filenames, so by the time we open we know `body` is
// non-empty.

namespace VostokModManager.Ui;

public class ChangelogDialog : Form
{
    public ChangelogDialog(string modName, string fileName, string body)
    {
        Text = $"Changelog — {modName}";
        StartPosition  = FormStartPosition.CenterParent;
        BackColor      = Color.FromArgb(26, 30, 40);
        ForeColor      = Color.FromArgb(220, 225, 235);
        Font           = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize    = new Size(640, 480);
        Width  = 820;
        Height = 640;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 3,
            Padding     = new Padding(16, 14, 16, 14),
            BackColor   = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var header = new Label
        {
            Text      = $"📜 {modName}  ·  {fileName}",
            AutoSize  = true,
            Font      = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(header, 0, 0);

        // RichTextBox so the markdown renderer can apply per-run
        // styling (bold headers, monospace code, coloured links,
        // indented bullets). MarkdownRenderer.Render does the
        // heavy lifting — this dialog just hosts the control.
        var box = new RichTextBox
        {
            Multiline   = true,
            ReadOnly    = true,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            Dock        = DockStyle.Fill,
            BackColor   = Color.FromArgb(18, 22, 30),
            ForeColor   = Color.FromArgb(220, 225, 235),
            Font        = new Font("Segoe UI", 11f),
            BorderStyle = BorderStyle.FixedSingle,
            WordWrap    = true,
            DetectUrls  = true,
        };
        MarkdownRenderer.Render(box, body);
        // Open URLs in the default browser when the user clicks
        // one — RichTextBox.DetectUrls makes them clickable; we
        // wire the click to ShellExecute.
        box.LinkClicked += (_, e) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(e.LinkText))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = e.LinkText,
                        UseShellExecute = true,
                    });
                }
            }
            catch { /* browser launch failures are rare; user can paste manually */ }
        };
        root.Controls.Add(box, 0, 1);

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            Anchor        = AnchorStyles.Right,
            BackColor     = Color.Transparent,
            Margin        = new Padding(0, 12, 0, 0),
        };
        var close = MainForm.ThemedButton("Close (Esc)");
        close.Width = 130; close.Height = 40; close.AutoSize = false;
        close.DialogResult = DialogResult.OK;
        close.Click += (_, _) => Close();
        btnRow.Controls.Add(close);
        root.Controls.Add(btnRow, 0, 2);

        AcceptButton = close;
        CancelButton = close;
    }
}
