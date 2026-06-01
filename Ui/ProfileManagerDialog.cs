// Profile manager dialog — list, save, import, export, delete, and
// apply named mod loadout profiles.
//
// Layout: SplitContainer
//   Left  (240px)  — ListBox of saved profiles + [New] [Import…] [Delete]
//   Right (fill)   — Detail panel: name, description, mod list, [Export…] [Apply ▶]
//
// "Save Current" builds a ModProfile from the live registry and
// prompts for a name. "Import…" loads a JSON file from anywhere.
// "Apply" opens ProfileApplyDialog which handles downloads + cfg writes.

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ProfileManagerDialog : Form
{
    // ── Public outcome ────────────────────────────────────────────────

    /// <summary>True if any profile was Applied and the caller should
    /// rescan / repopulate the mods grid.</summary>
    public bool NeedsRescan { get; private set; }

    // ── Dependencies ──────────────────────────────────────────────────

    private readonly ModRegistry       _registry;
    private readonly ModWorkshopClient _mw;
    private readonly ModConfig         _modConfig;
    private readonly string            _modsDir;
    /// <summary>Forwarded to ProfileApplyDialog so locked mods are
    /// excluded from the apply plan. Empty when the caller has no
    /// lock-aware settings to share (e.g. unit tests).</summary>
    private readonly IReadOnlyList<string> _lockedModIds;

    // ── Data ──────────────────────────────────────────────────────────

    private List<ModProfile> _profiles = new();

    // ── Controls ──────────────────────────────────────────────────────

    private ListBox  _list     = null!;
    private Label    _detName  = null!;
    private Label    _detDesc  = null!;
    private Label    _detMeta  = null!;
    private DataGridView _detGrid = null!;
    private TextBox      _detFilter = null!;
    private Button   _applyBtn  = null!;
    private Button   _exportBtn = null!;
    private Button   _diffBtn   = null!;
    private Button   _deleteBtn = null!;
    private Panel    _detailPanel = null!;

    // ── Construction ─────────────────────────────────────────────────

    public ProfileManagerDialog(
        ModRegistry registry,
        ModWorkshopClient mw,
        ModConfig modConfig,
        string modsDir,
        IReadOnlyList<string>? lockedModIds = null)
    {
        _registry     = registry;
        _mw           = mw;
        _modConfig    = modConfig;
        _modsDir      = modsDir;
        _lockedModIds = lockedModIds ?? Array.Empty<string>();

        InitUi();
        LoadProfiles();
    }

    private void InitUi()
    {
        Text            = "Mod Profiles";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize     = new Size(880, 560);
        Width  = 1226;
        Height = 663;
        ShowInTaskbar   = false;

        // ── Main title ──────────────────────────────────────────────
        var title = new Label
        {
            Text      = "★  MOD  PROFILES  ★",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Consolas", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 50, 60),
            Margin    = new Padding(0, 0, 0, 8),
        };

        // ── SplitContainer ──────────────────────────────────────────
        // Pinned a bit past one-third so the left pane fits long profile
        // names + the three toolbar buttons (Save Current / 📂 / Delete)
        // comfortably; the right pane still gets the majority of the
        // width for the per-mod detail grid.
        //
        // SplitterDistance + Panel*MinSize must be set together AFTER
        // the SplitContainer has a real Width — assigning Panel1MinSize
        // in the initializer (while SplitterDistance is still at its
        // 50px default) throws "SplitterDistance must be between
        // Panel1MinSize and Width - Panel2MinSize". Defer the whole
        // configuration to Shown, set SplitterDistance first, then
        // apply MinSizes that won't contradict it.
        var split = new SplitContainer
        {
            Dock          = DockStyle.Fill,
            Orientation   = Orientation.Vertical,
            SplitterWidth = 6,
            BackColor     = Color.Transparent,
        };
        Shown += (_, _) =>
        {
            if (split.Width <= 100) return;
            const int want = 450;
            // Reserve at least 200px for Panel2 so the detail grid never
            // collapses; cap dist so we never hand Panel2 less than that.
            var dist = Math.Clamp(want, 25, Math.Max(25, split.Width - 200 - split.SplitterWidth));
            try { split.SplitterDistance = dist; }
            catch { /* very narrow window — leave the default */ }
            // Apply MinSizes now that SplitterDistance is in a sane spot.
            try { split.Panel1MinSize = 200; } catch { }
            try { split.Panel2MinSize = 200; } catch { }
        };

        // Left pane
        BuildLeftPane(split.Panel1);

        // Right pane
        _detailPanel = BuildDetailPanel();
        split.Panel2.Controls.Add(_detailPanel);

        // Bottom button row
        var btnRow = BuildBottomRow();

        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 3,
            Padding     = new Padding(14, 10, 14, 10),
            BackColor   = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // title
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // split
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // btn row
        root.Controls.Add(title,   0, 0);
        root.Controls.Add(split,   0, 1);
        root.Controls.Add(btnRow,  0, 2);
        Controls.Add(root);
    }

    // ── Left pane ────────────────────────────────────────────────────

    private void BuildLeftPane(SplitterPanel panel)
    {
        _list = new ListBox
        {
            Dock            = DockStyle.Fill,
            BackColor       = Color.FromArgb(18, 22, 30),
            ForeColor       = Color.FromArgb(220, 225, 235),
            Font            = new Font("Segoe UI", 12f),
            BorderStyle     = BorderStyle.FixedSingle,
            SelectionMode   = SelectionMode.One,
            IntegralHeight  = false,
            AllowDrop       = true,
        };
        _list.SelectedIndexChanged += (_, _) => ShowSelected();

        // Drop target: drag ProfileMods from the detail grid onto a
        // profile in this list to ADD them to that profile.
        // Source-profile mods stay in place — drag is copy semantics
        // (less destructive; user can explicitly remove afterward).
        _list.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(typeof(DraggedProfileMods)) != true) return;
            e.Effect = DragDropEffects.Copy;
        };
        _list.DragOver += (_, e) =>
        {
            if (e.Data?.GetDataPresent(typeof(DraggedProfileMods)) != true) return;
            // Highlight the listbox item under the cursor as the drop
            // target. ListBox.IndexFromPoint takes client coords; the
            // drag event is in screen coords so convert first. -1 (no
            // item under cursor) means "drop won't land anywhere",
            // which we show as a no-drop cursor.
            var pt = _list.PointToClient(new Point(e.X, e.Y));
            var idx = _list.IndexFromPoint(pt);
            if (idx >= 0 && idx < _profiles.Count)
            {
                if (_list.SelectedIndex != idx) _list.SelectedIndex = idx;
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        };
        _list.DragDrop += (_, e) =>
        {
            if (e.Data?.GetDataPresent(typeof(DraggedProfileMods)) != true) return;
            var payload = (DraggedProfileMods)e.Data.GetData(typeof(DraggedProfileMods))!;
            var pt = _list.PointToClient(new Point(e.X, e.Y));
            var idx = _list.IndexFromPoint(pt);
            if (idx < 0 || idx >= _profiles.Count) return;
            var target = _profiles[idx];
            // No-op when dropping onto the same profile that
            // sourced the drag — copying a profile's mods onto
            // itself accomplishes nothing.
            if (object.ReferenceEquals(target, payload.SourceProfile)) return;
            AddProfileModsToProfile(target, payload.Mods);
        };

        var toolbar = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 52,
            BackColor = Color.Transparent,
        };

        // Four buttons sized to fit the ~450px left pane: Clone /
        // New Modlist / Import / Delete. "Clone" duplicates the
        // currently-active profile under a new name (the active
        // profile is whichever one mod_config.cfg's [settings]
        // active_profile names). The button replaces the legacy
        // "Save Current" which captured the live registry state
        // directly — under the new model, the active profile IS
        // the live state, so cloning it gives you the same effect
        // plus an explicit name.
        var saveBtn = MainForm.ThemedButton("🗍 Clone");
        saveBtn.Width  = 90;
        saveBtn.Height = 40;
        saveBtn.AutoSize = false;
        var cloneTip = new ToolTip();
        cloneTip.SetToolTip(saveBtn,
            "Clone the SELECTED profile (highlighted in the list) "
            + "under a new name. The clone has the same mod list "
            + "and settings but is not activated automatically; "
            + "switch to it from the title-row Active selector.");

        var emptyBtn = MainForm.ThemedButton("✪ New Modlist…");
        emptyBtn.Width  = 130;
        emptyBtn.Height = 40;
        emptyBtn.AutoSize = false;
        var tt = new ToolTip();
        tt.SetToolTip(emptyBtn,
            "Create a profile that starts from 0 mods — or just "
            + "the locked mods. Existing mods stay in the mods "
            + "folder; ApplyProfile prompts you per-duplicate "
            + "(keep / use bundle / download new).");

        var importBtn = MainForm.ThemedButton("📂 Import…");
        importBtn.Width  = 100;
        importBtn.Height = 40;
        importBtn.AutoSize = false;

        _deleteBtn = MainForm.ThemedButton("🗑 Delete");
        _deleteBtn.Width  = 90;
        _deleteBtn.Height = 40;
        _deleteBtn.AutoSize = false;
        _deleteBtn.ForeColor = Color.FromArgb(245, 130, 120);
        _deleteBtn.Enabled   = false;

        toolbar.Resize += (_, _) =>
        {
            saveBtn.Top    = 6;
            emptyBtn.Top   = 6;
            importBtn.Top  = 6;
            _deleteBtn.Top = 6;
            saveBtn.Left    = 0;
            emptyBtn.Left   = saveBtn.Right  + 4;
            importBtn.Left  = emptyBtn.Right + 4;
            _deleteBtn.Left = importBtn.Right + 4;
        };
        toolbar.Controls.Add(saveBtn);
        toolbar.Controls.Add(emptyBtn);
        toolbar.Controls.Add(importBtn);
        toolbar.Controls.Add(_deleteBtn);

        saveBtn.Click    += (_, _) => CloneSelectedProfile();
        emptyBtn.Click   += (_, _) => SaveNewModlistProfile();
        importBtn.Click  += (_, _) => ImportProfileFromFile();
        _deleteBtn.Click += (_, _) => DeleteSelected();

        panel.Controls.Add(_list);
        panel.Controls.Add(toolbar);
    }

    // ── Detail pane ──────────────────────────────────────────────────

    private Panel BuildDetailPanel()
    {
        var p = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding   = new Padding(12, 0, 0, 0),
        };

        // Match the main form's "Installed mods" / "Conflicts" panel
        // headers — Segoe UI 14pt bold — so the dialog feels visually
        // continuous with the main window rather than landing in a
        // different typographic register.
        _detName = new Label
        {
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 4),
        };
        _detDesc = new Label
        {
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 12f),
            ForeColor = Color.FromArgb(160, 170, 190),
            Margin    = new Padding(0, 0, 0, 4),
        };
        _detMeta = new Label
        {
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 12f),
            ForeColor = Color.FromArgb(120, 140, 170),
            Margin    = new Padding(0, 0, 0, 8),
        };

        _detGrid = BuildDetailGrid();

        // Filter row — sits between the metadata block and the
        // grid. Same affordances as the main form's mods toolbar
        // filter (textbox + × clear chip + Esc-to-clear), but
        // scoped to the currently-selected profile's mod list.
        var filterRow = BuildDetailFilterRow();

        var detBtnRow = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 52,
            BackColor = Color.Transparent,
        };

        _applyBtn = MainForm.ThemedButton("▶ Apply Profile");
        _applyBtn.Width   = 150;
        _applyBtn.Height  = 40;
        _applyBtn.AutoSize = false;
        _applyBtn.BackColor = Color.FromArgb(45, 90, 55);
        _applyBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _applyBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _applyBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 115, 70);
        _applyBtn.Enabled = false;

        _exportBtn = MainForm.ThemedButton("Export…");
        _exportBtn.Width  = 100;
        _exportBtn.Height = 40;
        _exportBtn.AutoSize = false;
        _exportBtn.Enabled  = false;

        _diffBtn = MainForm.ThemedButton("Diff with…");
        _diffBtn.Width  = 120;
        _diffBtn.Height = 40;
        _diffBtn.AutoSize = false;
        _diffBtn.Enabled  = false;

        detBtnRow.Resize += (_, _) =>
        {
            _applyBtn.Top  = 6;
            _exportBtn.Top = 6;
            _diffBtn.Top   = 6;
            _applyBtn.Left  = 0;
            _exportBtn.Left = _applyBtn.Right + 8;
            _diffBtn.Left   = _exportBtn.Right + 8;
        };
        _applyBtn.Click  += (_, _) => ApplySelected();
        _exportBtn.Click += (_, _) => ExportSelected();
        _diffBtn.Click   += (_, _) => DiffSelected();

        detBtnRow.Controls.Add(_applyBtn);
        detBtnRow.Controls.Add(_exportBtn);
        detBtnRow.Controls.Add(_diffBtn);

        // Reverse-add for Dock.Top stacking — first-added Top is
        // CLOSEST to the body (just above _detGrid here); the
        // last-added Top sits at the very top of the panel. Filter
        // row goes in just before _detMeta so the visual order is:
        // name → desc → meta → filter → grid.
        p.Controls.Add(detBtnRow);
        p.Controls.Add(_detGrid);
        p.Controls.Add(filterRow);
        p.Controls.Add(_detMeta);
        p.Controls.Add(_detDesc);
        p.Controls.Add(_detName);
        return p;
    }

    /// <summary>Builds the detail-pane filter row — `Filter:` label
    /// + textbox + × clear chip. Mirrors the main form's pattern:
    /// always-visible × button (dim when empty, bright when there's
    /// text to clear) and Esc-inside-filter clears without bubbling.
    /// Wires TextChanged → ShowSelected so the grid filters live.</summary>
    private Panel BuildDetailFilterRow()
    {
        var row = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 32,
            BackColor = Color.Transparent,
            Margin    = new Padding(0, 0, 0, 6),
        };
        var label = new Label
        {
            Text      = "Filter:",
            AutoSize  = true,
            ForeColor = Color.FromArgb(220, 225, 235),
            Font      = new Font("Segoe UI", 12f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Location  = new Point(0, 6),
        };
        _detFilter = new TextBox
        {
            BackColor   = Color.FromArgb(30, 36, 48),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font        = new Font("Segoe UI", 12f),
            PlaceholderText = "filter by name, id, or version",
            Anchor      = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        };
        var fgActive = Color.FromArgb(235, 240, 250);
        var fgDim    = Color.FromArgb(120, 132, 156);
        var clearBtn = new Label
        {
            Text        = "×",
            Font        = new Font("Segoe UI", 12f, FontStyle.Bold),
            TextAlign   = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor   = Color.FromArgb(60, 72, 92),
            ForeColor   = fgDim,
            Cursor      = Cursors.Hand,
            AutoSize    = false,
            Anchor      = AnchorStyles.Right | AnchorStyles.Top,
        };
        clearBtn.Click += (_, _) => { _detFilter.Clear(); _detFilter.Focus(); };
        clearBtn.MouseEnter += (_, _) => clearBtn.BackColor = Color.FromArgb(85, 100, 130);
        clearBtn.MouseLeave += (_, _) => clearBtn.BackColor = Color.FromArgb(60, 72, 92);

        // Layout: Filter: [textbox fills] [× 26px]
        // Recompute on resize so the textbox stretches with the panel.
        row.Resize += (_, _) =>
        {
            label.Location    = new Point(0, 6);
            const int xWidth  = 26;
            const int xGap    = 6;
            const int labelGap = 8;
            var labelRight    = label.Right + labelGap;
            clearBtn.Size     = new Size(xWidth, _detFilter.PreferredHeight);
            clearBtn.Location = new Point(row.Width - xWidth, 4);
            _detFilter.Size   = new Size(
                row.Width - labelRight - xWidth - xGap,
                _detFilter.PreferredHeight);
            _detFilter.Location = new Point(labelRight, 4);
        };

        _detFilter.TextChanged += (_, _) =>
        {
            clearBtn.ForeColor = _detFilter.Text.Length > 0 ? fgActive : fgDim;
            // Re-render the grid with the new filter.
            ShowSelected();
        };
        _detFilter.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            _detFilter.Clear();
            e.SuppressKeyPress = true;
            e.Handled = true;
        };

        row.Controls.Add(label);
        row.Controls.Add(_detFilter);
        row.Controls.Add(clearBtn);
        return row;
    }

    private DataGridView BuildDetailGrid()
    {
        // Mirror the main form's mods grid styling beat-for-beat —
        // 12pt Consolas cells, 12pt bold Segoe UI headers, 36px header,
        // 32px rows, identical selection / grid colours — so the
        // profile detail and the main list look like the same widget.
        // ReadOnly = false because the "On" checkbox is editable (toggles
        // the per-mod enabled flag inside the profile). Other columns
        // are individually marked ReadOnly = true.
        var grid = new DataGridView
        {
            Dock  = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows  = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly     = false,
            // Multi-select so the user can ctrl-click / shift-click a
            // batch of mods and remove them via the context menu in a
            // single action. The right-click handler below uses the
            // current selection rather than the click-target row when
            // the click lands on an already-selected row, so a
            // multi-row selection isn't accidentally collapsed to one.
            MultiSelect   = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(18, 22, 30),
            BorderStyle     = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle =
            {
                BackColor  = Color.FromArgb(36, 42, 54),
                ForeColor  = Color.FromArgb(220, 225, 235),
                Font       = new Font("Segoe UI", 12f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
                SelectionForeColor = Color.FromArgb(220, 225, 235),
            },
            DefaultCellStyle =
            {
                BackColor  = Color.FromArgb(18, 22, 30),
                ForeColor  = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font       = new Font("Consolas", 12f),
            },
            GridColor           = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 36,
            RowTemplate         = { Height = 32 },
        };

        // Editable checkbox — clicking toggles the mod's IsEnabled flag
        // INSIDE the profile (not in the live mods folder). Persists
        // immediately via SaveMetadataOnly so the profile.json on disk
        // stays in sync.
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name       = "On",
            HeaderText = "On",
            Width      = 50,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name         = "ModName",
            HeaderText   = "Mod",
            ReadOnly     = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 220,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "Version",
            HeaderText = "Version",
            Width      = 80,
            ReadOnly   = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        // Match the main grid's bumped, centered, bold load-order column
        // so the numbers read at a glance the same way they do there.
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "Priority",
            HeaderText = "LoadOrd",
            Width      = 100,
            ReadOnly   = true,
            DefaultCellStyle =
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font      = new Font("Consolas", 14f, FontStyle.Bold),
            },
        });

        foreach (DataGridViewColumn col in grid.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;

        // Commit checkbox edits the instant the cell value changes,
        // rather than waiting for focus to leave the cell — without
        // this the IsEnabled flag would lag a click behind reality.
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, e) => OnProfileCellEdited(e.RowIndex, e.ColumnIndex);

        // Right-click → context menu with "Remove from profile (N)".
        // Works on the current MULTI-row selection rather than just
        // the click-target — ctrl/shift-select a batch then
        // right-click to bulk-remove. MouseDown rule: if the click
        // hits a row that's NOT already selected, replace the
        // selection with that one row (matches WinForms convention
        // and avoids the surprise of "I right-clicked a different
        // row but the action ran on my old selection"). Clicks on a
        // row that's IN the current selection keep the whole
        // multi-row selection.
        var ctx = new ContextMenuStrip();
        grid.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = grid.HitTest(e.X, e.Y);
            if (hit.RowIndex < 0 || hit.RowIndex >= grid.Rows.Count) return;
            if (!grid.Rows[hit.RowIndex].Selected)
            {
                grid.ClearSelection();
                grid.Rows[hit.RowIndex].Selected = true;
            }
        };
        ctx.Opening += (_, e) =>
        {
            var selectedRows = grid.SelectedRows
                .Cast<DataGridViewRow>()
                .Where(r => r.Tag is ProfileMod)
                .ToList();
            if (selectedRows.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            ctx.Items.Clear();
            var label = selectedRows.Count == 1
                ? "Remove from profile"
                : $"Remove from profile ({selectedRows.Count})";
            var item = new ToolStripMenuItem(label);
            // Capture the snapshot of selected ProfileMods here so
            // a later refresh / re-sort can't shift the indices out
            // from under the click handler.
            var pmsToRemove = selectedRows
                .Select(r => (ProfileMod)r.Tag!)
                .ToList();
            item.Click += (_, _) => RemoveModsFromProfile(pmsToRemove);
            ctx.Items.Add(item);
        };
        grid.ContextMenuStrip = ctx;

        // Drag SOURCE — let the user pick up one or more mods and
        // drop them onto another profile in the left-hand list to
        // copy them across. Uses a manual mouse-down/move threshold
        // (default 4-pixel) so a normal click-to-select doesn't
        // start a drag; only deliberate drags do. The dragged
        // payload is a DraggedProfileMods snapshot containing the
        // source profile (so the drop target can no-op when it's
        // the same profile) plus the captured ProfileMod refs.
        var dragOrigin = Point.Empty;
        var dragArmed  = false;
        grid.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var hit = grid.HitTest(e.X, e.Y);
            if (hit.RowIndex < 0 || hit.RowIndex >= grid.Rows.Count) return;
            // Don't arm a drag from a click on the editable "On"
            // checkbox — that's a toggle, not a drag handle.
            if (hit.ColumnIndex >= 0
                && grid.Columns[hit.ColumnIndex].Name == "On") return;
            dragOrigin = new Point(e.X, e.Y);
            dragArmed  = true;
        };
        grid.MouseUp += (_, _) => dragArmed = false;
        grid.MouseMove += (_, e) =>
        {
            if (!dragArmed) return;
            if ((e.Button & MouseButtons.Left) == 0) { dragArmed = false; return; }
            // 4-pixel deadzone matches WinForms' default
            // SystemInformation.DragSize. Suppresses accidental
            // drags from minor mouse jitter during a click.
            var dx = Math.Abs(e.X - dragOrigin.X);
            var dy = Math.Abs(e.Y - dragOrigin.Y);
            if (dx + dy < SystemInformation.DragSize.Width) return;
            dragArmed = false;

            // Gather the current selection as the drag payload.
            // Multi-row selections drag the whole set; a single-
            // row drag picks up just that row.
            var pmsToDrag = grid.SelectedRows
                .Cast<DataGridViewRow>()
                .Where(r => r.Tag is ProfileMod)
                .Select(r => (ProfileMod)r.Tag!)
                .ToList();
            if (pmsToDrag.Count == 0) return;
            // Snapshot the source profile too — the drop handler
            // uses it to no-op when the user drags onto the same
            // profile (no point copying mods to themselves).
            var sourceProfile = _list.SelectedIndex >= 0
                                 && _list.SelectedIndex < _profiles.Count
                ? _profiles[_list.SelectedIndex]
                : null;
            var payload = new DraggedProfileMods(sourceProfile, pmsToDrag);
            grid.DoDragDrop(payload, DragDropEffects.Copy);
        };

        return grid;
    }

    /// <summary>Drag-drop payload carried from the detail grid to
    /// the profile list. SourceProfile may be null in pathological
    /// cases (no selected profile when the drag starts); the drop
    /// handler treats null as "different profile" and proceeds.
    /// </summary>
    private sealed record DraggedProfileMods(
        ModProfile? SourceProfile,
        List<ProfileMod> Mods);

    /// <summary>Append `mods` to `target.Mods`, deduping by mod_id
    /// (case-insensitive) so dragging an already-present mod is a
    /// no-op rather than a duplicate. Each added entry is a NEW
    /// ProfileMod with the same fields — references aren't shared
    /// between profiles so toggling enabled-state in one profile
    /// can't leak across to another. Persists with
    /// SaveMetadataOnly + refreshes the listbox row count so the
    /// "(N mods)" label updates immediately if the target profile
    /// is currently selected.</summary>
    private void AddProfileModsToProfile(
        ModProfile target,
        IReadOnlyList<ProfileMod> mods)
    {
        if (target == null || mods == null || mods.Count == 0) return;
        var existingIds = new HashSet<string>(
            target.Mods.Select(m => m.ModId),
            StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;
        foreach (var src in mods)
        {
            if (string.IsNullOrEmpty(src.ModId)) { skipped++; continue; }
            if (existingIds.Contains(src.ModId))  { skipped++; continue; }
            target.Mods.Add(new ProfileMod
            {
                ModId           = src.ModId,
                DisplayName     = src.DisplayName,
                Version         = src.Version,
                IsEnabled       = src.IsEnabled,
                Priority        = src.Priority,
                ModWorkshopId   = src.ModWorkshopId,
                // Don't share the bundled archive between profiles
                // — the source profile owns its mods/ folder; copying
                // an entry's metadata across is fine but pointing at
                // the source's bundled .vmz from the target would
                // break apply-time bundle resolution. The recipient
                // profile gets a metadata-only entry that falls
                // back to library / MW download on apply.
                ArchiveFileName = "",
            });
            existingIds.Add(src.ModId);
            added++;
        }
        if (added > 0)
        {
            target.UpdatedAt = DateTime.UtcNow;
            try { target.SaveMetadataOnly(); }
            catch { /* best-effort; in-memory state still reflects the change */ }
        }
        // Refresh the listbox so the per-profile mod-count label
        // updates, and the detail grid too when the user dropped
        // onto the currently-selected profile.
        var selIdx = _list.SelectedIndex;
        LoadProfiles();
        if (selIdx >= 0 && selIdx < _list.Items.Count)
            _list.SelectedIndex = selIdx;

        // Surface the outcome — silent success makes it unclear
        // whether the drag actually did anything.
        var msg = added > 0
            ? $"Copied {added} mod{(added == 1 ? "" : "s")} into '{target.Name}'"
              + (skipped > 0 ? $"  ({skipped} skipped — already present)" : "")
              + "."
            : skipped > 0
                ? $"Nothing added — all {skipped} dragged mod(s) are already in '{target.Name}'."
                : "Nothing to copy.";
        ThemedMessageBox.Show(this, msg, "Drag-and-drop copy",
            MessageBoxButtons.OK,
            added > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // ── Bottom row ───────────────────────────────────────────────────

    private Panel BuildBottomRow()
    {
        var p = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 52,
            BackColor = Color.FromArgb(30, 34, 44),
            Margin    = new Padding(0, 8, 0, 0),
        };
        var closeBtn = MainForm.ThemedButton("Close");
        closeBtn.Width  = 100;
        closeBtn.Height = 40;
        closeBtn.AutoSize = false;
        closeBtn.Click += (_, _) => Close();
        p.Resize += (_, _) =>
        {
            closeBtn.Top  = 6;
            closeBtn.Left = p.Width - closeBtn.Width - 16;
        };
        p.Controls.Add(closeBtn);
        CancelButton = closeBtn;
        return p;
    }

    // ── Data loading ─────────────────────────────────────────────────

    private void LoadProfiles()
    {
        _profiles = ModProfile.LoadAll();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in _profiles) _list.Items.Add(p.Name);
        _list.EndUpdate();
        ShowSelected();
    }

    private void ShowSelected()
    {
        var profile = SelectedProfile();
        _deleteBtn.Enabled = profile != null;
        _applyBtn.Enabled  = profile != null;
        _exportBtn.Enabled = profile != null;
        // Diff needs at least one OTHER profile to compare against.
        _diffBtn.Enabled = profile != null && _profiles.Count >= 2;

        if (profile == null)
        {
            _detName.Text = "(no profile selected)";
            _detDesc.Text = "";
            _detMeta.Text = "";
            _detGrid.Rows.Clear();
            return;
        }

        _detName.Text = profile.Name;
        _detDesc.Text = string.IsNullOrEmpty(profile.Description)
            ? "" : $"\"{profile.Description}\"";
        var updated = profile.UpdatedAt.HasValue
            ? $"  ·  updated {profile.UpdatedAt.Value.ToLocalTime():yyyy-MM-dd}"
            : "";
        var bundled = profile.BundledArchivesCount();
        var size    = profile.BundledArchivesSize();
        var bundleNote = bundled > 0
            ? $"  ·  {bundled} archive(s) bundled, {FormatBytes(size)}"
            : "  ·  metadata-only (no archives bundled)";
        _detMeta.Text =
            $"{profile.Mods.Count} mods  ·  "
            + $"created {profile.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}{updated}"
            + bundleNote;

        // Order the rows the same way each refresh so editing one entry
        // doesn't shuffle the rest, AND stash the ProfileMod ref in
        // Row.Tag so the edit/remove handlers can find the original
        // model without re-resolving by index against a re-sorted list.
        // Apply the live filter here so the grid only renders the
        // ProfileMods that match name / id / version — matches the
        // main form's filter semantics.
        var filter = (_detFilter?.Text ?? "").Trim().ToLowerInvariant();
        bool MatchesFilter(ProfileMod m)
        {
            if (filter.Length == 0) return true;
            if ((m.DisplayName ?? "").ToLowerInvariant().Contains(filter)) return true;
            if ((m.ModId ?? "").ToLowerInvariant().Contains(filter)) return true;
            if ((m.Version ?? "").ToLowerInvariant().Contains(filter)) return true;
            return false;
        }
        var ordered = profile.Mods
            .Where(MatchesFilter)
            .OrderBy(m => m.Priority)
            .ThenBy(m => m.DisplayName)
            .ToList();
        _detGrid.Rows.Clear();
        foreach (var m in ordered)
        {
            var i = _detGrid.Rows.Add();
            var r = _detGrid.Rows[i];
            r.Tag = m;
            r.Cells["On"].Value       = m.IsEnabled;
            r.Cells["ModName"].Value  = m.DisplayName;
            r.Cells["Version"].Value  = m.Version;
            r.Cells["Priority"].Value = m.Priority;
            // Bundle indicator in tooltip — mods missing an archive
            // will fall back to ModWorkshop on apply.
            var hasBundle = !string.IsNullOrEmpty(profile.ResolveBundledArchive(m));
            r.Cells["ModName"].ToolTipText = hasBundle
                ? $"id: {m.ModId}\nbundled: {m.ArchiveFileName}"
                : $"id: {m.ModId}\nno bundled archive — will fall back to ModWorkshop";
            if (!hasBundle) r.DefaultCellStyle.ForeColor = Color.FromArgb(160, 170, 190);
        }
    }

    /// <summary>Wires the "On" checkbox edit back to the underlying
    /// ProfileMod and persists. Other columns are ReadOnly so this
    /// only ever fires for the checkbox.</summary>
    private void OnProfileCellEdited(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || rowIndex >= _detGrid.Rows.Count) return;
        var profile = SelectedProfile();
        if (profile == null) return;
        var row = _detGrid.Rows[rowIndex];
        if (row.Tag is not ProfileMod pm) return;
        if (_detGrid.Columns[columnIndex].Name != "On") return;
        var newVal = row.Cells["On"].Value is bool b && b;
        if (pm.IsEnabled == newVal) return;
        pm.IsEnabled = newVal;
        profile.UpdatedAt = DateTime.UtcNow;
        try { profile.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this,
                $"Couldn't save profile change:\n{ex.Message}",
                "Save failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Drops one or more mods from the selected profile.
    /// For each: deletes its bundled .vmz from the profile's mods/
    /// folder (if any), removes the ProfileMod from profile.Mods,
    /// then rewrites profile.json once at the end. Single
    /// confirmation prompt covers the whole batch; the live mods
    /// folder is never touched.</summary>
    private void RemoveModsFromProfile(List<ProfileMod> mods)
    {
        if (mods.Count == 0) return;
        var profile = SelectedProfile();
        if (profile == null) return;

        var promptBody = mods.Count == 1
            ? $"Remove '{mods[0].DisplayName}' from profile '{profile.Name}'?"
            : $"Remove these {mods.Count} mods from profile "
              + $"'{profile.Name}'?\n\n  • "
              + string.Join("\n  • ",
                    mods.Take(8).Select(m => m.DisplayName))
              + (mods.Count > 8 ? $"\n  … and {mods.Count - 8} more" : "");
        var dr = ThemedMessageBox.Show(this,
            promptBody
            + "\n\nThis drops the entries from the profile and "
            + "deletes their bundled archives (if any). The live "
            + "mods folder is untouched.",
            "Remove from profile",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (dr != DialogResult.Yes) return;

        // Delete bundles first, then mutate Mods. A partial failure
        // mid-batch still leaves the profile.json consistent because
        // we rewrite it once at the end.
        foreach (var pm in mods)
        {
            var bundlePath = profile.ResolveBundledArchive(pm);
            if (!string.IsNullOrEmpty(bundlePath))
            {
                try { File.Delete(bundlePath); }
                catch { /* leave the orphan; not worth blocking */ }
            }
            profile.Mods.Remove(pm);
        }
        profile.UpdatedAt = DateTime.UtcNow;
        try { profile.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this,
                $"Couldn't save profile change:\n{ex.Message}",
                "Save failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        ShowSelected();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    private ModProfile? SelectedProfile()
    {
        var idx = _list.SelectedIndex;
        return (idx >= 0 && idx < _profiles.Count) ? _profiles[idx] : null;
    }

    // ── Actions ───────────────────────────────────────────────────────

    /// <summary>Clones the profile currently SELECTED in the
    /// dialog's left-pane list under a user-supplied name. The
    /// clone has the same Mods list + description + settings but
    /// is NOT activated — the user switches to it from the
    /// title-row dropdown when ready. Bundles aren't copied; the
    /// clone references the same library files the source does
    /// (looked up at apply / switch time).</summary>
    private void CloneSelectedProfile()
    {
        var source = SelectedProfile();
        if (source == null)
        {
            ThemedMessageBox.Show(this,
                "Select a profile in the list first, then click "
                + "Clone to make a copy of it under a new name.",
                "Nothing to clone",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var newName = TextInputDialog.Prompt(this,
            "Clone Profile",
            $"Clone of '{source.Name}'. New profile name:",
            $"{source.Name} (copy)");
        if (string.IsNullOrWhiteSpace(newName)) return;

        // Overwrite check — same flow as SaveCurrent / Empty.
        var existing = _profiles.FirstOrDefault(p =>
            string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var dr = ThemedMessageBox.Show(this,
                $"A profile named '{newName}' already exists.\n\n"
                + "Overwrite it?",
                "Overwrite?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (dr != DialogResult.Yes) return;
        }

        var clone = new ModProfile
        {
            Name        = newName,
            Description = $"Clone of '{source.Name}' — "
                        + $"{DateTime.Now:yyyy-MM-dd HH:mm}",
            CreatedAt   = DateTime.UtcNow,
            Mods        = source.Mods.Select(m => new ProfileMod
            {
                ModId         = m.ModId,
                DisplayName   = m.DisplayName,
                Version       = m.Version,
                IsEnabled     = m.IsEnabled,
                Priority      = m.Priority,
                ModWorkshopId = m.ModWorkshopId,
                // ArchiveFileName intentionally NOT copied — clone
                // doesn't carry bundles; ModLibrary.Find at apply
                // / switch time is the lookup path.
            }).ToList(),
        };
        try { clone.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this,
                $"Couldn't write the new profile:\n{ex.Message}",
                "Clone failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        LoadProfiles();
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (string.Equals(_list.Items[i]?.ToString(), newName,
                    StringComparison.OrdinalIgnoreCase))
            { _list.SelectedIndex = i; break; }
        }
    }

    private void SaveCurrentProfile()
    {
        var name = TextInputDialog.Prompt(this,
            "Save Profile",
            "Profile name:",
            $"Profile {DateTime.Now:yyyy-MM-dd}");
        if (string.IsNullOrWhiteSpace(name)) return;

        // Default description summarises the snapshot — total mod
        // count, enabled / disabled split, and timestamp. Pre-filled
        // so the user can hit OK without typing; still freely
        // editable if they want a real note.
        var total    = _registry.Entries.Count(e => !string.IsNullOrEmpty(e.ModId));
        var enabled  = _registry.Entries.Count(e => !string.IsNullOrEmpty(e.ModId) && e.IsEnabled);
        var disabled = total - enabled;
        var defaultDesc =
            $"{total} mods ({enabled} enabled, {disabled} disabled) — "
            + $"snapshot {DateTime.Now:yyyy-MM-dd HH:mm}";
        var desc = TextInputDialog.Prompt(this,
            "Save Profile — description (optional)",
            "Short description (edit or leave as-is):",
            defaultDesc);

        // Check for existing profile with same name
        var existing = _profiles.FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var dr = ThemedMessageBox.Show(this,
                $"A profile named '{name}' already exists.\n\n"
                + "Overwrite it (existing bundled archives will be replaced "
                + "with copies of the currently-installed mods)?",
                "Overwrite?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (dr != DialogResult.Yes) return;
        }

        // Build the profile + bundle EVERY installed mod, enabled or
        // disabled. The earlier "enabled-only" filter saved a bit of
        // disk but meant disabled mods were silently flagged as
        // "failed to bundle" (because they weren't in the live
        // entries list passed to SaveWithBundles) — a confusing false
        // positive. Bundling the full set keeps the failure report
        // accurate (only directory mods / locked files appear) and
        // gives the apply flow a real archive to restore from when
        // the user later re-enables one of the bundled mods.
        Cursor = Cursors.WaitCursor;
        List<ProfileMod> failed;
        ModProfile profile;
        try
        {
            profile = ModProfile.FromRegistry(name, desc ?? "", _registry.Entries);
            failed = profile.SaveWithBundles(_registry.Entries);
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        if (failed.Count > 0)
        {
            var sample = string.Join("\n  • ",
                failed.Take(6).Select(m => $"{m.DisplayName} ({m.Version})"));
            var more = failed.Count > 6 ? $"\n  … and {failed.Count - 6} more" : "";
            ThemedMessageBox.Show(this,
                $"Profile saved, but {failed.Count} mod(s) couldn't be bundled:\n  • {sample}{more}\n\n"
                + "These are typically directory mods (unpacked) or .vmz "
                + "files that were locked at copy time. On apply, the manager "
                + "will fall back to downloading them from ModWorkshop if they "
                + "have a linked ID.",
                "Partial bundle",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        LoadProfiles();
        // Select the newly saved profile
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (string.Equals(_list.Items[i]?.ToString(), name, StringComparison.OrdinalIgnoreCase))
            { _list.SelectedIndex = i; break; }
        }
    }

    /// <summary>Creates a NEW MODLIST profile — Mods list contains
    /// only the user's locked mods (or is literally empty if there
    /// are none). Applying it doesn't disable any other installed
    /// mods; it just establishes a small starting set. Duplicate
    /// handling lives in ProfileApplyDialog (per-row Source
    /// dropdown: keep current / use bundle / download new).</summary>
    private void SaveNewModlistProfile()
    {
        var lockedIds = new HashSet<string>(
            _lockedModIds, StringComparer.OrdinalIgnoreCase);
        var lockedEntries = _registry.Entries
            .Where(e => !string.IsNullOrEmpty(e.ModId)
                     && lockedIds.Contains(e.ModId))
            .ToList();

        // Explain what the user is about to create. The prompt
        // doubles as the confirmation — if they hit Cancel the
        // profile isn't created.
        var preview = lockedEntries.Count > 0
            ? $"Starts with {lockedEntries.Count} locked mod(s). "
              + "Applying it later won't disable anything else — "
              + "duplicates are handled per-row in the apply dialog."
            : "Starts from zero mods. Applying it later won't "
              + "disable anything else — duplicates are handled "
              + "per-row in the apply dialog.";
        var name = TextInputDialog.Prompt(this,
            "New Modlist",
            "Name the new-modlist profile:\n\n" + preview,
            $"Modlist  {DateTime.Now:yyyy-MM-dd HH:mm}");
        if (string.IsNullOrWhiteSpace(name)) return;

        // Same overwrite confirmation as SaveCurrentProfile so the
        // user doesn't lose an existing profile by accident.
        var existing = _profiles.FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var dr = ThemedMessageBox.Show(this,
                $"A profile named '{name}' already exists.\n\n"
                + "Overwrite it?",
                "Overwrite?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (dr != DialogResult.Yes) return;
        }

        // Profile contains ONLY the locked mods (or nothing). The
        // apply phase reads "anything in registry but not in profile
        // and not locked" → disable. That's how the empty list
        // becomes "everything off" on apply.
        Cursor = Cursors.WaitCursor;
        ModProfile profile;
        List<ProfileMod> failed;
        try
        {
            profile = new ModProfile
            {
                Name        = name,
                Description = "New modlist — "
                            + (lockedEntries.Count > 0
                                ? $"starts with {lockedEntries.Count} locked mod(s)."
                                : "starts from zero mods."),
                CreatedAt   = DateTime.UtcNow,
                Mods        = lockedEntries
                    .Select(e => new ProfileMod
                    {
                        ModId         = e.ModId,
                        DisplayName   = string.IsNullOrEmpty(e.DisplayName)
                                            ? e.ModId
                                            : e.DisplayName,
                        Version       = e.Version,
                        IsEnabled     = e.IsEnabled,
                        Priority      = e.Priority,
                        ModWorkshopId = e.ModWorkshopId,
                    })
                    .ToList(),
            };
            failed = profile.SaveWithBundles(lockedEntries);
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        if (failed.Count > 0)
        {
            var sample = string.Join("\n  • ",
                failed.Take(6).Select(m => $"{m.DisplayName} ({m.Version})"));
            var more = failed.Count > 6
                ? $"\n  … and {failed.Count - 6} more" : "";
            ThemedMessageBox.Show(this,
                $"Empty profile saved, but {failed.Count} locked "
                + $"mod(s) couldn't be bundled:\n  • {sample}{more}\n\n"
                + "Usually directory mods or files locked at copy "
                + "time. The profile still applies correctly — "
                + "those entries just have no bundled .vmz to "
                + "restore from later.",
                "Partial bundle",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        LoadProfiles();
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (string.Equals(_list.Items[i]?.ToString(), name,
                    StringComparison.OrdinalIgnoreCase))
            { _list.SelectedIndex = i; break; }
        }
    }

    private void ImportProfileFromFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title  = "Import Profile",
            Filter = "Profile bundle (*.vmprofile;*.zip)|*.vmprofile;*.zip"
                   + "|Profile JSON (*.json)|*.json"
                   + "|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        ModProfile? profile;
        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        var isZip = (ext == ".vmprofile" || ext == ".zip");
        Cursor = Cursors.WaitCursor;
        try
        {
            profile = isZip
                ? ModProfile.LoadFromZip(dlg.FileName)
                : ModProfile.LoadFromJsonFile(dlg.FileName);
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        if (profile == null)
        {
            ThemedMessageBox.Show(this,
                "Could not parse the selected file as a mod profile.\n\n"
                + "Expected a .vmprofile zip (bundled archives) or a "
                + ".json file (metadata only).",
                "Import failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Save metadata if it came from a bare .json (Zip imports
        // already unzip themselves into a folder during LoadFromZip).
        if (string.IsNullOrEmpty(profile.FolderPath))
        {
            profile.SaveMetadataOnly();
        }

        // .vmprofile path: every bundled archive inside the imported
        // profile's `mods/` subfolder gets ingested into the active
        // mods folder's Library so the new profile model can find
        // them on activate. ModLibrary.Add is idempotent — if the
        // recipient already has the same (mod_id, version), this is
        // a no-op for that file.
        var libraryAdded   = 0;
        var librarySkipped = 0;
        if (isZip && !string.IsNullOrEmpty(profile.FolderPath))
        {
            var bundleDir = Path.Combine(profile.FolderPath, "mods");
            if (Directory.Exists(bundleDir))
            {
                foreach (var vmz in Directory.GetFiles(bundleDir, "*.vmz"))
                {
                    var added = ModLibrary.Add(_modsDir, vmz);
                    if (added != null) libraryAdded++;
                    else               librarySkipped++;
                }
            }
        }

        LoadProfiles();
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (string.Equals(_list.Items[i]?.ToString(), profile.Name, StringComparison.OrdinalIgnoreCase))
            { _list.SelectedIndex = i; break; }
        }

        // Decide which follow-up prompt makes sense. .vmprofile
        // imports are fully self-contained once their bundles land
        // in the library — the user can switch profiles from the
        // title-row selector. .json imports name mods but bring no
        // archives; those need a ModWorkshop download, which the
        // existing ApplySelected flow handles via its
        // MissingDownload row kind.
        if (isZip)
        {
            var libraryNote = libraryAdded > 0
                ? $"  ·  {libraryAdded} archive(s) added to Library"
                  + (librarySkipped > 0 ? $" ({librarySkipped} skipped)" : "")
                : "  ·  no archives ingested";
            ThemedMessageBox.Show(this,
                $"Profile '{profile.Name}' imported{libraryNote}.\n\n"
                + "Switch to it from the title-row Active selector "
                + "to activate.",
                "Imported",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            // JSON spec — count mods that aren't in the library yet
            // so the prompt explains what Apply will do.
            int missingFromLib = 0;
            foreach (var pm in profile.Mods)
            {
                if (string.IsNullOrEmpty(pm.ModId)) continue;
                if (string.IsNullOrEmpty(
                        ModLibrary.Find(_modsDir, pm.ModId, pm.Version)))
                    missingFromLib++;
            }
            var dlBlurb = missingFromLib > 0
                ? $"{missingFromLib} mod(s) need to be downloaded "
                  + "from ModWorkshop. "
                : "";
            var applyNow = ThemedMessageBox.Show(this,
                $"Profile '{profile.Name}' imported (JSON spec).\n\n"
                + dlBlurb
                + "Apply it now? Apply runs the per-row plan "
                + "(downloads + activate).",
                "Apply now?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
            if (applyNow == DialogResult.Yes) ApplySelected();
        }
    }

    /// <summary>Open a small picker dialog listing every OTHER
    /// profile, then show ProfileDiffDialog with the selected
    /// profile and the picked one. Triggered by the "Diff with…"
    /// button on the detail pane.</summary>
    private void DiffSelected()
    {
        var profile = SelectedProfile();
        if (profile == null) return;
        var others = _profiles
            .Where(p => !object.ReferenceEquals(p, profile))
            .ToList();
        if (others.Count == 0)
        {
            ThemedMessageBox.Show(this,
                "Need at least one OTHER profile to diff against.",
                "Nothing to compare",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Quick picker — single-select listbox in a small dialog.
        // Worth its own form rather than a combo because users
        // tend to have many profiles and the listbox is friendlier
        // to read at a glance.
        using var picker = new ProfileDiffPickerDialog(profile.Name, others);
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var other = picker.Selected;
        if (other == null) return;

        using var dlg = new ProfileDiffDialog(profile, other);
        dlg.ShowDialog(this);
        // If the user copied entries between profiles inside the
        // diff dialog, their on-disk profile.json files changed
        // out from under our in-memory _profiles list. Refresh
        // so the right pane (detail grid + mod counts) reflects
        // the new state.
        if (dlg.MadeChanges)
        {
            var keepIdx = _list.SelectedIndex;
            LoadProfiles();
            if (keepIdx >= 0 && keepIdx < _list.Items.Count)
                _list.SelectedIndex = keepIdx;
        }
    }

    private void ExportSelected()
    {
        var profile = SelectedProfile();
        if (profile == null) return;
        using var dlg = new SaveFileDialog
        {
            Title    = "Export Profile",
            Filter   = "Profile bundle (*.vmprofile)|*.vmprofile"
                     + "|Profile JSON only (*.json)|*.json",
            FileName = ModProfile.SafeFileName(profile.Name) + ".vmprofile",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        Cursor = Cursors.WaitCursor;
        try
        {
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            if (ext == ".vmprofile" || ext == ".zip")
            {
                // Two export paths for .vmprofile depending on where
                // the bundles live:
                //   • LEGACY (pre-Phase-5): the profile has a
                //     per-profile mods/ folder with bundled archives
                //     — use ExportToZip which zips that folder.
                //   • NEW MODEL: bundles live in the library under
                //     `<modsDir>/Library/` — use ExportToZipUsingLibrary
                //     which looks each ProfileMod up by (mod_id, version)
                //     and pulls from there.
                if (profile.BundledArchivesCount() > 0)
                    profile.ExportToZip(dlg.FileName);
                else
                    profile.ExportToZipUsingLibrary(dlg.FileName, _modsDir);
            }
            else
            {
                // Metadata-only export. Strip ArchiveFileName fields
                // so the recipient knows there are no bundles to look for.
                var copy = new ModProfile
                {
                    Name        = profile.Name,
                    Description = profile.Description,
                    CreatedAt   = profile.CreatedAt,
                    UpdatedAt   = profile.UpdatedAt,
                    Mods = profile.Mods.Select(m => new ProfileMod
                    {
                        ModId = m.ModId,
                        DisplayName = m.DisplayName,
                        Version = m.Version,
                        IsEnabled = m.IsEnabled,
                        Priority = m.Priority,
                        ModWorkshopId = m.ModWorkshopId,
                    }).ToList(),
                };
                File.WriteAllText(dlg.FileName,
                    System.Text.Json.JsonSerializer.Serialize(copy,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            ThemedMessageBox.Show(this,
                $"Profile exported to:\n{dlg.FileName}",
                "Exported",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this,
                $"Export failed:\n{ex.Message}",
                "Export error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void DeleteSelected()
    {
        var profile = SelectedProfile();
        if (profile == null) return;
        var dr = ThemedMessageBox.Show(this,
            $"Delete profile '{profile.Name}'?\nThis cannot be undone.",
            "Delete profile",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (dr != DialogResult.Yes) return;
        profile.Delete();
        LoadProfiles();
    }

    private void ApplySelected()
    {
        var profile = SelectedProfile();
        if (profile == null) return;

        using var dlg = new ProfileApplyDialog(
            profile, _registry, _mw, _modConfig, _modsDir, _lockedModIds);
        dlg.ShowDialog(this);
        if (dlg.Applied)
        {
            NeedsRescan = true;
            // Refresh the profile list in case a profile was modified
            LoadProfiles();
        }
    }
}
