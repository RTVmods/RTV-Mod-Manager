// Asks which profile an import (a modpack or a mod list) should land
// in: the active profile, a new profile named after the pack, or another
// saved profile. Pure prompt: MainForm creates or switches profiles and
// then runs the import into whichever is active.

using VostokModManager.Domain;

namespace VostokModManager.Ui;

public enum ImportTarget
{
    ActiveProfile,
    NewProfile,
    ExistingProfile,
}

public class ImportTargetDialog : Form
{
    private readonly ModProfile _active;
    private readonly List<ModProfile> _others;

    private RadioButton _activeRadio = null!;
    private RadioButton _newRadio = null!;
    private RadioButton _existingRadio = null!;
    private TextBox _newName = null!;
    private ComboBox _existingList = null!;
    private Button _okBtn = null!;

    public ImportTarget Choice { get; private set; } = ImportTarget.ActiveProfile;

    /// <summary>The name for the new profile (Choice == NewProfile).</summary>
    public string NewProfileName => _newName.Text.Trim();

    /// <summary>The chosen saved profile (Choice == ExistingProfile).</summary>
    public ModProfile? ExistingProfile => _existingList.SelectedItem as ModProfile;

    public ImportTargetDialog(string packName, ModProfile active, IEnumerable<ModProfile> all)
    {
        _active = active;
        _others = all
            .Where(p => !string.Equals(p.Name, active.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        InitUi(packName, all.Select(p => p.Name));
    }

    private void InitUi(string packName, IEnumerable<string> takenNames)
    {
        Text            = "Where should this go?";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        Width           = 640;
        Height          = 400;
        DialogSizing.ClampToWorkingArea(this);

        var layout = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            Padding     = new Padding(20, 16, 20, 12),
            BackColor   = Color.Transparent,
            AutoSize    = true,
        };

        var title = new Label
        {
            Text      = string.IsNullOrEmpty(packName) ? "Import mods into…" : $"Import '{packName}' into…",
            AutoSize  = true,
            Font      = new Font("Segoe UI", 15f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 12),
        };
        layout.Controls.Add(title);

        _activeRadio = Radio($"The active profile  ·  {_active.Name}", true);
        layout.Controls.Add(_activeRadio);

        _newRadio = Radio("A new profile, which becomes active:", false);
        layout.Controls.Add(_newRadio);
        _newName = new TextBox
        {
            Width       = 520,
            Margin      = new Padding(28, 2, 0, 10),
            BackColor   = Color.FromArgb(18, 22, 30),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Text        = UniqueName(string.IsNullOrWhiteSpace(packName) ? "Imported mods" : packName.Trim(), takenNames),
        };
        _newName.TextChanged += (_, _) => { _newRadio.Checked = true; UpdateOk(); };
        _newName.GotFocus += (_, _) => _newRadio.Checked = true;
        layout.Controls.Add(_newName);

        _existingRadio = Radio("Another saved profile, which becomes active:", false);
        _existingRadio.Enabled = _others.Count > 0;
        layout.Controls.Add(_existingRadio);
        _existingList = new ComboBox
        {
            Width         = 520,
            Margin        = new Padding(28, 2, 0, 10),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor     = Color.FromArgb(18, 22, 30),
            ForeColor     = Color.FromArgb(220, 225, 235),
            FlatStyle     = FlatStyle.Flat,
            DisplayMember = nameof(ModProfile.Name),
            Enabled       = _others.Count > 0,
        };
        foreach (var p in _others) _existingList.Items.Add(p);
        if (_others.Count > 0) _existingList.SelectedIndex = 0;
        _existingList.SelectedIndexChanged += (_, _) => _existingRadio.Checked = true;
        _existingList.GotFocus += (_, _) => { if (_existingRadio.Enabled) _existingRadio.Checked = true; };
        layout.Controls.Add(_existingList);

        var note = new Label
        {
            AutoSize  = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.FromArgb(170, 185, 210),
            Font      = new Font("Segoe UI", 10.5f),
            Margin    = new Padding(0, 4, 0, 0),
            Text      = "Switching profiles swaps the mods folder to that profile's mods "
                      + "first (locked mods stay), then the import adds to it. Close the "
                      + "game before switching.",
        };
        layout.Controls.Add(note);

        var buttons = new FlowLayoutPanel
        {
            Dock          = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height        = 60,
            Padding       = new Padding(16, 10, 16, 10),
            BackColor     = Color.FromArgb(22, 26, 35),
        };
        var cancel = MainForm.ThemedButton("Cancel");
        cancel.Width = 110;
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _okBtn = MainForm.ThemedButton("Continue");
        _okBtn.Width = 130;
        _okBtn.Margin = new Padding(8, 0, 0, 0);
        _okBtn.BackColor = Color.FromArgb(45, 90, 55);
        _okBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _okBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _okBtn.Click += (_, _) => Accept();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_okBtn);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = _okBtn;
        CancelButton = cancel;
        foreach (var r in new[] { _activeRadio, _newRadio, _existingRadio })
            r.CheckedChanged += (_, _) => UpdateOk();
        UpdateOk();
    }

    private RadioButton Radio(string text, bool isChecked) => new()
    {
        Text      = text,
        Checked   = isChecked,
        AutoSize  = true,
        ForeColor = Color.FromArgb(220, 225, 235),
        Margin    = new Padding(0, 4, 0, 4),
    };

    private void UpdateOk()
    {
        _okBtn.Enabled = !_newRadio.Checked || NewProfileName.Length > 0;
    }

    private void Accept()
    {
        if (_newRadio.Checked)
        {
            if (NewProfileName.Length == 0) return;
            if (_others.Concat(new[] { _active }).Any(p =>
                    string.Equals(p.Name, NewProfileName, StringComparison.OrdinalIgnoreCase)))
            {
                ThemedMessageBox.Show(this,
                    $"A profile named '{NewProfileName}' already exists. Pick another name, "
                    + "or choose it under \"Another saved profile\".",
                    "Name taken", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Choice = ImportTarget.NewProfile;
        }
        else if (_existingRadio.Checked && ExistingProfile != null)
        {
            Choice = ImportTarget.ExistingProfile;
        }
        else
        {
            Choice = ImportTarget.ActiveProfile;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>`name`, or `name (2)`, `name (3)`… when taken.</summary>
    private static string UniqueName(string name, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(name)) return name;
        for (var i = 2; i < 100; i++)
            if (!set.Contains($"{name} ({i})")) return $"{name} ({i})";
        return name + " " + DateTime.Now.ToString("yyyyMMdd-HHmm");
    }
}
