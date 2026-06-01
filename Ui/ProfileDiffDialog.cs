// Side-by-side diff of two profiles.
//
// Three sections in the grid, each colour-coded:
//   • Only in A           — mods present in left profile, absent in right
//   • Only in B           — mods present in right profile, absent in left
//   • Different in A vs B — same mod_id, different version / priority /
//                            enabled state
//
// Supports copying rows between the two profiles — pick rows, then
// "Copy → B" or "Copy ← A" to transplant the metadata (mod_id /
// display_name / version / priority / is_enabled / mod_workshop_id).
// For "only-in-one" rows the destination gets a brand-new ProfileMod;
// for "different" rows the destination's existing ProfileMod fields
// are overwritten with the source's values. After every copy the
// diff is rebuilt + the destination's profile.json saved.

using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ProfileDiffDialog : Form
{
    private enum DiffKind { OnlyInA, OnlyInB, Different }

    private record DiffRow(
        DiffKind Kind,
        string   ModId,
        string   DisplayName,
        string   ValueA,
        string   ValueB,
        string   What);

    private readonly ModProfile _profA;
    private readonly ModProfile _profB;
    private DataGridView _grid = null!;
    private Label        _summaryLabel = null!;
    private TextBox      _filterBox    = null!;
    private List<DiffRow> _rows = new();
    /// <summary>True when the dialog applied at least one copy.
    /// Caller (ProfileManagerDialog) checks this on close to know
    /// whether to refresh the profile list / re-read disk.</summary>
    public bool MadeChanges { get; private set; }

    public ProfileDiffDialog(ModProfile a, ModProfile b)
    {
        _profA = a;
        _profB = b;
        Text = $"Diff — '{a.Name}' ↔ '{b.Name}'";
        MinimumSize = new Size(900, 540);
        Width  = 1100;
        Height = 660;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font      = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16, 14, 16, 14),
            BackColor = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // title
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // summary
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // filter row
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // grid
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // buttons
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text      = $"Diff: '{a.Name}'  ↔  '{b.Name}'",
            AutoSize  = true,
            Font      = new Font("Segoe UI", 16f, FontStyle.Bold),
            Margin    = new Padding(0, 0, 0, 4),
        }, 0, 0);

        // Build diff rows + cache for filter / copy actions.
        _rows = BuildDiff(a, b);
        _summaryLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(170, 185, 210),
            Margin = new Padding(0, 0, 0, 10),
        };
        RefreshSummary();
        root.Controls.Add(_summaryLabel, 0, 1);

        // Filter row — case-insensitive substring match against
        // display name, mod_id, or the "What differs" text. Empty
        // filter shows everything. × clear chip + Esc-to-clear
        // matches the main form's mods toolbar filter so the
        // affordance reads the same.
        var filterRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 3, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8),
        };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterRow.Controls.Add(new Label
        {
            Text = "Filter:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(220, 225, 235),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Margin = new Padding(4, 6, 6, 0),
        }, 0, 0);
        _filterBox = new TextBox
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(30, 36, 48),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "filter by mod name, id, or change description",
            Margin = new Padding(0, 2, 6, 0),
        };
        filterRow.Controls.Add(_filterBox, 1, 0);
        var clearBtn = new Label
        {
            Text = "×",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(60, 72, 92),
            ForeColor = Color.FromArgb(120, 132, 156),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Cursor = Cursors.Hand,
            AutoSize = false,
            Width = 28,
            Height = _filterBox.PreferredHeight,
        };
        clearBtn.Click += (_, _) => { _filterBox.Clear(); _filterBox.Focus(); };
        filterRow.Controls.Add(clearBtn, 2, 0);
        root.Controls.Add(filterRow, 0, 2);

        _grid = BuildGrid(a, b);
        RepopulateGrid();
        _filterBox.TextChanged += (_, _) =>
        {
            clearBtn.ForeColor = _filterBox.Text.Length > 0
                ? Color.FromArgb(235, 240, 250)
                : Color.FromArgb(120, 132, 156);
            RepopulateGrid();
        };
        _filterBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            _filterBox.Clear();
            e.SuppressKeyPress = true;
            e.Handled = true;
        };
        root.Controls.Add(_grid, 0, 3);

        // Bottom row: copy buttons left, close button right. The
        // table is two columns so the close button stays right-
        // aligned regardless of how wide the copy-button labels
        // grow (long profile names push the text).
        var btnRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 12, 0, 0),
        };
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var copyLeft = MainForm.ThemedButton(
            $"← Copy selected from '{TruncateName(b.Name)}' to '{TruncateName(a.Name)}'");
        copyLeft.AutoSize = false;
        copyLeft.Width  = 360;
        copyLeft.Height = 40;
        copyLeft.BackColor = Color.FromArgb(45, 70, 100);
        copyLeft.ForeColor = Color.FromArgb(225, 240, 255);
        copyLeft.FlatAppearance.BorderColor = Color.FromArgb(90, 140, 200);
        copyLeft.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 90, 130);
        copyLeft.Margin = new Padding(0, 0, 8, 0);
        copyLeft.Click += (_, _) => CopySelected(fromA: false);
        btnRow.Controls.Add(copyLeft, 0, 0);

        var copyRight = MainForm.ThemedButton(
            $"Copy selected from '{TruncateName(a.Name)}' to '{TruncateName(b.Name)}' →");
        copyRight.AutoSize = false;
        copyRight.Width  = 360;
        copyRight.Height = 40;
        copyRight.BackColor = Color.FromArgb(45, 70, 100);
        copyRight.ForeColor = Color.FromArgb(225, 240, 255);
        copyRight.FlatAppearance.BorderColor = Color.FromArgb(90, 140, 200);
        copyRight.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 90, 130);
        copyRight.Margin = new Padding(0, 0, 0, 0);
        copyRight.Click += (_, _) => CopySelected(fromA: true);
        // Place copyRight to the right of the fill column — middle
        // column 1 stretches; copyRight goes in column 2.
        var copyRightWrap = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        copyRightWrap.Controls.Add(copyRight);
        btnRow.Controls.Add(copyRightWrap, 1, 0);

        var close = MainForm.ThemedButton("Close (Esc)");
        close.Width = 130; close.Height = 40; close.AutoSize = false;
        close.DialogResult = DialogResult.OK;
        close.Click += (_, _) => Close();
        btnRow.Controls.Add(close, 2, 0);
        root.Controls.Add(btnRow, 0, 4);
        AcceptButton = close;
        CancelButton = close;
    }

    private static string TruncateName(string name)
        => name.Length > 24 ? name[..23] + "…" : name;

    // ── Grid / summary rebuild ─────────────────────────────────

    private void RefreshSummary()
    {
        var onlyA = _rows.Count(r => r.Kind == DiffKind.OnlyInA);
        var onlyB = _rows.Count(r => r.Kind == DiffKind.OnlyInB);
        var diff  = _rows.Count(r => r.Kind == DiffKind.Different);
        if (_rows.Count == 0)
        {
            _summaryLabel.Text = "Profiles are identical — same mods, "
                + "same versions, same priorities, same enabled state.";
            _summaryLabel.ForeColor = Color.FromArgb(120, 200, 130);
        }
        else
        {
            _summaryLabel.Text =
                $"{onlyA} only in '{_profA.Name}'  ·  "
                + $"{onlyB} only in '{_profB.Name}'  ·  "
                + $"{diff} different — {_rows.Count} differences total.";
            _summaryLabel.ForeColor = Color.FromArgb(170, 185, 210);
        }
    }

    private void RepopulateGrid()
    {
        var f = (_filterBox?.Text ?? "").Trim().ToLowerInvariant();
        _grid.Rows.Clear();
        foreach (var r in _rows)
        {
            if (f.Length > 0)
            {
                bool hit =
                    r.DisplayName.ToLowerInvariant().Contains(f)
                    || r.ModId.ToLowerInvariant().Contains(f)
                    || r.What.ToLowerInvariant().Contains(f)
                    || r.ValueA.ToLowerInvariant().Contains(f)
                    || r.ValueB.ToLowerInvariant().Contains(f);
                if (!hit) continue;
            }
            var idx = _grid.Rows.Add();
            var row = _grid.Rows[idx];
            row.Tag = r;
            row.Cells["Kind"].Value = KindLabel(r.Kind);
            row.Cells["Kind"].Style.ForeColor = KindColor(r.Kind);
            row.Cells["ModId"].Value       = r.ModId;
            row.Cells["DisplayName"].Value = r.DisplayName;
            row.Cells["ValueA"].Value      = r.ValueA;
            row.Cells["ValueB"].Value      = r.ValueB;
            row.Cells["What"].Value        = r.What;
        }
    }

    // ── Copy logic ─────────────────────────────────────────────

    /// <summary>Copy every selected row from one profile to the
    /// other. `fromA` true → copy A → B; false → copy B → A.
    /// For "only in source" rows the destination gets a fresh
    /// ProfileMod; for "different" rows the destination's
    /// existing entry is overwritten with the source's fields.
    /// For rows that already point the wrong direction (e.g.
    /// selected an OnlyInB row when copying A→B), we skip with
    /// a note in the summary message.</summary>
    private void CopySelected(bool fromA)
    {
        var selected = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.Tag as DiffRow)
            .Where(r => r != null)
            .Cast<DiffRow>()
            .ToList();
        if (selected.Count == 0)
        {
            ThemedMessageBox.Show(this,
                "Pick at least one row in the grid first. Ctrl/Shift-"
                + "click to multi-select.",
                "Nothing to copy",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var src = fromA ? _profA : _profB;
        var dst = fromA ? _profB : _profA;
        var copied = 0;
        var skipped = 0;
        foreach (var r in selected)
        {
            // Skip rows where the source side is empty — can't
            // copy "absent" into the other profile.
            var srcAbsent = fromA
                ? r.Kind == DiffKind.OnlyInB
                : r.Kind == DiffKind.OnlyInA;
            if (srcAbsent) { skipped++; continue; }

            var srcPm = src.Mods.FirstOrDefault(m =>
                string.Equals(m.ModId, r.ModId,
                    StringComparison.OrdinalIgnoreCase));
            if (srcPm == null) { skipped++; continue; }

            var dstPm = dst.Mods.FirstOrDefault(m =>
                string.Equals(m.ModId, r.ModId,
                    StringComparison.OrdinalIgnoreCase));
            if (dstPm == null)
            {
                // Brand-new ProfileMod on the destination. No
                // ArchiveFileName — bundled archives belong to
                // the source profile's mods/ folder; the
                // destination gets a metadata-only entry that
                // resolves via library / MW download on apply.
                dst.Mods.Add(new ProfileMod
                {
                    ModId           = srcPm.ModId,
                    DisplayName     = srcPm.DisplayName,
                    Version         = srcPm.Version,
                    IsEnabled       = srcPm.IsEnabled,
                    Priority        = srcPm.Priority,
                    ModWorkshopId   = srcPm.ModWorkshopId,
                    ArchiveFileName = "",
                });
            }
            else
            {
                dstPm.DisplayName   = srcPm.DisplayName;
                dstPm.Version       = srcPm.Version;
                dstPm.IsEnabled     = srcPm.IsEnabled;
                dstPm.Priority      = srcPm.Priority;
                if (srcPm.ModWorkshopId > 0)
                    dstPm.ModWorkshopId = srcPm.ModWorkshopId;
            }
            copied++;
        }
        if (copied == 0)
        {
            ThemedMessageBox.Show(this,
                $"Nothing was copied. {skipped} row(s) skipped — "
                + "the source side was absent (can't copy what isn't there).",
                "Copy skipped",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        dst.UpdatedAt = DateTime.UtcNow;
        try { dst.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this,
                $"Saved copies to memory but couldn't write profile.json:\n\n{ex.Message}",
                "Save failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            // Fall through to refresh grid + summary — in-memory
            // state still reflects the change.
        }
        MadeChanges = true;
        // Rebuild the diff against the new state — copied rows
        // should now match (so they disappear or change Kind),
        // and the summary recomputes.
        _rows = BuildDiff(_profA, _profB);
        RefreshSummary();
        RepopulateGrid();

        var skipNote = skipped > 0
            ? $" ({skipped} skipped — source absent)"
            : "";
        var arrow = fromA ? "→" : "←";
        ThemedMessageBox.Show(this,
            $"Copied {copied} entr{(copied == 1 ? "y" : "ies")} "
            + $"'{src.Name}' {arrow} '{dst.Name}'{skipNote}.",
            "Copy done",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private DataGridView BuildGrid(ModProfile a, ModProfile b)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            // MultiSelect so the user can Ctrl/Shift-click ranges
            // and apply the copy buttons to a whole batch at once.
            MultiSelect = true,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(18, 22, 30),
            BorderStyle = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle =
            {
                BackColor = Color.FromArgb(36, 42, 54),
                ForeColor = Color.FromArgb(220, 225, 235),
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
            },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font = new Font("Consolas", 11f),
            },
            GridColor = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 34,
            RowTemplate = { Height = 28 },
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Kind", HeaderText = "Diff", Width = 90,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "DisplayName", HeaderText = "Mod",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 180,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ModId", HeaderText = "mod_id", Width = 180,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ValueA", HeaderText = TruncateHeader($"'{a.Name}'"), Width = 200,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ValueB", HeaderText = TruncateHeader($"'{b.Name}'"), Width = 200,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "What", HeaderText = "What differs", Width = 200,
        });
        foreach (DataGridViewColumn c in grid.Columns)
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
        return grid;
    }

    private static string TruncateHeader(string s) => s.Length > 22 ? s[..21] + "…" : s;

    private static List<DiffRow> BuildDiff(ModProfile a, ModProfile b)
    {
        var rows = new List<DiffRow>();
        var byA = a.Mods.ToDictionary(
            m => m.ModId, StringComparer.OrdinalIgnoreCase);
        var byB = b.Mods.ToDictionary(
            m => m.ModId, StringComparer.OrdinalIgnoreCase);

        // Only-in-A
        foreach (var pm in a.Mods)
        {
            if (string.IsNullOrEmpty(pm.ModId)) continue;
            if (byB.ContainsKey(pm.ModId)) continue;
            rows.Add(new DiffRow(
                DiffKind.OnlyInA,
                pm.ModId,
                string.IsNullOrEmpty(pm.DisplayName) ? pm.ModId : pm.DisplayName,
                FormatProfileMod(pm), "(absent)",
                "Only in left profile"));
        }
        // Only-in-B
        foreach (var pm in b.Mods)
        {
            if (string.IsNullOrEmpty(pm.ModId)) continue;
            if (byA.ContainsKey(pm.ModId)) continue;
            rows.Add(new DiffRow(
                DiffKind.OnlyInB,
                pm.ModId,
                string.IsNullOrEmpty(pm.DisplayName) ? pm.ModId : pm.DisplayName,
                "(absent)", FormatProfileMod(pm),
                "Only in right profile"));
        }
        // Different
        foreach (var pmA in a.Mods)
        {
            if (string.IsNullOrEmpty(pmA.ModId)) continue;
            if (!byB.TryGetValue(pmA.ModId, out var pmB)) continue;
            var what = new List<string>();
            if (!string.Equals(pmA.Version, pmB.Version, StringComparison.Ordinal))
                what.Add($"version {pmA.Version} → {pmB.Version}");
            if (pmA.Priority != pmB.Priority)
                what.Add($"priority {pmA.Priority} → {pmB.Priority}");
            if (pmA.IsEnabled != pmB.IsEnabled)
                what.Add(pmA.IsEnabled ? "enabled → disabled" : "disabled → enabled");
            if (what.Count == 0) continue;
            rows.Add(new DiffRow(
                DiffKind.Different,
                pmA.ModId,
                string.IsNullOrEmpty(pmA.DisplayName) ? pmA.ModId : pmA.DisplayName,
                FormatProfileMod(pmA),
                FormatProfileMod(pmB),
                string.Join(", ", what)));
        }
        // Stable ordering: OnlyInA → Different → OnlyInB, then by name.
        rows.Sort((x, y) =>
        {
            var kc = ((int)x.Kind).CompareTo((int)y.Kind);
            if (kc != 0) return kc;
            return string.Compare(x.DisplayName, y.DisplayName,
                StringComparison.OrdinalIgnoreCase);
        });
        return rows;
    }

    private static string FormatProfileMod(ProfileMod m)
    {
        var bits = new List<string>();
        if (!string.IsNullOrEmpty(m.Version)) bits.Add($"v{m.Version}");
        bits.Add($"prio {m.Priority}");
        bits.Add(m.IsEnabled ? "on" : "off");
        return string.Join("  ", bits);
    }

    private static string KindLabel(DiffKind k) => k switch
    {
        DiffKind.OnlyInA    => "← only",
        DiffKind.OnlyInB    => "only →",
        DiffKind.Different  => "↔ diff",
        _ => "?",
    };

    private static Color KindColor(DiffKind k) => k switch
    {
        DiffKind.OnlyInA    => Color.FromArgb(245, 180, 110),
        DiffKind.OnlyInB    => Color.FromArgb(170, 200, 240),
        DiffKind.Different  => Color.FromArgb(255, 200, 80),
        _ => Color.FromArgb(220, 225, 235),
    };
}

/// <summary>Small modal for picking the second profile to diff
/// against. Single-selection listbox; OK / Cancel.</summary>
public class ProfileDiffPickerDialog : Form
{
    public ModProfile? Selected { get; private set; }

    public ProfileDiffPickerDialog(string againstName, IList<ModProfile> options)
    {
        Text = $"Diff '{againstName}' against…";
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(420, 360);
        Width = 460; Height = 420;
        Padding = new Padding(16, 14, 16, 14);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 3,
            BackColor = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = $"Pick a profile to compare with '{againstName}':",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        }, 0, 0);

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            Font = new Font("Segoe UI", 12f),
            BorderStyle = BorderStyle.FixedSingle,
            SelectionMode = SelectionMode.One,
            IntegralHeight = false,
        };
        foreach (var p in options) list.Items.Add(p);
        list.DisplayMember = nameof(ModProfile.Name);
        if (options.Count > 0) list.SelectedIndex = 0;
        root.Controls.Add(list, 0, 1);

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 12, 0, 0),
        };
        var ok = MainForm.ThemedButton("OK");
        ok.Width = 100; ok.Height = 40; ok.AutoSize = false;
        ok.DialogResult = DialogResult.OK;
        ok.Click += (_, _) => { Selected = list.SelectedItem as ModProfile; Close(); };
        var cancel = MainForm.ThemedButton("Cancel");
        cancel.Width = 100; cancel.Height = 40; cancel.AutoSize = false;
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Click += (_, _) => Close();
        btnRow.Controls.Add(ok);
        btnRow.Controls.Add(cancel);
        root.Controls.Add(btnRow, 0, 2);

        AcceptButton = ok;
        CancelButton = cancel;
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItem == null) return;
            Selected = list.SelectedItem as ModProfile;
            DialogResult = DialogResult.OK;
            Close();
        };
    }
}
