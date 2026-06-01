// Drop-in dark-themed replacement for System.Windows.Forms.MessageBox.
//
// WinForms' built-in MessageBox.Show wraps the Win32 task dialog, which
// honours the SYSTEM theme and ignores the form's BackColor / ForeColor
// — so a "yes/no" prompt fired from our dark UI lands as a white-on-grey
// native popup that visually breaks the rest of the manager. Rolling our
// own form keeps every modal in-theme without dragging in a third-party
// MessageBox library.
//
// API surface mirrors MessageBox.Show overloads we actually use in the
// codebase (owner / text / title / buttons / icon / defaultButton).
// Returns DialogResult so call sites switch with a literal find-and-
// replace from `MessageBox.Show` → `ThemedMessageBox.Show`.

using System.Drawing.Drawing2D;

namespace VostokModManager.Ui;

public static class ThemedMessageBox
{
    public static DialogResult Show(string text)
        => Show(null, text, "Vostok Mod Manager",
            MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);

    public static DialogResult Show(string text, string title)
        => Show(null, text, title,
            MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);

    public static DialogResult Show(string text, string title, MessageBoxButtons buttons)
        => Show(null, text, title, buttons,
            MessageBoxIcon.None, MessageBoxDefaultButton.Button1);

    public static DialogResult Show(
        string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
        => Show(null, text, title, buttons, icon, MessageBoxDefaultButton.Button1);

    public static DialogResult Show(
        IWin32Window? owner, string text, string title)
        => Show(owner, text, title,
            MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);

    public static DialogResult Show(
        IWin32Window? owner, string text, string title, MessageBoxButtons buttons)
        => Show(owner, text, title, buttons,
            MessageBoxIcon.None, MessageBoxDefaultButton.Button1);

    public static DialogResult Show(
        IWin32Window? owner, string text, string title,
        MessageBoxButtons buttons, MessageBoxIcon icon)
        => Show(owner, text, title, buttons, icon, MessageBoxDefaultButton.Button1);

    public static DialogResult Show(
        IWin32Window? owner,
        string text,
        string title,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton)
    {
        using var dlg = new ThemedMessageBoxForm(text, title, buttons, icon, defaultButton);
        return owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
    }
}

/// <summary>The actual form. Kept internal so callers only see the
/// static facade (matches MessageBox's surface area).</summary>
internal class ThemedMessageBoxForm : Form
{
    private DialogResult _result = DialogResult.Cancel;

    public ThemedMessageBoxForm(
        string text,
        string title,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton)
    {
        Text          = title;
        BackColor     = Color.FromArgb(26, 30, 40);
        ForeColor     = Color.FromArgb(220, 225, 235);
        Font          = new Font("Segoe UI", 11f);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox   = false;
        MinimizeBox   = false;
        ShowInTaskbar = false;
        // Width caps at ~640 so long error messages wrap rather than
        // stretching past the user's monitor; Height is computed from
        // the wrapped text below.
        Width = 480;

        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            RowCount    = 2,
            Padding     = new Padding(20, 18, 20, 16),
            BackColor   = Color.Transparent,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // ── Icon ────────────────────────────────────────────────
        var iconPanel = BuildIconPanel(icon);
        if (iconPanel != null) root.Controls.Add(iconPanel, 0, 0);

        // ── Body text ───────────────────────────────────────────
        // AutoSize-wrapped label. MaximumSize caps the horizontal
        // span so wrapping kicks in; the form's Height then chases
        // PreferredSize so very long messages don't get clipped.
        var body = new Label
        {
            Text        = text,
            AutoSize    = true,
            MaximumSize = new Size(540, 0),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BackColor   = Color.Transparent,
            Padding     = new Padding(12, 0, 0, 0),
        };
        root.Controls.Add(body, 1, 0);

        // ── Buttons ─────────────────────────────────────────────
        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            Anchor        = AnchorStyles.Right,
            BackColor     = Color.Transparent,
            Margin        = new Padding(0, 14, 0, 0),
        };
        root.SetColumnSpan(btnRow, 2);
        root.Controls.Add(btnRow, 0, 1);

        var btns = BuildButtons(buttons);
        // Add right-to-left so the natural reading order matches
        // MessageBox's layout (Yes / No / Cancel left-to-right →
        // Cancel / No / Yes added in that order to a RightToLeft
        // FlowLayout).
        for (int i = btns.Count - 1; i >= 0; i--)
            btnRow.Controls.Add(btns[i]);

        // Wire default + cancel
        int defaultIdx = defaultButton switch
        {
            MessageBoxDefaultButton.Button2 => 1,
            MessageBoxDefaultButton.Button3 => 2,
            _ => 0,
        };
        if (defaultIdx >= 0 && defaultIdx < btns.Count)
        {
            AcceptButton = btns[defaultIdx];
            btns[defaultIdx].Focus();
        }
        // Pick a sensible CancelButton: the rightmost button that
        // semantically means "abort" (Cancel > No > the last OK).
        var cancelBtn = btns.FirstOrDefault(b => b.Tag is DialogResult dr
                                              && dr == DialogResult.Cancel)
                     ?? btns.FirstOrDefault(b => b.Tag is DialogResult dr2
                                              && dr2 == DialogResult.No)
                     ?? btns.LastOrDefault();
        if (cancelBtn != null) CancelButton = cancelBtn;

        // Size to content once layout settles.
        Shown += (_, _) => SizeToContent(body);
    }

    private void SizeToContent(Label body)
    {
        // Add the body's preferred height + the button row + padding.
        // Width stays as configured; height grows for long messages.
        var w = Math.Max(360, body.PreferredWidth + 100);
        var h = Math.Max(150, body.PreferredHeight + 110);
        ClientSize = new Size(Math.Min(720, w), Math.Min(540, h));
        // Re-center on owner now that the size has changed.
        if (Owner is Form owner)
        {
            Location = new Point(
                owner.Left + (owner.Width  - Width)  / 2,
                owner.Top  + (owner.Height - Height) / 2);
        }
    }

    public new DialogResult ShowDialog()
    {
        base.ShowDialog();
        return _result;
    }

    public new DialogResult ShowDialog(IWin32Window owner)
    {
        base.ShowDialog(owner);
        return _result;
    }

    private List<Button> BuildButtons(MessageBoxButtons kind)
    {
        var list = new List<Button>();
        switch (kind)
        {
            case MessageBoxButtons.OK:
                list.Add(MakeBtn("OK", DialogResult.OK, primary: true));
                break;
            case MessageBoxButtons.OKCancel:
                list.Add(MakeBtn("OK",     DialogResult.OK, primary: true));
                list.Add(MakeBtn("Cancel", DialogResult.Cancel));
                break;
            case MessageBoxButtons.YesNo:
                list.Add(MakeBtn("Yes", DialogResult.Yes, primary: true));
                list.Add(MakeBtn("No",  DialogResult.No));
                break;
            case MessageBoxButtons.YesNoCancel:
                list.Add(MakeBtn("Yes",    DialogResult.Yes, primary: true));
                list.Add(MakeBtn("No",     DialogResult.No));
                list.Add(MakeBtn("Cancel", DialogResult.Cancel));
                break;
            case MessageBoxButtons.RetryCancel:
                list.Add(MakeBtn("Retry",  DialogResult.Retry, primary: true));
                list.Add(MakeBtn("Cancel", DialogResult.Cancel));
                break;
            case MessageBoxButtons.AbortRetryIgnore:
                list.Add(MakeBtn("Abort",  DialogResult.Abort));
                list.Add(MakeBtn("Retry",  DialogResult.Retry, primary: true));
                list.Add(MakeBtn("Ignore", DialogResult.Ignore));
                break;
            default:
                list.Add(MakeBtn("OK", DialogResult.OK, primary: true));
                break;
        }
        return list;
    }

    private Button MakeBtn(string label, DialogResult result, bool primary = false)
    {
        var b = new Button
        {
            Text      = label,
            Width     = 96,
            Height    = 36,
            AutoSize  = false,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI", 11f),
            ForeColor = Color.FromArgb(220, 225, 235),
            BackColor = primary
                ? Color.FromArgb(45, 90, 55)
                : Color.FromArgb(48, 56, 70),
            UseVisualStyleBackColor = false,
            Margin    = new Padding(6, 0, 0, 0),
            Tag       = result,
        };
        b.FlatAppearance.BorderColor = primary
            ? Color.FromArgb(90, 160, 100)
            : Color.FromArgb(80, 90, 110);
        b.FlatAppearance.MouseOverBackColor = primary
            ? Color.FromArgb(60, 115, 70)
            : Color.FromArgb(64, 76, 96);
        b.FlatAppearance.MouseDownBackColor = primary
            ? Color.FromArgb(35, 70, 45)
            : Color.FromArgb(38, 46, 60);
        b.Click += (_, _) =>
        {
            _result = result;
            DialogResult = result;
            Close();
        };
        return b;
    }

    /// <summary>Renders a colour-coded glyph in a 56×56 panel
    /// matching MessageBoxIcon's standard semantics. Drawn via
    /// OnPaint so we don't need bundled image assets and DPI
    /// scales the artwork cleanly.</summary>
    private static Panel? BuildIconPanel(MessageBoxIcon icon)
    {
        if (icon == MessageBoxIcon.None) return null;
        var (bg, fg, glyph) = icon switch
        {
            MessageBoxIcon.Information  => (Color.FromArgb(40, 65, 100),
                                            Color.FromArgb(170, 200, 240), "i"),
            MessageBoxIcon.Question     => (Color.FromArgb(40, 70, 60),
                                            Color.FromArgb(150, 220, 180), "?"),
            MessageBoxIcon.Warning      => (Color.FromArgb(95, 70, 30),
                                            Color.FromArgb(255, 200, 110), "!"),
            // Error / Stop / Hand share the red severity.
            MessageBoxIcon.Error        => (Color.FromArgb(100, 40, 45),
                                            Color.FromArgb(255, 140, 130), "✗"),
            _                            => (Color.FromArgb(50, 58, 74),
                                            Color.FromArgb(220, 225, 235), "•"),
        };
        var p = new Panel
        {
            Width  = 56,
            Height = 56,
            BackColor = Color.Transparent,
            Anchor    = AnchorStyles.Left | AnchorStyles.Top,
            Margin    = new Padding(0, 0, 8, 0),
        };
        p.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(bg);
            g.FillEllipse(brush, 2, 2, 52, 52);
            using var pen = new Pen(fg, 2f);
            g.DrawEllipse(pen, 2, 2, 52, 52);
            using var font = new Font("Segoe UI", 22f, FontStyle.Bold);
            using var fgBrush = new SolidBrush(fg);
            var size = g.MeasureString(glyph, font);
            g.DrawString(glyph, font, fgBrush,
                (p.Width  - size.Width)  / 2,
                (p.Height - size.Height) / 2 - 2);
        };
        return p;
    }
}
