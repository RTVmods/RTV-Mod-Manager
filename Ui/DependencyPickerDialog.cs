// Pick required dependencies from a checklist of installed mods.
// Beats the bare comma-separated text input — the user doesn't
// have to remember exact mod IDs, and typos are impossible because
// they only check things that exist. A small text box at the
// bottom still lets them add a not-yet-installed mod ID by hand.

using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class DependencyPickerDialog : Form
{
    /// <summary>Final selected list of mod IDs in arbitrary order.
    /// Empty when the user clicked OK with no boxes ticked
    /// (intentional clear). Caller can also check ShowDialog()'s
    /// return for OK vs Cancel.</summary>
    public List<string> Result { get; private set; } = new();

    private readonly CheckedListBox _list;
    private readonly TextBox _filter;
    private readonly TextBox _manualEntry;
    private readonly List<string> _allModIds;
    private readonly HashSet<string> _checked;

    public DependencyPickerDialog(
        ModEntry subject,
        IEnumerable<ModEntry> registry,
        IEnumerable<string> currentDeps)
    {
        Text = $"Required dependencies — {DisplayLabel(subject)}";
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(560, 480);
        Width = 600;
        Height = 560;
        ShowInTaskbar = false;
        Padding = new Padding(16, 14, 16, 14);

        _allModIds = registry
            .Select(e => e.ModId)
            .Where(id => !string.IsNullOrEmpty(id)
                         && !string.Equals(id, subject.ModId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _checked = new HashSet<string>(currentDeps, StringComparer.OrdinalIgnoreCase);
        // Carry forward any pre-existing deps that aren't currently
        // installed — they're missing on this machine but the
        // declaration should still survive a round-trip.
        foreach (var dep in _checked)
            if (!_allModIds.Contains(dep, StringComparer.OrdinalIgnoreCase))
                _allModIds.Insert(0, dep);

        // Stack: prompt → filter → list → manual-add → buttons.
        // Reverse-add for Top docking (last-added ends at top).
        var btnRow = BuildButtonRow();
        var manualRow = BuildManualEntryRow(out _manualEntry);
        var listPanel = BuildListPanel();
        _list = (CheckedListBox)((Panel)listPanel).Controls[0];
        var filterRow = BuildFilterRow(out _filter);
        var prompt = BuildPromptLabel(subject);

        Controls.Add(btnRow);
        Controls.Add(manualRow);
        Controls.Add(listPanel);
        Controls.Add(filterRow);
        Controls.Add(prompt);

        ApplyFilter("");
        _filter.TextChanged += (_, _) => ApplyFilter(_filter.Text);
        // Track checked-state across filter changes so toggling the
        // visible subset doesn't lose hidden ticks.
        _list.ItemCheck += (_, e) =>
        {
            // ItemCheck fires before the new state is applied, so
            // compute against e.NewValue.
            var id = (string)_list.Items[e.Index];
            if (e.NewValue == CheckState.Checked) _checked.Add(id);
            else _checked.Remove(id);
        };
    }

    private Label BuildPromptLabel(ModEntry subject)
    {
        return new Label
        {
            Text = $"Tick the mods that `{DisplayLabel(subject)}` requires. "
                + "Filter by ID below, or paste a not-yet-installed ID into "
                + "the manual-add box.",
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            Margin = new Padding(0, 0, 0, 10),
        };
    }

    private Panel BuildFilterRow(out TextBox filter)
    {
        var p = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.Transparent };
        var lbl = new Label { Text = "Filter:", AutoSize = true, Top = 6, Left = 0 };
        filter = new TextBox
        {
            Top = 4, Left = 60, Width = 480,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10f),
        };
        p.Controls.Add(lbl);
        p.Controls.Add(filter);
        return p;
    }

    private Panel BuildListPanel()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10f),
            CheckOnClick = true,
            IntegralHeight = false,
        };
        p.Controls.Add(list);
        return p;
    }

    private Panel BuildManualEntryRow(out TextBox box)
    {
        var p = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        var lbl = new Label
        {
            Text = "Add by ID:",
            AutoSize = true,
            Top = 8, Left = 0,
        };
        box = new TextBox
        {
            Top = 6, Left = 90, Width = 360,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10f),
            PlaceholderText = "mod_id_not_installed_yet",
        };
        var add = MainForm.ThemedButton("Add");
        add.Top = 4;
        add.Left = 460;
        add.Width = 70;
        add.AutoSize = false;
        add.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var localBox = box;
        add.Click += (_, _) => AddManualEntry(localBox.Text);
        // Pressing Enter in the textbox should also add it without
        // submitting the dialog.
        box.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddManualEntry(localBox.Text);
            }
        };
        p.Controls.Add(lbl);
        p.Controls.Add(box);
        p.Controls.Add(add);
        return p;
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
        var ok = MainForm.ThemedButton("OK");
        ok.Width = 90;
        ok.AutoSize = false;
        ok.DialogResult = DialogResult.OK;
        ok.Click += (_, _) => { Result = _checked.ToList(); Close(); };
        var cancel = MainForm.ThemedButton("Cancel");
        cancel.Width = 90;
        cancel.AutoSize = false;
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Click += (_, _) => { Result.Clear(); Close(); };
        btnRow.Controls.Add(ok);
        btnRow.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        return btnRow;
    }

    private void AddManualEntry(string raw)
    {
        var id = raw.Trim();
        if (string.IsNullOrEmpty(id)) return;
        if (!_allModIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            _allModIds.Insert(0, id);
        _checked.Add(id);
        _manualEntry.Clear();
        ApplyFilter(_filter.Text);
    }

    private void ApplyFilter(string filter)
    {
        var f = filter.Trim().ToLowerInvariant();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var id in _allModIds)
        {
            if (f.Length > 0 && !id.ToLowerInvariant().Contains(f)) continue;
            _list.Items.Add(id, _checked.Contains(id));
        }
        _list.EndUpdate();
    }

    private static string DisplayLabel(ModEntry e)
        => !string.IsNullOrEmpty(e.DisplayName)
            ? e.DisplayName
            : !string.IsNullOrEmpty(e.ModId)
                ? e.ModId
                : Path.GetFileName(e.Path);

    /// <summary>Convenience: shows the dialog and returns the new
    /// dep list, or null if the user cancelled.</summary>
    public static List<string>? Pick(
        IWin32Window owner,
        ModEntry subject,
        IEnumerable<ModEntry> registry,
        IEnumerable<string> currentDeps)
    {
        using var d = new DependencyPickerDialog(subject, registry, currentDeps);
        return d.ShowDialog(owner) == DialogResult.OK ? d.Result : null;
    }
}
