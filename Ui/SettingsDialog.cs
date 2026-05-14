// Modal dialog hosting the three configurable paths (Mods folder,
// Claude Code, Game source / Decomp). Replaces the inline rows
// that used to sit above the mods/conflicts split — moving them
// here reclaims ~100px of vertical space on the main form.
//
// Design: read-modify-write on the Settings instance the caller
// passes in. Save commits + persists; Cancel discards. Caller
// handles any post-save side effects (Claude detect, registry
// rescan, banner refresh) since those need fields the dialog
// doesn't have access to (ClaudeCodeRunner, ModRegistry, etc.).

namespace VostokModManager.Ui;

public class SettingsDialog : Form
{
    private readonly Settings _settings;
    private readonly TextBox _modsBox;
    private readonly TextBox _claudeBox;
    private readonly TextBox _decompBox;

    /// <summary>Pre-fills inputs from `current` and writes back to
    /// it on Save. The dialog never calls `current.Save()` — the
    /// caller does that after applying any runtime side effects
    /// (Detect, Rescan, etc.).</summary>
    public SettingsDialog(Settings current)
    {
        _settings = current;

        Text = "Settings";
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 13f);
        FormBorderStyle = FormBorderStyle.Sizable;
        // Sized for the +2pt-bumped fonts. Path sections grew taller
        // (label/explainer wrap higher) and the button row is bigger
        // too, so the prior 380/420 sizes clipped Save/Cancel and
        // the row panels squashed the Browse buttons. Pinned the
        // minimum a bit larger than what fits today so subsequent
        // small layout tweaks don't immediately re-introduce
        // clipping.
        MinimumSize = new Size(840, 660);
        Width = 920;
        Height = 700;
        ShowInTaskbar = false;
        Padding = new Padding(18, 16, 18, 16);

        // Stack: 1-3 path rows + a help blurb + button row, in
        // reverse-add order so DockStyle.Top puts the prompt at
        // the top and buttons at the bottom.
        //
        // Lite edition: Game source (Decomp/) and Claude Code path
        // are AI-only configuration — both are hidden so the dialog
        // only shows the Mods folder. The backing _claudeBox /
        // _decompBox TextBoxes still exist (so CommitAndClose can
        // round-trip the persisted values) but never get added to
        // the form, so the user can't accidentally clear them.
        var btnRow = BuildButtonRow();
        var help = BuildHelpLabel();
#if AI_RESOLVER
        _decompBox = AddPathSection(out var decompPanel,
            "Game source (Decomp/):",
            "Path to the decompiled game source folder. Optional, but "
            + "AI conflict resolution gives better merges when it has "
            + "the original game script as context.",
            current.GameSourcePath,
            isFolder: true);
        _claudeBox = AddPathSection(out var claudePanel,
            "Claude Code path:",
            "Override path to claude.exe / claude.cmd. Leave empty to "
            + "auto-detect (npm-global install at "
            + "%APPDATA%/npm/claude.cmd usually wins).",
            current.ClaudePath,
            isFolder: false);
#else
        _decompBox = new TextBox { Text = current.GameSourcePath, Visible = false };
        _claudeBox = new TextBox { Text = current.ClaudePath, Visible = false };
#endif
        _modsBox = AddPathSection(out var modsPanel,
            "Mods folder:",
            "Where Road to Vostok looks for installed mods. Leave "
            + "empty to use the default Steam path; set this if you "
            + "moved the game to another drive.",
            current.ModsDir,
            isFolder: true);

        // The OS title-bar font is system-controlled and looks tiny
        // next to our 13pt body text. Add a big in-form title label
        // so users get a properly-sized visual heading without us
        // trying to repaint the non-client area.
        var titleLabel = new Label
        {
            Text = "Settings",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font("Segoe UI", 22f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin = new Padding(0, 0, 0, 12),
        };

        // Add in reverse so Top-dock children stack with the first-
        // added at the bottom (closest to the docked edge processed
        // last by WinForms).
        Controls.Add(btnRow);
        Controls.Add(help);
#if AI_RESOLVER
        Controls.Add(decompPanel);
        Controls.Add(claudePanel);
#endif
        Controls.Add(modsPanel);
        Controls.Add(titleLabel);
    }

    private TextBox AddPathSection(
        out Panel panel,
        string label,
        string explainer,
        string initial,
        bool isFolder)
    {
        var p = new Panel
        {
            Dock = DockStyle.Top,
            BackColor = Color.Transparent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 14),
        };
        var lbl = new Label
        {
            Text = label,
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        };
        var explain = new Label
        {
            Text = explainer,
            AutoSize = true,
            MaximumSize = new Size(740, 0),
            Dock = DockStyle.Top,
            ForeColor = Color.FromArgb(160, 170, 190),
            Margin = new Padding(0, 0, 0, 6),
        };
        var rowPanel = new Panel
        {
            Dock = DockStyle.Top,
            // 44px gives the bumped-font Browse button room to
            // render without clipping its top/bottom borders. 32
            // (the prior value) was sized for 10pt and squashed
            // the button after the font bump.
            Height = 44,
            BackColor = Color.Transparent,
        };
        var box = new TextBox
        {
            Text = initial,
            BackColor = Color.FromArgb(30, 36, 48),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 13f),
            Top = 6, Left = 0, Width = 600, Height = 32,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        var browse = MainForm.ThemedButton("Browse…");
        browse.Top = 2;
        browse.Width = 110;
        // Explicit Height — without this, the Button keeps its
        // pre-AutoSize-disabled internal size (~23px) and clips
        // the bumped 13pt text. 40px matches the dialog's Save /
        // Cancel buttons for visual consistency.
        browse.Height = 40;
        browse.AutoSize = false;
        browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        browse.Click += (_, _) => DoBrowse(box, isFolder);
        // Position browse to the right of box; resize handler keeps
        // the gap fixed when the dialog grows.
        rowPanel.Resize += (_, _) =>
        {
            browse.Left = rowPanel.Width - browse.Width;
            box.Width = rowPanel.Width - browse.Width - 8;
        };
        rowPanel.Controls.Add(box);
        rowPanel.Controls.Add(browse);

        // Reverse-add for Dock.Top stacking.
        p.Controls.Add(rowPanel);
        p.Controls.Add(explain);
        p.Controls.Add(lbl);
        panel = p;
        return box;
    }

    private static void DoBrowse(TextBox box, bool isFolder)
    {
        if (isFolder)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select folder",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
            if (dlg.ShowDialog() == DialogResult.OK)
                box.Text = dlg.SelectedPath;
        }
        else
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select file",
                Filter = "Executable / script (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*",
            };
            if (File.Exists(box.Text))
                dlg.InitialDirectory = Path.GetDirectoryName(box.Text);
            if (dlg.ShowDialog() == DialogResult.OK)
                box.Text = dlg.FileName;
        }
    }

    private Label BuildHelpLabel()
    {
        return new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(740, 0),
            ForeColor = Color.FromArgb(140, 150, 170),
            Margin = new Padding(0, 6, 0, 0),
            Text = "Save writes %APPDATA%/VostokModManager/settings.json "
                + "and re-applies paths immediately — no restart needed.",
        };
    }

    private FlowLayoutPanel BuildButtonRow()
    {
        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 48,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 12, 0, 0),
        };
        var save = MainForm.ThemedButton("Save");
        save.Width = 110;
        save.Height = 40;
        save.AutoSize = false;
        save.DialogResult = DialogResult.OK;
        save.Click += (_, _) => CommitAndClose();
        var cancel = MainForm.ThemedButton("Cancel");
        cancel.Width = 110;
        cancel.Height = 40;
        cancel.AutoSize = false;
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Click += (_, _) => Close();
        btnRow.Controls.Add(save);
        btnRow.Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        return btnRow;
    }

    private void CommitAndClose()
    {
        _settings.ModsDir = _modsBox.Text.Trim();
        _settings.ClaudePath = _claudeBox.Text.Trim();
        _settings.GameSourcePath = _decompBox.Text.Trim();
        Close();
    }
}
