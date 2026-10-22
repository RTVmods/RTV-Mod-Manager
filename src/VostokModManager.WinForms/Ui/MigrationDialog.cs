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

    /// <summary>The loader profiles the user ticked for import, in
    /// list order. Empty when there were none to offer or the user
    /// unticked them all.</summary>
    public IReadOnlyList<string> ImportProfiles { get; private set; } = Array.Empty<string>();

    private readonly TextBox _nameBox;
    private readonly CheckedListBox? _loaderList;
    private readonly List<string> _loaderNames = new();

    /// <param name="loaderProfiles">The other profiles the mod loader
    /// keeps in mod_config.cfg, with the number of mods each lists.
    /// Offered for import, all ticked; pass none to hide the section.</param>
    public MigrationDialog(int modCount, string defaultName,
        IReadOnlyList<(string Name, int ModCount)>? loaderProfiles = null)
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
            Text      = "⌘  MOD MANAGER  ·  PROFILE MODEL  ⌘",
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

        // The loader's other profiles: each becomes a manager profile
        // too, so a set-up made in the loader's launcher carries over.
        Panel? loaderPanel = null;
        if (loaderProfiles != null && loaderProfiles.Count > 0)
        {
            loaderPanel = new Panel
            {
                Dock      = DockStyle.Top,
                AutoSize  = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin    = new Padding(0, 0, 0, 10),
            };
            _loaderList = new CheckedListBox
            {
                Dock           = DockStyle.Top,
                BackColor      = Color.FromArgb(18, 22, 30),
                ForeColor      = Color.FromArgb(220, 225, 235),
                BorderStyle    = BorderStyle.FixedSingle,
                Font           = new Font("Consolas", 11f),
                CheckOnClick   = true,
                IntegralHeight = false,
            };
            foreach (var (lname, count) in loaderProfiles)
            {
                _loaderNames.Add(lname);
                _loaderList.Items.Add(
                    $"{lname}   ({count} mod{(count == 1 ? "" : "s")})", true);
            }
            // Just tall enough for the list, scrolling past eight.
            _loaderList.Height = _loaderList.ItemHeight * Math.Min(8, loaderProfiles.Count) + 6;
            var loaderLabel = new Label
            {
                Text =
                    $"The mod loader also keeps {loaderProfiles.Count} other "
                    + $"profile{(loaderProfiles.Count == 1 ? "" : "s")} in "
                    + "mod_config.cfg. Ticked ones are imported as manager "
                    + "profiles with their enabled state and load order; mods "
                    + "they list that are not installed show as missing until "
                    + "installed or downloaded.",
                Dock        = DockStyle.Top,
                AutoSize    = true,
                MaximumSize = new Size(540, 0),
                ForeColor   = Color.FromArgb(210, 220, 235),
                Padding     = new Padding(0, 10, 0, 8),
            };
            loaderPanel.Controls.Add(_loaderList);
            loaderPanel.Controls.Add(loaderLabel);
        }

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
            if (_loaderList != null)
            {
                var picked = new List<string>();
                for (var i = 0; i < _loaderList.Items.Count; i++)
                    if (_loaderList.GetItemChecked(i)) picked.Add(_loaderNames[i]);
                ImportProfiles = picked;
            }
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
        if (loaderPanel != null) Controls.Add(loaderPanel);
        Controls.Add(nameRow);
        Controls.Add(body);
        Controls.Add(title);
    }
}
