// "About" modal — shows version, edition, author, and links. Reads
// the version from the loaded assembly so the csproj <Version> is the
// single source of truth; the AI_RESOLVER compile constant decides
// which edition string + extra credits to render.
//
// Soviet aesthetic stays: red star ornament, faux-Cyrillic title.

using System.Reflection;

namespace VostokModManager.Ui;

public class AboutDialog : Form
{
    public AboutDialog()
    {
        Text            = "About";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        Padding         = new Padding(20, 18, 20, 14);
        AutoSize        = true;
        AutoSizeMode    = AutoSizeMode.GrowAndShrink;
        MinimumSize     = new Size(540, 0);

        // Resolve version from assembly metadata.
        var asmVer = Assembly.GetExecutingAssembly().GetName().Version
                   ?? new Version(0, 0, 0);
        var version = $"v{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}";

#if AI_RESOLVER
        const string edition       = "AI edition";
        const string editionDetail =
            "Includes AI-driven conflict resolution powered by your "
            + "local Claude Code CLI. The manager analyses file_overlap "
            + "conflicts and suggests merges, load-order fixes, or flags "
            + "true incompatibilities.";
        const string titleText     = "ЯOAD TO VOSTOK MOD MAИAGEЯ  ·  AI";
#else
        const string edition       = "Integrated edition";
        const string editionDetail = "";
        const string titleText     = "ЯOAD TO VOSTOK MOD MAИAGEЯ  ·  IИTEGЯATED";
#endif

        // ── Decorative star + title ─────────────────────────────────
        var topRow = new TableLayoutPanel
        {
            Dock         = DockStyle.Top,
            ColumnCount  = 2,
            RowCount     = 1,
            AutoSize     = true,
            BackColor    = Color.Transparent,
            Margin       = new Padding(0, 0, 0, 8),
        };
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var star = new Panel
        {
            Width     = 56,
            Height    = 56,
            BackColor = Color.Transparent,
            Margin    = new Padding(0, 4, 14, 0),
        };
        star.Paint += (_, e) => DrawStar(
            e.Graphics, 28, 28, 24,
            // Edition-specific accent: red for AI, blue for Integrated —
            // matches the per-edition .exe icon colour treatment.
#if AI_RESOLVER
            Color.FromArgb(220, 200, 50, 60)
#else
            Color.FromArgb(220, 70, 130, 200)
#endif
        );
        topRow.Controls.Add(star, 0, 0);

        var titleStack = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount    = 3,
            AutoSize    = true,
            BackColor   = Color.Transparent,
            Anchor      = AnchorStyles.Left,
        };
        titleStack.Controls.Add(new Label
        {
            Text      = titleText,
            Font      = new Font("Segoe UI", 18f, FontStyle.Bold),
            AutoSize  = true,
            ForeColor = Color.FromArgb(225, 230, 240),
            Margin    = new Padding(0, 0, 0, 2),
        }, 0, 0);
        titleStack.Controls.Add(new Label
        {
            Text      = $"{version}  ·  {edition}",
            Font      = new Font("Consolas", 12f, FontStyle.Bold),
#if AI_RESOLVER
            ForeColor = Color.FromArgb(220, 110, 120),
#else
            ForeColor = Color.FromArgb(120, 170, 230),
#endif
            AutoSize  = true,
            Margin    = new Padding(0, 0, 0, 2),
        }, 0, 1);
        titleStack.Controls.Add(new Label
        {
            Text      = "by Joni",
            Font      = new Font("Segoe UI", 11f, FontStyle.Italic),
            ForeColor = Color.FromArgb(160, 170, 190),
            AutoSize  = true,
        }, 0, 2);
        topRow.Controls.Add(titleStack, 1, 0);

        // ── Edition description ─────────────────────────────────────
        var editionLabel = new Label
        {
            Text       = editionDetail,
            Dock       = DockStyle.Top,
            AutoSize   = true,
            MaximumSize = new Size(500, 0),
            ForeColor  = Color.FromArgb(200, 210, 225),
            Margin     = new Padding(0, 8, 0, 12),
        };

        // ── Divider ────────────────────────────────────────────────
        var divider = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 2,
            BackColor = Color.FromArgb(60, 70, 90),
            Margin    = new Padding(0, 6, 0, 10),
        };

        // ── Links section ───────────────────────────────────────────
        var linksHeader = new Label
        {
            Text      = "★  LINKS  ★",
            Font      = new Font("Consolas", 11f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 50, 60),
            AutoSize  = true,
            Dock      = DockStyle.Top,
            Margin    = new Padding(0, 0, 0, 6),
        };

        var linksPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            AutoSize      = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            BackColor     = Color.Transparent,
            Margin        = new Padding(0, 0, 0, 12),
        };
        linksPanel.Controls.Add(BuildLink(
            "Road to Vostok on ModWorkshop",
            "https://modworkshop.net/g/roadtovostok"));
        linksPanel.Controls.Add(BuildLink(
            "Road to Vostok (game site)",
            "https://www.roadtovostok.com"));
#if AI_RESOLVER
        linksPanel.Controls.Add(BuildLink(
            "Claude Code (powers AI resolve)",
            "https://docs.claude.com/en/docs/claude-code"));
#endif

        // ── Tech footer ─────────────────────────────────────────────
        var runtime = Environment.Version;
        var footer = new Label
        {
            Text      = $".NET {runtime.Major}.{runtime.Minor}  ·  WinForms  ·  "
                      + $"{Environment.OSVersion.VersionString}",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Consolas", 10f),
            ForeColor = Color.FromArgb(120, 130, 150),
            Margin    = new Padding(0, 0, 0, 8),
        };

        // ── Button row ──────────────────────────────────────────────
        var btnRow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            BackColor     = Color.Transparent,
            Margin        = new Padding(0, 8, 0, 0),
        };
        var close = MainForm.ThemedButton("Close");
        close.Width  = 110;
        close.Height = 40;
        close.AutoSize  = false;
        close.DialogResult = DialogResult.OK;
        close.Click += (_, _) => Close();
        btnRow.Controls.Add(close);
        AcceptButton = close;
        CancelButton = close;

        // Add in reverse so Top-dock children stack with first-added at top.
        Controls.Add(btnRow);
        Controls.Add(footer);
        Controls.Add(linksPanel);
        Controls.Add(linksHeader);
        Controls.Add(divider);
        // Skip the description label when there's nothing to say —
        // otherwise the empty label still claims its top/bottom Margin
        // and leaves dead vertical space between the title and the
        // divider.
        if (!string.IsNullOrEmpty(editionDetail))
            Controls.Add(editionLabel);
        Controls.Add(topRow);
    }

    /// <summary>Themed link label — opens the URL in the user's default
    /// browser via ShellExecute.</summary>
    private static LinkLabel BuildLink(string text, string url)
    {
        var lk = new LinkLabel
        {
            Text          = text,
            AutoSize      = true,
            LinkColor     = Color.FromArgb(120, 170, 240),
            ActiveLinkColor = Color.FromArgb(160, 200, 255),
            VisitedLinkColor = Color.FromArgb(120, 170, 240),
            LinkBehavior  = LinkBehavior.HoverUnderline,
            Margin        = new Padding(0, 2, 0, 2),
            BackColor     = Color.Transparent,
            Font          = new Font("Segoe UI", 12f),
        };
        lk.LinkClicked += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true,
                    });
            }
            catch { /* user has no default browser configured — give up */ }
        };
        return lk;
    }

    /// <summary>Five-pointed star polygon, same algorithm as MainForm's
    /// title star (just duplicated here so the AboutDialog has no
    /// reverse dependency on the main form). Centered at (cx, cy)
    /// with outer radius r.</summary>
    private static void DrawStar(Graphics g, int cx, int cy, int r, Color color)
    {
        var pts = new PointF[10];
        var inner = r * 0.4f;
        for (var i = 0; i < 10; i++)
        {
            var angle = -Math.PI / 2 + i * Math.PI / 5;
            var rad   = (i % 2 == 0) ? r : inner;
            pts[i] = new PointF(
                cx + (float)(rad * Math.Cos(angle)),
                cy + (float)(rad * Math.Sin(angle)));
        }
        var prev = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, pts);
        g.SmoothingMode = prev;
    }
}
