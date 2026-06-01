// First-launch prompt for users with an existing mods folder but no
// profile model adopted yet. Explains what the new model gives them
// + what migration actually does on disk, then accepts a profile
// name. Distinct from the older WelcomeDialog (which offers a
// backup of the whole mods folder) — that one fires before any
// profile concept exists; this one fires after the first scan,
// when we know how many mods would be captured.
//
// Three outcomes:
//   • OK / chosen name        — caller runs MigrateToProfileModel
//   • Skip for now             — caller flips Settings.MigrationDeclined
//                                so we don't re-prompt every launch
//   • X / Esc                  — same as Skip (we treat dialog
//                                close as a "not now" rather than
//                                trying to read intent from the
//                                close gesture)

namespace VostokModManager.Ui;

public class MigrationDialog : Form
{
    /// <summary>Profile name the user entered, or null when they
    /// chose Skip / closed the dialog without confirming.</summary>
    public string? ChosenName { get; private set; }

    private readonly TextBox _nameBox;

    public MigrationDialog(int modCount, string defaultName)
    {
        Text            = "Adopt the profile model?";
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
        MinimumSize     = new Size(580, 0);

        var title = new Label
        {
            Text      = "★  MOD MANAGER  ·  PROFILE MODEL  ★",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Consolas", 13f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 50, 60),
            Margin    = new Padding(0, 0, 0, 8),
        };

        var body = new Label
        {
            Text =
                $"Found {modCount} mod(s) in your mods folder.\n\n"
                + "The manager has a NEW profile model — your mods\n"
                + "folder becomes a single named profile that you can\n"
                + "clone, switch, and share. The active profile drives\n"
                + "the mods grid and mod_config.cfg; other profiles sit\n"
                + "alongside ready to swap in.\n\n"
                + "Migrating now:\n"
                + "  • Copies each .vmz into a Library folder under your\n"
                + "    mods directory (no live file is moved or deleted)\n"
                + "  • Creates a profile with the current enabled / load\n"
                + "    order state\n"
                + "  • Activates that profile so the title-row selector\n"
                + "    shows it\n\n"
                + "Cfg state and the live mods folder stay exactly as\n"
                + "they are — only the Library + profile metadata are\n"
                + "new on disk.",
            Dock        = DockStyle.Top,
            AutoSize    = true,
            MaximumSize = new Size(540, 0),
            ForeColor   = Color.FromArgb(210, 220, 235),
            Margin      = new Padding(0, 0, 0, 10),
        };

        var nameRow = new TableLayoutPanel
        {
            Dock        = DockStyle.Top,
            ColumnCount = 2,
            RowCount    = 1,
            AutoSize    = true,
            BackColor   = Color.Transparent,
            Margin      = new Padding(0, 4, 0, 10),
        };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        nameRow.Controls.Add(new Label
        {
            Text      = "Profile name:",
            AutoSize  = true,
            Anchor    = AnchorStyles.Left,
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 6, 8, 0),
        }, 0, 0);
        _nameBox = new TextBox
        {
            Text        = defaultName,
            Anchor      = AnchorStyles.Left | AnchorStyles.Right,
            BackColor   = Color.FromArgb(30, 36, 48),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Margin      = new Padding(0),
        };
        _nameBox.SelectAll();
        nameRow.Controls.Add(_nameBox, 1, 0);

        // Button row.
        var btnRow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            BackColor     = Color.Transparent,
            Margin        = new Padding(0, 6, 0, 0),
        };
        var migrate = MainForm.ThemedButton("▶ Migrate");
        migrate.Width    = 140;
        migrate.Height   = 40;
        migrate.AutoSize = false;
        migrate.BackColor = Color.FromArgb(45, 90, 55);
        migrate.ForeColor = Color.FromArgb(225, 240, 230);
        migrate.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        migrate.DialogResult = DialogResult.OK;
        migrate.Click += (_, _) =>
        {
            var name = _nameBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(name))
            {
                ThemedMessageBox.Show(this,
                    "Profile name can't be empty.",
                    "Name required",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None; // stay open
                return;
            }
            ChosenName = name;
        };
        btnRow.Controls.Add(migrate);

        var skip = MainForm.ThemedButton("Skip for now");
        skip.Width    = 140;
        skip.Height   = 40;
        skip.AutoSize = false;
        skip.DialogResult = DialogResult.Cancel;
        btnRow.Controls.Add(skip);

        AcceptButton = migrate;
        CancelButton = skip;

        // Reverse-add so Dock=Top stacks correctly.
        Controls.Add(btnRow);
        Controls.Add(nameRow);
        Controls.Add(body);
        Controls.Add(title);
    }
}
