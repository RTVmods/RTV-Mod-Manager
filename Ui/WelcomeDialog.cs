// First-run welcome modal. Offers to back up the current mods folder
// + mod_config.cfg before the manager makes any changes.
//
// Detection lives in MainForm: `!File.Exists(Settings.Path)` BEFORE
// Settings.Load() reads its defaults. The dialog is shown once at the
// end of RunStartupAsync (so the scan results are already on screen),
// and afterwards _settings.Save() persists, which means the
// settings.json materialises and the next launch sees us as non-
// first-run.
//
// Returns DialogResult.Yes when the user picked "Back up now",
// DialogResult.No otherwise (Skip, X-close, Esc). The backup itself
// is performed by MainForm — this dialog is purely the prompt.

namespace VostokModManager.Ui;

public class WelcomeDialog : Form
{
    public WelcomeDialog(string modsDir, string modConfigPath, string mcmDir)
    {
        Text            = "Welcome";
        StartPosition   = FormStartPosition.CenterScreen;
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
        MinimumSize     = new Size(560, 0);

        // ── Title ───────────────────────────────────────────────────
        var title = new Label
        {
            Text      = "★  ЯOAD TO VOSTOK MOD MAИAGEЯ  ★",
            Font      = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(225, 230, 240),
            AutoSize  = true,
            Dock      = DockStyle.Top,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin    = new Padding(0, 0, 0, 4),
        };

        var subtitle = new Label
        {
            Text      = "First-run setup",
            Font      = new Font("Consolas", 11f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 50, 60),
            AutoSize  = true,
            Dock      = DockStyle.Top,
            Margin    = new Padding(0, 0, 0, 12),
        };

        // ── Body copy ──────────────────────────────────────────────
        var body = new Label
        {
            Text =
                "Looks like this is your first time running the manager.\n"
                + "\n"
                + "Before anything is changed, would you like to take a\n"
                + "backup of your current mods folder, load order, and\n"
                + "in-game mod settings? Recommended.\n"
                + "\n"
                + "The backup is a single .zip file you can keep, copy\n"
                + "off-disk, or restore from later.\n",
            Font        = new Font("Segoe UI", 12f),
            ForeColor   = Color.FromArgb(210, 220, 235),
            Dock        = DockStyle.Top,
            AutoSize    = true,
            MaximumSize = new Size(520, 0),
            Margin      = new Padding(0, 0, 0, 10),
        };

        // ── What will be included (the actual paths) ───────────────
        var detailHeader = new Label
        {
            Text      = "★  WILL BE INCLUDED  ★",
            Font      = new Font("Consolas", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 50, 60),
            AutoSize  = true,
            Dock      = DockStyle.Top,
            Margin    = new Padding(0, 0, 0, 4),
        };

        var detail = new Label
        {
            Text =
                $"mods folder      ·  {Shorten(modsDir)}\n"
                + $"mod_config.cfg   ·  {Shorten(modConfigPath)}\n"
                + $"MCM settings     ·  {Shorten(mcmDir)}\n",
            Font        = new Font("Consolas", 10.5f),
            ForeColor   = Color.FromArgb(180, 190, 210),
            Dock        = DockStyle.Top,
            AutoSize    = true,
            MaximumSize = new Size(520, 0),
            Margin      = new Padding(4, 0, 0, 14),
        };

        // ── Button row ─────────────────────────────────────────────
        var btnRow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            BackColor     = Color.Transparent,
            Margin        = new Padding(0, 6, 0, 0),
        };

        var backup = MainForm.ThemedButton("Back up now");
        backup.Width        = 150;
        backup.Height       = 40;
        backup.AutoSize     = false;
        backup.DialogResult = DialogResult.Yes;
        btnRow.Controls.Add(backup);

        var skip = MainForm.ThemedButton("Skip");
        skip.Width        = 100;
        skip.Height       = 40;
        skip.AutoSize     = false;
        skip.DialogResult = DialogResult.No;
        btnRow.Controls.Add(skip);

        // Enter triggers backup; Esc / X-close maps to Skip — both
        // safe defaults (no destructive action without explicit click).
        AcceptButton = backup;
        CancelButton = skip;

        // Stack via reverse-add so DockStyle.Top puts the title at top
        // and the button row at the bottom.
        Controls.Add(btnRow);
        Controls.Add(detail);
        Controls.Add(detailHeader);
        Controls.Add(body);
        Controls.Add(subtitle);
        Controls.Add(title);
    }

    /// <summary>Trims overly long paths so the dialog doesn't grow
    /// past its MaximumSize. ~60 chars keeps things on one line at
    /// the dialog's typical width.</summary>
    private static string Shorten(string p)
    {
        if (string.IsNullOrEmpty(p)) return "(not set)";
        if (p.Length <= 60)         return p;
        return string.Concat("…", p.AsSpan(p.Length - 59));
    }
}
