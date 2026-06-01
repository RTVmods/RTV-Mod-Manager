// Mod creator's "pack me a .vmz" tool. Takes a source (existing
// .vmz / .zip OR a folder), surfaces the editable manifest fields
// (mod_id, name, version, priority, ModWorkshop id, description) +
// a dependency picker, then writes a fresh .vmz with the edits
// applied.
//
// The dependency picker has two complementary modes:
//   1) Tick boxes against every installed mod in the registry, so
//      common deps (MCM, the various API libs, etc.) are a single
//      click each.
//   2) Free-form text for mod_ids the author wants to depend on
//      but doesn't have installed locally (e.g. a mod's first
//      release naming a future dependency by id alone). One id
//      per line or CSV.
//
// Both modes merge into the same Required / Optional buckets the
// final mod.txt's [dependencies] section ends up with. The dialog
// is non-destructive — the source file is read-only; the output is
// a separate .vmz the user names at save time.

using System.Text.Json;
using System.Text.Json.Serialization;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ModPackagerDialog : Form
{
    private readonly ModRegistry _registry;

    /// <summary>Persistent tick state for the dep grid, keyed by
    /// manifest mod_id. Lives outside the grid so a filter change
    /// (which drops rows from `_depGrid`) doesn't lose the user's
    /// selections — PopulateDepGrid syncs grid → set BEFORE
    /// rebuilding, then rows pull their initial tick value back
    /// from these sets. Pack / Export / SaveState read from these
    /// sets directly so a filter active at save time can't drop
    /// ticks either.</summary>
    private readonly HashSet<string> _checkedReq = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checkedOpt = new(StringComparer.OrdinalIgnoreCase);

    // ── Source ────────────────────────────────────────────────────
    private RadioButton _srcFromArchive = null!;
    private RadioButton _srcFromFolder  = null!;
    private TextBox     _srcPath        = null!;
    private Button      _srcBrowseBtn   = null!;
    private Button      _reloadBtn      = null!;

    // ── Manifest ──────────────────────────────────────────────────
    private TextBox _modIdBox        = null!;
    private TextBox _nameBox         = null!;
    private TextBox _versionBox      = null!;
    private NumericUpDown _priorityBox = null!;
    private TextBox _mwIdBox         = null!;
    private TextBox _descBox         = null!;

    // ── Dependencies ──────────────────────────────────────────────
    private TextBox        _depFilterBox  = null!;
    private DataGridView   _depGrid       = null!;
    private TextBox        _customReqBox  = null!;
    private TextBox        _customOptBox  = null!;

    // Each grid row maps to one installed mod; the ✓ Required and
    // ✓ Optional checkbox cells drive what ends up in the packed
    // mod.txt's [dependencies] section.
    private List<DepRow> _depRows = new();

    // ── Output ────────────────────────────────────────────────────
    private TextBox _outPath = null!;
    private Button  _outBrowseBtn = null!;

    // ── Buttons + log ─────────────────────────────────────────────
    private Button       _packBtn    = null!;
    private Button       _closeBtn   = null!;
    private TextBox      _log        = null!;

    /// <summary>One installable-mod row in the dep grid. ModId is
    /// the MANIFEST id (string slug, the form mod.txt's
    /// [dependencies] section needs); ModWorkshopId is the numeric
    /// id from the mod's ModWorkshop URL. We surface the MW id as
    /// the primary identifier in the UI (it's the value mod authors
    /// actually share with each other) but always write the
    /// manifest id when packing, because that's what the in-game
    /// loader matches against.</summary>
    private record DepRow(
        string ModId,
        int    ModWorkshopId,
        string DisplayName,
        bool   IsInstalledHere);

    /// <summary>Optional active profile — when present, its
    /// ProfileMod entries are a SECOND source of mod_workshop_id
    /// values that the registry / mod.txt may lack. A mod can
    /// have its MW id stored in profile.json (because it was
    /// originally added via a JSON import that carried it) even
    /// when the on-disk mod.txt's [updates] modworkshop is
    /// missing. We treat the profile as authoritative for the
    /// MW id when both sources disagree because the profile is
    /// where the user's intent lives. Null when the manager
    /// opened the packager without an active profile — falls
    /// back to registry-only lookup.</summary>
    private readonly ModProfile? _activeProfile;

    /// <summary>mod_id → MW id index built once at dialog open
    /// from registry mod.txts UNION profile entries. Built in
    /// PopulateDepGrid so it stays in sync if the registry rows
    /// change shape (rare; just covering all entry points).</summary>
    private Dictionary<string, int> _modIdToMwIndex = new(StringComparer.OrdinalIgnoreCase);

    public ModPackagerDialog(ModRegistry registry, ModProfile? activeProfile = null)
    {
        _registry      = registry;
        _activeProfile = activeProfile;
        Text = "Mod Packager";
        MinimumSize    = new Size(1100, 720);
        Width          = 1280;
        Height         = 820;
        StartPosition  = FormStartPosition.CenterParent;
        BackColor      = Color.FromArgb(26, 30, 40);
        ForeColor      = Color.FromArgb(220, 225, 235);
        Font           = new Font("Segoe UI", 12f);
        ShowInTaskbar  = false;

        BuildUi();
        PopulateDepGrid("");
        WireDragDrop();
        WireCustomDepBoxDrop(_customReqBox, "required");
        WireCustomDepBoxDrop(_customOptBox, "optional");
    }

    /// <summary>Drag-drop on the two custom-dep textboxes: drop a
    /// .vmz / .zip / mod folder onto either box, the packager
    /// opens the dragged mod's mod.txt, extracts its `id`, and
    /// appends the id to that box's content as a dependency.
    /// Multiple files at once → one id per appended line; dups
    /// against existing content are silently skipped so the user
    /// can keep dragging without scrubbing the list afterward.
    /// </summary>
    private void WireCustomDepBoxDrop(TextBox box, string kindLabel)
    {
        box.AllowDrop = true;
        box.DragEnter += (_, e) =>
        {
            if (!TryPickModDropPaths(e, out List<string> _)) return;
            e.Effect = DragDropEffects.Copy;
        };
        box.DragDrop += (_, e) =>
        {
            if (!TryPickModDropPaths(e, out var paths)) return;
            var added = new List<string>();
            var skipped = new List<string>();
            int mwCaptured = 0;
            foreach (var p in paths)
            {
                var (id, mwId) = ExtractModInfoFromPath(p);
                if (string.IsNullOrEmpty(id))
                {
                    skipped.Add(Path.GetFileName(p) + " (no mod.txt / no id)");
                    continue;
                }
                if (BoxAlreadyContains(box, id))
                {
                    skipped.Add($"{id} (already listed)");
                    continue;
                }
                added.Add(id);
                // Remember the MW id (if the dropped file declared
                // one) so the eventual Export reads it back when
                // resolving this slug — covers the case where the
                // dep isn't installed locally so neither the
                // registry nor the library knows its MW id.
                if (mwId > 0) { _modIdToMwIndex[id] = mwId; mwCaptured++; }
            }
            if (added.Count > 0)
                AppendLinesToBox(box, added);
            if (added.Count > 0)
            {
                var mwNote = mwCaptured > 0 ? $" ({mwCaptured} with MW id)" : "";
                Log($"↓ added {added.Count} {kindLabel} dep(s){mwNote}: {string.Join(", ", added)}");
            }
            foreach (var s in skipped)
                Log($"  · skipped: {s}");
        };
    }

    /// <summary>Like TryPickDropPath but returns ALL valid mod-
    /// shaped paths in the drag payload, not just the first. A
    /// folder counts only when it contains a mod.txt; archives
    /// (.vmz / .zip) count if the file exists.</summary>
    private static bool TryPickModDropPaths(DragEventArgs e, out List<string> paths)
    {
        paths = new List<string>();
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return false;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        foreach (var f in files)
        {
            if (Directory.Exists(f))
            {
                if (File.Exists(Path.Combine(f, "mod.txt"))) paths.Add(f);
                continue;
            }
            if (File.Exists(f)
                && (f.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase)
                 || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                paths.Add(f);
        }
        return paths.Count > 0;
    }

    /// <summary>Read mod.txt from `path` (folder or archive) and
    /// return its `[mod] id`. Empty string when the file is
    /// unreadable, isn't a valid mod archive, or has no id field.
    /// </summary>
    private static string ExtractModIdFromPath(string path)
        => ExtractModInfoFromPath(path).Id;

    /// <summary>Read mod.txt from `path` (folder or archive) and
    /// return both its `[mod] id` AND `[updates] modworkshop` —
    /// in one open pass. Empty id / zero mw_id when fields are
    /// missing or the file isn't parseable.</summary>
    private static (string Id, int MwId) ExtractModInfoFromPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var p = Path.Combine(path, "mod.txt");
                if (!File.Exists(p)) return ("", 0);
                var parsed = ModArchive.ParseConfigFile(File.ReadAllText(p));
                var id = parsed.TryGetValue("mod", out var sec)
                    && sec.TryGetValue("id", out var v) ? v : "";
                var mw = 0;
                if (parsed.TryGetValue("updates", out var us)
                    && us.TryGetValue("modworkshop", out var raw)
                    && int.TryParse(raw, out var n) && n > 0)
                    mw = n;
                return (id, mw);
            }
            if (File.Exists(path))
            {
                using var arch = new ModArchive();
                if (!arch.Open(path)) return ("", 0);
                return (arch.ModId ?? "", arch.ModWorkshopId);
            }
        }
        catch { /* malformed input → empty, caller logs */ }
        return ("", 0);
    }

    /// <summary>True when `box.Text` already contains `id` as a
    /// distinct dep entry (newline / comma separated, case-
    /// insensitive). Matches the same split rule ParseCustomList
    /// uses on pack so "exists" here equals "will be written".
    /// </summary>
    private static bool BoxAlreadyContains(TextBox box, string id)
    {
        foreach (var part in (box.Text ?? "").Split(
            new[] { '\n', '\r', ',' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(
                    part.Trim().Trim('"', '\''),
                    id,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void AppendLinesToBox(TextBox box, IEnumerable<string> lines)
    {
        var existing = box.Text ?? "";
        var nl = Environment.NewLine;
        // Insert a leading newline only if the box has content AND
        // doesn't already end in one — keeps the list visually
        // clean across multiple drops.
        if (existing.Length > 0 && !existing.EndsWith("\n") && !existing.EndsWith(nl))
            existing += nl;
        existing += string.Join(nl, lines);
        box.Text = existing;
        // Park the caret at the end so the user sees what was
        // just added without having to scroll the textbox.
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.ScrollToCaret();
    }

    /// <summary>Form-level drag-drop: drop a .vmz / .zip / folder
    /// anywhere on the packager dialog to set it as the source.
    /// The grid + textboxes block drops on themselves by default
    /// (their AllowDrop is false), but the form's drop area covers
    /// the title bar + group-box headers + any empty pane region,
    /// which is plenty of target surface for a quick drop.</summary>
    private void WireDragDrop()
    {
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (!TryPickDropPath(e, out string _)) return;
            e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (!TryPickDropPath(e, out var path)) return;
            // Route by extension: .json → load as a pack list;
            // anything else (.vmz / .zip / folder) → treat as the
            // Main mod source the packager edits.
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                Log($"↓ dropped pack JSON: {path}");
                LoadPackJsonFromPath(path);
                return;
            }
            // Match the radio to what the user actually dropped —
            // folder vs archive — so a re-pick via Browse won't
            // surprise them with the wrong filter.
            if (Directory.Exists(path))
                _srcFromFolder.Checked = true;
            else
                _srcFromArchive.Checked = true;
            _srcPath.Text = path;
            Log($"↓ dropped source: {path}");
            ReloadManifestFromSource();
            AutosuggestOutputPath();
        };
    }

    /// <summary>Inspect a drag-drop payload and return the first
    /// path that looks like a valid packager source — a folder, a
    /// .vmz, a .zip, or a .json pack. Returns false when nothing
    /// matches so the drag cursor shows "not allowed" instead of
    /// a misleading "drop ok" hover.</summary>
    private static bool TryPickDropPath(DragEventArgs e, out string path)
    {
        path = "";
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return false;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        foreach (var f in files)
        {
            if (Directory.Exists(f)) { path = f; return true; }
            if (File.Exists(f)
                && (f.EndsWith(".vmz",  StringComparison.OrdinalIgnoreCase)
                 || f.EndsWith(".zip",  StringComparison.OrdinalIgnoreCase)
                 || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                path = f;
                return true;
            }
        }
        return false;
    }

    // ── UI build ─────────────────────────────────────────────────

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 4,
            Padding     = new Padding(16, 12, 16, 12),
            BackColor   = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // header
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // two-pane body
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // output row
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // buttons + log
        Controls.Add(root);

        var title = new Label
        {
            Text      = "🔨 Mod Packager",
            AutoSize  = true,
            Font      = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(title, 0, 0);

        // ── Body: 2-column split (manifest left, deps right) ─────
        var body = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            RowCount    = 1,
            BackColor   = Color.Transparent,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
        body.Controls.Add(BuildLeftPane(),  0, 0);
        body.Controls.Add(BuildRightPane(), 1, 0);
        root.Controls.Add(body, 0, 1);

        // ── Output row ───────────────────────────────────────────
        var outRow = new TableLayoutPanel
        {
            Dock        = DockStyle.Top,
            AutoSize    = true,
            ColumnCount = 3,
            BackColor   = Color.Transparent,
            Margin      = new Padding(0, 10, 0, 0),
        };
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outRow.Controls.Add(new Label
        {
            Text      = "Output .vmz:",
            AutoSize  = true,
            Anchor    = AnchorStyles.Left,
            ForeColor = Color.FromArgb(220, 225, 235),
            BackColor = Color.Transparent,
            Margin    = new Padding(4, 8, 6, 0),
        }, 0, 0);
        _outPath = new TextBox
        {
            Anchor      = AnchorStyles.Left | AnchorStyles.Right,
            BackColor   = Color.FromArgb(30, 36, 48),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "where to save the packaged .vmz",
            Margin      = new Padding(0, 4, 6, 0),
        };
        outRow.Controls.Add(_outPath, 1, 0);
        _outBrowseBtn = MainForm.ThemedButton("Browse…");
        _outBrowseBtn.Width  = 110;
        _outBrowseBtn.Height = 32;
        _outBrowseBtn.AutoSize = false;
        _outBrowseBtn.Margin = new Padding(0, 2, 0, 0);
        _outBrowseBtn.Click += (_, _) => BrowseOutput();
        outRow.Controls.Add(_outBrowseBtn, 2, 0);
        root.Controls.Add(outRow, 0, 2);

        // ── Buttons + log ────────────────────────────────────────
        var btmStack = new TableLayoutPanel
        {
            Dock        = DockStyle.Top,
            AutoSize    = true,
            ColumnCount = 1,
            RowCount    = 2,
            BackColor   = Color.Transparent,
            Margin      = new Padding(0, 8, 0, 0),
        };
        btmStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        btmStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _log = new TextBox
        {
            Multiline   = true,
            ReadOnly    = true,
            ScrollBars  = ScrollBars.Vertical,
            Dock        = DockStyle.Top,
            Height      = 110,
            BackColor   = Color.FromArgb(18, 22, 30),
            ForeColor   = Color.FromArgb(180, 220, 160),
            Font        = new Font("Consolas", 11f),
            BorderStyle = BorderStyle.FixedSingle,
        };
        btmStack.Controls.Add(_log, 0, 0);

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            Anchor        = AnchorStyles.Right,
            BackColor     = Color.Transparent,
            Margin        = new Padding(0, 8, 0, 0),
        };
        _closeBtn = MainForm.ThemedButton("Close");
        _closeBtn.Width = 110;
        _closeBtn.Height = 40;
        _closeBtn.AutoSize = false;
        _closeBtn.Click += (_, _) => Close();
        btnRow.Controls.Add(_closeBtn);

        _packBtn = MainForm.ThemedButton("🔨 Build .vmz");
        _packBtn.Width  = 170;
        _packBtn.Height = 40;
        _packBtn.AutoSize = false;
        _packBtn.BackColor = Color.FromArgb(45, 90, 55);
        _packBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _packBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _packBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 115, 70);
        _packBtn.Click += (_, _) => Pack();
        btnRow.Controls.Add(_packBtn);

        // 📤 Export JSON — same dialog, different output. Builds a
        // mod-pack JSON file (ModListImport shape) from the
        // current Main mod fields + the ticked Required/Optional
        // rows + the custom-dep textboxes. Recipient drops the
        // file on the mod manager → every listed mod gets added
        // to their active profile. Slate-blue accent to visually
        // distinguish it from the green "Build .vmz" primary
        // action — both are valid outputs, neither is wrong, the
        // user picks based on what they're trying to ship.
        var exportBtn = MainForm.ThemedButton("📤 Export JSON…");
        exportBtn.Width  = 180;
        exportBtn.Height = 40;
        exportBtn.AutoSize = false;
        exportBtn.BackColor = Color.FromArgb(45, 70, 100);
        exportBtn.ForeColor = Color.FromArgb(225, 240, 255);
        exportBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 140, 200);
        exportBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 90, 130);
        exportBtn.Click += (_, _) => ExportModPackJson();
        btnRow.Controls.Add(exportBtn);

        // 📂 Open pack JSON — load a previously-exported (or hand-
        // authored) mod-pack JSON back into the packager so the
        // user can add / remove mods + re-export. Edit-in-place
        // round-trip for a list authored as a JSON file. Same
        // slate-blue accent as Export so the pair reads as the
        // "JSON I/O" buttons; the green Build .vmz stays the
        // primary action visually.
        var openJsonBtn = MainForm.ThemedButton("📂 Open pack JSON…");
        openJsonBtn.Width  = 190;
        openJsonBtn.Height = 40;
        openJsonBtn.AutoSize = false;
        openJsonBtn.BackColor = Color.FromArgb(45, 70, 100);
        openJsonBtn.ForeColor = Color.FromArgb(225, 240, 255);
        openJsonBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 140, 200);
        openJsonBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 90, 130);
        openJsonBtn.Click += (_, _) => OpenPackJsonFromPicker();
        btnRow.Controls.Add(openJsonBtn);

        btmStack.Controls.Add(btnRow, 0, 1);

        root.Controls.Add(btmStack, 0, 3);
        CancelButton = _closeBtn;
    }

    private Control BuildLeftPane()
    {
        var panel = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 2,
            BackColor   = Color.Transparent,
            Margin      = new Padding(0, 0, 8, 0),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        panel.Controls.Add(BuildSourceGroup(),   0, 0);
        panel.Controls.Add(BuildManifestGroup(), 0, 1);
        return panel;
    }

    private Control BuildRightPane()
    {
        var panel = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 1,
            BackColor   = Color.Transparent,
            Margin      = new Padding(8, 0, 0, 0),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        panel.Controls.Add(BuildDependenciesGroup(), 0, 0);
        return panel;
    }

    private GroupBox BuildSourceGroup()
    {
        // Group label is "Main mod" rather than "Source" — the
        // user reads this as "this is the mod I'm packaging",
        // not "source code". Variable names stay _srcPath / etc.
        // because they're still semantically the input to the
        // packer.
        var grp = ThemedGroupBox("Main mod");
        grp.AutoSize = true;
        var t = new TableLayoutPanel
        {
            Dock      = DockStyle.Top,
            AutoSize  = true,
            ColumnCount = 3,
            RowCount    = 3,
            BackColor   = Color.Transparent,
            Padding     = new Padding(12, 24, 12, 12),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _srcFromArchive = new RadioButton
        {
            Text      = "From .vmz / .zip",
            AutoSize  = true,
            ForeColor = Color.FromArgb(220, 225, 235),
            BackColor = Color.Transparent,
            Checked   = true,
            Anchor    = AnchorStyles.Left,
        };
        _srcFromFolder = new RadioButton
        {
            Text      = "From folder",
            AutoSize  = true,
            ForeColor = Color.FromArgb(220, 225, 235),
            BackColor = Color.Transparent,
            Anchor    = AnchorStyles.Left,
            Margin    = new Padding(20, 0, 0, 0),
        };
        var radioRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Margin    = new Padding(0, 0, 0, 6),
        };
        radioRow.Controls.Add(_srcFromArchive);
        radioRow.Controls.Add(_srcFromFolder);
        t.SetColumnSpan(radioRow, 3);
        t.Controls.Add(radioRow, 0, 0);

        _srcPath = new TextBox
        {
            Anchor      = AnchorStyles.Left | AnchorStyles.Right,
            BackColor   = Color.FromArgb(30, 36, 48),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "drop a .vmz / .zip or pick via Browse…",
            Margin      = new Padding(0, 0, 6, 0),
        };
        t.Controls.Add(new Label
        {
            Text      = "Path:",
            AutoSize  = true,
            Anchor    = AnchorStyles.Left,
            Margin    = new Padding(0, 8, 6, 0),
            ForeColor = Color.FromArgb(220, 225, 235),
            BackColor = Color.Transparent,
        }, 0, 1);
        t.Controls.Add(_srcPath, 1, 1);
        _srcBrowseBtn = MainForm.ThemedButton("Browse…");
        _srcBrowseBtn.Width = 110; _srcBrowseBtn.Height = 32;
        _srcBrowseBtn.AutoSize = false;
        _srcBrowseBtn.Click += (_, _) => BrowseSource();
        t.Controls.Add(_srcBrowseBtn, 2, 1);

        _reloadBtn = MainForm.ThemedButton("↻ Reload manifest");
        _reloadBtn.Width = 200; _reloadBtn.Height = 32;
        _reloadBtn.AutoSize = false;
        _reloadBtn.Click += (_, _) => ReloadManifestFromSource();
        t.SetColumnSpan(_reloadBtn, 3);
        t.Controls.Add(_reloadBtn, 0, 2);

        grp.Controls.Add(t);
        return grp;
    }

    private GroupBox BuildManifestGroup()
    {
        var grp = ThemedGroupBox("Manifest (mod.txt) / Json Editor");
        grp.Dock = DockStyle.Fill;

        var t = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            RowCount    = 6,
            BackColor   = Color.Transparent,
            Padding     = new Padding(12, 24, 12, 12),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 5; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        TextBox Field(string ph)
            => new TextBox
            {
                Anchor      = AnchorStyles.Left | AnchorStyles.Right,
                BackColor   = Color.FromArgb(30, 36, 48),
                ForeColor   = Color.FromArgb(220, 225, 235),
                BorderStyle = BorderStyle.FixedSingle,
                PlaceholderText = ph,
                Margin      = new Padding(0, 4, 0, 4),
            };
        Label Lbl(string s) => new Label
        {
            Text = s,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 6, 0),
            ForeColor = Color.FromArgb(220, 225, 235),
            BackColor = Color.Transparent,
        };

        _modIdBox    = Field("e.g. weapon-rig-api");
        _nameBox     = Field("Display name");
        _versionBox  = Field("e.g. 1.0.0");
        _priorityBox = new NumericUpDown
        {
            Minimum     = -1000, Maximum = 10000,
            Value       = 0,
            BackColor   = Color.FromArgb(30, 36, 48),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Anchor      = AnchorStyles.Left,
            Width       = 90,
            Margin      = new Padding(0, 4, 0, 4),
        };
        _mwIdBox     = Field("ModWorkshop numeric id (optional)");
        _descBox     = new TextBox
        {
            Anchor      = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
            Multiline   = true,
            ScrollBars  = ScrollBars.Vertical,
            BackColor   = Color.FromArgb(30, 36, 48),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Margin      = new Padding(0, 4, 0, 0),
            MinimumSize = new Size(0, 80),
        };

        // Field order: name first (the human-readable label the
        // user thinks about), then mod_id (the slug — derived from
        // name in most cases), then the rest. Easier to fill in
        // top-to-bottom; the slug is usually obvious once the name
        // is in place.
        t.Controls.Add(Lbl("Name:"),       0, 0); t.Controls.Add(_nameBox,     1, 0);
        t.Controls.Add(Lbl("mod_id:"),     0, 1); t.Controls.Add(_modIdBox,    1, 1);
        t.Controls.Add(Lbl("version:"),    0, 2); t.Controls.Add(_versionBox,  1, 2);
        t.Controls.Add(Lbl("priority:"),   0, 3); t.Controls.Add(_priorityBox, 1, 3);
        t.Controls.Add(Lbl("ModWorkshop:"),0, 4); t.Controls.Add(_mwIdBox,     1, 4);
        t.Controls.Add(Lbl("description:"),0, 5); t.Controls.Add(_descBox,     1, 5);

        grp.Controls.Add(t);
        return grp;
    }

    private GroupBox BuildDependenciesGroup()
    {
        var grp = ThemedGroupBox("Dependencies");
        grp.Dock = DockStyle.Fill;

        var t = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 4,
            BackColor   = Color.Transparent,
            Padding     = new Padding(12, 24, 12, 12),
        };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // filter
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 60f)); // grid
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // custom labels
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 40f)); // custom boxes

        // Filter
        _depFilterBox = new TextBox
        {
            Dock        = DockStyle.Top,
            BackColor   = Color.FromArgb(30, 36, 48),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "filter installed mods by name, MW id, or mod_id",
            Margin      = new Padding(0, 0, 0, 6),
        };
        _depFilterBox.TextChanged += (_, _) => PopulateDepGrid(_depFilterBox.Text);
        t.Controls.Add(_depFilterBox, 0, 0);

        // Grid
        _depGrid = new DataGridView
        {
            Dock                  = DockStyle.Fill,
            AutoGenerateColumns   = false,
            AllowUserToAddRows    = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly              = false,
            SelectionMode         = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect           = false,
            RowHeadersVisible     = false,
            BackgroundColor       = Color.FromArgb(18, 22, 30),
            BorderStyle           = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle =
            {
                BackColor = Color.FromArgb(36, 42, 54),
                ForeColor = Color.FromArgb(220, 225, 235),
                Font      = new Font("Segoe UI", 12f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
            },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font      = new Font("Consolas", 11f),
            },
            GridColor           = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 32,
            RowTemplate         = { Height = 28 },
        };
        _depGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Req", HeaderText = "Required", Width = 80,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        _depGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Opt", HeaderText = "Optional", Width = 80,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        // MW first — primary identifier mod authors actually share
        // with each other (the number in the modworkshop.net/mod/
        // URL). Manifest mod_id is shown alongside as the secondary
        // identifier; it's what the in-game loader matches against
        // and what we write into the packaged mod.txt.
        _depGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "MwId", HeaderText = "MW",
            Width = 90, ReadOnly = true,
            DefaultCellStyle =
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(170, 200, 240),
            },
        });
        _depGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ModId", HeaderText = "mod_id",
            Width = 220, ReadOnly = true,
            DefaultCellStyle = { ForeColor = Color.FromArgb(170, 185, 210) },
        });
        _depGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "DisplayName", HeaderText = "Name",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            ReadOnly = true,
        });
        foreach (DataGridViewColumn col in _depGrid.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;

        // Commit checkbox edits immediately so the two boxes act
        // mutually exclusive (the moment one ticks, untick the
        // other on the same row).
        _depGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (!_depGrid.IsCurrentCellDirty) return;
            if (_depGrid.CurrentCell is DataGridViewCheckBoxCell)
                _depGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _depGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var col = _depGrid.Columns[e.ColumnIndex].Name;
            if (col != "Req" && col != "Opt") return;
            var r = _depGrid.Rows[e.RowIndex];
            var reqVal = r.Cells["Req"].Value is true;
            var optVal = r.Cells["Opt"].Value is true;
            // Mutually exclusive: a mod is either required, optional,
            // or neither. Required wins on simultaneous-truthy because
            // it's the stronger statement.
            if (reqVal && optVal)
            {
                if (col == "Req") { r.Cells["Opt"].Value = false; optVal = false; }
                else              { r.Cells["Req"].Value = false; reqVal = false; }
            }
            // Mirror into the persistent sets so Pack / Export read
            // up-to-date state even when the row gets filtered out
            // before they fire.
            var modId = r.Cells["ModId"].Value as string ?? "";
            if (!string.IsNullOrEmpty(modId))
            {
                if (reqVal) _checkedReq.Add(modId); else _checkedReq.Remove(modId);
                if (optVal) _checkedOpt.Add(modId); else _checkedOpt.Remove(modId);
            }
        };
        t.Controls.Add(_depGrid, 0, 1);

        // Custom mod_ids
        t.Controls.Add(new Label
        {
            Text      = "Extra dependencies by MW id or URL (one per line or CSV) — for mods not installed locally:",
            AutoSize  = true,
            ForeColor = Color.FromArgb(170, 185, 210),
            BackColor = Color.Transparent,
            Margin    = new Padding(0, 6, 0, 4),
        }, 0, 2);

        var customGrid = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            RowCount    = 2,
            BackColor   = Color.Transparent,
        };
        customGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        customGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        customGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        customGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        customGrid.Controls.Add(new Label
        {
            Text      = "Required:",
            AutoSize  = true,
            ForeColor = Color.FromArgb(245, 180, 110),
            BackColor = Color.Transparent,
            Margin    = new Padding(0, 0, 0, 2),
        }, 0, 0);
        customGrid.Controls.Add(new Label
        {
            Text      = "Optional:",
            AutoSize  = true,
            ForeColor = Color.FromArgb(170, 200, 240),
            BackColor = Color.Transparent,
            Margin    = new Padding(8, 0, 0, 2),
        }, 1, 0);
        _customReqBox = new TextBox
        {
            Dock        = DockStyle.Fill,
            Multiline   = true,
            ScrollBars  = ScrollBars.Vertical,
            BackColor   = Color.FromArgb(30, 36, 48),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "56781\nhttps://modworkshop.net/mod/56123",
        };
        _customOptBox = new TextBox
        {
            Dock        = DockStyle.Fill,
            Multiline   = true,
            ScrollBars  = ScrollBars.Vertical,
            BackColor   = Color.FromArgb(30, 36, 48),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "56999",
            Margin      = new Padding(8, 0, 0, 0),
        };
        customGrid.Controls.Add(_customReqBox, 0, 1);
        customGrid.Controls.Add(_customOptBox, 1, 1);
        t.Controls.Add(customGrid, 0, 3);

        grp.Controls.Add(t);
        return grp;
    }

    private static GroupBox ThemedGroupBox(string title) => new GroupBox
    {
        Text      = title,
        ForeColor = Color.FromArgb(170, 200, 240),
        BackColor = Color.Transparent,
        Font      = new Font("Segoe UI", 12f, FontStyle.Bold),
        Dock      = DockStyle.Fill,
    };

    // ── Dep grid population ──────────────────────────────────────

    private void PopulateDepGrid(string filter)
    {
        // Sync VISIBLE grid → persistent sets before the rebuild.
        // We only update entries we can see, so ticks for rows
        // currently filtered out survive untouched in the sets.
        // Without this, ticking a row, applying a filter that
        // hides it, then changing the filter again would lose the
        // earlier tick.
        for (int i = 0; i < _depGrid.Rows.Count; i++)
        {
            var modId = _depGrid.Rows[i].Cells["ModId"].Value as string ?? "";
            if (string.IsNullOrEmpty(modId)) continue;
            if (_depGrid.Rows[i].Cells["Req"].Value is true) _checkedReq.Add(modId);
            else                                              _checkedReq.Remove(modId);
            if (_depGrid.Rows[i].Cells["Opt"].Value is true) _checkedOpt.Add(modId);
            else                                              _checkedOpt.Remove(modId);
        }

        // Rebuild the mod_id → MW id index from THREE sources, in
        // increasing priority order so later writes overwrite
        // earlier ones (last source wins):
        //   1. Library entries (canonical .vmz copies under
        //      <mods>/Library/) — every mod the user ever
        //      installed. Captures MW ids for mods not currently
        //      live AND not in the profile.
        //   2. Registry entries (mod.txt of currently-live mods).
        //   3. Active profile's ProfileMod entries — the user's
        //      source of truth for "what MW id does this mod live
        //      under", since profile mods can carry MW ids that
        //      survive even when the on-disk mod.txt loses them.
        _modIdToMwIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var le in ModLibrary.List(_registry.ModsDir))
            {
                if (string.IsNullOrEmpty(le.ModId)) continue;
                if (le.ModWorkshopId > 0)
                    _modIdToMwIndex[le.ModId] = le.ModWorkshopId;
            }
        }
        catch { /* best-effort; library scan failures fall back to registry/profile */ }
        foreach (var e in _registry.Entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (e.ModWorkshopId > 0) _modIdToMwIndex[e.ModId] = e.ModWorkshopId;
        }
        if (_activeProfile != null)
        {
            foreach (var pm in _activeProfile.Mods)
            {
                if (string.IsNullOrEmpty(pm.ModId)) continue;
                if (pm.ModWorkshopId > 0)
                    _modIdToMwIndex[pm.ModId] = pm.ModWorkshopId;
            }
        }

        _depRows = _registry.Entries
            .Where(e => !string.IsNullOrEmpty(e.ModId))
            // Distinct by mod_id — duplicates would let the user
            // tick the same id twice, which serializes to an array
            // with duplicates.
            .GroupBy(e => e.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DepRow(
                g.Key,
                // Prefer the merged index — falls back to the
                // registry's own ModWorkshopId only when no
                // profile-level value exists.
                _modIdToMwIndex.TryGetValue(g.Key, out var mw) ? mw : g.First().ModWorkshopId,
                g.First().DisplayName ?? "",
                true))
            // Sort by display name (case-insensitive) so the grid
            // matches the cognitive order the user reads the list
            // in. MW-id sort would look random because the numbers
            // don't correlate to mod identity at a glance.
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var f = (filter ?? "").Trim().ToLowerInvariant();
        bool Matches(DepRow r) =>
            f.Length == 0
            || r.ModId.ToLowerInvariant().Contains(f)
            || r.DisplayName.ToLowerInvariant().Contains(f)
            || (r.ModWorkshopId > 0
                && r.ModWorkshopId.ToString().Contains(f));

        _depGrid.Rows.Clear();
        foreach (var r in _depRows.Where(Matches))
        {
            var i = _depGrid.Rows.Add();
            var row = _depGrid.Rows[i];
            row.Cells["Req"].Value = _checkedReq.Contains(r.ModId);
            row.Cells["Opt"].Value = _checkedOpt.Contains(r.ModId);
            row.Cells["MwId"].Value = r.ModWorkshopId > 0
                ? r.ModWorkshopId.ToString() : "—";
            row.Cells["ModId"].Value = r.ModId;
            row.Cells["DisplayName"].Value = r.DisplayName;
        }
    }

    // ── Source actions ───────────────────────────────────────────

    private void BrowseSource()
    {
        if (_srcFromArchive.Checked)
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Pick source .vmz / .zip",
                Filter = "Mod archives (*.vmz;*.zip)|*.vmz;*.zip|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _srcPath.Text = dlg.FileName;
        }
        else
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Pick the folder containing mod.txt",
                ShowNewFolderButton = false,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _srcPath.Text = dlg.SelectedPath;
        }
        // Auto-load the manifest on browse so the form populates.
        ReloadManifestFromSource();
        AutosuggestOutputPath();
    }

    private void BrowseOutput()
    {
        using var dlg = new SaveFileDialog
        {
            Title  = "Save packaged .vmz",
            Filter = "Vostok mod archive (*.vmz)|*.vmz",
            FileName = SuggestedOutputFilename(),
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _outPath.Text = dlg.FileName;
    }

    private void AutosuggestOutputPath()
    {
        if (!string.IsNullOrEmpty(_outPath.Text)) return;
        var src = _srcPath.Text;
        if (string.IsNullOrEmpty(src)) return;
        var dir = Directory.Exists(src)
            ? Path.GetDirectoryName(src) ?? src
            : Path.GetDirectoryName(src) ?? "";
        var name = SuggestedOutputFilename();
        _outPath.Text = Path.Combine(dir, name);
    }

    private string SuggestedOutputFilename()
    {
        var id  = _modIdBox.Text.Trim();
        var ver = _versionBox.Text.Trim();
        if (string.IsNullOrEmpty(id)) id = "mod";
        if (!string.IsNullOrEmpty(ver))
            return $"{ModProfile.SafeFileName(id)}-v{ver}.vmz";
        return $"{ModProfile.SafeFileName(id)}.vmz";
    }

    private void ReloadManifestFromSource()
    {
        var src = _srcPath.Text.Trim();
        if (string.IsNullOrEmpty(src))
        {
            Log("(no source set yet)");
            return;
        }
        try
        {
            string modTxt;
            if (Directory.Exists(src))
            {
                var p = Path.Combine(src, "mod.txt");
                if (!File.Exists(p))
                {
                    // Try one-level-deep — common single-folder wrap.
                    var entries = Directory.GetFileSystemEntries(src);
                    if (entries.Length == 1 && Directory.Exists(entries[0]))
                    {
                        p = Path.Combine(entries[0], "mod.txt");
                    }
                }
                modTxt = File.Exists(p) ? File.ReadAllText(p) : "";
            }
            else if (File.Exists(src))
            {
                using var arch = new ModArchive();
                if (!arch.Open(src))
                {
                    Log("✗ Couldn't open source archive (not a valid zip?).");
                    return;
                }
                modTxt = arch.ReadText("mod.txt");
            }
            else
            {
                Log($"✗ Source not found: {src}");
                return;
            }

            if (string.IsNullOrEmpty(modTxt))
            {
                Log("(source has no mod.txt — fields left blank)");
                return;
            }

            var parsed = ModArchive.ParseConfigFile(modTxt);
            _modIdBox.Text     = Get(parsed, "mod", "id");
            _nameBox.Text      = Get(parsed, "mod", "name");
            _versionBox.Text   = Get(parsed, "mod", "version");
            _descBox.Text      = Get(parsed, "mod", "description");
            var prio = Get(parsed, "mod", "priority");
            if (int.TryParse(prio, out var pn))
                _priorityBox.Value = Math.Max(_priorityBox.Minimum, Math.Min(_priorityBox.Maximum, pn));
            else _priorityBox.Value = 0;
            _mwIdBox.Text = Get(parsed, "updates", "modworkshop");

            // Pre-tick deps the source already declares. Required +
            // optional sections come in as Godot-array or CSV — let
            // ModEntry.ParseCsvOrArray do the actual parsing.
            var reqExisting = ModEntry.ParseCsvOrArray(Get(parsed, "dependencies", "required"));
            var optExisting = ModEntry.ParseCsvOrArray(Get(parsed, "dependencies", "optional"));
            ApplyDepSelections(reqExisting, optExisting);

            Log($"✓ Loaded manifest ({parsed.Count} sections).");
        }
        catch (Exception ex)
        {
            Log($"✗ Reload failed: {ex.Message}");
        }
    }

    private void ApplyDepSelections(List<string> req, List<string> opt)
    {
        // Existing dep entries may be EITHER manifest mod_ids
        // (the conventional form) OR numeric MW ids (the form our
        // packager falls back to when a dep wasn't resolvable at
        // pack time). Normalise both kinds into a "matches this
        // grid row?" predicate before applying ticks.
        bool RowMatches(DepRow r, string depId)
        {
            if (string.Equals(r.ModId, depId, StringComparison.OrdinalIgnoreCase))
                return true;
            if (r.ModWorkshopId > 0
                && int.TryParse(depId, out var n)
                && n == r.ModWorkshopId)
                return true;
            return false;
        }

        var reqMatchedRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optMatchedRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reqLeftover    = new List<string>();
        var optLeftover    = new List<string>();
        foreach (var dep in req)
        {
            var hit = _depRows.FirstOrDefault(r => RowMatches(r, dep));
            if (hit != null) reqMatchedRows.Add(hit.ModId);
            else             reqLeftover.Add(dep);
        }
        foreach (var dep in opt)
        {
            var hit = _depRows.FirstOrDefault(r => RowMatches(r, dep));
            if (hit != null) optMatchedRows.Add(hit.ModId);
            else             optLeftover.Add(dep);
        }

        for (int i = 0; i < _depGrid.Rows.Count; i++)
        {
            var modId = _depGrid.Rows[i].Cells["ModId"].Value as string ?? "";
            _depGrid.Rows[i].Cells["Req"].Value = reqMatchedRows.Contains(modId);
            _depGrid.Rows[i].Cells["Opt"].Value = optMatchedRows.Contains(modId);
        }
        _customReqBox.Text = string.Join(Environment.NewLine, reqLeftover);
        _customOptBox.Text = string.Join(Environment.NewLine, optLeftover);
    }

    private static string Get(Dictionary<string, Dictionary<string, string>> m, string s, string k)
        => m.TryGetValue(s, out var sec) && sec.TryGetValue(k, out var v) ? v : "";

    // ── Pack ─────────────────────────────────────────────────────

    private void Pack()
    {
        var src = _srcPath.Text.Trim();
        var dst = _outPath.Text.Trim();
        if (string.IsNullOrEmpty(src))
        {
            Log("✗ Pick a source first.");
            return;
        }
        if (string.IsNullOrEmpty(dst))
        {
            Log("✗ Pick an output .vmz path first.");
            return;
        }
        if (!dst.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase))
            dst += ".vmz";

        // Collect dep ids from BOTH the grid + the custom textareas.
        // Deduped (case-insensitive) and ordered: grid picks first
        // (alphabetical, matches the on-screen list), then custom.
        //
        // What lands in mod.txt is ALWAYS the manifest mod_id string.
        // The grid carries it directly (each row knows the mod's
        // manifest id from the registry); the custom text accepts
        // MW numeric ids / URLs, which we resolve to the manifest
        // id via _registry when the dep mod is installed locally.
        // Unresolvable numerics are written verbatim with a warning
        // so the author sees it and can fix later.
        var req = new List<string>();
        var opt = new List<string>();
        var reqSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Pull from FULL row list (not filtered grid) so deps that
        // were ticked then filtered out aren't lost on pack.
        // Read tick state from the PERSISTENT sets, not the grid
        // (which is filtered). Any ticks the user set then hid via
        // the filter box still count toward the packed output.
        foreach (var id in _checkedReq)
            if (reqSeen.Add(id)) req.Add(id);
        foreach (var id in _checkedOpt)
            if (optSeen.Add(id) && !reqSeen.Contains(id)) opt.Add(id);

        // Track dep_id → MW id pairs so the produced .vmz carries
        // a [dependency_sources] section the install-time
        // resolver can use to auto-download these deps without
        // round-tripping to the user for URLs.
        var depSources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Grid ticks: rows know their manifest mod_id AND the
        // MW id from the registry, so this is a direct map.
        foreach (var r in _depRows)
        {
            if (r.ModWorkshopId <= 0) continue;
            if (reqSeen.Contains(r.ModId) || optSeen.Contains(r.ModId))
                depSources[r.ModId] = r.ModWorkshopId;
        }

        // Resolve each custom-text entry: tries MW-id / URL parse
        // first, falls back to treating the raw string as a
        // manifest mod_id (legacy / hand-typed slug form). When
        // the input was MW-shaped AND resolvable to a registry
        // entry, record (resolved_id, mw_id) in depSources too.
        foreach (var raw in ParseCustomList(_customReqBox.Text))
        {
            var (id, mwForSources) = ResolveCustomDepEntry(raw);
            if (string.IsNullOrEmpty(id)) continue;
            if (reqSeen.Add(id)) req.Add(id);
            if (mwForSources > 0) depSources[id] = mwForSources;
        }
        foreach (var raw in ParseCustomList(_customOptBox.Text))
        {
            var (id, mwForSources) = ResolveCustomDepEntry(raw);
            if (string.IsNullOrEmpty(id)) continue;
            if (optSeen.Add(id) && !reqSeen.Contains(id)) opt.Add(id);
            if (mwForSources > 0) depSources[id] = mwForSources;
        }

        var opts = new ModPacker.PackOptions
        {
            ModId       = _modIdBox.Text.Trim(),
            Name        = _nameBox.Text.Trim(),
            Version     = _versionBox.Text.Trim(),
            Description = _descBox.Text.Trim(),
            Priority    = (int)_priorityBox.Value,
            ModWorkshopId = ManifestEditor.ParseModWorkshopIdInput(_mwIdBox.Text.Trim()) is var mw && mw > 0
                ? mw : (int?)null,
            RequiredDependencies = req,
            OptionalDependencies = opt,
            DependencySources    = depSources,
        };

        Log("");
        Log($"Packing → {dst}");
        var result = ModPacker.Pack(src, dst, opts);
        foreach (var line in result.Log)    Log("  " + line);
        foreach (var err in result.Errors)  Log("  ✗ " + err);
        if (result.Success)
        {
            Log($"✓ Done — {result.FilesPacked} file(s), {result.OutputBytes:N0} bytes.");
        }
    }

    /// <summary>Translate a single user-typed dep entry into the
    /// dep id we'll write to mod.txt PLUS the MW numeric id (when
    /// we have one) for the [dependency_sources] sidecar section.
    ///
    /// Input forms:
    ///   • bare numeric id   "56781"
    ///   • full MW URL       "https://modworkshop.net/mod/56781/slug"
    ///   • URL fragment      "modworkshop.net/mod/56781"
    ///   • manifest slug     "mcm" / "weapon-rig-api" / etc.
    ///
    /// Numeric / URL → try the local registry: a row with a
    /// matching `ModWorkshopId` gives us the real manifest mod_id
    /// the in-game loader will recognise. We return BOTH the slug
    /// AND the MW id so the packed mod carries a (slug → MW id)
    /// entry the install-time resolver can use.
    /// On miss we write the numeric id verbatim (loader won't
    /// match, but the manager's resolver can still read the MW id
    /// directly), AND still report the MW id for the sources map.
    ///
    /// Non-numeric input is assumed to already BE a manifest
    /// mod_id and is passed through unchanged — no MW id known.
    /// Returns ("", 0) when the input doesn't parse at all.</summary>
    private (string id, int mwForSources) ResolveCustomDepEntry(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", 0);
        var trimmed = raw.Trim().Trim('"', '\'');

        var mw = ManifestEditor.ParseModWorkshopIdInput(trimmed);
        if (mw <= 0)
        {
            // Not a number / URL. Treat as a manifest mod_id slug.
            // Look up the MW id in the index built from registry +
            // active profile — common when the user types a slug
            // for a mod they HAVE installed locally; the MW id is
            // available even without re-typing the URL.
            var mwFromIndex = _modIdToMwIndex.TryGetValue(trimmed, out var v) ? v : 0;
            return (trimmed, mwFromIndex);
        }

        var match = _registry.Entries.FirstOrDefault(
            e => e.ModWorkshopId == mw && !string.IsNullOrEmpty(e.ModId));
        if (match != null)
        {
            Log($"  ↪ MW {mw} → manifest id '{match.ModId}' (from installed '{match.DisplayName}')");
            return (match.ModId, mw);
        }
        // Unresolvable to a manifest slug. Write the numeric id
        // literal — the loader won't match, but [dependency_sources]
        // still gets the MW id so the install-time resolver can
        // auto-download it.
        Log($"  ⚠ MW {mw}: not in your installed mods — writing '{mw}' literally; "
            + "install the dep locally then re-pack to get the manifest mod_id.");
        return (mw.ToString(), mw);
    }

    private static IEnumerable<string> ParseCustomList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        // Accept both newline and comma separators so the user
        // can paste either style.
        foreach (var part in raw.Split(
            new[] { '\n', '\r', ',' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = part.Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(trimmed)) yield return trimmed;
        }
    }

    private void Log(string m)
    {
        _log.AppendText(m + Environment.NewLine);
        _log.SelectionStart  = _log.TextLength;
        _log.SelectionLength = 0;
        _log.ScrollToCaret();
    }

    // ── Export JSON ─────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonExportOpts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder                = System.Text.Encodings.Web.JavaScriptEncoder
                                    .UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes the current dialog state as a mod-pack
    /// JSON (ModListImport shape) — the file the recipient drops
    /// on the manager to merge this set into their active
    /// profile. NOT a .vmz; complementary output to Build .vmz.
    ///
    /// Contents:
    ///   • Main mod entry (mod_id / name / version /
    ///     mod_workshop_id) — included when at least one of
    ///     mod_id or modworkshop_id is set, so the user can
    ///     build a pack without a "headline" mod (just a list
    ///     of deps) when they want to.
    ///   • Every ticked Required + Optional row from the dep
    ///     grid — read from the persistent tick sets, so a
    ///     filter active when Export is clicked doesn't drop
    ///     ticks for currently-hidden rows.
    ///   • Every custom-text dep entry — MW URLs / numerics
    ///     resolve to their manifest mod_id + MW id via the
    ///     local registry (same path Build .vmz uses), so
    ///     pasted URLs come out as proper id+mw_id pairs in
    ///     the JSON, not as raw numbers.
    ///
    /// Dedup is by mod_id (case-insensitive) primary, MW id
    /// secondary — so the same mod ticked AND pasted as a URL
    /// only shows up once.</summary>
    private void ExportModPackJson()
    {
        // Main mod manifest fields.
        var mainModId  = _modIdBox.Text.Trim();
        var mainName   = _nameBox.Text.Trim();
        var mainVer    = _versionBox.Text.Trim();
        var mainMwId   = ManifestEditor.ParseModWorkshopIdInput(
            _mwIdBox.Text.Trim());

        var entries = new List<ImportEntry>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenMw  = new HashSet<int>();

        void AddEntry(ImportEntry e)
        {
            // Dedup: same mod_id OR same MW id collapses to one.
            if (!string.IsNullOrEmpty(e.ModId))
            {
                if (!seenIds.Add(e.ModId)) return;
            }
            if (e.ModWorkshopId > 0)
            {
                if (!seenMw.Add(e.ModWorkshopId))
                {
                    // Add back to seenIds to keep predicate stable.
                    if (!string.IsNullOrEmpty(e.ModId)) seenIds.Add(e.ModId);
                    return;
                }
            }
            entries.Add(e);
        }

        // Pack mode vs single-mod mode: if there's no source set,
        // the Main mod fields are just naming metadata for the
        // pack (e.g. "My Pack" with mod_id "my_pack"), not an
        // actual mod the recipient should try to install. Skip
        // adding it as a mod entry — the JSON's top-level `name`
        // and `description` still carry the pack label for the
        // import dialog header. The source-set case is "user is
        // packaging a real mod that also bundles deps", and the
        // main entry IS a real mod worth exporting.
        var hasSource = !string.IsNullOrEmpty(_srcPath.Text.Trim());
        if (hasSource && (!string.IsNullOrEmpty(mainModId) || mainMwId > 0))
        {
            // The Main mod is always required — it's the headline
            // of a single-mod pack. Leaving IsOptional null skips
            // the field on serialize (cleaner JSON; old importers
            // already read missing as required).
            AddEntry(new ImportEntry
            {
                ModId         = mainModId,
                DisplayName   = !string.IsNullOrEmpty(mainName) ? mainName : mainModId,
                ModWorkshopId = mainMwId,
                Version       = mainVer,
            });
        }

        // Index dep rows by mod_id for quick MW + name lookup
        // when a tick is set.
        var depByModId = _depRows.ToDictionary(
            r => r.ModId, StringComparer.OrdinalIgnoreCase);

        void AddFromTickedSet(IEnumerable<string> ticked, bool isOptional)
        {
            foreach (var id in ticked)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!depByModId.TryGetValue(id, out var r)) continue;
                AddEntry(new ImportEntry
                {
                    ModId         = r.ModId,
                    DisplayName   = r.DisplayName,
                    ModWorkshopId = r.ModWorkshopId,
                    // Only emit the flag when truly optional —
                    // keeps required entries' JSON clean and
                    // round-trips cleanly with pre-flag exports.
                    IsOptional    = isOptional ? true : (bool?)null,
                });
            }
        }
        // Required FIRST so AddEntry's dedup (first-write-wins) keeps
        // them required if the same id is somehow ticked in both
        // buckets — should be impossible given the grid's mutual-
        // exclusion, but the persistent sets are independent so a
        // hand-edited state could trip it.
        AddFromTickedSet(_checkedReq, isOptional: false);
        AddFromTickedSet(_checkedOpt, isOptional: true);

        // Custom text — required textbox emits IsOptional=false,
        // optional textbox emits IsOptional=true. This is the
        // round-trip surface that lets a recipient see the
        // author's intent: "must have" vs "nice to have".
        void AddFromCustomBox(string raw, bool isOptional)
        {
            foreach (var token in ParseCustomList(raw))
            {
                var (id, mwForSources) = ResolveCustomDepEntry(token);
                if (string.IsNullOrEmpty(id) && mwForSources <= 0) continue;
                // ResolveCustomDepEntry returns either (slug, 0) for
                // typed slugs we couldn't resolve to a MW id, or
                // (slug, mw) when we matched against the registry,
                // or (numeric_string, mw) when the MW id wasn't
                // resolvable to a slug. The JSON entry mirrors the
                // best info we have.
                string entryId = id;
                int mwId = mwForSources;
                // If id is purely numeric (unresolved MW fallback),
                // leave it empty in the JSON — recipient resolves
                // via mod_workshop_id alone.
                if (int.TryParse(entryId, out _)) entryId = "";
                AddEntry(new ImportEntry
                {
                    ModId         = entryId,
                    ModWorkshopId = mwId,
                    // Same null-when-required rule — keeps the
                    // emitted JSON minimal.
                    IsOptional    = isOptional ? true : (bool?)null,
                });
            }
        }
        AddFromCustomBox(_customReqBox.Text, isOptional: false);
        AddFromCustomBox(_customOptBox.Text, isOptional: true);

        if (entries.Count == 0)
        {
            Log("✗ Nothing to export. Fill in Main mod or tick at least one dependency.");
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title    = "Export mod pack JSON",
            Filter   = "Mod pack (*.json)|*.json",
            FileName = SuggestedExportFilename(),
            OverwritePrompt = true,
        };
        var src = _srcPath.Text.Trim();
        if (!string.IsNullOrEmpty(src))
        {
            var dir = Directory.Exists(src)
                ? Path.GetDirectoryName(src) ?? src
                : Path.GetDirectoryName(src) ?? "";
            if (!string.IsNullOrEmpty(dir)) dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var import = new ModListImport
        {
            Name = !string.IsNullOrEmpty(mainName)
                ? mainName
                : (!string.IsNullOrEmpty(mainModId) ? mainModId : "Mod pack"),
            Description = _descBox.Text.Trim(),
            Mods = entries,
        };

        try
        {
            var json = JsonSerializer.Serialize(import, _jsonExportOpts);
            File.WriteAllText(dlg.FileName, json);
            Log($"📤 Exported {entries.Count} mod(s) → {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Log($"✗ Export failed: {ex.Message}");
        }
    }

    private string SuggestedExportFilename()
    {
        var id = _modIdBox.Text.Trim();
        if (string.IsNullOrEmpty(id))
        {
            var name = _nameBox.Text.Trim();
            id = !string.IsNullOrEmpty(name) ? name : "mod-pack";
        }
        return $"{ModProfile.SafeFileName(id)}-pack.json";
    }

    // ── Load pack JSON ──────────────────────────────────────────

    private void OpenPackJsonFromPicker()
    {
        using var dlg = new OpenFileDialog
        {
            Title       = "Open mod pack JSON",
            Filter      = "Mod pack (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true,
        };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var downloads = Path.Combine(home, "Downloads");
            if (Directory.Exists(downloads)) dlg.InitialDirectory = downloads;
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        LoadPackJsonFromPath(dlg.FileName);
    }

    /// <summary>Parse `path` as a ModListImport-shaped pack JSON
    /// and drive the dialog's dep state from it. Called by the
    /// Open pack JSON button AND the form-level drag-drop when a
    /// `.json` lands on the dialog. Existing tick state + custom
    /// textbox content gets a confirmation prompt before being
    /// replaced; on Yes we wipe everything dep-related and rebuild
    /// from the file. Manifest fields (mod_id / version / etc.)
    /// are NOT touched — those describe the "headline" mod a user
    /// is packaging, not the contents of a pack list. The pack's
    /// `name` and `description` populate the Name / Description
    /// fields though, so a round-trip preserves them.</summary>
    public void LoadPackJsonFromPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Log($"✗ Open failed: file not found — {path}");
            return;
        }

        ModListImport import;
        try
        {
            import = ModListImport.LoadFromFile(path);
        }
        catch (Exception ex)
        {
            Log($"✗ Open failed: {ex.Message}");
            return;
        }

        // Warn before clobbering existing user input. "Has content"
        // = any ticks set OR any custom-textbox content.
        bool hasExisting = _checkedReq.Count > 0
                        || _checkedOpt.Count > 0
                        || !string.IsNullOrWhiteSpace(_customReqBox.Text)
                        || !string.IsNullOrWhiteSpace(_customOptBox.Text);
        if (hasExisting)
        {
            var dr = ThemedMessageBox.Show(this,
                "Opening a pack JSON will REPLACE your current dependency "
                + "selections (ticks + custom mod_ids). Manifest fields "
                + "(mod_id, name, version, etc.) stay as-is.\n\nContinue?",
                "Replace dependencies?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (dr != DialogResult.Yes) return;
        }

        // Clear current dep state.
        _checkedReq.Clear();
        _checkedOpt.Clear();
        _customReqBox.Text = "";
        _customOptBox.Text = "";

        // Carry over the pack's name + description so a re-export
        // preserves them. Only overwrite if the JSON had something —
        // a hand-authored file with no top-level name shouldn't
        // wipe a label the user already typed.
        if (!string.IsNullOrEmpty(import.Name))        _nameBox.Text = import.Name;
        if (!string.IsNullOrEmpty(import.Description)) _descBox.Text = import.Description;

        // Flatten so any nested `dependencies` arrays get pulled
        // up to the same level — the Mod Packager treats the pack
        // as a flat list when authoring.
        var flat = import.Flatten();
        var customReqLines = new List<string>();
        var customOptLines = new List<string>();
        int reqCount = 0, optCount = 0, skippedAsMeta = 0;
        foreach (var fe in flat)
        {
            var entry = fe.Entry;

            // Detect "pack metadata leaked into mods[]" — older
            // exports (pre-0.5.37) wrote the dialog's Main mod
            // manifest fields as the first mod entry even when no
            // source was set. Signature: an entry whose
            // display_name matches the pack's top-level `name` AND
            // has no mod_workshop_id. Real mods can share a name
            // with the pack but they'll have an MW id; the no-MW
            // + name-match pair is unambiguous "this is the pack
            // talking about itself." Skip with a log so the user
            // sees what happened.
            if (entry.ModWorkshopId <= 0
                && !string.IsNullOrEmpty(import.Name)
                && !string.IsNullOrEmpty(entry.DisplayName)
                && string.Equals(entry.DisplayName, import.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                skippedAsMeta++;
                continue;
            }

            // Surface every JSON entry in EITHER the Required or
            // Optional textbox depending on its is_optional flag.
            // Missing flag → required (back-compat with pre-0.5.66
            // exports). Textboxes are the visible "what's in this
            // pack" view: open a pack → read the boxes → see
            // exactly what's required vs nice-to-have. Editing the
            // textboxes is the editing surface. Dep-grid ticks
            // remain a separate manual add path for installed-only
            // mods the user wants in the pack.
            //
            // Prefer the mod_id SLUG over the numeric MW id since
            // the slug is the readable identifier. MW id is
            // preserved separately via _modIdToMwIndex below, so
            // re-export still emits it.
            string? line = null;
            if (!string.IsNullOrEmpty(entry.ModId))
                line = entry.ModId;
            else if (entry.ModWorkshopId > 0)
                line = entry.ModWorkshopId.ToString();
            else
                continue;

            // Treat null IsOptional as required so older pack
            // JSONs (no flag in the schema) keep landing in the
            // Required box exactly as before.
            var optional = entry.IsOptional == true;
            if (optional) { customOptLines.Add(line); optCount++; }
            else          { customReqLines.Add(line); reqCount++; }

            // Persist the JSON's (slug → MW id) mapping into the
            // packager's index so the next Export round-trips the
            // MW id even though the textbox only shows the slug.
            // Without this, reopen → re-export would silently drop
            // the MW id for any custom-text entry whose mod isn't
            // also installed locally.
            if (!string.IsNullOrEmpty(entry.ModId)
                && entry.ModWorkshopId > 0)
                _modIdToMwIndex[entry.ModId] = entry.ModWorkshopId;
        }
        _customReqBox.Text = string.Join(Environment.NewLine, customReqLines);
        _customOptBox.Text = string.Join(Environment.NewLine, customOptLines);

        // Force the grid to repaint so any stale ticks from prior
        // session state are cleared (we cleared _checkedReq /
        // _checkedOpt earlier in the method; the grid still needs
        // to redraw with no ticks).
        PopulateDepGrid(_depFilterBox.Text);

        var metaNote = skippedAsMeta > 0
            ? $", {skippedAsMeta} pack-metadata row(s) skipped"
            : "";
        Log($"📂 Loaded pack '{import.Name}' from {Path.GetFileName(path)} "
            + $"— {reqCount} required + {optCount} optional{metaNote}.");
    }
}
