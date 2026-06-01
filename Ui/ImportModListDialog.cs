// "Import mod list" — batch-adds a flat list of mods (from a JSON
// file) into the currently-active profile. Distinct from the Apply
// Profile flow: this never wipes anything, never changes the active
// profile, never touches mods that aren't in the import list.
//
// Per-row planning:
//   ✓  Already in profile  — same mod_id is already a ProfileMod;
//                            no file or json change.
//   ↪  Live, add to profile — live .vmz on disk but not in profile;
//                            updates profile.json only.
//   📚  From library       — already-downloaded copy of (mod_id,
//                            any version) exists under Library/;
//                            copy into live + add to profile.
//   ⬇  Download via MW    — not installed anywhere; download via the
//                            entry's mod_workshop_id, ensure-in-lib,
//                            add to profile.
//   ✗  Cannot resolve      — no mod_id match, no MW id; informational
//                            only, skipped.
//
// Mirrors ProfileApplyDialog's two-pane shape (plan grid + progress
// log) so the user gets a consistent "see before you do" surface
// across every batch op.

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ImportModListDialog : Form
{
    public bool Applied { get; private set; }

    /// <summary>Manifest mod_ids of every mod the import actually
    /// installed or refreshed against the registry — populated in
    /// Phase 3 of RunImportAsync. Caller uses this to scan declared
    /// dependencies after the dialog closes (the imported mods may
    /// declare deps the import list didn't itself include).</summary>
    public List<string> InstalledModIds { get; } = new();

    // ── Inputs ───────────────────────────────────────────────────────
    private readonly ModListImport      _import;
    private readonly ModProfile         _profile;     // active profile (caller asserts non-null)
    private readonly ModRegistry        _registry;
    private readonly ModWorkshopClient  _mw;
    private readonly string             _modsDir;

    // ── Plan rows ────────────────────────────────────────────────────
    private enum RowKind { AlreadyInProfile, LivePresent, FromLibrary, Download, Unresolvable }

    private record PlanRow(
        RowKind      Kind,
        ImportEntry  Entry,
        List<string> ParentChain,   // empty = top-level entry; else lineage
        ModEntry?    Live,
        string       LibraryPath,
        string       StatusIcon,
        string       StatusText,
        bool         OptIn);

    private List<PlanRow> _plan = new();

    // ── Controls ─────────────────────────────────────────────────────
    private DataGridView _grid = null!;
    private Label        _summaryLabel = null!;
    private Button       _applyBtn = null!;
    private Button       _cancelBtn = null!;
    private Panel        _progressPanel = null!;
    private ProgressBar  _progressBar = null!;
    private TextBox      _log = null!;
    private bool         _running;
    private CancellationTokenSource? _cts;

    /// <summary>Optional companion directory the dialog scans for
    /// `.vmz` files to recover MW ids when the JSON doesn't carry
    /// them. Typically Path.GetDirectoryName(jsonPath) — same
    /// folder the import file lives in, where a user dropping
    /// JSON + .vmz files together expects them to be considered.
    /// Null skips the scan; the import still works via library
    /// + JSON fields alone.</summary>
    private readonly string? _companionDir;

    public ImportModListDialog(
        ModListImport      import,
        ModProfile         activeProfile,
        ModRegistry        registry,
        ModWorkshopClient  mw,
        string             modsDir,
        string?            companionDir = null)
    {
        _import       = import;
        _profile      = activeProfile;
        _registry     = registry;
        _mw           = mw;
        _modsDir      = modsDir;
        _companionDir = companionDir;

        BuildPlan();
        InitUi();
    }

    private void BuildPlan()
    {
        // Live index by mod_id for fast lookup. Live registry holds
        // ModEntry per installed .vmz / directory.
        var liveById = new Dictionary<string, ModEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var e in _registry.Entries)
            if (!string.IsNullOrEmpty(e.ModId)) liveById[e.ModId] = e;

        // Profile mod_ids — anything already here is a no-op for the
        // profile-add step (we may still want to ensure it's live if
        // somehow missing, but typical case is "already covered").
        var profileIds = new HashSet<string>(
            _profile.Mods.Select(m => m.ModId),
            StringComparer.OrdinalIgnoreCase);

        // Library snapshot keyed by mod_id → newest entry. Imports
        // tend to use slug ids that match library ids since the
        // library is keyed by the manifest mod_id; if the import id
        // doesn't match the library, the download path takes over.
        var libByMod = new Dictionary<string, ModLibrary.Entry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var le in ModLibrary.List(_modsDir))
        {
            if (string.IsNullOrEmpty(le.ModId)) continue;
            if (!libByMod.TryGetValue(le.ModId, out var cur)
                || ModRegistry.CompareVersions(le.Version, cur.Version) > 0)
                libByMod[le.ModId] = le;
        }

        // Fallback MW-id map for entries whose JSON omitted the
        // mod_workshop_id. Sources (highest priority first):
        //   • live registry mods carrying [updates] modworkshop
        //   • library .vmz files (read at List() time)
        //   • .vmz files sitting alongside the JSON (_companionDir),
        //     so a "drop the JSON + the .vmz files together" flow
        //     can recover MW ids the JSON didn't bother to record.
        // Last source wins so the companion dir's .vmz (most
        // recently authored) overrules older snapshots.
        var fallbackMwIds = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var e in _registry.Entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (e.ModWorkshopId > 0) fallbackMwIds[e.ModId] = e.ModWorkshopId;
        }
        foreach (var le in libByMod.Values)
        {
            if (le.ModWorkshopId > 0)
                fallbackMwIds[le.ModId] = le.ModWorkshopId;
        }
        if (!string.IsNullOrEmpty(_companionDir)
            && Directory.Exists(_companionDir))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(
                    _companionDir, "*.vmz", SearchOption.TopDirectoryOnly))
                {
                    using var arch = new ModArchive();
                    if (!arch.Open(f)) continue;
                    var id = arch.ModId;
                    var mw = arch.ModWorkshopId;
                    if (!string.IsNullOrEmpty(id) && mw > 0)
                        fallbackMwIds[id] = mw;
                }
            }
            catch { /* best-effort; bad permissions / locked files shouldn't block import */ }
        }

        var flat = _import.Flatten();
        // Patch missing MW ids in-place — Flatten gave us mutable
        // entries, so the rest of BuildPlan reads the augmented
        // values directly. Empty mod_id stays empty (we have no
        // way to look it up).
        foreach (var fe in flat)
        {
            var ent = fe.Entry;
            if (ent.ModWorkshopId > 0) continue;
            if (string.IsNullOrEmpty(ent.ModId)) continue;
            if (fallbackMwIds.TryGetValue(ent.ModId, out var mw) && mw > 0)
                ent.ModWorkshopId = mw;
        }
        // Stable, predictable ordering: top-level entries first (so
        // the user reads them as the headline mods of the import),
        // then transitively-included deps, then alphabetical within
        // each tier. Without an explicit sort the order depends on
        // dictionary insertion which is hash-y for large lists.
        flat.Sort((a, b) =>
        {
            var topCmp = a.IsTopLevel == b.IsTopLevel
                ? 0
                : (a.IsTopLevel ? -1 : 1);
            if (topCmp != 0) return topCmp;
            return string.Compare(
                a.Entry.DisplayName ?? "", b.Entry.DisplayName ?? "",
                StringComparison.OrdinalIgnoreCase);
        });
        _plan = new List<PlanRow>();
        foreach (var flat_ in flat)
        {
            var entry = flat_.Entry;
            var parentChain = flat_.ParentChain;
            // Resolve a usable live ModEntry: prefer mod_id, then
            // ModWorkshopId match (recovers when the import uses a
            // URL slug rather than the manifest id).
            ModEntry? live = null;
            if (!string.IsNullOrEmpty(entry.ModId))
                liveById.TryGetValue(entry.ModId, out live);
            if (live == null && entry.ModWorkshopId > 0)
                live = _registry.Entries.FirstOrDefault(
                    e => e.ModWorkshopId == entry.ModWorkshopId
                      && !string.IsNullOrEmpty(e.ModId));

            // Library path (same dual lookup).
            string libPath = "";
            if (!string.IsNullOrEmpty(entry.ModId)
                && libByMod.TryGetValue(entry.ModId, out var le))
                libPath = le.Path;
            if (string.IsNullOrEmpty(libPath) && entry.ModWorkshopId > 0)
            {
                // No id index for MW id in library; fall back to a
                // linear scan once per row (library is typically
                // small — well under 200 entries even for power users).
                foreach (var l in ModLibrary.List(_modsDir))
                {
                    if (l.ModId == "") continue;
                    // We can't read MW id from a library Entry —
                    // ModLibrary doesn't surface it. Skip the MW-id
                    // fallback for the library; the download path
                    // will handle this case fine.
                    break;
                }
            }

            // Is this id already in the profile?
            bool inProfile = false;
            if (!string.IsNullOrEmpty(entry.ModId)
                && profileIds.Contains(entry.ModId))
                inProfile = true;
            // Profile-by-MW-id fallback. Same reason as the live
            // lookup — slug id mismatches.
            if (!inProfile && entry.ModWorkshopId > 0)
            {
                var match = _profile.Mods.FirstOrDefault(
                    m => m.ModWorkshopId == entry.ModWorkshopId
                      && !string.IsNullOrEmpty(m.ModId));
                if (match != null) inProfile = true;
            }

            string label = !string.IsNullOrEmpty(entry.DisplayName)
                ? entry.DisplayName
                : (!string.IsNullOrEmpty(entry.ModId)
                    ? entry.ModId
                    : (entry.ModWorkshopId > 0
                        ? $"MW {entry.ModWorkshopId}"
                        : "(unknown)"));

            // Optional mods default to UNticked so the user
            // explicitly opts INTO each nice-to-have; required
            // mods default to ticked (the pack author flagged
            // them as essential). Pre-0.5.66 JSONs have no flag,
            // which Flatten leaves as null — treat as required.
            var optional   = entry.IsOptional == true;
            var defaultOptIn = !optional;

            PlanRow row;
            if (inProfile && live != null)
            {
                row = new PlanRow(RowKind.AlreadyInProfile, entry, parentChain, live, libPath,
                    "✓", "Already in profile and live", false);
            }
            else if (inProfile)
            {
                // In profile but the file isn't live — surface that
                // so the user knows applying / switching will need
                // to fetch it. We won't auto-fix here; an Apply
                // round does the right thing.
                row = new PlanRow(RowKind.AlreadyInProfile, entry, parentChain, null, libPath,
                    "✓", "Already in profile (live missing — apply to populate)", false);
            }
            else if (live != null)
            {
                row = new PlanRow(RowKind.LivePresent, entry, parentChain, live, libPath,
                    "↪", "Live on disk — will add entry to profile", defaultOptIn);
            }
            else if (!string.IsNullOrEmpty(libPath))
            {
                row = new PlanRow(RowKind.FromLibrary, entry, parentChain, null, libPath,
                    "📚", "Will install from library (already downloaded)", defaultOptIn);
            }
            else if (entry.ModWorkshopId > 0)
            {
                row = new PlanRow(RowKind.Download, entry, parentChain, null, "",
                    "⬇", "Will download from ModWorkshop", defaultOptIn);
            }
            else
            {
                row = new PlanRow(RowKind.Unresolvable, entry, parentChain, null, "",
                    "✗", "No mod_workshop_id — cannot download", false);
            }
            _plan.Add(row);
        }
    }

    private bool IsCheckable(RowKind k) =>
        k is RowKind.LivePresent
            or RowKind.FromLibrary
            or RowKind.Download;

    // ── UI ───────────────────────────────────────────────────────────

    private void InitUi()
    {
        // Title bar threads the PACK name (source) and the
        // active profile name (destination) so the user reads
        // "what am I importing → where is it going" at a glance.
        // Falls back to the generic phrasing when the JSON didn't
        // carry a name.
        Text = !string.IsNullOrEmpty(_import.Name)
            ? $"Import '{_import.Name}' → '{_profile.Name}'"
            : $"Import mod list — adding to '{_profile.Name}'";
        MinimumSize    = new Size(980, 600);
        Width          = 1180;
        Height         = 700;
        StartPosition  = FormStartPosition.CenterParent;
        BackColor      = Color.FromArgb(26, 30, 40);
        ForeColor      = Color.FromArgb(220, 225, 235);
        Font           = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar  = false;

        var title = new Label
        {
            Text      = !string.IsNullOrEmpty(_import.Name)
                ? $"Import '{_import.Name}'  →  '{_profile.Name}'"
                : $"Import mod list → '{_profile.Name}'",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 4),
        };

        _summaryLabel = new Label
        {
            Text      = BuildSummaryText(),
            Dock      = DockStyle.Top,
            AutoSize  = true,
            ForeColor = Color.FromArgb(170, 185, 210),
            Margin    = new Padding(0, 0, 0, 10),
        };

        _grid = BuildGrid();
        var selectionBar = BuildSelectionBar();
        _progressPanel = BuildProgressPanel();
        _progressPanel.Visible = false;

        var btnRow = BuildButtonRow();

        var content = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 5,
            Padding     = new Padding(16, 12, 16, 12),
            BackColor   = Color.Transparent,
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(title,          0, 0);
        content.Controls.Add(_summaryLabel,  0, 1);
        content.Controls.Add(selectionBar,   0, 2);
        content.Controls.Add(_grid,          0, 3);
        content.Controls.Add(_progressPanel, 0, 4);

        // Add content FIRST so it docks last and the button row
        // doesn't clip its bottom edge — same ordering rule as
        // ProfileApplyDialog.
        Controls.Add(content);
        Controls.Add(btnRow);

        CancelButton = _cancelBtn;
    }

    private string BuildSummaryText()
    {
        int already   = _plan.Count(r => r.Kind == RowKind.AlreadyInProfile);
        int live      = _plan.Count(r => r.Kind == RowKind.LivePresent);
        int fromLib   = _plan.Count(r => r.Kind == RowKind.FromLibrary);
        int downloads = _plan.Count(r => r.Kind == RowKind.Download);
        int blocked   = _plan.Count(r => r.Kind == RowKind.Unresolvable);
        int topLevel  = _plan.Count(r => r.ParentChain.Count == 0);
        int transit   = _plan.Count - topLevel;
        int requiredN = _plan.Count(r => r.Entry.IsOptional != true);
        int optionalN = _plan.Count - requiredN;
        var parts = new List<string>();
        if (already   > 0) parts.Add($"{already} already in profile");
        if (live      > 0) parts.Add($"{live} live → add to profile");
        if (fromLib   > 0) parts.Add($"{fromLib} from library");
        if (downloads > 0) parts.Add($"{downloads} to download");
        if (blocked   > 0) parts.Add($"{blocked} unresolvable");
        var head = string.IsNullOrEmpty(_import.Description)
            ? "" : $"  \"{_import.Description}\"  ";
        var lineage = transit > 0
            ? $"  ({topLevel} top-level, {transit} via deps)"
            : "";
        // Surface the required/optional split when the pack
        // actually has optionals — otherwise the line is noise.
        var split = optionalN > 0
            ? $"  · {requiredN} required + {optionalN} optional (optionals unticked by default)"
            : "";
        return $"{head}{string.Join(", ", parts)} — {_plan.Count} entries total{lineage}{split}.";
    }

    private DataGridView BuildGrid()
    {
        var grid = new DataGridView
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
            ColumnHeadersHeight = 36,
            RowTemplate         = { Height = 30 },
        };
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name       = "Apply",
            HeaderText = "✓",
            Width      = 42,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "Icon",
            HeaderText = "",
            Width      = 36,
            ReadOnly   = true,
            DefaultCellStyle =
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI Symbol", 14f),
            },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name         = "ModName",
            HeaderText   = "Mod",
            ReadOnly     = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight   = 50,
            MinimumWidth = 160,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "ModId",
            HeaderText = "mod_id",
            Width      = 200,
            ReadOnly   = true,
        });
        // "Kind": Required vs Optional badge. Colour-coded the
        // same way the Mod Packager labels the two textboxes
        // (amber for Required = "you need this", blue for
        // Optional = "nice to have") so the colour vocabulary is
        // consistent across both halves of the round-trip.
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "Kind",
            HeaderText = "Kind",
            Width      = 90,
            ReadOnly   = true,
            DefaultCellStyle =
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
            },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "MwId",
            HeaderText = "MW",
            Width      = 70,
            ReadOnly   = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        // "Via": lineage column. Empty for top-level entries; for
        // transitively-included rows shows "↳ dep of X" or
        // "↳ dep of X → Y" (full chain joined by →, truncated).
        // Lets the user see at a glance which rows came from a
        // parent's dependencies vs. were listed at the top level.
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "Via",
            HeaderText = "Via",
            Width      = 220,
            ReadOnly   = true,
            DefaultCellStyle =
            {
                ForeColor = Color.FromArgb(150, 165, 195),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
            },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name         = "Status",
            HeaderText   = "Status",
            ReadOnly     = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight   = 50,
            MinimumWidth = 220,
        });
        foreach (DataGridViewColumn col in grid.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;

        for (int i = 0; i < _plan.Count; i++)
        {
            var row = _plan[i];
            var idx = grid.Rows.Add();
            var r = grid.Rows[idx];
            var checkable = IsCheckable(row.Kind);
            r.Cells["Apply"].Value     = checkable ? row.OptIn : (object?)null;
            r.Cells["Apply"].ReadOnly  = !checkable;
            r.Cells["Icon"].Value      = row.StatusIcon;
            r.Cells["ModName"].Value   = string.IsNullOrEmpty(row.Entry.DisplayName)
                ? row.Entry.ModId : row.Entry.DisplayName;
            r.Cells["ModId"].Value     = row.Entry.ModId;
            r.Cells["MwId"].Value      = row.Entry.ModWorkshopId > 0
                ? row.Entry.ModWorkshopId.ToString() : "—";
            // Default-null IsOptional reads as "required" — same
            // back-compat rule the rest of the importer uses.
            var isOptional = row.Entry.IsOptional == true;
            r.Cells["Kind"].Value = isOptional ? "Optional" : "Required";
            r.Cells["Kind"].Style.ForeColor = isOptional
                ? Color.FromArgb(170, 200, 240)  // blue — nice-to-have
                : Color.FromArgb(245, 180, 110); // amber — essential
            r.Cells["Via"].Value       = FormatLineage(row.ParentChain);
            r.Cells["Status"].Value    = row.StatusText;
            ApplyKindStyle(grid, idx, row.Kind);
        }

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (!grid.IsCurrentCellDirty) return;
            if (grid.CurrentCell is DataGridViewCheckBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        // Hide the Via column when no entry has a parent chain
        // — flat mod packs (no nested `dependencies` arrays) have
        // nothing to surface there, so the column is just dead
        // space. Auto-hiding keeps the grid focused on the
        // identifying columns when lineage isn't relevant.
        var anyLineage = _plan.Any(r => r.ParentChain.Count > 0);
        grid.Columns["Via"].Visible = anyLineage;

        return grid;
    }

    private static void ApplyKindStyle(DataGridView grid, int idx, RowKind kind)
    {
        var fg = kind switch
        {
            RowKind.AlreadyInProfile => Color.FromArgb(120, 200, 130),
            RowKind.LivePresent      => Color.FromArgb(200, 180, 255),
            RowKind.FromLibrary      => Color.FromArgb(140, 220, 210),
            RowKind.Download         => Color.FromArgb(120, 170, 255),
            RowKind.Unresolvable     => Color.FromArgb(245, 130, 120),
            _                        => Color.FromArgb(220, 225, 235),
        };
        var r = grid.Rows[idx];
        r.Cells["Status"].Style.ForeColor = fg;
        r.Cells["Icon"].Style.ForeColor   = fg;
    }

    private Panel BuildProgressPanel()
    {
        var p = new Panel
        {
            Dock      = DockStyle.Fill,
            Height    = 220,
            BackColor = Color.Transparent,
            Padding   = new Padding(0, 0, 0, 10),
        };
        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Top, Height = 24,
            Minimum = 0, Maximum = 100,
            Style = ProgressBarStyle.Continuous,
            BackColor = Color.FromArgb(30, 36, 48),
            ForeColor = Color.FromArgb(90, 160, 100),
        };
        _log = new TextBox
        {
            Dock        = DockStyle.Fill,
            Multiline   = true, ReadOnly = true,
            ScrollBars  = ScrollBars.Vertical,
            BackColor   = Color.FromArgb(18, 22, 30),
            ForeColor   = Color.FromArgb(180, 220, 160),
            Font        = new Font("Consolas", 11f),
            BorderStyle = BorderStyle.None,
        };
        p.Controls.Add(_log);
        p.Controls.Add(_progressBar);
        return p;
    }

    /// <summary>Selection chips above the grid — All / None /
    /// Required only. Operate only on rows whose "Apply" cell is
    /// checkable (live / library / download); locked rows
    /// (already-in-profile / unresolvable) are skipped so the
    /// chips can't accidentally show "all selected" when there
    /// are uncheckable rows mixed in.</summary>
    private Panel BuildSelectionBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            AutoSize      = true,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor     = Color.Transparent,
            Margin        = new Padding(0, 0, 0, 6),
        };

        bar.Controls.Add(new Label
        {
            Text      = "Select:",
            AutoSize  = true,
            ForeColor = Color.FromArgb(170, 185, 210),
            BackColor = Color.Transparent,
            Margin    = new Padding(0, 7, 8, 0),
            Font      = new Font("Segoe UI", 11f),
        });

        Button Chip(string text, Color accent)
        {
            var b = MainForm.ThemedButton(text);
            b.Width    = 0;       // measured by AutoSize
            b.Height   = 28;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding  = new Padding(10, 0, 10, 0);
            b.Margin   = new Padding(0, 2, 6, 2);
            b.Font     = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            b.FlatAppearance.BorderColor = accent;
            return b;
        }

        var allBtn   = Chip("All",           Color.FromArgb(90, 160, 100));
        var noneBtn  = Chip("None",          Color.FromArgb(150, 160, 170));
        var reqBtn   = Chip("Required only", Color.FromArgb(245, 180, 110));

        allBtn.Click  += (_, _) => SetAllApply(includeOptional: true,  ticked: true);
        noneBtn.Click += (_, _) => SetAllApply(includeOptional: true,  ticked: false);
        reqBtn.Click  += (_, _) => SetAllApply(includeOptional: false, ticked: true);

        bar.Controls.Add(allBtn);
        bar.Controls.Add(noneBtn);
        bar.Controls.Add(reqBtn);
        return bar;
    }

    /// <summary>Bulk-toggle the Apply checkbox across every
    /// checkable row. Rows whose plan kind isn't checkable
    /// (already-in-profile, unresolvable) are left alone — their
    /// Apply cell is read-only and bears no actionable value.
    /// `includeOptional=false` skips rows the pack flagged as
    /// optional, so "Required only" reads as the obvious thing.
    /// `ticked` decides on vs off uniformly.</summary>
    private void SetAllApply(bool includeOptional, bool ticked)
    {
        if (_grid == null) return;
        for (int i = 0; i < _plan.Count && i < _grid.Rows.Count; i++)
        {
            var row = _plan[i];
            if (!IsCheckable(row.Kind)) continue;
            var optional = row.Entry.IsOptional == true;
            if (!includeOptional && optional)
            {
                // "Required only" both UNticks optionals AND ticks
                // requireds, so a single click guarantees the final
                // state matches the chip label regardless of where
                // ticks were before.
                _grid.Rows[i].Cells["Apply"].Value = false;
                continue;
            }
            _grid.Rows[i].Cells["Apply"].Value = ticked;
        }
    }

    private Panel BuildButtonRow()
    {
        var p = new Panel
        {
            Dock = DockStyle.Bottom, Height = 56,
            BackColor = Color.FromArgb(30, 34, 44),
        };
        _applyBtn = MainForm.ThemedButton("▶ Import");
        _applyBtn.Width  = 140;
        _applyBtn.Height = 40;
        _applyBtn.AutoSize  = false;
        _applyBtn.BackColor = Color.FromArgb(45, 90, 55);
        _applyBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _applyBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _applyBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 115, 70);
        _applyBtn.Anchor  = AnchorStyles.Right | AnchorStyles.Top;
        _applyBtn.Click  += async (_, _) => await RunImportAsync();

        _cancelBtn = MainForm.ThemedButton("Cancel");
        _cancelBtn.Width  = 100;
        _cancelBtn.Height = 40;
        _cancelBtn.AutoSize = false;
        _cancelBtn.Anchor   = AnchorStyles.Right | AnchorStyles.Top;
        _cancelBtn.Click   += (_, _) => Close();

        p.Resize += (_, _) =>
        {
            _cancelBtn.Top  = 8;
            _applyBtn.Top   = 8;
            _cancelBtn.Left = p.Width - _cancelBtn.Width - 16;
            _applyBtn.Left  = _cancelBtn.Left - _applyBtn.Width - 8;
        };
        p.Controls.Add(_applyBtn);
        p.Controls.Add(_cancelBtn);
        return p;
    }

    // ── Execute ──────────────────────────────────────────────────────

    private async Task RunImportAsync()
    {
        if (_running) return;
        _running = true;
        _applyBtn.Enabled = false;
        _progressPanel.Visible = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Snapshot opt-in state per row.
        var actions = new List<(PlanRow row, bool optIn)>();
        for (int i = 0; i < _plan.Count; i++)
        {
            var row = _plan[i];
            var optIn = IsCheckable(row.Kind)
                && _grid.Rows[i].Cells["Apply"].Value is true;
            actions.Add((row, optIn));
        }

        var totalSteps = actions.Count(a => a.optIn) + 2; // ops + rescan + save
        var step = 0;

        void Log(string m)
        {
            _log.AppendText(m + Environment.NewLine);
            _log.SelectionStart  = _log.TextLength;
            _log.SelectionLength = 0;
            _log.ScrollToCaret();
        }
        void Advance(string m)
        {
            step = Math.Min(step + 1, totalSteps);
            _progressBar.Value = (int)((double)step / Math.Max(1, totalSteps) * 100);
            Log(m);
        }

        var profileDirty = false;

        // Library copies first (no network), then downloads.
        foreach (var (row, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            if (!optIn) continue;
            if (row.Kind != RowKind.FromLibrary) continue;
            try
            {
                var liveName = LiveFilenameFor(row.Entry);
                var dst      = Path.Combine(_modsDir, liveName);
                if (File.Exists(dst)) dst = Path.Combine(
                    _modsDir, MakeUniqueName(liveName));
                File.Copy(row.LibraryPath, dst, overwrite: false);
                Advance($"  📚 {DisplayLabel(row.Entry)} → {Path.GetFileName(dst)}");
            }
            catch (Exception ex)
            {
                Advance($"  ✗ {DisplayLabel(row.Entry)}: library copy failed — {ex.Message}");
            }
        }

        // MW downloads.
        foreach (var (row, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            if (!optIn) continue;
            if (row.Kind != RowKind.Download) continue;
            try
            {
                var liveName = LiveFilenameFor(row.Entry);
                var dst      = Path.Combine(_modsDir, liveName);
                if (File.Exists(dst)) dst = Path.Combine(
                    _modsDir, MakeUniqueName(liveName));
                Advance($"⬇ {DisplayLabel(row.Entry)} (MW {row.Entry.ModWorkshopId}) …");
                await Task.Run(() => _mw.DownloadLatestAsync(
                    row.Entry.ModWorkshopId, dst, ct), ct);
                // Snapshot into library so a future profile switch
                // can short-circuit the redownload.
                try { ModLibrary.Add(_modsDir, dst); } catch { }
                Log($"  ✓ → {Path.GetFileName(dst)}");
            }
            catch (OperationCanceledException) { Log("  ✗ Cancelled."); break; }
            catch (Exception ex)               { Log($"  ✗ Download failed: {ex.Message}"); }
        }

        // LivePresent rows: just a profile-add, no file op.
        foreach (var (row, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            if (!optIn) continue;
            if (row.Kind != RowKind.LivePresent) continue;
            // Recorded below alongside the others — fall-through.
        }

        if (ct.IsCancellationRequested) goto Done;

        // Rescan so the freshly-installed/downloaded mods appear in
        // _registry.Entries with their actual manifest mod_id.
        Advance("↻ Rescanning mods …");
        // No cfg passed — this is an additive op, not a profile
        // switch. The grid behind us will redo a full scan with cfg
        // overlay after the dialog closes (PopulateModsGrid is
        // called from MainForm's wrapper), so live state catches
        // up there.
        _registry.Scan(_modsDir, cfg: null);

        // Now reconcile each acted-on row into the profile.
        foreach (var (row, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            if (!optIn) continue;
            if (row.Kind == RowKind.Unresolvable
             || row.Kind == RowKind.AlreadyInProfile) continue;

            var entry = ResolveRegistryEntry(row.Entry);
            if (entry == null)
            {
                Log($"  — {DisplayLabel(row.Entry)}: not found after scan, skipping profile add.");
                continue;
            }
            var existing = _profile.Mods.FirstOrDefault(m =>
                string.Equals(m.ModId, entry.ModId, StringComparison.OrdinalIgnoreCase));
            // Pack label for grouping in the mods grid. Imports
            // tag every added/updated entry with the JSON pack's
            // top-level `name` so mods land grouped under that
            // header. Falls back to the file's stem when the
            // JSON doesn't carry a name. Existing entries get
            // their PackName overwritten only when they had no
            // pack assignment before — moving a mod between
            // packs via re-import is intentional.
            var packLabel = !string.IsNullOrEmpty(_import.Name)
                ? _import.Name
                : "Imported mods";

            if (existing == null)
            {
                _profile.Mods.Add(new ProfileMod
                {
                    ModId         = entry.ModId,
                    DisplayName   = !string.IsNullOrEmpty(entry.DisplayName)
                                      ? entry.DisplayName
                                      : (string.IsNullOrEmpty(row.Entry.DisplayName)
                                          ? entry.ModId : row.Entry.DisplayName),
                    Version       = entry.Version,
                    IsEnabled     = row.Entry.IsEnabled ?? true,
                    Priority      = row.Entry.Priority  ?? entry.DeclaredPriority,
                    ModWorkshopId = entry.ModWorkshopId > 0
                                      ? entry.ModWorkshopId
                                      : row.Entry.ModWorkshopId,
                    PackName      = packLabel,
                });
                Log($"  + profile: {DisplayLabel(row.Entry)} ({entry.ModId} v{entry.Version}) [{packLabel}]");
            }
            else
            {
                existing.Version     = entry.Version;
                if (string.IsNullOrEmpty(existing.DisplayName))
                    existing.DisplayName = entry.DisplayName;
                if (existing.ModWorkshopId == 0 && entry.ModWorkshopId > 0)
                    existing.ModWorkshopId = entry.ModWorkshopId;
                if (string.IsNullOrEmpty(existing.PackName))
                    existing.PackName = packLabel;
                Log($"  ↻ profile updated: {DisplayLabel(row.Entry)} ({entry.ModId} v{entry.Version})");
            }
            profileDirty = true;
            // Track manifest mod_id for the caller's post-close
            // dependency scan. We add the REGISTRY's mod_id (not
            // the import row's possibly-slug pm.ModId) because
            // that's the id ModEntry.RequiredDependencies will
            // match against.
            if (!string.IsNullOrEmpty(entry.ModId)
                && !InstalledModIds.Contains(entry.ModId,
                    StringComparer.OrdinalIgnoreCase))
                InstalledModIds.Add(entry.ModId);
        }

        if (profileDirty)
        {
            _profile.UpdatedAt = DateTime.UtcNow;
            try { _profile.SaveMetadataOnly(); Advance("✓ profile.json saved."); }
            catch (Exception ex) { Advance($"✗ profile save failed: {ex.Message}"); }
        }
        else
        {
            Advance("(no profile changes — every row was already in profile)");
        }

        Done:
        _progressBar.Value = 100;
        Applied = !ct.IsCancellationRequested && profileDirty;
        _cancelBtn.Text = "Close";
        Log(Environment.NewLine
            + (ct.IsCancellationRequested
               ? "✗ Cancelled — partial changes may have been applied."
               : "✓ Import done. Click Close to return."));
    }

    /// <summary>Locate the just-installed registry entry for an
    /// import row. Mod_id first, then ModWorkshop id — mirrors the
    /// fallback path the Apply dialog uses.</summary>
    private ModEntry? ResolveRegistryEntry(ImportEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ModId))
        {
            var byId = _registry.Entries.FirstOrDefault(e =>
                string.Equals(e.ModId, entry.ModId,
                    StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;
        }
        if (entry.ModWorkshopId > 0)
        {
            return _registry.Entries.FirstOrDefault(e =>
                e.ModWorkshopId == entry.ModWorkshopId
                && !string.IsNullOrEmpty(e.ModId));
        }
        return null;
    }

    private static string DisplayLabel(ImportEntry e) =>
        !string.IsNullOrEmpty(e.DisplayName)
            ? e.DisplayName
            : (!string.IsNullOrEmpty(e.ModId)
                ? e.ModId
                : (e.ModWorkshopId > 0 ? $"MW {e.ModWorkshopId}" : "(unknown)"));

    /// <summary>Renders a parent chain into the "Via" column text:
    /// empty for top-level, "↳ dep of X" for one parent,
    /// "↳ dep of X → Y → Z" for deeper. Truncates the middle to
    /// keep the cell readable when chains get long.</summary>
    private static string FormatLineage(List<string> chain)
    {
        if (chain == null || chain.Count == 0) return "";
        // Truncate the middle when the chain is long. Show first +
        // last with an ellipsis between; chains of ≤3 render whole.
        IEnumerable<string> parts = chain;
        if (chain.Count > 3)
            parts = new[] { chain[0], "…", chain[^1] };
        return "↳ dep of " + string.Join(" → ", parts);
    }

    /// <summary>Live filename for a freshly-downloaded / copied mod.
    /// Prefers the mod_id slug from the import (consistent with how
    /// the Apply dialog names its downloads); falls back to the MW
    /// id when no mod_id is present.</summary>
    private static string LiveFilenameFor(ImportEntry e)
    {
        var key = !string.IsNullOrEmpty(e.ModId)
            ? e.ModId
            : (e.ModWorkshopId > 0 ? $"mw{e.ModWorkshopId}" : "unknown");
        return $"{ModProfile.SafeFileName(key)}.vmz";
    }

    private string MakeUniqueName(string filename)
    {
        var baseName = Path.GetFileNameWithoutExtension(filename);
        var ext      = Path.GetExtension(filename);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}_{i}{ext}";
            if (!File.Exists(Path.Combine(_modsDir, candidate))) return candidate;
        }
        return $"{baseName}_{Guid.NewGuid():N}{ext}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosing(e);
    }
}
