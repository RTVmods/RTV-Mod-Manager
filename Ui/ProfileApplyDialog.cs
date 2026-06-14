// Profile-import plan and execution dialog.
//
// Given a ModProfile and the currently-installed ModRegistry, it
// builds a plan table showing what will happen for each profile mod.
// With bundled-archive profiles (the default for "Save Current"), the
// plan can RESTORE the exact version from the bundle — no network round
// trip required. Profiles without bundles fall back to ModWorkshop
// downloads.
//
// Row states:
//   ✓  Match      — installed, version OK; will apply enabled/priority
//   ↔  Replace    — installed at wrong version, bundled archive available;
//                   will overwrite the live .vmz with the bundled copy
//   ⬇  Install    — not installed, bundled archive available; will copy in
//   ⬆  Update DL  — installed but older, no bundle; will download from MW
//   ⬇  Missing DL — not installed, no bundle, has MW ID; will download
//   ✗  No source  — not installed, no bundle, no MW ID; skipped

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ProfileApplyDialog : Form
{
    // ── Public outcome ────────────────────────────────────────────────

    /// <summary>True when the user successfully applied the profile
    /// (at least partially). Caller should rescan after Close.</summary>
    public bool Applied { get; private set; }

    // ── Dependencies ──────────────────────────────────────────────────

    private readonly ModProfile        _profile;
    private readonly ModRegistry       _registry;
    private readonly ModWorkshopClient _mw;
    private readonly ModConfig         _modConfig;
    private readonly string            _modsDir;
    /// <summary>Mod IDs the user has explicitly locked. Locked mods
    /// are passed through to the plan as LockedSkip rows — neither
    /// their .vmz nor their enabled/priority state is touched on
    /// apply, regardless of what the profile says.</summary>
    private readonly HashSet<string>   _lockedModIds;

    // ── Plan rows ─────────────────────────────────────────────────────

    private enum RowKind
    {
        Match,
        ReplaceFromBundle,
        InstallFromBundle,
        ReplaceFromLibrary,
        InstallFromLibrary,
        UpdateDownload,
        MissingDownload,
        NoSource,
        LockedSkip,
    }

    private record PlanRow(
        RowKind    Kind,
        ProfileMod ProfileMod,
        ModEntry?  Installed,       // null when not installed
        string     BundlePath,      // non-empty when a bundled archive is available
        string     LibraryPath,     // non-empty when a library copy is available (already-downloaded .vmz)
        string     StatusIcon,
        string     StatusText,
        bool       OptIn);          // initial checkbox value for downloadable rows

    private List<PlanRow> _plan = new();

    /// <summary>Per-row override map populated when the user picks a
    /// different Source dropdown value. EffectiveKind(idx) reads this
    /// first, falling back to _plan[idx].Kind when there's no
    /// override — so user-modified rows always win, and PlanRow can
    /// stay an immutable record.</summary>
    private readonly Dictionary<int, RowKind> _kindOverrides = new();

    /// <summary>Human-facing Source dropdown values. Each maps to a
    /// RowKind contextually: "Keep current" + installed → Match;
    /// "Use bundle" + installed → ReplaceFromBundle, not installed
    /// → InstallFromBundle; etc. See ResolveKindFromSource for the
    /// full table.</summary>
    private static class SourceChoice
    {
        public const string KeepCurrent = "Keep current";
        public const string UseBundle   = "Use bundle";
        public const string UseLibrary  = "Use library";
        public const string DownloadMW  = "Download new";
        public const string Skip        = "Skip";
        public const string Locked      = "Locked (skip)";
    }

    // ── Controls ──────────────────────────────────────────────────────

    private DataGridView _grid = null!;
    private Label        _summaryLabel = null!;
    private Button       _applyBtn = null!;
    private Button       _cancelBtn = null!;
    private Panel        _progressPanel = null!;
    private ProgressBar  _progressBar = null!;
    private TextBox      _log = null!;
    private bool         _running;

    // ── Construction ─────────────────────────────────────────────────

    public ProfileApplyDialog(
        ModProfile profile,
        ModRegistry registry,
        ModWorkshopClient mw,
        ModConfig modConfig,
        string modsDir,
        IEnumerable<string>? lockedModIds = null)
    {
        _profile      = profile;
        _registry     = registry;
        _mw           = mw;
        _modConfig    = modConfig;
        _modsDir      = modsDir;
        _lockedModIds = new HashSet<string>(
            lockedModIds ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        BuildPlan();
        InitUi();
    }

    private void BuildPlan()
    {
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _registry.Entries)
            if (!string.IsNullOrEmpty(e.ModId)) byId[e.ModId] = e;

        // Library snapshot keyed by mod_id → list of (version, path).
        // ModLibrary.List parses each .vmz once; we cache the result
        // for the lifetime of this dialog so a 50-mod plan doesn't
        // re-read the same Library/ directory 50 times.
        var libByMod = new Dictionary<string, List<ModLibrary.Entry>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var le in ModLibrary.List(_modsDir))
        {
            if (string.IsNullOrEmpty(le.ModId)) continue;
            if (!libByMod.TryGetValue(le.ModId, out var lst))
                libByMod[le.ModId] = lst = new List<ModLibrary.Entry>();
            lst.Add(le);
        }

        _plan = new List<PlanRow>();
        foreach (var pm in _profile.Mods)
        {
            byId.TryGetValue(pm.ModId, out var installed);
            var bundle = _profile.ResolveBundledArchive(pm);
            var hasBundle = !string.IsNullOrEmpty(bundle);
            var hasMw     = pm.ModWorkshopId > 0;

            // Library lookup. Exact (mod_id, version) match wins; if
            // none exists (common for older profiles whose pm.Version
            // is a SPEC version rather than the manifest version a
            // download produces) fall back to the newest version
            // we've ever stored for that mod. Mirrors the same fallback
            // ProfileSwitcher uses on profile switch.
            var libPath = "";
            if (libByMod.TryGetValue(pm.ModId, out var libEntries) && libEntries.Count > 0)
            {
                var exact = libEntries.FirstOrDefault(
                    e => string.Equals(e.Version, pm.Version, StringComparison.Ordinal));
                var pick = exact ?? libEntries
                    .OrderByDescending(e => e.Version, Comparer<string>.Create(
                        (a, b) => ModRegistry.CompareVersions(a, b)))
                    .FirstOrDefault();
                if (pick != null) libPath = pick.Path;
            }
            var hasLibrary = !string.IsNullOrEmpty(libPath);

            PlanRow row;
            // Locked mods short-circuit every other plan-row decision.
            // They render as 🔒 LockedSkip and the executor never
            // touches their archive or cfg state.
            if (installed != null && _lockedModIds.Contains(pm.ModId))
            {
                row = new PlanRow(RowKind.LockedSkip, pm, installed, "", "",
                    "🔒", "Locked — file + state untouched", false);
                _plan.Add(row);
                continue;
            }
            if (installed != null)
            {
                var cmp = ModRegistry.CompareVersions(installed.Version, pm.Version);
                if (cmp == 0)
                {
                    row = new PlanRow(RowKind.Match, pm, installed, "", libPath,
                        "✓", "Installed (match)", false);
                }
                else if (hasBundle)
                {
                    var direction = cmp > 0 ? "downgrade" : "upgrade";
                    row = new PlanRow(RowKind.ReplaceFromBundle, pm, installed, bundle, libPath,
                        "↔", $"Will {direction} from bundle (installed {installed.Version} → {pm.Version})", true);
                }
                else if (hasLibrary)
                {
                    // Already-downloaded copy beats a network round
                    // trip — explicit user request: "why are we
                    // redownloading mods that have already been
                    // downloaded before?"
                    var direction = cmp > 0 ? "downgrade" : "upgrade";
                    row = new PlanRow(RowKind.ReplaceFromLibrary, pm, installed, "", libPath,
                        "↔", $"Will {direction} from library (installed {installed.Version} → {pm.Version})", true);
                }
                else if (cmp < 0 && hasMw)
                {
                    row = new PlanRow(RowKind.UpdateDownload, pm, installed, "", "",
                        "⬆", "Outdated — update available from ModWorkshop", true);
                }
                else
                {
                    // Installed at the wrong version but no way to switch
                    // — surface it but it'll just apply settings to whatever's there.
                    row = new PlanRow(RowKind.Match, pm, installed, "", libPath,
                        "✓", $"Installed (version mismatch: {installed.Version} vs {pm.Version})", false);
                }
            }
            else if (hasBundle)
            {
                row = new PlanRow(RowKind.InstallFromBundle, pm, null, bundle, libPath,
                    "⬇", "Will install from bundle", true);
            }
            else if (hasLibrary)
            {
                // Skip the download entirely — the file is already on
                // disk under Library/, copy it into the live folder.
                row = new PlanRow(RowKind.InstallFromLibrary, pm, null, "", libPath,
                    "⬇", "Will install from library (already downloaded)", true);
            }
            else if (hasMw)
            {
                row = new PlanRow(RowKind.MissingDownload, pm, null, "", "",
                    "⬇", "Not installed — will download from ModWorkshop", true);
            }
            else
            {
                row = new PlanRow(RowKind.NoSource, pm, null, "", "",
                    "✗", "Not installed (no bundle, no library, no ModWorkshop ID)", false);
            }
            _plan.Add(row);
        }
        // NOTE: Mods that are installed but NOT in the profile (and
        // not locked) will be REMOVED from the live folder by Phase 0
        // of RunApplyAsync — their .vmz survives in Library/ for any
        // later profile that wants them back. This makes Apply Profile
        // behave like a true profile switch, not a merge. Locked mod
        // ids bypass both the wipe and the plan rows.
    }

    private bool IsCheckable(RowKind k) =>
        k is RowKind.ReplaceFromBundle
            or RowKind.InstallFromBundle
            or RowKind.ReplaceFromLibrary
            or RowKind.InstallFromLibrary
            or RowKind.UpdateDownload
            or RowKind.MissingDownload;

    /// <summary>Current Kind for a plan row — user's dropdown choice
    /// if any, otherwise the auto-picked default.</summary>
    private RowKind EffectiveKind(int index) =>
        _kindOverrides.TryGetValue(index, out var k) ? k : _plan[index].Kind;

    /// <summary>The dropdown values offered for a given plan row,
    /// gated by what's actually possible. Locked rows are read-only
    /// "Locked"; rows with no installable source are read-only
    /// "Skip".</summary>
    private static List<string> SourceChoicesFor(PlanRow row)
    {
        if (row.Kind == RowKind.LockedSkip)
            return new List<string> { SourceChoice.Locked };
        var choices = new List<string>();
        var isInstalled = row.Installed != null;
        var hasBundle   = !string.IsNullOrEmpty(row.BundlePath);
        var hasLibrary  = !string.IsNullOrEmpty(row.LibraryPath);
        var hasMw       = row.ProfileMod.ModWorkshopId > 0;
        if (isInstalled) choices.Add(SourceChoice.KeepCurrent);
        if (hasBundle)   choices.Add(SourceChoice.UseBundle);
        if (hasLibrary)  choices.Add(SourceChoice.UseLibrary);
        if (hasMw)       choices.Add(SourceChoice.DownloadMW);
        if (choices.Count == 0) choices.Add(SourceChoice.Skip);
        return choices;
    }

    /// <summary>The default Source for a plan row — mirrors the auto-
    /// pick logic that BuildPlan used to settle on a Kind.</summary>
    private static string DefaultSourceFor(PlanRow row) => row.Kind switch
    {
        RowKind.LockedSkip          => SourceChoice.Locked,
        RowKind.Match               => SourceChoice.KeepCurrent,
        RowKind.ReplaceFromBundle   => SourceChoice.UseBundle,
        RowKind.InstallFromBundle   => SourceChoice.UseBundle,
        RowKind.ReplaceFromLibrary  => SourceChoice.UseLibrary,
        RowKind.InstallFromLibrary  => SourceChoice.UseLibrary,
        RowKind.UpdateDownload      => SourceChoice.DownloadMW,
        RowKind.MissingDownload     => SourceChoice.DownloadMW,
        RowKind.NoSource            => SourceChoice.Skip,
        _                           => SourceChoice.Skip,
    };

    /// <summary>Translates a Source dropdown choice into the RowKind
    /// that Phase 1 + Phase 3 dispatch on. Context-aware: "Use
    /// bundle" + installed=true → ReplaceFromBundle, installed=false
    /// → InstallFromBundle; same for Download.</summary>
    private static RowKind ResolveKindFromSource(PlanRow row, string source)
    {
        var isInstalled = row.Installed != null;
        return source switch
        {
            SourceChoice.KeepCurrent => RowKind.Match,
            SourceChoice.UseBundle   => isInstalled
                                            ? RowKind.ReplaceFromBundle
                                            : RowKind.InstallFromBundle,
            SourceChoice.UseLibrary  => isInstalled
                                            ? RowKind.ReplaceFromLibrary
                                            : RowKind.InstallFromLibrary,
            SourceChoice.DownloadMW  => isInstalled
                                            ? RowKind.UpdateDownload
                                            : RowKind.MissingDownload,
            SourceChoice.Locked      => RowKind.LockedSkip,
            SourceChoice.Skip        => RowKind.NoSource,
            _                        => row.Kind,
        };
    }

    private void InitUi()
    {
        Text = $"Apply Profile — {_profile.Name}";
        StartPosition  = FormStartPosition.CenterParent;
        BackColor      = Color.FromArgb(26, 30, 40);
        ForeColor      = Color.FromArgb(220, 225, 235);
        Font           = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        // Min width derived from column constraints: 42 + 36 + 80 +
        // 80 + 160 (ModName min) + 220 (Status min) + scrollbar/
        // padding ≈ 660. Add headroom so users with default DPI
        // still see status text without resizing.
        MinimumSize = new Size(980, 580);
        Width       = 1180;
        Height      = 660;
        ShowInTaskbar  = false;
        DialogSizing.ClampToWorkingArea(this);

        // Top: title + description
        var title = new Label
        {
            Text      = $"Apply Profile: {_profile.Name}",
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
        _progressPanel = BuildProgressPanel();
        _progressPanel.Visible = false;

        var btnRow = BuildButtonRow();

        var content = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 4,
            Padding     = new Padding(16, 12, 16, 12),
            BackColor   = Color.Transparent,
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(title,          0, 0);
        content.Controls.Add(_summaryLabel,  0, 1);
        content.Controls.Add(_grid,          0, 2);
        content.Controls.Add(_progressPanel, 0, 3);

        // Order matters here: WinForms docks the LAST-added control
        // first. To get `content` (Dock=Fill) sized correctly INSIDE
        // the space NOT consumed by `btnRow` (Dock=Bottom), add
        // content FIRST so it docks last. The previous order put
        // btnRow on top of content's bottom edge, clipping the last
        // line of the log textbox behind the Apply / Close buttons.
        Controls.Add(content);
        Controls.Add(btnRow);

        CancelButton = _cancelBtn;
    }

    private string BuildSummaryText()
    {
        int matches      = _plan.Count(r => r.Kind == RowKind.Match);
        int replaces     = _plan.Count(r => r.Kind == RowKind.ReplaceFromBundle);
        int installs     = _plan.Count(r => r.Kind == RowKind.InstallFromBundle);
        int libReplaces  = _plan.Count(r => r.Kind == RowKind.ReplaceFromLibrary);
        int libInstalls  = _plan.Count(r => r.Kind == RowKind.InstallFromLibrary);
        int updates      = _plan.Count(r => r.Kind == RowKind.UpdateDownload);
        int downloads    = _plan.Count(r => r.Kind == RowKind.MissingDownload);
        int noSrc        = _plan.Count(r => r.Kind == RowKind.NoSource);
        int locked       = _plan.Count(r => r.Kind == RowKind.LockedSkip);

        var parts = new List<string>();
        if (matches      > 0) parts.Add($"{matches} matching");
        if (replaces     > 0) parts.Add($"{replaces} from bundle (replace)");
        if (installs     > 0) parts.Add($"{installs} from bundle (install)");
        if (libReplaces  > 0) parts.Add($"{libReplaces} from library (replace)");
        if (libInstalls  > 0) parts.Add($"{libInstalls} from library (install)");
        if (updates      > 0) parts.Add($"{updates} update via MW");
        if (downloads    > 0) parts.Add($"{downloads} download via MW");
        if (noSrc        > 0) parts.Add($"{noSrc} unavailable");
        if (locked       > 0) parts.Add($"{locked} 🔒 locked (skipped)");

        var desc = string.IsNullOrEmpty(_profile.Description)
            ? "" : $"  \"{_profile.Description}\"  ";
        return $"{desc}{string.Join(", ", parts)} — {_profile.Mods.Count} mods total.";
    }

    private DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
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
            ReadOnly   = false,
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
        // ModName + Status are both Fill columns sharing leftover
        // width 50/50. Previously Status was fixed at 320px and got
        // cut off on long messages like "Not installed — will
        // download from ModWorkshop", while ProfileVer/Installed
        // hogged 100px each for content as short as "1.0.0" / "—".
        // The two version columns are now sized to their realistic
        // max (centered, monospace cell font, ~7 chars).
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
            Name       = "ProfileVer",
            HeaderText = "Profile ver.",
            Width      = 80,
            ReadOnly   = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "InstalledVer",
            HeaderText = "Installed",
            Width      = 80,
            ReadOnly   = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        // Source dropdown — the per-row picker. Each row gets its own
        // DataSource list (set in PopulateGrid) so available choices
        // reflect what's actually possible: locked rows show
        // "Locked", a row with no bundle won't list "Use bundle",
        // etc. Changing the value mutates _kindOverrides and the
        // execute phase reads EffectiveKind(idx).
        grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name         = "Source",
            HeaderText   = "Source",
            Width        = 140,
            ReadOnly     = false,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleLeft },
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle    = FlatStyle.Flat,
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

        for (int idx = 0; idx < _plan.Count; idx++)
        {
            var row = _plan[idx];
            var i = grid.Rows.Add();
            var r = grid.Rows[i];
            var checkable = IsCheckable(row.Kind);
            r.Cells["Apply"].Value     = checkable ? row.OptIn : (object?)null;
            r.Cells["Apply"].ReadOnly  = !checkable;
            r.Cells["Icon"].Value      = row.StatusIcon;
            r.Cells["ModName"].Value   = row.ProfileMod.DisplayName;
            r.Cells["ModName"].ToolTipText = string.IsNullOrEmpty(row.BundlePath)
                ? $"id: {row.ProfileMod.ModId}\nno bundle"
                : $"id: {row.ProfileMod.ModId}\nbundle: {Path.GetFileName(row.BundlePath)}";
            r.Cells["ProfileVer"].Value   = row.ProfileMod.Version;
            r.Cells["InstalledVer"].Value = row.Installed?.Version ?? "—";
            r.Cells["Status"].Value       = row.StatusText;

            // Per-row Source dropdown. Each cell carries its own
            // DataSource — locked rows only get "Locked", a row
            // without a bundle never lists "Use bundle", etc.
            // Single-option rows are made ReadOnly so the dropdown
            // shows as static text.
            var srcCell = (DataGridViewComboBoxCell)r.Cells["Source"];
            var choices = SourceChoicesFor(row);
            srcCell.DataSource = choices;
            srcCell.Value      = DefaultSourceFor(row);
            srcCell.ReadOnly   = choices.Count <= 1
                              || row.Kind == RowKind.LockedSkip;

            ApplyKindStyle(grid, i, row.Kind);
        }

        // Commit Source-dropdown edits the instant the value changes
        // (combobox + checkbox both — without the live commit a
        // dropdown change wouldn't fire CellValueChanged until the
        // user clicked away).
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (!grid.IsCurrentCellDirty) return;
            if (grid.CurrentCell is DataGridViewCheckBoxCell
                or DataGridViewComboBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        // Source change → recompute the effective Kind for this row,
        // re-render the icon / status / colour, and re-arm the Apply
        // checkbox so it reflects what'll happen on Apply.
        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _plan.Count) return;
            if (grid.Columns[e.ColumnIndex].Name != "Source") return;
            var planRow = _plan[e.RowIndex];
            var picked  = grid.Rows[e.RowIndex].Cells["Source"].Value as string ?? "";
            var newKind = ResolveKindFromSource(planRow, picked);
            _kindOverrides[e.RowIndex] = newKind;

            var (icon, status) = LabelsForKind(planRow, newKind);
            grid.Rows[e.RowIndex].Cells["Icon"].Value   = icon;
            grid.Rows[e.RowIndex].Cells["Status"].Value = status;
            ApplyKindStyle(grid, e.RowIndex, newKind);

            // Sync the Apply checkbox to the new kind. Checkable
            // kinds auto-check (the user's just picked an active
            // source); non-checkable kinds go null + read-only.
            var checkable = IsCheckable(newKind);
            grid.Rows[e.RowIndex].Cells["Apply"].Value
                = checkable ? true : (object?)null;
            grid.Rows[e.RowIndex].Cells["Apply"].ReadOnly = !checkable;
        };

        return grid;
    }

    /// <summary>Icon glyph + status text for a row's effective kind.
    /// Mirrors the auto-built labels from BuildPlan so a dropdown
    /// change re-renders the cells consistently.</summary>
    private static (string icon, string status) LabelsForKind(
        PlanRow row, RowKind kind) => kind switch
    {
        RowKind.Match
            => ("✓", "Keep installed version"),
        RowKind.ReplaceFromBundle
            => ("↔", $"Replace from bundle (installed v{row.Installed?.Version} → v{row.ProfileMod.Version})"),
        RowKind.InstallFromBundle
            => ("⬇", "Install from bundle"),
        RowKind.ReplaceFromLibrary
            => ("↔", $"Replace from library (installed v{row.Installed?.Version} → v{row.ProfileMod.Version})"),
        RowKind.InstallFromLibrary
            => ("⬇", "Install from library (already downloaded)"),
        RowKind.UpdateDownload
            => ("⬆", "Download latest from ModWorkshop"),
        RowKind.MissingDownload
            => ("⬇", "Download from ModWorkshop"),
        RowKind.NoSource
            => ("✗", "Skipped"),
        RowKind.LockedSkip
            => ("🔒", "Locked — file + state untouched"),
        _ => (row.StatusIcon, row.StatusText),
    };

    /// <summary>Repaints a row's icon + status colours to match the
    /// effective kind. Pulled out of the row-build loop so the
    /// Source-dropdown change handler can call it after the user
    /// picks a different source.</summary>
    private static void ApplyKindStyle(DataGridView grid, int idx, RowKind kind)
    {
        var fg = kind switch
        {
            RowKind.Match              => Color.FromArgb(120, 200, 130),
            RowKind.ReplaceFromBundle  => Color.FromArgb(200, 180, 255),
            RowKind.InstallFromBundle  => Color.FromArgb(150, 200, 255),
            // Library = "already on disk, no network" — distinct teal
            // so the user can scan the plan and instantly see which
            // rows touch the network vs which reuse local copies.
            RowKind.ReplaceFromLibrary => Color.FromArgb(140, 220, 210),
            RowKind.InstallFromLibrary => Color.FromArgb(140, 220, 210),
            RowKind.UpdateDownload     => Color.FromArgb(255, 200, 80),
            RowKind.MissingDownload    => Color.FromArgb(120, 170, 255),
            RowKind.NoSource           => Color.FromArgb(140, 140, 160),
            RowKind.LockedSkip         => Color.FromArgb(160, 170, 190),
            _ => Color.FromArgb(220, 225, 235),
        };
        var r = grid.Rows[idx];
        r.Cells["Status"].Style.ForeColor = fg;
        r.Cells["Icon"].Style.ForeColor   = fg;
        // Reset to inherit when not Skip — switching FROM Skip to
        // anything else needs to clear the previous grey override.
        r.DefaultCellStyle.ForeColor = (kind == RowKind.NoSource)
            ? Color.FromArgb(140, 140, 160)
            : Color.Empty;
    }

    private Panel BuildProgressPanel()
    {
        // Height bumped 180 → 220 and bottom Padding added so the
        // log textbox's last line gets breathing room before the
        // panel edge meets the button row — without it the very
        // last log entry rendered partially clipped (Windows TextBox
        // sometimes positions the final wrapped line a pixel below
        // its client rect in multiline ReadOnly mode).
        var p = new Panel
        {
            Dock      = DockStyle.Fill,
            Height    = 220,
            BackColor = Color.Transparent,
            Padding   = new Padding(0, 0, 0, 10),
        };
        _progressBar = new ProgressBar
        {
            Dock    = DockStyle.Top,
            Height  = 24,
            Minimum = 0,
            Maximum = 100,
            Style   = ProgressBarStyle.Continuous,
            BackColor = Color.FromArgb(30, 36, 48),
            ForeColor = Color.FromArgb(90, 160, 100),
        };
        _log = new TextBox
        {
            Dock      = DockStyle.Fill,
            Multiline = true,
            ReadOnly  = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(180, 220, 160),
            Font      = new Font("Consolas", 11f),
            BorderStyle = BorderStyle.None,
        };
        p.Controls.Add(_log);
        p.Controls.Add(_progressBar);
        return p;
    }

    private Panel BuildButtonRow()
    {
        var p = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 56,
            BackColor = Color.FromArgb(30, 34, 44),
        };
        _applyBtn = MainForm.ThemedButton("▶ Apply");
        _applyBtn.Width  = 130;
        _applyBtn.Height = 40;
        _applyBtn.AutoSize  = false;
        _applyBtn.BackColor = Color.FromArgb(45, 90, 55);
        _applyBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _applyBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _applyBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 115, 70);
        _applyBtn.Anchor  = AnchorStyles.Right | AnchorStyles.Top;
        _applyBtn.Click  += async (_, _) => await RunApplyAsync();

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

    // ── Apply logic ───────────────────────────────────────────────────

    private CancellationTokenSource? _cts;

    private async Task RunApplyAsync()
    {
        if (_running) return;
        _running = true;
        _applyBtn.Enabled = false;
        _progressPanel.Visible = true;

        // Snapshot user choices. `kind` is the effective kind (with
        // any Source-dropdown override applied) so Phase 1/3 always
        // act on the user's latest pick, not the auto-default.
        var actions = new List<(PlanRow row, RowKind kind, bool optIn)>();
        for (int i = 0; i < _plan.Count; i++)
        {
            var row = _plan[i];
            var kind = EffectiveKind(i);
            var optIn = IsCheckable(kind)
                && _grid.Rows[i].Cells["Apply"].Value is true;
            actions.Add((row, kind, optIn));
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var totalSteps =
            actions.Count(a => a.optIn) * 2  // file op + cfg apply per acted row
            + actions.Count(a => a.kind == RowKind.Match) // cfg apply only
            + 2; // rescan + save
        var step = 0;

        void Log(string m)
        {
            _log.AppendText(m + Environment.NewLine);
            // Force the caret to the absolute end, THEN scroll. Just
            // calling ScrollToCaret() after AppendText sometimes
            // leaves the caret on the line above the visible bottom,
            // which makes the final line appear cut off.
            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.ScrollToCaret();
        }
        void Advance(string m)
        {
            step = Math.Min(step + 1, totalSteps);
            _progressBar.Value = (int)((double)step / Math.Max(1, totalSteps) * 100);
            Log(m);
        }

        // ── Phase 0: ensure-in-library + wipe extras ─────────────────
        // Apply = make this the active profile, not "merge in these
        // mods on top of whatever's already there". That means every
        // live .vmz whose mod_id isn't in the target profile (and
        // isn't on the user's lock list) needs to come out of the
        // live folder — otherwise the previous profile's 50 mods sit
        // beside the new profile's 10, the mod count shoots to 60,
        // and the cfg/grid stop matching the profile.
        //
        // Safety net: every live .vmz is captured in Library/ first.
        // ModLibrary.Add is idempotent (skips when (mod_id, version)
        // already exists), so this is cheap when there's nothing new
        // and is the difference between "wipe is reversible" and
        // "data loss" when there is.
        var profileModIds = new HashSet<string>(
            _profile.Mods
                .Select(p => p.ModId)
                .Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.OrdinalIgnoreCase);
        // Parallel MW-id set for the wipe predicate. Some profiles
        // (especially JSON-spec imports) carry URL-slug ModIds that
        // don't match the manifest id of the live .vmz; without a
        // second key we'd wipe a mod that's actually in the profile
        // just because the string ids disagree.
        var profileMwIds = new HashSet<int>(
            _profile.Mods
                .Select(p => p.ModWorkshopId)
                .Where(i => i > 0));

        // Snapshot live entries once — _registry.Entries mutates on
        // rescan and we read it again in Phase 3.
        var liveSnapshot = _registry.Entries
            .Where(e => e.IsArchive
                     && !string.IsNullOrEmpty(e.ModId)
                     && !string.IsNullOrEmpty(e.Path)
                     && File.Exists(e.Path))
            .ToList();

        var ensured = 0;
        foreach (var entry in liveSnapshot)
        {
            if (ct.IsCancellationRequested) break;
            // Locked mods stay live — no need to library-snapshot
            // them here (the lock guarantees we won't delete the
            // live copy), but Add() is idempotent so a redundant
            // call is harmless if we change our mind later.
            try
            {
                var added = ModLibrary.Add(_modsDir, entry.Path);
                if (added != null) ensured++;
            }
            catch { /* best-effort — wipe still proceeds */ }
        }
        if (ensured > 0)
            Log($"  +lib captured {ensured} live mod(s) before wipe");

        var wiped = 0;
        foreach (var entry in liveSnapshot)
        {
            if (ct.IsCancellationRequested) break;
            if (_lockedModIds.Contains(entry.ModId)) continue;
            // Keep when EITHER identity matches the profile — the
            // string id (manifest match) or the MW id (recovers when
            // the profile carries a slug instead of the manifest id).
            if (profileModIds.Contains(entry.ModId)) continue;
            if (entry.ModWorkshopId > 0 && profileMwIds.Contains(entry.ModWorkshopId)) continue;
            try
            {
                File.Delete(entry.Path);
                wiped++;
            }
            catch (Exception ex)
            {
                Log($"  ✗ couldn't remove {Path.GetFileName(entry.Path)}: {ex.Message}");
            }
        }
        if (wiped > 0)
            Log($"  − wiped {wiped} mod(s) not in '{_profile.Name}'");

        // ── Phase 1: File operations ─────────────────────────────────
        // Local sources first (bundle + library — both are free), then
        // MW downloads. Library copies short-circuit a redownload when
        // we already have the .vmz on disk from a previous profile.

        // Local-bundle ops
        foreach (var (row, kind, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            if (!optIn) continue;
            if (kind != RowKind.InstallFromBundle && kind != RowKind.ReplaceFromBundle)
                continue;

            try
            {
                if (kind == RowKind.ReplaceFromBundle && row.Installed != null
                    && row.Installed.IsArchive && File.Exists(row.Installed.Path))
                {
                    // Move the old .vmz aside (.bak) so users can recover it
                    // if they ever apply a different profile back.
                    var bak = row.Installed.Path + ".replaced-by-profile.bak";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(row.Installed.Path, bak);
                }
                var srcName = Path.GetFileName(row.BundlePath);
                var dst     = Path.Combine(_modsDir, srcName);
                // If a different file already occupies the destination,
                // rename ours to avoid clobbering.
                if (File.Exists(dst))
                {
                    var alt = MakeUniqueName(srcName);
                    dst = Path.Combine(_modsDir, alt);
                }
                File.Copy(row.BundlePath, dst, overwrite: false);
                Advance($"  ✓ {row.ProfileMod.DisplayName}: bundle → {Path.GetFileName(dst)}");
            }
            catch (Exception ex)
            {
                Advance($"  ✗ {row.ProfileMod.DisplayName}: bundle copy failed — {ex.Message}");
            }
        }

        // Library copies (same shape as the bundle branch — the file
        // lives under <mods>/Library/ and a plain copy seeds the live
        // folder with no network round-trip). The library is already
        // canonical so we don't re-Add the file to the library after.
        foreach (var (row, kind, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            if (!optIn) continue;
            if (kind != RowKind.InstallFromLibrary && kind != RowKind.ReplaceFromLibrary)
                continue;

            try
            {
                if (kind == RowKind.ReplaceFromLibrary && row.Installed != null
                    && row.Installed.IsArchive && File.Exists(row.Installed.Path))
                {
                    var bak = row.Installed.Path + ".replaced-by-profile.bak";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(row.Installed.Path, bak);
                }
                // Library files are named `<safeId>__v<ver>.vmz`; live
                // mods conventionally use just `<safeId>.vmz`. Use the
                // safe-id form so the layout in `<mods>/` stays clean
                // and matches what an MW download would have produced.
                var pm = row.ProfileMod;
                var liveName = $"{ModProfile.SafeFileName(pm.ModId)}.vmz";
                var dst      = Path.Combine(_modsDir, liveName);
                if (File.Exists(dst))
                {
                    var alt = MakeUniqueName(liveName);
                    dst = Path.Combine(_modsDir, alt);
                }
                File.Copy(row.LibraryPath, dst, overwrite: false);
                Advance($"  ✓ {pm.DisplayName}: library → {Path.GetFileName(dst)}");
            }
            catch (Exception ex)
            {
                Advance($"  ✗ {row.ProfileMod.DisplayName}: library copy failed — {ex.Message}");
            }
        }

        // MW downloads
        var downloads = actions
            .Where(a => a.optIn && (a.kind == RowKind.UpdateDownload
                                  || a.kind == RowKind.MissingDownload))
            .ToList();
        foreach (var (row, _, _) in downloads)
        {
            if (ct.IsCancellationRequested) break;
            var pm = row.ProfileMod;
            var fileName = $"{ModProfile.SafeFileName(pm.ModId)}.vmz";
            var dest = Path.Combine(_modsDir, fileName);
            Advance($"⬇ {pm.DisplayName} (MW {pm.ModWorkshopId}) …");
            try
            {
                await Task.Run(() => _mw.DownloadLatestAsync(pm.ModWorkshopId, dest, ct), ct);
                // Snapshot the freshly-downloaded .vmz into the
                // library too. Library is canonical for the new
                // profile model — without this step a .json-spec
                // import would download into <mods>/ but never seed
                // the library, so a later profile switch would
                // report the mod as "missing from library".
                try { ModLibrary.Add(_modsDir, dest); }
                catch { /* best-effort; mod still works from live folder */ }
                Log($"  ✓ → {fileName}");
            }
            catch (OperationCanceledException) { Log("  ✗ Cancelled."); break; }
            catch (Exception ex) { Log($"  ✗ Download failed: {ex.Message}"); }
        }

        if (ct.IsCancellationRequested) goto Done;

        // ── Phase 2: Rescan ──────────────────────────────────────────
        Advance("↻ Rescanning mods …");
        _registry.Scan(_modsDir, _modConfig);

        // ── Phase 3a: switch active to the applied profile ───────────
        // Apply = "this is now the active profile". Previously, cfg
        // writes below kept landing in the OLD active profile's
        // `[profile.<old>.enabled]` section, which produced a
        // confusing "merge" where the live state matched the
        // applied profile but the cfg pointer still named a
        // different one. Mirror ProfileSwitcher's behaviour:
        //   • snapshot locked mods' cfg state from the old section
        //     (locks survive profile changes by design)
        //   • flip ActiveProfile to the applied name
        //   • clear the target section so it's deterministically
        //     rebuilt from the apply plan, not merged with stale
        //     entries from a previous switch
        //   • restore the locked entries at the end of Phase 3
        var lockedSnapshot =
            new List<(string ModId, string Version, bool Enabled, int Priority)>();
        if (_lockedModIds.Count > 0)
        {
            foreach (var entry in _registry.Entries)
            {
                if (string.IsNullOrEmpty(entry.ModId)) continue;
                if (!_lockedModIds.Contains(entry.ModId)) continue;
                lockedSnapshot.Add((
                    entry.ModId,
                    entry.Version,
                    _modConfig.IsEnabled(entry.ModId, entry.Version, fallback: true),
                    _modConfig.Priority(entry.ModId, entry.Version, fallback: 0)));
            }
        }
        _modConfig.ActiveProfile = _profile.Name;
        _modConfig.ClearProfileEntries(_profile.Name);

        // ── Phase 3: Apply enabled/priority state ─────────────────────
        // Also reconciles each ProfileMod's recorded Version with
        // what's actually on disk after the file ops + rescan. This
        // is critical for the next profile switch: the library is
        // keyed by (mod_id, MANIFEST version), so a stale
        // pm.Version (e.g. the spec version "0.0.420" while the MW
        // download contains "0.4.20_R" in its mod.txt) would cause
        // ProfileSwitcher.Find to miss the library file entirely
        // and report it as "missing from library (skipped)".
        var profileDirty = false;
        foreach (var (row, kind, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            // Skip rows we couldn't act on (NoSource / Skip),
            // couldn't update (LockedSkip), or downloadable rows the
            // user unchecked.
            if (kind == RowKind.NoSource) continue;
            if (kind == RowKind.LockedSkip)
            {
                Log($"  🔒 {row.ProfileMod.DisplayName}: locked — left untouched.");
                continue;
            }
            if (IsCheckable(kind) && !optIn) continue;

            var pm = row.ProfileMod;
            // Primary: match by mod_id. Works when the profile was
            // built from already-installed mods (their pm.ModId is
            // the manifest id straight out of the live mod.txt).
            var entry = _registry.Entries.FirstOrDefault(
                e => string.Equals(e.ModId, pm.ModId, StringComparison.OrdinalIgnoreCase));
            // Fallback: MW id. Profiles imported from a .json spec
            // commonly use the ModWorkshop URL slug as `pm.ModId`,
            // which doesn't match what the author wrote in mod.txt.
            // The numeric MW id is stable across both, so when the
            // string ids don't agree we recover via that — and
            // reconcile pm.ModId to the manifest's real id so the
            // next apply / switch matches on the primary path.
            if (entry == null && pm.ModWorkshopId > 0)
            {
                entry = _registry.Entries.FirstOrDefault(
                    e => e.ModWorkshopId == pm.ModWorkshopId
                      && !string.IsNullOrEmpty(e.ModId));
                if (entry != null)
                {
                    Log($"  ↳ {pm.DisplayName}: profile id '{pm.ModId}' "
                        + $"→ archive id '{entry.ModId}' (matched via MW {pm.ModWorkshopId})");
                    pm.ModId = entry.ModId;
                    profileDirty = true;
                }
            }
            if (entry == null)
            {
                Advance($"  — {pm.DisplayName}: not found after scan, skipping.");
                continue;
            }
            // Sync profile.json's recorded version to the actually-
            // installed manifest version. Without this, library
            // lookups on the next profile switch fail.
            if (!string.Equals(pm.Version, entry.Version, StringComparison.Ordinal))
            {
                pm.Version  = entry.Version;
                profileDirty = true;
            }
            // Cfg keys are `mod_id@version` — the in-game loader
            // reads them keyed by manifest id, so use entry.ModId
            // (which IS the manifest id after the fallback above)
            // not the possibly-slug pm.ModId for the cfg write.
            _modConfig.SetEnabled(entry.ModId, entry.Version, pm.IsEnabled);
            _modConfig.SetPriority(entry.ModId, entry.Version, pm.Priority);
            Advance($"  ✓ {pm.DisplayName}: {(pm.IsEnabled ? "enabled" : "disabled")}, prio {pm.Priority}");
        }

        // ── Phase 3b: restore locked entries to the new active section.
        // Their cfg state was captured in Phase 3a before the wipe;
        // re-emit it here so locks survive the profile change and
        // don't fall back to the default-enabled state on next launch.
        foreach (var (modId, version, enabled, priority) in lockedSnapshot)
        {
            _modConfig.SetEnabled(modId, version, enabled);
            _modConfig.SetPriority(modId, version, priority);
        }

        // ── Phase 4: Save cfg ─────────────────────────────────────────
        try
        {
            _modConfig.Save();
            Advance("✓ mod_config.cfg saved.");
        }
        catch (Exception ex)
        {
            Advance($"✗ mod_config.cfg save failed: {ex.Message}");
        }

        // ── Phase 5: Persist profile.json if any ProfileMod.Version
        // got reconciled. One write at the end so a 50-mod apply
        // doesn't produce 50 partial saves.
        if (profileDirty)
        {
            _profile.UpdatedAt = DateTime.UtcNow;
            try
            {
                _profile.SaveMetadataOnly();
                Advance("✓ profile.json versions reconciled.");
            }
            catch (Exception ex)
            {
                Advance($"✗ profile.json save failed: {ex.Message}");
            }
        }

        Done:
        _progressBar.Value = 100;
        Applied = !ct.IsCancellationRequested;
        _cancelBtn.Text = "Close";
        Log(Environment.NewLine
            + (Applied
               ? "✓ Profile applied. Click Close to return."
               : "✗ Cancelled — partial changes may have been applied."));
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
