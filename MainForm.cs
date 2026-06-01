// Top-level window. v0.3.3 — interactive mods grid: per-row Toggle
// (enable/disable, moves the .vmz to/from Disabled/) and Update
// (downloads the latest .vmz from ModWorkshop, swaps it in place).
//
// Conflicts column is still a read-only ListBox; AI Resolve buttons +
// Settings panel + resolution dialog land in the next commit.

#if AI_RESOLVER
using VostokModManager.Ai;
#endif
using VostokModManager.Api;
using VostokModManager.Domain;
using VostokModManager.Ui;

namespace VostokModManager;

public class MainForm : Form
{
    /// <summary>Hardcoded fallback when the user hasn't set a custom
    /// ModsDir. Default Steam install layout for Road to Vostok.</summary>
    private const string SteamFallbackModsDir =
        @"C:\Program Files (x86)\Steam\steamapps\common\Road to Vostok\mods";

    /// <summary>The active mods directory. Reads from the user's
    /// settings if set; otherwise falls back to the Steam install
    /// path. This is the path scan/toggle/install operations target.</summary>
    private string ModsDir =>
        !string.IsNullOrEmpty(_settings.ModsDir)
            ? _settings.ModsDir
            : SteamFallbackModsDir;

    private readonly Settings _settings;
    private readonly ModRegistry _registry = new();
#if AI_RESOLVER
    private readonly ClaudeCodeRunner _claude = new();
#endif
    private readonly ModWorkshopClient _mw = new();
#if AI_RESOLVER
    private readonly ConflictResolver _resolver;
#endif
    /// <summary>The in-game mod loader's mod_config.cfg state under
    /// %APPDATA%\Road to Vostok\. Reloaded on every Rescan so we
    /// pick up any changes the loader made between our scans;
    /// written back via SaveModConfigSafely whenever the user
    /// toggles or re-prioritises a mod.</summary>
    private ModConfig _modConfig = new();

    /// <summary>mod_workshop_id → latest version (populated after the
    /// /mods/versions call).</summary>
    private readonly Dictionary<int, string> _latestVersions = new();
    private List<ConflictDetector.Conflict> _lastConflicts = new();

    /// <summary>Backing list for the mods grid, in display order. The
    /// grid binds to row indices into this list, so click handlers
    /// look up entries by row number.</summary>
    private List<ModEntry> _displayed = new();

    /// <summary>True while a long-running per-row action is in flight
    /// (download / etc.). Disables the action buttons across the grid.</summary>
    private bool _busy;

    /// <summary>True while we're rewriting the mods grid programmatically
    /// — guards the CellValueChanged handler so it doesn't react to
    /// our own Value assignments and try to "toggle" each row.</summary>
    private bool _populatingMods;

#if AI_RESOLVER
    private Label _claudeLabel = null!;
#endif
    private Label _modsLabel = null!;
    private Label _updatesLabel = null!;
    private Label _conflictsLabel = null!;
    private Label _mmlLabel = null!;
    private Label _managerLabel = null!;
    /// <summary>Status row that surfaces drift between the live
    /// registry and the active profile — enabled/priority/version
    /// changes the user made without saving back to profile.json.
    /// Hidden when there's no drift; click to commit live state
    /// into the profile.</summary>
    private Label _driftLabel = null!;

    /// <summary>Status row offering a one-click restore of the
    /// pre-launch checkpoint. Visible whenever
    /// CrashCheckpoint.Exists() returns true — i.e. the user has
    /// launched the game since the last restore. Click to roll
    /// mod_config.cfg + profile.json back to their pre-launch
    /// state. Cleared on successful restore so it hides again.</summary>
    private Label _checkpointLabel = null!;
    private readonly MmlVersionChecker _mml = new();

    /// <summary>The mod manager's own ModWorkshop ID. The page lives
    /// at https://modworkshop.net/mod/56801. Used by
    /// CheckManagerUpdateAsync to compare the assembly version
    /// against the latest published release, surfacing an "update
    /// available" hint in the Manager status row.</summary>
    private const int ManagerModWorkshopId = 56801;
    private DataGridView _modsGrid = null!;
    private DataGridView _conflictsGrid = null!;
    private SplitContainer _split = null!;
    private Button _conflictsToggle = null!;

    /// <summary>Title-row profile-selector button. Shows the active
    /// profile name + a dropdown chevron; clicking opens a context
    /// menu of all known profiles. Null when the new profile model
    /// hasn't been adopted (pre-migration installs) — in that case
    /// the button still appears but reads "(no profile)" and the
    /// menu shows the migration prompt instead of a profile list.</summary>
    private Button _profileSelector = null!;

    /// <summary>Currently-active mod profile, or null when no
    /// profile is active (pre-migration). When non-null, the mods
    /// grid + conflict scope filter to its Mods list.</summary>
    private Domain.ModProfile? _activeProfile;

    /// <summary>Temporary mod-id allowlist applied on top of the
    /// active-profile filter. Non-null only while the Dependencies
    /// dialog is open — narrows the grid to mods that participate
    /// in dependency relationships (dependents + their declared
    /// deps) so the user can correlate dialog rows with grid rows
    /// without scrolling through unrelated mods. Locked mods still
    /// bypass this just like they bypass the profile filter.</summary>
    private HashSet<string>? _dependencyFilter;

    /// <summary>FileSystemWatcher on the active mods folder.
    /// Triggers an auto-rescan when a `.vmz` lands or disappears,
    /// so the user doesn't have to click Refresh after dropping
    /// a file in Explorer. Disposed + recreated when the user
    /// changes the mods folder in Settings.</summary>
    private FileSystemWatcher? _modsWatcher;

    /// <summary>Debounce timer for the file-system watcher. Multi-
    /// file copies fire one event per file at high frequency;
    /// without a debounce we'd rescan N times in a second. The
    /// timer collapses a burst into a single rescan ~800ms after
    /// the last event.</summary>
    private System.Windows.Forms.Timer? _watcherDebounce;

    /// <summary>Snapshot of every profile on disk, refreshed on
    /// startup and after profile-manager actions. Used to populate
    /// the title-row selector menu.</summary>
    private List<Domain.ModProfile> _allProfiles = new();
    /// <summary>Reapplies the saved splitter ratio. Captured from
    /// InitializeWindow so the toolbar's "Show conflicts" handler can
    /// re-snap Panel2 to the right width when the sidebar comes back —
    /// otherwise the SplitContainer keeps whatever transient
    /// SplitterDistance it had at startup-while-collapsed.</summary>
    private Action _applySplit = () => { };
    private Panel _setupBanner = null!;
    private Label _setupBannerLabel = null!;
    private TextBox _filterBox = null!;

    /// <summary>Single ContextMenuStrip instance assigned to the
    /// mods grid. Items are rebuilt each time it opens, based on
    /// the row index captured by MouseDown.</summary>
    private ContextMenuStrip _modsContextMenu = null!;
    private int _modsContextRow = -1;

    /// <summary>True when settings.json didn't exist at startup —
    /// i.e. the user has never run any edition of the manager on
    /// this machine. Drives the welcome / backup prompt. Captured
    /// before Settings.Load() because Load() returns defaults for a
    /// missing file, erasing the signal otherwise.</summary>
    private readonly bool _isFirstRun;

    public MainForm()
    {
        _isFirstRun = !File.Exists(Settings.Path);
        _settings = Settings.Load();
        _modConfig = ModConfig.Load(ModConfig.DefaultPath);
#if AI_RESOLVER
        _claude.OverridePath = _settings.ClaudePath;
        _resolver = new ConflictResolver(_claude, _registry)
        {
            GameSourcePath = _settings.GameSourcePath,
        };
#endif
        InitializeWindow();
        BuildLayout();
        Shown += async (_, _) => await RunStartupAsync();
    }

    /// <summary>Reloads mod_config.cfg from disk and rescans the
    /// mods folder, applying cfg as the source of truth for
    /// per-mod enabled state and priority. Wraps `_registry.Scan`
    /// so every refresh path (RefreshAllAsync, after-toggle, etc.)
    /// picks up cfg changes the in-game loader may have written
    /// since our last scan.</summary>
    /// <summary>Return the ModEntry attached to a given grid row,
    /// or null when the row is a pack-header (Tag is a "pack:…"
    /// string sentinel). All click / context-menu / value-changed
    /// handlers route through this so they uniformly skip header
    /// rows without indexing into the wrong place.</summary>
    private ModEntry? ModAtRow(int rowIdx)
    {
        if (_modsGrid == null) return null;
        if (rowIdx < 0 || rowIdx >= _modsGrid.Rows.Count) return null;
        return _modsGrid.Rows[rowIdx].Tag as ModEntry;
    }

    /// <summary>If the given grid row is a pack-header, returns
    /// its pack name; otherwise empty string. The header's Tag
    /// is the sentinel "pack:&lt;name&gt;".</summary>
    private string PackHeaderAt(int rowIdx)
    {
        if (_modsGrid == null) return "";
        if (rowIdx < 0 || rowIdx >= _modsGrid.Rows.Count) return "";
        if (_modsGrid.Rows[rowIdx].Tag is string s
            && s.StartsWith("pack:", StringComparison.Ordinal))
            return s.Substring(5);
        return "";
    }

    /// <summary>Convert a GRID row index into a _displayed
    /// (mod-only) list index. With pack-header rows interleaved,
    /// the two indexes don't line up: a grid row at position 5
    /// might map to _displayed[3] if there are two headers
    /// above it. When `nearestMod` is true and the targeted row
    /// is a header, walks down to the first mod row below it
    /// (so dropping on a header lands the mod at the top of
    /// that pack). Returns -1 when no valid mod position can
    /// be derived.</summary>
    private int GridRowToDisplayedIndex(int gridRowIdx, bool nearestMod = false)
    {
        if (_modsGrid == null) return -1;
        if (gridRowIdx < 0 || gridRowIdx >= _modsGrid.Rows.Count) return -1;
        // Walk down from gridRowIdx until we land on a mod row,
        // counting mod rows we pass.
        var target = nearestMod ? FindNearestModRow(gridRowIdx) : gridRowIdx;
        if (target < 0) return -1;
        var entryAtTarget = ModAtRow(target);
        if (entryAtTarget == null) return -1;
        // Index of entryAtTarget within _displayed.
        for (int i = 0; i < _displayed.Count; i++)
            if (ReferenceEquals(_displayed[i], entryAtTarget)) return i;
        return -1;
    }

    private int FindNearestModRow(int gridRowIdx)
    {
        // Try the row itself, then walk down, then walk up.
        if (ModAtRow(gridRowIdx) != null) return gridRowIdx;
        for (int i = gridRowIdx + 1; i < _modsGrid.Rows.Count; i++)
            if (ModAtRow(i) != null) return i;
        for (int i = gridRowIdx - 1; i >= 0; i--)
            if (ModAtRow(i) != null) return i;
        return -1;
    }

    /// <summary>Toggle the collapsed/expanded state of a pack
    /// group and refresh the grid. Persists to Settings so the
    /// state survives between sessions.</summary>
    private void TogglePackCollapse(string packName)
    {
        if (string.IsNullOrEmpty(packName)) return;
        var existing = _settings.CollapsedPacks.FirstOrDefault(p =>
            string.Equals(p, packName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(existing))
            _settings.CollapsedPacks.Remove(existing);
        else
            _settings.CollapsedPacks.Add(packName);
        try { _settings.Save(); } catch { /* best-effort */ }
        PopulateModsGrid();
    }

    private bool Rescan()
    {
        _modConfig = ModConfig.Load(ModConfig.DefaultPath);
        var ok = _registry.Scan(ModsDir, _modConfig);
        AutoAdoptOrphansIntoActiveProfile();
        return ok;
    }

    /// <summary>Reconcile the registry against the active profile:
    /// any `.vmz` (or directory mod) sitting in the live mods
    /// folder but NOT listed in the profile gets adopted —
    /// recorded as a ProfileMod entry and given a cfg
    /// enabled/priority row. Without this step, mods the user
    /// drops directly in Explorer end up "registered but
    /// invisible" — the grid's active-profile filter hides them
    /// AND mod_config.cfg has no entry so the in-game loader
    /// leaves them off too.
    ///
    /// Constraints:
    ///   • Only runs when an active profile exists. Pre-migration
    ///     (no profile) the grid shows everything anyway, so
    ///     there's nothing to "adopt".
    ///   • Skips mods with empty mod_id — can't be tracked in
    ///     profile.json or cfg without one.
    ///   • Idempotent: a second scan finds nothing to adopt
    ///     because the previous scan put it in the profile.</summary>
    private void AutoAdoptOrphansIntoActiveProfile()
    {
        if (_activeProfile == null) return;

        var profileIds = new HashSet<string>(
            _activeProfile.Mods.Select(m => m.ModId),
            StringComparer.OrdinalIgnoreCase);

        var adopted = 0;
        var cfgDirty = false;
        foreach (var entry in _registry.Entries)
        {
            if (string.IsNullOrEmpty(entry.ModId)) continue;
            if (profileIds.Contains(entry.ModId)) continue;

            // Add to profile with the registry's read of the mod's
            // current state. IsEnabled mirrors whatever the on-
            // disk location implies (true unless the file lives
            // under Disabled/), so a mod the user manually parked
            // in Disabled/ stays disabled. Priority comes from
            // DeclaredPriority — the [mod] priority in mod.txt —
            // so the load order matches what the mod author
            // suggested.
            _activeProfile.Mods.Add(new Domain.ProfileMod
            {
                ModId         = entry.ModId,
                DisplayName   = entry.DisplayName,
                Version       = entry.Version,
                IsEnabled     = entry.IsEnabled,
                Priority      = entry.DeclaredPriority,
                ModWorkshopId = entry.ModWorkshopId,
            });
            profileIds.Add(entry.ModId);

            // Write the cfg row too — without this the in-game
            // loader doesn't know the mod is enabled (it doesn't
            // apply the manager's fallback=true rule).
            _modConfig.SetEnabled(entry.ModId, entry.Version, entry.IsEnabled);
            _modConfig.SetPriority(entry.ModId, entry.Version, entry.DeclaredPriority);
            cfgDirty = true;
            adopted++;
        }
        if (adopted == 0) return;

        _activeProfile.UpdatedAt = DateTime.UtcNow;
        try { _activeProfile.SaveMetadataOnly(); } catch { /* best-effort */ }
        if (cfgDirty)
        {
            try { _modConfig.Save(); } catch { /* best-effort; rare */ }
        }
        // Re-apply cfg to the in-memory registry so each adopted
        // entry's Priority field reflects the freshly-written cfg
        // value (cfg writes don't mutate entries directly — they
        // only persist; the next read picks them up).
        _registry.Scan(ModsDir, _modConfig);
    }

    /// <summary>Hook a `FileSystemWatcher` onto the active mods
    /// folder so dropping a `.vmz` in Explorer (or any other
    /// external write) auto-triggers a rescan + refresh + orphan
    /// adoption — no more clicking Refresh after every Explorer
    /// drop. Disposes any previous watcher, so it's safe to call
    /// repeatedly (e.g. after the user changes the mods folder
    /// in Settings).</summary>
    private void InitModsFolderWatcher()
    {
        if (_modsWatcher != null)
        {
            try { _modsWatcher.EnableRaisingEvents = false; } catch { }
            try { _modsWatcher.Dispose(); } catch { }
            _modsWatcher = null;
        }
        if (!Directory.Exists(ModsDir)) return;
        try
        {
            _modsWatcher = new FileSystemWatcher(ModsDir)
            {
                Filter = "*.vmz",
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
                // We only care about top-level live mods. Library/
                // and Backups/ subfolders are manager-managed and
                // shouldn't trigger user-visible rescans.
                IncludeSubdirectories = false,
            };
            _modsWatcher.Created += (_, _) => ScheduleAutoRescan();
            _modsWatcher.Deleted += (_, _) => ScheduleAutoRescan();
            _modsWatcher.Renamed += (_, _) => ScheduleAutoRescan();
            _modsWatcher.Changed += (_, _) => ScheduleAutoRescan();
            _modsWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Permission denied / unsupported FS — fall back to
            // manual Refresh, but don't crash the manager.
            _modsWatcher = null;
        }
    }

    /// <summary>Marshal a debounced rescan onto the UI thread. The
    /// watcher fires on its own thread; we can't touch UI from
    /// there. The debounce timer collapses a burst (multi-file
    /// copy from Explorer = N events in a row) into a single
    /// rescan after the burst settles.</summary>
    private void ScheduleAutoRescan()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (_watcherDebounce == null)
                {
                    _watcherDebounce = new System.Windows.Forms.Timer
                    {
                        Interval = 800,
                    };
                    _watcherDebounce.Tick += (_, _) =>
                    {
                        _watcherDebounce!.Stop();
                        try
                        {
                            Rescan();
                            UpdateModsStatus();
                            PopulateModsGrid();
                            _lastConflicts = DetectConflictsForActive();
                            UpdateConflictsStatus(_lastConflicts);
                            PopulateConflictsList(_lastConflicts);
                            _modsLabel.Text =
                                $"Mods: {_registry.Entries.Count} found "
                                + "— auto-refreshed (mods folder changed).";
                        }
                        catch { /* best-effort */ }
                    };
                }
                _watcherDebounce.Stop();
                _watcherDebounce.Start();
            }));
        }
        catch { /* BeginInvoke can throw if the form's closing */ }
    }

    /// <summary>Saves mod_config.cfg, surfacing failures in a
    /// dialog. Returns true on success so callers can short-circuit
    /// (revert UI state, abort batched flows). Save-failure is rare
    /// — APPDATA is user-writable — but file-locking by the in-game
    /// loader during simultaneous use is the most likely real
    /// cause.</summary>
    private bool SaveModConfigSafely()
    {
        try { _modConfig.Save(); return true; }
        catch (Exception ex)
        {
            ShowError("Couldn't save mod_config.cfg", ex);
            return false;
        }
    }

    /// <summary>Soviet-aesthetic decorations painted on the form's
    /// background BEHIND every control. Three layers:
    ///   1. weathered noise texture (subtle stippled greys)
    ///   2. faux-Cyrillic watermark (large diagonal stencil-style
    ///      banner — `★ ВФSТФК ★ МФD ★ МАNАGЕЯ ★`)
    ///   3. red stencil divider with a `★ MOD CATALOG ★` label below
    ///      the title row
    /// All layered at low opacity so the actual UI text on top
    /// stays primary. Repainted on every form resize.</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        PaintSovietDecorations(e.Graphics);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Repaint background so the watermark + divider follow the
        // new form size. Children invalidate themselves naturally.
        Invalidate();
    }

    private void PaintSovietDecorations(Graphics g)
    {
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        // --- Layer 1: weathered noise (cached + tiled) ----------
        var noise = GetOrBuildNoise();
        for (var ny = 0; ny < h; ny += noise.Height)
            for (var nx = 0; nx < w; nx += noise.Width)
                g.DrawImageUnscaled(noise, nx, ny);

        // --- Layer 2: huge faded faux-Cyrillic watermark --------
        // Tilted, drawn with VERY low alpha so UI text reads cleanly
        // on top. Visible only in the form's empty-background strips
        // between/beside controls — the layered-overlay experiment
        // that tried to paint it on TOP of controls produced solid
        // pink halos from chroma-key alpha bleed and was reverted.
        var prev = g.SmoothingMode;
        var prevText = g.TextRenderingHint;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        var watermark = "★ ВФSТФК ★ МФD ★ МАNАGЕЯ ★";
        using (var wf = new Font("Impact", 110f, FontStyle.Bold))
        using (var wb = new SolidBrush(Color.FromArgb(52, 210, 210, 230)))
        {
            var st = g.Save();
            g.TranslateTransform(50, h * 0.36f);
            g.RotateTransform(-6f);
            g.DrawString(watermark, wf, wb, 0, 0);
            g.Restore(st);
        }

        // --- Layer 3: stencil divider with "★ MOD CATALOG ★" label
        // Just below the title row. The label uses Latin so it stays
        // readable; the faux-Cyrillic is reserved for purely
        // decorative elements (watermark above). Bumped to ~2x for
        // weight — at 12pt it read as a fine-print caption rather
        // than a stencil banner.
        // Sits in the gap between the title-row baseline and the
        // first status-label row — overlapped "Mods: N found" at y=102.
        var dividerY = 86f;
        using (var dp = new Pen(Color.FromArgb(110, 200, 50, 60), 3f))
            g.DrawLine(dp, 24, dividerY, w - 24, dividerY);
        var label = "★  MOD  CATALOG  ★";
        using (var lf = new Font("Consolas", 24f, FontStyle.Bold))
        using (var bgBrush = new SolidBrush(BackColor))
        using (var lb = new SolidBrush(Color.FromArgb(220, 200, 50, 60)))
        {
            var sz = g.MeasureString(label, lf);
            var labelX = (w - sz.Width) / 2f;
            // Erase the line behind the label so the text floats on
            // a clean background. Padding bumped with the font to
            // keep the same visual breathing room around the label.
            g.FillRectangle(bgBrush, labelX - 18, dividerY - sz.Height / 2f,
                sz.Width + 36, sz.Height);
            g.DrawString(label, lf, lb, labelX, dividerY - sz.Height / 2f);
        }
        g.SmoothingMode = prev;
        g.TextRenderingHint = prevText;
    }

    /// <summary>Cached noise texture. Built once at lazy first-use
    /// (small, 256x256 tile that we DrawImageUnscaled across the
    /// form). Static so it survives MainForm reconstruction in
    /// case we ever support multiple instances.</summary>
    private static Bitmap? _noiseTile;
    private static Bitmap GetOrBuildNoise()
    {
        if (_noiseTile != null) return _noiseTile;
        const int size = 256;
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rand = new Random(42);
        for (var y = 0; y < size; y += 2)
        {
            for (var x = 0; x < size; x += 2)
            {
                if (rand.Next(0, 8) < 3)
                    bmp.SetPixel(x, y, Color.FromArgb(8, 200, 200, 200));
            }
        }
        _noiseTile = bmp;
        return _noiseTile;
    }

    private void InitializeWindow()
    {
        // OS title bar text — kept clean Latin so Alt-Tab + the
        // taskbar list it correctly. The decorative faux-Cyrillic
        // version lives on the in-form title label.
#if AI_RESOLVER
        Text = "Road to Vostok Mod Manager AI";
#else
        Text = "Road to Vostok Mod Manager Integrated";
#endif
        MinimumSize = new Size(900, 600);
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 12f);
        // Global dark theme for every ContextMenuStrip / popup
        // ToolStrip the app creates — must run BEFORE any menu
        // is constructed so the renderer is in place when those
        // controls cache their parent's renderer.
        Ui.DarkMenuTheme.Install();
        // Compose the whole frame off-screen and blit once — without
        // this the soviet decorations painted in OnPaintBackground are
        // visibly drawn first, then over-painted by child controls a
        // moment later, which shows up as flicker on startup/resize
        // and as ghostly white control-rectangles in intermediate
        // paint frames. The form itself is fixed here; every child
        // container needs its own DoubleBuffered turned on (via the
        // EnableDoubleBufferRecursive reflection helper called from
        // BuildLayout's tail) for the chain to actually compose
        // glitch-free.
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint,
            true);
        // ExtractAssociatedIcon pulls the .exe's own embedded icon
        // (set via <ApplicationIcon> in the .csproj). Wrapped — older
        // Win10 builds occasionally throw IOException on this call.
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { /* form falls back to the default WinForms icon */ }
        // Drag-and-drop install:
        //   .vmz → copy into mods folder + add to active profile
        //   .json → treat as a mod pack manifest (ModListImport
        //           shape); routes through the Import list flow,
        //           same as picking the file via the toolbar.
        // Mixed drops act on whichever file types match; unknown
        // extensions are silently ignored.
        AllowDrop = true;
        DragEnter += (_, e) => TryAcceptFileDrag(e);
        DragDrop += async (_, e) => await HandleFileDropAsync(e);

        // Restore last-session size + position if we have one. Validate
        // the saved bounds intersect a current screen so a multi-monitor
        // disconnect can't park us offscreen.
        var saved = new Rectangle(
            _settings.WindowLeft, _settings.WindowTop,
            _settings.WindowWidth, _settings.WindowHeight);
        if (saved.Width >= 600 && saved.Height >= 400
            && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)))
        {
            StartPosition = FormStartPosition.Manual;
            Location = saved.Location;
            Size = saved.Size;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            Width = 1280;
            Height = 800;
        }
        if (_settings.WindowMaximized)
            WindowState = FormWindowState.Maximized;

        // Persist the new bounds on close. Skip the save when
        // minimised — RestoreBounds gives us the un-minimised values
        // so we don't get stuck restoring a tiny "minimised" rect.
        FormClosing += (_, _) =>
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settings.WindowLeft = b.Left;
            _settings.WindowTop = b.Top;
            _settings.WindowWidth = b.Width;
            _settings.WindowHeight = b.Height;
            _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
            // Snapshot per-grid column widths. Wrapped null-checks
            // because BuildLayout assigns the grid fields, but if a
            // construction error fired FormClosing before that ran
            // we don't want to NRE on the way out.
            if (_modsGrid != null) SaveColumnWidths(_modsGrid, "mods");
            if (_conflictsGrid != null) SaveColumnWidths(_conflictsGrid, "conflicts");
            try { _settings.Save(); }
            catch { /* best-effort; don't block app close on a write error */ }
            // Stop the file-system watcher so the rescan timer
            // can't fire mid-shutdown and touch disposed controls.
            try { _modsWatcher?.Dispose(); } catch { }
            try { _watcherDebounce?.Stop(); _watcherDebounce?.Dispose(); } catch { }
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            // 11 rows: title (0), banner (1), Claude/Mods/Updates/
            // Conflicts/MML/Manager/Drift/Checkpoint status labels
            // (2-9), split (10, fills). Drift + Checkpoint rows
            // are hidden by default and only appear when
            // their condition fires.
            RowCount = 11,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Color.Transparent,
        };
        for (var i = 0; i < 10; i++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        // Title row: [star] [title] [Launch] [Settings]
        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 7,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8),
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // star
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); // title (fills)
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // launch
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // profile selector
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // profiles
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // mod packager
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // settings

        // Decorative red star ornament next to the title — pure
        // soviet-aesthetic flourish, no behaviour. Painted via
        // OnPaint so it can be a proper polygon at any DPI.
        var titleStar = new Panel
        {
            Width = 44,
            Height = 44,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 12, 0),
        };
        titleStar.Paint += (s, e) => DrawStar(
            e.Graphics, 22, 22, 18,
            Color.FromArgb(220, 200, 50, 60));
        titleRow.Controls.Add(titleStar, 0, 0);

        // Title in faux-Cyrillic — Я for R (distinctive mirror) and
        // И for N (also distinctive). Cyrillic look-alikes for the
        // rest (О, А, Т, К, М, Е) are visually identical to Latin
        // so the text still reads as "Road to Vostok Mod Manager"
        // at a glance, but the spotted Я / И give it the soviet
        // stencil-poster flavour.
        // Title + version stack. Title doubles as the About-dialog
        // entry point — hand cursor + tooltip make it discoverable
        // without crowding the toolbar with another button next to
        // Launch / Profiles / Settings. The version label below is
        // small / dim and also clickable so users can spot the
        // version at a glance.
        var titleStack = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount    = 2,
            AutoSize    = true,
            BackColor   = Color.Transparent,
            Anchor      = AnchorStyles.Left,
            Margin      = new Padding(0),
        };
        var title = new Label
        {
#if AI_RESOLVER
            Text = "ЯOAD TO VOSTOK MOD MAИAGEЯ  ·  AI",
#else
            Text = "ЯOAD TO VOSTOK MOD MAИAGEЯ  ·  IИTEGЯATED",
#endif
            Font      = new Font(Font.FontFamily, 24f, FontStyle.Bold),
            AutoSize  = true,
            Anchor    = AnchorStyles.Left,
            Cursor    = Cursors.Hand,
            Margin    = new Padding(0),
            BackColor = Color.Transparent,
        };
        var asmVer = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version ?? new Version(0, 0, 0);
#if AI_RESOLVER
        var editionTag = "AI";
#else
        var editionTag = "Integrated";
#endif
        var versionLabel = new Label
        {
            Text      = $"v{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}  ·  "
                      + $"{editionTag} edition  ·  click for About",
            Font      = new Font("Consolas", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 150, 170),
            AutoSize  = true,
            Anchor    = AnchorStyles.Left,
            Cursor    = Cursors.Hand,
            Margin    = new Padding(2, 0, 0, 0),
            BackColor = Color.Transparent,
        };
        var titleTip = new ToolTip();
        titleTip.SetToolTip(title, "Click for version and credits");
        titleTip.SetToolTip(versionLabel, "Click for version and credits");
        title.Click        += (_, _) => OpenAboutDialog();
        versionLabel.Click += (_, _) => OpenAboutDialog();
        titleStack.Controls.Add(title,        0, 0);
        titleStack.Controls.Add(versionLabel, 0, 1);
        titleRow.Controls.Add(titleStack, 1, 0);

        // Green-themed Launch Game button. Reuses ThemedButton's
        // FlatStyle + sizing scaffolding then overrides the colours
        // for a forest-green look that visually distinguishes it
        // from the (slate) Settings button next to it. Sits at col
        // 2 — leftmost of the action group — so the primary action
        // ("play the game") is the first thing the user sees in
        // the right-hand button cluster.
        var launchBtn = ThemedButton("▶ Launch Game");
        launchBtn.Width = 170;
        launchBtn.Height = 40;
        launchBtn.AutoSize = false;
        launchBtn.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        launchBtn.Margin = new Padding(0, 0, 8, 0);
        launchBtn.BackColor = Color.FromArgb(45, 90, 55);
        launchBtn.ForeColor = Color.FromArgb(225, 240, 230);
        launchBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        launchBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 115, 70);
        launchBtn.FlatAppearance.MouseDownBackColor = Color.FromArgb(35, 70, 45);
        launchBtn.Click += (_, _) => LaunchVostok();
        titleRow.Controls.Add(launchBtn, 2, 0);

        // Active-profile selector. Reads "📋 Active: <name> ▾" and
        // clicking opens a ContextMenuStrip with every known
        // profile + the migration-prompt entry when no profile is
        // active yet. Populated lazily on click so the menu always
        // reflects the latest _allProfiles snapshot. The menu's
        // Font matches the button so the dropdown items don't
        // jump to a smaller system menu font when the menu opens.
        _profileSelector = ThemedButton("📋 (no profile) ▾");
        _profileSelector.Width    = 220;
        _profileSelector.Height   = 40;
        _profileSelector.AutoSize = false;
        _profileSelector.Anchor   = AnchorStyles.Right | AnchorStyles.Top;
        _profileSelector.Margin   = new Padding(0, 0, 8, 0);
        _profileSelector.TextAlign = ContentAlignment.MiddleLeft;
        // Tinted slate-blue to differentiate from the neutral
        // Profiles… / Packager / Settings buttons next to it —
        // this is the only one that reflects current STATE
        // (which profile is live), not a static action, so it
        // earns its own accent. Distinct from Launch's forest
        // green and the destructive-action red elsewhere in the
        // app.
        _profileSelector.BackColor = Color.FromArgb(45, 70, 100);
        _profileSelector.ForeColor = Color.FromArgb(225, 240, 255);
        _profileSelector.FlatAppearance.BorderColor      = Color.FromArgb(90, 140, 200);
        _profileSelector.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 90, 130);
        _profileSelector.FlatAppearance.MouseDownBackColor = Color.FromArgb(35, 55, 80);
        // Explicit Font, not `_profileSelector.Font` — the button
        // hasn't been parented yet at this point, so its Font
        // property still returns SystemFonts.DefaultFont (Segoe UI
        // 9pt). Once the button gets added to titleRow it inherits
        // the form's Segoe UI 12pt, but the menu was already
        // constructed with the wrong size. Hard-code to match the
        // form's font.
        var profileMenu = new ContextMenuStrip
        {
            Font = new Font("Segoe UI", 12f),
        };
        _profileSelector.Click += (_, _) =>
        {
            RebuildProfileSelectorMenu(profileMenu);
            profileMenu.Show(_profileSelector,
                new Point(0, _profileSelector.Height));
        };
        titleRow.Controls.Add(_profileSelector, 3, 0);

        // Profiles button — sits between Launch and Settings so the
        // user can save / apply mod loadouts without digging through
        // the mods toolbar. Same slate theme as Settings; the green
        // accent is reserved for the launch action.
        var profilesBtn = ThemedButton("⊞ Profiles…");
        profilesBtn.Width = 140;
        profilesBtn.Height = 40;
        profilesBtn.AutoSize = false;
        profilesBtn.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        profilesBtn.Margin = new Padding(0, 0, 8, 0);
        profilesBtn.Click += async (_, _) => await OpenProfilesDialogAsync();
        titleRow.Controls.Add(profilesBtn, 4, 0);

        // Mod Packager — creator-side tool: takes a folder or
        // existing .vmz/.zip, lets the author edit manifest fields
        // + tick required/optional dependencies, then writes a
        // fresh .vmz with forward-slash entry paths (so the in-game
        // loader accepts it). Lives in the title row alongside
        // Profiles so creators discover it without digging.
        var packagerBtn = ThemedButton("🔨 Mod Packager…");
        packagerBtn.Width = 180;
        packagerBtn.Height = 40;
        packagerBtn.AutoSize = false;
        packagerBtn.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        packagerBtn.Margin = new Padding(0, 0, 8, 0);
        packagerBtn.Click += (_, _) => OpenModPackagerDialog();
        titleRow.Controls.Add(packagerBtn, 5, 0);

        var settingsBtn = ThemedButton("⚙ Settings…");
        settingsBtn.Width = 140;
        settingsBtn.Height = 40;
        settingsBtn.AutoSize = false;
        settingsBtn.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        settingsBtn.Margin = new Padding(0, 0, 0, 0);
        settingsBtn.Click += (_, _) => OpenSettingsDialog();
        titleRow.Controls.Add(settingsBtn, 6, 0);

        root.Controls.Add(titleRow, 0, 0);

        _setupBanner = BuildSetupBanner();
        root.Controls.Add(_setupBanner, 0, 1);

#if AI_RESOLVER
        _claudeLabel = NewStatus("Claude Code: detecting ...");
        root.Controls.Add(_claudeLabel, 0, 2);
#else
        // Reserve the row anyway so the status stack below it lines up
        // with the AI build. A blank zero-height filler keeps the
        // TableLayoutPanel row index assignments stable across editions.
        root.Controls.Add(new Panel { Height = 0, BackColor = Color.Transparent }, 0, 2);
#endif

        _modsLabel = NewStatus("Mods: scanning ...");
        root.Controls.Add(_modsLabel, 0, 3);

        _updatesLabel = NewStatus("Updates: —");
        root.Controls.Add(_updatesLabel, 0, 4);

        _conflictsLabel = NewStatus("Conflicts: —");
        root.Controls.Add(_conflictsLabel, 0, 5);

        // MML latest-release indicator. Click opens the GitHub
        // releases page in the user's default browser. We only know
        // the LATEST published release here — installed-version
        // detection isn't implemented because MML ships as a Godot
        // .pck and reading its internal VERSION constant requires
        // pck-walking we don't do today.
        _mmlLabel = NewStatus("MML: checking …");
        _mmlLabel.Cursor = Cursors.Hand;
        _mmlLabel.Click += async (_, _) => await HandleMmlClickAsync();
        root.Controls.Add(_mmlLabel, 0, 6);

        // Mod-manager self-update indicator. Same data flow as the
        // per-mod update column — polls ModWorkshop /mods/versions
        // with our own modid (ManagerModWorkshopId) and compares
        // against the assembly version. Clickable: opens the mod page
        // so the user can grab the new .exe.
        _managerLabel = NewStatus("Manager: checking …");
        _managerLabel.Cursor = Cursors.Hand;
        _managerLabel.Click += async (_, _) => await HandleManagerLabelClickAsync();
        root.Controls.Add(_managerLabel, 0, 7);

        // Live-vs-active-profile drift indicator. Hidden by
        // default; UpdateDriftStatus toggles it visible whenever
        // the live registry's enable / priority / version state
        // diverges from the active profile. Click to commit the
        // live state back into the profile so they re-converge.
        _driftLabel = NewStatus("");
        _driftLabel.Visible = false;
        _driftLabel.Cursor = Cursors.Hand;
        _driftLabel.Click += (_, _) => SyncLiveStateIntoActiveProfile();
        root.Controls.Add(_driftLabel, 0, 8);

        // Pre-launch checkpoint indicator. Visible only when a
        // checkpoint exists on disk (set by LaunchVostok before
        // each launch). Click to restore the snapshot — useful
        // when the game crashed or behaved oddly after a launch
        // and the user wants to undo whatever cfg / profile
        // change preceded it.
        _checkpointLabel = NewStatus("");
        _checkpointLabel.Visible = false;
        _checkpointLabel.Cursor = Cursors.Hand;
        _checkpointLabel.Click += (_, _) => RestoreCheckpoint();
        root.Controls.Add(_checkpointLabel, 0, 9);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = Color.Transparent,
            SplitterWidth = 6,
        };

        // Left: interactive mods grid + toolbar (filter, bulk, refresh).
        _modsGrid = BuildModsGrid();
        ApplyColumnWidths(_modsGrid, "mods");
        _split.Panel1.Controls.Add(
            WrapInPanel("Installed mods", _modsGrid, BuildModsToolbar()));

        // Right: interactive conflicts grid with Resolve button.
        // Empty spacer toolbar of the same height as the mods
        // toolbar (48px) so the two grids' column headers vertically
        // align — without it the conflicts headers floated up to the
        // top of the panel while the mods headers sat below the
        // toolbar, looking misaligned across the splitter.
        _conflictsGrid = BuildConflictsGrid();
        ApplyColumnWidths(_conflictsGrid, "conflicts");
        var conflictsSpacer = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = Color.Transparent,
        };
        _split.Panel2.Controls.Add(
            WrapInPanel("Conflicts", _conflictsGrid, conflictsSpacer));
        // Apply persisted sidebar visibility. Panel2Collapsed hides the
        // panel and the splitter; the SplitterDistance underneath is
        // preserved so re-showing restores the same ratio.
        _split.Panel2Collapsed = !_settings.ConflictsVisible;
        SyncConflictsToggleLabel();

        // Mods is the primary view — give it ~62% of the width by
        // default, or whatever ratio the user dragged it to last
        // session. Subscribe to Resize BEFORE adding the container to
        // its parent — the Add triggers the initial layout pass, and
        // a later subscription would miss that first event entirely.
        // Shown fires after the form has its real client size, so it
        // also guarantees a correct distance regardless of when the
        // SplitContainer first landed at its final width.
        // Use any positive saved ratio with a safety clamp at apply
        // time. Earlier we used `is > 0.1 and < 0.9` for the load
        // check, which silently rejected saved values outside that
        // band — including reasonable user preferences like 0.95
        // (very wide mods) or 0.05 (very narrow). The fallback to
        // 0.62 only kicks in for the "fresh install, no saved value"
        // case (ratio = 0).
        var ratio = _settings.SplitterRatio > 0
            ? Math.Clamp(_settings.SplitterRatio, 0.05, 0.95)
            : 0.62;
        // Layout-time SplitterMoved events fire DURING the form's
        // initial layout pass, with whatever transient sizes the
        // SplitContainer happens to be in (default SplitterDistance
        // 50, intermediate parent widths, etc.). Those events
        // shouldn't be persisted — they don't represent user intent.
        // Only honor SplitterMoved after Form.Shown completes.
        var formShown = false;
        void ApplySplit()
        {
            // Don't fight the ratio while the sidebar is collapsed —
            // the SplitContainer ignores SplitterDistance changes in
            // that state, and re-asserting them would also generate
            // spurious SplitterMoved events when toggling visibility.
            if (_split.Panel2Collapsed) return;
            if (_split.Width > 100)
            {
                var dist = (int)(_split.Width * ratio);
                // Clamp inside the SplitContainer's allowed range so
                // a small window can't crash with an out-of-range
                // SplitterDistance.
                var min = _split.Panel1MinSize;
                var max = _split.Width - _split.Panel2MinSize - _split.SplitterWidth;
                if (max > min)
                    _split.SplitterDistance = Math.Clamp(dist, min, max);
            }
        }
        _applySplit = ApplySplit;   // captured for the toggle-button handler
        _split.Resize += (_, _) => ApplySplit();
        _split.SplitterMoved += (_, _) =>
        {
            if (!formShown) return;
            if (_split.Panel2Collapsed) return;
            if (_split.Width <= 100) return;
            var observed = (double)_split.SplitterDistance / _split.Width;
            // 0.005 = half a percent — well above float drift /
            // int-truncation noise, well below any deliberate drag.
            if (Math.Abs(observed - ratio) < 0.005) return;
            ratio = observed;
            _settings.SplitterRatio = ratio;
        };
        root.Controls.Add(_split, 0, 10);
        Shown += (_, _) =>
        {
            ApplySplit();
            // Flip the flag AFTER ApplySplit so its own induced
            // SplitterMoved (still mid-Shown handler) is also
            // ignored. The first event we treat as user-intent is
            // whatever fires after Shown returns — i.e. an actual
            // drag from the user.
            BeginInvoke(() => formShown = true);
        };

        // Once the whole tree is built, flip DoubleBuffered on every
        // container we own. The form's own DoubleBuffered (set in
        // InitializeWindow) buffers the FORM's paint surface; every
        // child container (TableLayoutPanel, Panel, SplitContainer
        // panels, ...) still paints directly unless its own
        // DoubleBuffered is true — and the property is protected, so
        // the only way in from outside is reflection.
        EnableDoubleBufferRecursive(this);
    }

    /// <summary>Walks the control tree and forces DoubleBuffered = true
    /// on every container. Skips DataGridView (which has its own
    /// internal double-buffering toggle and gets unhappy if the parent
    /// trick is applied to it). The property is protected on Control,
    /// so we reach it via reflection — standard WinForms workaround,
    /// safe because the property has no side effects beyond toggling
    /// the OptimizedDoubleBuffer style bit.</summary>
    private static void EnableDoubleBufferRecursive(Control root)
    {
        var prop = typeof(Control).GetProperty(
            "DoubleBuffered",
            System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance);
        void Walk(Control c)
        {
            if (c is not DataGridView)
            {
                try { prop?.SetValue(c, true, null); }
                catch { /* control type doesn't expose the setter — fine */ }
            }
            foreach (Control child in c.Controls) Walk(child);
        }
        Walk(root);
    }

    private static Label NewStatus(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        Margin    = new Padding(0, 2, 0, 2),
        ForeColor = Color.FromArgb(180, 190, 210),
        // Explicit Transparent so the form's soviet decorations
        // (watermark, noise, MOD CATALOG divider) show through the
        // status row instead of being masked by an opaque label
        // rectangle. Without this the labels render with the system
        // default Control color (a light gray that reads as white on
        // our dark theme) for the brief window before parent BackColor
        // inheritance kicks in — which is what produces the white-bar
        // intermediate paint state.
        BackColor = Color.Transparent,
    };

    /// <summary>Themed Button factory — by default WinForms paints
    /// Button controls in the system-light scheme regardless of the
    /// parent form's BackColor/ForeColor, which renders our text in
    /// near-white on near-white. FlatStyle=Flat with explicit colors
    /// keeps everything readable against the dark slate panel.</summary>
    /// <summary>Inspects a DragEnter / DragOver event for a file
    /// drop. If at least one path is acceptable (.vmz or .json),
    /// sets the effect to Copy so the drop cursor lights up.
    /// Shared between the form-level drop and the mods-grid drop so
    /// dropping ON the grid works as obviously as dropping on the
    /// title bar.</summary>
    private void TryAcceptFileDrag(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (files.Any(IsAcceptedDropPath))
            e.Effect = DragDropEffects.Copy;
    }

    /// <summary>Handles the file-drop body for both the form and
    /// the mods grid: .vmz files install via the toolbar's installer
    /// flow, .json files each kick off their own Import-list session.
    /// </summary>
    private async Task HandleFileDropAsync(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var vmz = files
            .Where(f => f.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var jsons = files
            .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (vmz.Count > 0) await InstallModFilesAsync(vmz);
        // Each .json gets its own Import list session — multiple
        // packs at once would otherwise serialise their plan
        // dialogs into a single mega-list, which makes the
        // accept/cancel decision less granular.
        foreach (var json in jsons)
            await ImportModListFromFilePickerAsync(json);
    }

    public static Button ThemedButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 55, 70),
            ForeColor = Color.FromArgb(225, 230, 240),
            AutoSize = true,
            UseVisualStyleBackColor = false,
            // Universal safety net for high-DPI / non-100%-scaling
            // displays. Most callers set AutoSize=false + an explicit
            // Width (e.g. 110, 140) because the layouts assume those
            // pixel sizes. On a 125%/150% DPI screen the system font
            // scales up but the explicit Width doesn't, so rendered
            // text can exceed the client rect. Without AutoEllipsis
            // WinForms doesn't truncate — it clips, and on certain
            // combos of font scaling vs. button height the text gets
            // clipped to nothing visible (the "buttons with no text"
            // bug). AutoEllipsis=true makes the worst case "Br…"
            // instead of blank.
            AutoEllipsis = true,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(85, 100, 120);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(65, 80, 105);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(35, 45, 60);
        return b;
    }

    private Panel BuildSetupBanner()
    {
        var p = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Visible = false,
            BackColor = Color.FromArgb(140, 100, 30),
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 4, 0, 8),
        };
        _setupBannerLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(255, 240, 220),
            MaximumSize = new Size(1200, 0),
            Font = new Font("Segoe UI", 12.5f),
        };
        p.Controls.Add(_setupBannerLabel);
        return p;
    }

    private DataGridView BuildModsGrid()
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
            // Ctrl/Shift-click ranges so the user can sweep up a
            // batch of mods to delete in one operation. The
            // checkbox-toggle column (Cells["On"]) still acts on a
            // single row at a time — bulk enable/disable already
            // has its own toolbar buttons.
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
                SelectionForeColor = Color.FromArgb(220, 225, 235),
            },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font = new Font("Consolas", 12f),
            },
            GridColor = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 36,
            RowTemplate = { Height = 32 },
        };

        // Columns. AutoGenerateColumns = false so we control the layout.
        // The grid is read-only by default, but the Enabled checkbox
        // column overrides that so the user can flip mods on/off.
        grid.ReadOnly = false;
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Enabled",
            HeaderText = "On",
            Width = 40,
            ReadOnly = false,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        // Single icon-style Update column. Five cell states:
        //   "⬆"  in orange    — outdated, click to update
        //   "✓"  in green     — current
        //   "↑"  in cool blue — local is newer than ModWorkshop
        //   "≠"  in lavender  — same release, version strings differ
        //                       in format (mod.txt vs MW listing)
        //   "—"  muted        — no MW link / unknown
        // Using a LinkColumn gets the hand cursor + visited-color
        // semantics for free; non-link states are styled per-cell in
        // PopulateModsGrid. Font is bumped so the single-char icons
        // read at a glance even though the column itself is narrow;
        // the version number lives in the tooltip rather than the
        // cell to keep the column slim.
        grid.Columns.Add(new DataGridViewLinkColumn
        {
            Name = "Update",
            HeaderText = "Updt",
            Width = 50,
            ReadOnly = true,
            TrackVisitedState = false,
            LinkBehavior = LinkBehavior.HoverUnderline,
            ActiveLinkColor = Color.FromArgb(255, 220, 120),
            LinkColor = Color.FromArgb(255, 200, 80),
            VisitedLinkColor = Color.FromArgb(255, 200, 80),
            DefaultCellStyle =
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Symbol", 16f, FontStyle.Bold),
            },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Pos",
            HeaderText = "#",
            Width = 36,
            ReadOnly = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Version",
            HeaderText = "Version",
            Width = 80,
            ReadOnly = true,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Priority",
            HeaderText = "LoadOrd",
            // 100px to fit the wider header text + bigger cell font
            // without truncating multi-digit values like -100 / 922.
            Width = 100,
            // Editable in-place — commits write the new value to
            // mod_config.cfg's [profile.<active>.priority] block.
            ReadOnly = false,
            DefaultCellStyle =
            {
                // Center alignment so the load-order numbers read as
                // their own visual column rather than rag-right
                // against the (auto-fill) Mod column boundary.
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                // Bigger numeric font so the load order is glanceable
                // at a distance — these are the values the user
                // tweaks to fix dependency_order conflicts.
                Font = new Font("Consolas", 14f, FontStyle.Bold),
            },
        });
        // Mod (Fill) goes LAST so every other column has a draggable
        // right edge. With Mod in the middle, Prio (last) had no
        // right boundary to drag, and dragging "Mod's right edge"
        // actually resized the next fixed column (Version) — the
        // Fill column auto-recomputes, so what looks like resizing
        // Mod is really resizing its neighbour. Putting Mod at the
        // end inverts that: Prio gets a right boundary, every fixed
        // column is drag-resizable, and Mod absorbs leftover width
        // without needing a drag handle of its own.
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = "Mod",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 100,
            ReadOnly = true,
            MinimumWidth = 220,
        });
        // (Update + version status now collapse into the single
        // "Update" link column at the start of the row.)
        // Mod ID intentionally not a column — it lives on the row's
        // ToolTipText (hover the Name cell) since it's rarely needed
        // visually and wastes space.

        // Lock the order. The grid renders mods in load-order priority;
        // letting the user click a column header to re-sort would
        // silently reshuffle the list out of load order, which is the
        // ONE invariant we never want broken. Toggling a mod also
        // shouldn't move it, which the priority-only sort in
        // PopulateModsGrid already takes care of.
        foreach (DataGridViewColumn col in grid.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;

        grid.CellContentClick += async (_, e) => await OnGridCellClickedAsync(e);

        // Right-click → context menu. Use the standard pattern of
        // attaching a ContextMenuStrip directly to the grid + capturing
        // the row in MouseDown, instead of CellContextMenuStripNeeded.
        // The latter doesn't fire reliably for every DataGridView cell
        // type — link cells and checkbox cells in particular swallow
        // right-clicks before the event reaches our handler.
        _modsContextMenu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(36, 42, 54),
            ForeColor = Color.FromArgb(220, 225, 235),
            ShowImageMargin = false,
            // ContextMenuStrip default Font is the system menu font
            // (~9pt) which looks tiny next to the bumped 12pt body
            // text. Match the body so right-click items read at the
            // same scale as the rest of the UI.
            Font = new Font("Segoe UI", 12f),
        };
        _modsContextMenu.Opening += (_, e) =>
        {
            // Skip the context menu for pack-header rows — they're
            // not mods and have no per-mod actions to offer.
            if (_modsContextRow < 0
                || _modsContextRow >= _modsGrid.Rows.Count
                || ModAtRow(_modsContextRow) == null)
            {
                e.Cancel = true;
                return;
            }
            PopulateModsContextMenu(_modsContextMenu, _modsContextRow);
        };
        grid.ContextMenuStrip = _modsContextMenu;
        // MouseDown on the grid surface (NOT CellMouseDown) so we
        // capture even when the click lands in a checkbox/link cell.
        grid.MouseDown += (_, e) =>
        {
            var hit = grid.HitTest(e.X, e.Y);
            // Left-click on a pack-header row → toggle collapse.
            // Header rows have row.Tag = "pack:<name>" set by
            // PopulateModsGrid; ModAtRow returns null for them.
            if (e.Button == MouseButtons.Left
                && hit.RowIndex >= 0
                && hit.RowIndex < grid.Rows.Count
                && ModAtRow(hit.RowIndex) == null)
            {
                var pack = PackHeaderAt(hit.RowIndex);
                if (!string.IsNullOrEmpty(pack))
                {
                    TogglePackCollapse(pack);
                    return;
                }
            }
            if (e.Button != MouseButtons.Right) return;
            _modsContextRow = hit.RowIndex;
            if (hit.RowIndex < 0 || hit.RowIndex >= grid.Rows.Count) return;
            // If the right-clicked row is ALREADY part of a multi-
            // selection (the user built up a batch with Ctrl/Shift-
            // click), keep the whole selection so the context menu's
            // bulk actions act on all of them. Right-clicking a row
            // OUTSIDE the existing selection collapses to that row
            // alone — matches Explorer's behaviour and avoids
            // accidentally deleting an unrelated batch.
            if (!grid.Rows[hit.RowIndex].Selected)
            {
                grid.ClearSelection();
                grid.Rows[hit.RowIndex].Selected = true;
            }
        };
        // Bonus discoverability — double-click a row's Mod cell to
        // open the mod page directly (skip the right-click menu).
        // No-op for mods without a ModWorkshop ID.
        grid.CellDoubleClick += (_, e) =>
        {
            var entry = ModAtRow(e.RowIndex);
            if (entry == null) return;
            if (grid.Columns[e.ColumnIndex].Name != "Name") return;
            if (entry.ModWorkshopId > 0) OpenModPage(entry);
        };

        // Drag-reorder priority: press a row + drag up or down,
        // release → the row's priority is set so its visible
        // position matches the drop. We arm the drag on left-
        // mouse-down outside the "On" checkbox column (so the
        // checkbox still toggles cleanly), then DoDragDrop once
        // the user moves past WinForms' standard drag deadzone.
        // The Move semantics make the cursor read "I'm moving
        // this row", which matches the intent.
        int? dragArmedRow = null;
        var dragStart = Point.Empty;
        grid.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var hit = grid.HitTest(e.X, e.Y);
            // Only arm drag on a real mod row — header rows are
            // collapse toggles, not drag handles.
            if (ModAtRow(hit.RowIndex) == null) return;
            if (hit.ColumnIndex >= 0
                && grid.Columns[hit.ColumnIndex].Name == "On") return;
            dragArmedRow = hit.RowIndex;
            dragStart = new Point(e.X, e.Y);
        };
        grid.MouseUp += (_, _) => dragArmedRow = null;
        grid.MouseMove += (_, e) =>
        {
            if (dragArmedRow == null) return;
            if ((e.Button & MouseButtons.Left) == 0)
            {
                dragArmedRow = null;
                return;
            }
            var dx = Math.Abs(e.X - dragStart.X);
            var dy = Math.Abs(e.Y - dragStart.Y);
            if (dx + dy < SystemInformation.DragSize.Width) return;
            var idx = dragArmedRow.Value;
            dragArmedRow = null;
            grid.DoDragDrop(new ModGridReorderPayload(idx),
                DragDropEffects.Move);
        };
        grid.AllowDrop = true;
        // DragEnter is fired before DragOver and matters for the OS
        // to register the cursor as a drop target at all. Without an
        // explicit DragEnter the grid sometimes refuses external-file
        // drops outright (the form's DragEnter doesn't fire when the
        // cursor is over a child control with AllowDrop = true).
        grid.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(typeof(ModGridReorderPayload)) == true)
            {
                e.Effect = DragDropEffects.Move;
                return;
            }
            // Fall through to the same file-drop accept logic the
            // form uses, so dropping a .vmz / .json directly on the
            // mods list works instead of bouncing off.
            TryAcceptFileDrag(e);
        };
        grid.DragOver += (_, e) =>
        {
            if (e.Data?.GetDataPresent(typeof(ModGridReorderPayload)) == true)
            {
                e.Effect = DragDropEffects.Move;
                return;
            }
            TryAcceptFileDrag(e);
        };
        grid.DragDrop += async (_, e) =>
        {
            // External file drop (.vmz install / .json import) —
            // route through the same handler the form uses so the
            // grid is a valid drop target instead of swallowing the
            // event. Checked FIRST so a foreign payload never gets
            // misinterpreted as a reorder.
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            {
                await HandleFileDropAsync(e);
                return;
            }
            if (e.Data?.GetDataPresent(typeof(ModGridReorderPayload)) != true) return;
            var payload = (ModGridReorderPayload)
                e.Data.GetData(typeof(ModGridReorderPayload))!;
            var pt = grid.PointToClient(new Point(e.X, e.Y));
            var hit = grid.HitTest(pt.X, pt.Y);
            // The payload + hit indexes are GRID-row indexes, but
            // DragReorderPriority works on _displayed indexes.
            // Convert: walk to the nearest mod-row above the
            // dropped position (header rows can't be drop
            // targets in their own right).
            var srcDisplayedIdx = GridRowToDisplayedIndex(payload.SourceRowIndex);
            var tgtDisplayedIdx = hit.RowIndex >= 0
                ? GridRowToDisplayedIndex(hit.RowIndex, nearestMod: true)
                : _displayed.Count - 1;
            if (srcDisplayedIdx < 0 || tgtDisplayedIdx < 0) return;
            DragReorderPriority(srcDisplayedIdx, tgtDisplayedIdx);
        };

        // Checkbox-cell plumbing: by default DataGridView only fires
        // CellValueChanged after the cell loses focus. CommitEdit on
        // dirty-state-change makes it fire immediately on click,
        // which is what users expect from a checkbox.
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty
                && grid.CurrentCell is DataGridViewCheckBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, e) => OnGridCellValueChanged(e);
        // Cross-grid highlight: selecting a mod in the Installed
        // mods grid auto-selects every conflict row that mod
        // appears in. Helps the user trace "which conflicts does
        // this mod participate in" without scanning the Mods
        // column by hand. Implementation handles its own
        // re-entrancy via SyncConflictsHighlight so a chain of
        // SelectionChanged events doesn't loop.
        grid.SelectionChanged += (_, _) => SyncConflictsHighlight();
        return grid;
    }

    /// <summary>Builds a row-specific context menu for the mods grid.
    /// Item enable-state and labels depend on whether the mod has a
    /// ModWorkshop ID — Open is greyed when there's nothing to open;
    /// Set toggles between "Set…" and "Change…" based on presence.</summary>
    /// <summary>Rebuilds the mods context menu's items for the given
    /// row index. Called from the menu's Opening event so the items
    /// always reflect the right-clicked row's current state (which
    /// could differ from a stale captured row if the grid was
    /// repopulated between the right-click and the menu open).</summary>
    private void PopulateModsContextMenu(ContextMenuStrip menu, int rowIndex)
    {
        menu.Items.Clear();
        // rowIndex is a GRID row index, not a _displayed index — the
        // two diverge whenever pack-header rows are present (each
        // header shifts the mod rows below it down by one). Validate
        // against the grid + ModAtRow, NOT _displayed.Count, or the
        // last mods in a packed/filtered list bail out here with an
        // empty menu (and an empty ContextMenuStrip silently refuses
        // to open). A filter shrinks _displayed, which made the
        // off-by-headers mismatch trip far more often.
        if (rowIndex < 0
            || rowIndex >= _modsGrid.Rows.Count
            || ModAtRow(rowIndex) == null) return;

        // Multi-select branch: when the user has 2+ rows selected
        // (Ctrl/Shift-click), the per-row actions (Open MW page,
        // Set MW id, etc.) don't make sense — we surface only the
        // batch operations. Right now that's a single "Delete N
        // mods…" item; future bulk actions plug in here too.
        var selectedEntries = GetSelectedModEntries();
        if (selectedEntries.Count > 1)
        {
            var label = $"Delete {selectedEntries.Count} selected mods…";
            var batchDeleteItem = new ToolStripMenuItem(label)
            {
                ToolTipText = "Send every selected mod's file (or directory) "
                    + "to the Recycle Bin and clear its mod_config.cfg + "
                    + "active-profile entries. Single confirmation up "
                    + "front — no per-mod prompts mid-batch.",
                ForeColor = Color.FromArgb(245, 130, 120),
            };
            batchDeleteItem.Click += (_, _) => DeleteMods(selectedEntries);
            menu.Items.Add(batchDeleteItem);

            menu.Items.Add(new ToolStripSeparator());

            // Bulk enable / disable on the selection — same as the
            // toolbar's "Enable all / Disable all" but scoped to
            // the rows the user picked. Mixed-state selections
            // show both items; uniform selections show only the
            // useful one.
            int enabledCount = selectedEntries.Count(e => e.IsEnabled);
            int disabledCount = selectedEntries.Count - enabledCount;
            if (disabledCount > 0)
            {
                var enableSelItem = new ToolStripMenuItem(
                    $"✓ Enable {disabledCount} selected");
                enableSelItem.Click += (_, _) =>
                    BulkToggleSelected(selectedEntries, enable: true);
                menu.Items.Add(enableSelItem);
            }
            if (enabledCount > 0)
            {
                var disableSelItem = new ToolStripMenuItem(
                    $"✗ Disable {enabledCount} selected");
                disableSelItem.Click += (_, _) =>
                    BulkToggleSelected(selectedEntries, enable: false);
                menu.Items.Add(disableSelItem);
            }

            menu.Items.Add(new ToolStripSeparator());

            // Bulk lock / unlock — useful when you want to pin a
            // batch of mods so Enable all / Disable all / profile
            // apply skip them all. Mixed-state selections (some
            // locked, some not) show both items so the user can
            // pick the action they want.
            int lockedCount = selectedEntries.Count(IsLocked);
            int unlockedCount = selectedEntries.Count - lockedCount;
            if (unlockedCount > 0)
            {
                var bulkLockItem = new ToolStripMenuItem(
                    $"🔒 Lock {unlockedCount} selected")
                {
                    ToolTipText = "Add these mods to the lock list — "
                        + "they'll survive profile switches and skip "
                        + "Enable all / Disable all bulk actions.",
                };
                bulkLockItem.Click += (_, _) => BulkSetLocked(selectedEntries, true);
                menu.Items.Add(bulkLockItem);
            }
            if (lockedCount > 0)
            {
                var bulkUnlockItem = new ToolStripMenuItem(
                    $"🔓 Unlock {lockedCount} selected")
                {
                    ToolTipText = "Remove the lock from these mods.",
                };
                bulkUnlockItem.Click += (_, _) => BulkSetLocked(selectedEntries, false);
                menu.Items.Add(bulkUnlockItem);
            }

            // Bulk priority — one number applied to every selected
            // mod. Saves the typing for "set this group to load
            // priority 100" workflows.
            var bulkPriorityItem = new ToolStripMenuItem(
                $"Set priority for {selectedEntries.Count} selected…")
            {
                ToolTipText = "Prompt for a load-order number; "
                    + "applies to every selected mod.",
            };
            bulkPriorityItem.Click += (_, _) => BulkSetPriority(selectedEntries);
            menu.Items.Add(bulkPriorityItem);

            return;
        }

        var entry = ModAtRow(rowIndex);
        if (entry == null) return; // pack-header row — no per-mod menu
        var hasMw = entry.ModWorkshopId > 0;

        var open = new ToolStripMenuItem("Open ModWorkshop page")
        {
            Enabled = hasMw,
            ToolTipText = hasMw
                ? $"Open https://modworkshop.net/mod/{entry.ModWorkshopId} in your browser."
                : "No ModWorkshop ID linked — use Set ModWorkshop ID first.",
        };
        open.Click += (_, _) => OpenModPage(entry);
        menu.Items.Add(open);

        var showDescItem = new ToolStripMenuItem("Show description…")
        {
            Enabled = hasMw,
            ToolTipText = hasMw
                ? "Fetches and displays the mod's description from "
                  + $"https://api.modworkshop.net/mods/{entry.ModWorkshopId}. "
                  + "Cached locally on first view; click Refresh in the "
                  + "dialog to re-fetch."
                : "No ModWorkshop ID linked — set one to enable description "
                  + "lookup.",
        };
        showDescItem.Click += (_, _) => ShowDescription(entry);
        menu.Items.Add(showDescItem);

        var showInFolderItem = new ToolStripMenuItem("Show mod in folder")
        {
            ToolTipText = "Open Windows Explorer with this mod's file "
                + "selected (or its folder, for directory mods).",
        };
        showInFolderItem.Click += (_, _) => ShowModInFolder(entry);
        menu.Items.Add(showInFolderItem);

        var setLabel = hasMw ? "Change ModWorkshop ID…" : "Set ModWorkshop ID…";
        var setItem = new ToolStripMenuItem(setLabel)
        {
            ToolTipText = "Edit mod.txt to add or update the [updates] modworkshop = N "
                + "field. Lets the manager track this mod for updates.",
        };
        setItem.Click += async (_, _) => await SetModWorkshopIdAsync(entry);
        menu.Items.Add(setItem);

        menu.Items.Add(new ToolStripSeparator());

        var depsCount = entry.RequiredDependencies.Count
            + entry.OptionalDependencies.Count;
        var depsLabel = depsCount > 0
            ? $"Show dependencies ({depsCount})…"
            : "Show dependencies…";
        var depsItem = new ToolStripMenuItem(depsLabel)
        {
            ToolTipText = depsCount > 0
                ? $"List the {depsCount} declared dependencies and their "
                  + "current state (enabled / disabled / not installed)."
                : "This mod doesn't declare any dependencies. The dialog "
                  + "will explain how mod authors can add them.",
        };
        depsItem.Click += (_, _) => ShowDependenciesDialog(entry);
        menu.Items.Add(depsItem);

        var editDepsItem = new ToolStripMenuItem("Edit required dependencies…")
        {
            ToolTipText = "Rewrite [dependencies] required = ... in mod.txt. "
                + "Comma-separated list of mod IDs; leave empty to clear.",
        };
        editDepsItem.Click += async (_, _) => await SetModDependenciesAsync(entry);
        menu.Items.Add(editDepsItem);

        var setPrioItem = new ToolStripMenuItem(
            $"Set priority… (current: {entry.Priority})")
        {
            ToolTipText = "Rewrite [mod] priority = N in mod.txt. Lower "
                + "numbers load earlier; default is 0; negatives are fine.",
        };
        setPrioItem.Click += async (_, _) => await SetModPriorityAsync(entry);
        menu.Items.Add(setPrioItem);

        menu.Items.Add(new ToolStripSeparator());
        var locked = IsLocked(entry);
        var lockItem = new ToolStripMenuItem(locked ? "Unlock mod" : "Lock mod")
        {
            Checked = locked,
            ToolTipText = locked
                ? "Currently locked — Enable all / Disable all skip this mod. "
                + "Click to unlock so bulk toggles include it again."
                : "Lock this mod so Enable all / Disable all skip it. The "
                + "checkbox still works for individual toggling.",
        };
        lockItem.Click += (_, _) => ToggleLock(entry);
        menu.Items.Add(lockItem);

        // 🧪 Testing flag — purely a visual marker so the user can
        // spot mods they're currently evaluating. Yellow row tint
        // applies on the next grid repopulate. State persists in
        // settings.json so it survives restarts.
        var testing = IsTesting(entry);
        var testItem = new ToolStripMenuItem(
            testing ? "🧪 Clear Testing flag" : "🧪 Mark as Testing Mod")
        {
            Checked = testing,
            ToolTipText = testing
                ? "Currently flagged for testing — the row is highlighted "
                + "yellow. Click to clear the flag and remove the highlight."
                : "Highlight this row yellow so you can spot the mod "
                + "you're shaking out at a glance. Doesn't change enable, "
                + "lock, or priority — purely a visual marker.",
        };
        testItem.Click += (_, _) => ToggleTesting(entry);
        menu.Items.Add(testItem);

        menu.Items.Add(new ToolStripSeparator());
        // Revert to an earlier on-disk snapshot of this mod. Backups
        // are created automatically by UpdateModAsync before each
        // overwrite, plus once more at the start of the revert
        // itself (so the revert is reversible). Disabled when no
        // backups exist on disk.
        var backups = string.IsNullOrEmpty(entry.ModId)
            ? new List<ModBackup.BackupEntry>()
            : ModBackup.ListBackups(ModsDir, entry.ModId);
        var revertLabel = backups.Count > 0
            ? $"Revert to previous version… ({backups.Count})"
            : "Revert to previous version…";
        var revertItem = new ToolStripMenuItem(revertLabel)
        {
            Enabled = backups.Count > 0,
            ToolTipText = backups.Count > 0
                ? $"Pick one of {backups.Count} on-disk snapshot(s) and "
                  + "roll back this mod's .vmz to that version. The "
                  + "current version is auto-backed-up first so the "
                  + "revert is itself reversible."
                : "No backups on disk yet. Backups are created "
                  + "automatically the next time the manager updates "
                  + "this mod from ModWorkshop.",
        };
        revertItem.Click += async (_, _) =>
            await RevertModFromBackupAsync(entry, backups);
        menu.Items.Add(revertItem);

        // Changelog viewer — reads CHANGELOG.md (or README.md /
        // CHANGES.md as fallbacks) from inside the .vmz and shows
        // it in a small dialog. Enabled only when the mod is an
        // archive AND has one of those files.
        var changelogItem = new ToolStripMenuItem("Show changelog…")
        {
            ToolTipText = "Reads CHANGELOG.md / README.md from the mod's "
                + ".vmz and displays it. No network call — pure local read.",
            Enabled = entry.IsArchive && HasInternalChangelog(entry),
        };
        changelogItem.Click += (_, _) => ShowChangelogDialog(entry);
        menu.Items.Add(changelogItem);

        // Per-mod note — free-form text persisted in settings.json,
        // surfaced as a tooltip on the Mod-name cell so the user
        // sees their own note when scanning the grid. Label shifts
        // between "Add" and "Edit" based on whether a note exists.
        var hasNote = !string.IsNullOrEmpty(entry.ModId)
            && _settings.ModNotes.ContainsKey(entry.ModId);
        var noteItem = new ToolStripMenuItem(
            hasNote ? "Edit note…" : "Add note…")
        {
            Enabled = !string.IsNullOrEmpty(entry.ModId),
            ToolTipText = hasNote
                ? "Edit the personal note attached to this mod. "
                  + "Stored in settings.json, shown as a tooltip on the row."
                : "Attach a personal note to this mod — appears as a "
                  + "tooltip on the Mod-name cell.",
        };
        noteItem.Click += (_, _) => EditModNote(entry);
        menu.Items.Add(noteItem);

        menu.Items.Add(new ToolStripSeparator());
        var deleteItem = new ToolStripMenuItem("Delete mod…")
        {
            ToolTipText = "Send the .vmz file (or directory mod's folder) "
                + "to the Recycle Bin and remove its entries from "
                + "mod_config.cfg. Recoverable — restore from the Recycle "
                + "Bin if you change your mind.",
            ForeColor = Color.FromArgb(245, 130, 120),
        };
        deleteItem.Click += (_, _) => DeleteMod(entry);
        menu.Items.Add(deleteItem);
    }

    // ── Changelog viewer ───────────────────────────────────────

    /// <summary>True when the archive has any of the conventional
    /// changelog filenames at the root. Checked at menu-build time
    /// so the "Show changelog…" item disables for mods that don't
    /// ship one — saves the user a useless click.</summary>
    private static bool HasInternalChangelog(ModEntry e)
    {
        if (!e.IsArchive) return false;
        foreach (var name in _changelogCandidates)
            if (e.Files.Any(f => string.Equals(
                    f, name, StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }

    /// <summary>File names checked in priority order — CHANGELOG.md
    /// first because it's the conventional name, README.md as a
    /// fallback for mods that put everything in one file, CHANGES.md
    /// for older conventions.</summary>
    private static readonly string[] _changelogCandidates =
    {
        "CHANGELOG.md", "CHANGELOG.txt",
        "README.md", "README.txt",
        "CHANGES.md", "CHANGES.txt",
    };

    private void ShowChangelogDialog(ModEntry entry)
    {
        if (!entry.IsArchive) return;
        string? body = null;
        string? source = null;
        foreach (var name in _changelogCandidates)
        {
            // Files in the archive may be case-different from our
            // candidate list (CHANGELOG.md vs changelog.md). Match
            // case-insensitive and use the actual stored name when
            // calling ReadFileText so the ZipArchive's exact-match
            // lookup hits.
            var match = entry.Files.FirstOrDefault(f =>
                string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(match)) continue;
            try
            {
                var text = entry.ReadFileText(match);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    body   = text;
                    source = match;
                    break;
                }
            }
            catch { /* try the next candidate */ }
        }
        if (string.IsNullOrEmpty(body))
        {
            Ui.ThemedMessageBox.Show(this,
                $"`{entry.DisplayName}` doesn't ship a CHANGELOG / README "
                + "file the manager can find. Try the mod's ModWorkshop "
                + "page for release notes.",
                "No changelog",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new Ui.ChangelogDialog(
            entry.DisplayName, source!, body!);
        dlg.ShowDialog(this);
    }

    // ── Mod notes ──────────────────────────────────────────────

    private void EditModNote(ModEntry entry)
    {
        if (string.IsNullOrEmpty(entry.ModId)) return;
        var current = _settings.ModNotes.TryGetValue(entry.ModId, out var n)
            ? n : "";
        var prompt =
            $"Personal note for `{entry.DisplayName}`. Stored locally in "
            + "settings.json; appears as a tooltip on the Mod column. "
            + "Leave blank and click OK to remove an existing note.";
        var input = Ui.TextInputDialog.PromptMultiline(
            this, "Edit mod note", prompt, current);
        if (input == null) return; // Cancel
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed))
            _settings.ModNotes.Remove(entry.ModId);
        else
            _settings.ModNotes[entry.ModId] = trimmed;
        try { _settings.Save(); } catch { /* best-effort */ }
        PopulateModsGrid();
    }

    /// <summary>Returns the ModEntry for every currently-selected
    /// row in the mods grid, in display order, deduped. Wraps
    /// _modsGrid.SelectedRows (which is an unstable enumeration —
    /// the underlying selection is a HashSet that doesn't track
    /// click order) into a list indexed by `_displayed`.</summary>
    private List<ModEntry> GetSelectedModEntries()
    {
        var rows = new List<(int gridIdx, ModEntry mod)>();
        foreach (DataGridViewRow r in _modsGrid.SelectedRows)
        {
            // Skip pack-header rows — they're selectable visually
            // but don't correspond to a mod. ModAtRow returns
            // null for them.
            if (r.Tag is ModEntry mod) rows.Add((r.Index, mod));
        }
        rows.Sort((a, b) => a.gridIdx.CompareTo(b.gridIdx));
        return rows.Select(x => x.mod).ToList();
    }

    /// <summary>Batch-delete N selected mods. ONE confirmation up
    /// front (with a preview list of names — capped so a 50-mod
    /// selection doesn't produce a wall of text), then iterates
    /// each and sends to Recycle Bin + clears cfg + drops from
    /// active profile. Per-mod failures are collected and shown
    /// in a single report at the end; one mod failing doesn't
    /// abort the rest of the batch.
    ///
    /// Library copies are KEPT (same default as single-mod
    /// delete) — bulk-prompting per mod for the library-cleanup
    /// stage would be obnoxious mid-batch. The user can delete
    /// library copies individually afterward if they want.</summary>
    /// <summary>Add or remove every entry's mod_id from the lock
    /// list and persist once. Skips entries with empty mod_id (lock
    /// state is keyed on it; nothing to add). Refreshes the grid
    /// so the 🔒 indicator and priority cells update.</summary>
    private void BulkSetLocked(List<ModEntry> entries, bool lockThem)
    {
        if (entries.Count == 0) return;
        var changed = 0;
        var skipped = 0;
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) { skipped++; continue; }
            var isLocked = _settings.LockedMods.Contains(e.ModId);
            if (lockThem)
            {
                if (isLocked) continue;
                _settings.LockedMods.Add(e.ModId);
                changed++;
            }
            else
            {
                if (!isLocked) continue;
                _settings.LockedMods.Remove(e.ModId);
                changed++;
            }
        }
        if (changed == 0)
        {
            _modsLabel.Text = lockThem
                ? "All selected mods were already locked."
                : "No selected mods were locked.";
            return;
        }
        try { _settings.Save(); } catch { /* best-effort */ }
        PopulateModsGrid();
        var skipNote = skipped > 0 ? $" ({skipped} skipped — no mod_id)" : "";
        _modsLabel.Text = lockThem
            ? $"Locked {changed} mod(s){skipNote}."
            : $"Unlocked {changed} mod(s){skipNote}.";
    }

    /// <summary>Prompt once for a priority value, apply it to
    /// every selected mod via mod_config.cfg. Same write path as
    /// the single-mod SetModPriorityAsync — we don't touch mod.txt
    /// because the in-game loader's cfg overrides it. One save at
    /// the end of the batch.</summary>
    private void BulkSetPriority(List<ModEntry> entries)
    {
        if (entries.Count == 0) return;
        // Use the first selected mod's current priority as the
        // default — most useful when the user already has one mod
        // at the priority they want others to inherit.
        var defaultStr = entries[0].Priority.ToString();
        var prompt =
            $"Enter the load-order priority to apply to {entries.Count} "
            + "selected mod(s).\n\n"
            + "Lower numbers load earlier; default is 0. Negative "
            + "values are fine (e.g. -100 to pin above everything else).\n\n"
            + "Writes to mod_config.cfg's "
            + $"[profile.{_modConfig.ActiveProfile}.priority] block — "
            + "the same key the in-game loader UI edits.";
        var input = Ui.TextInputDialog.Prompt(
            this, "Set priority for selected mods", prompt, defaultStr);
        if (input == null) return;
        if (!int.TryParse(input.Trim(), out var newPriority))
        {
            Ui.ThemedMessageBox.Show(this,
                $"Couldn't parse `{input}` as an integer. Priority "
                + "must be a whole number.",
                "Invalid input",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var changed = 0;
        var skipped = 0;
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) { skipped++; continue; }
            _modConfig.SetPriority(e.ModId, e.Version, newPriority);
            changed++;
        }
        if (changed == 0)
        {
            _modsLabel.Text = "No mods updated (selected entries have no mod_id).";
            return;
        }
        if (!SaveModConfigSafely()) return;

        // Mirror into the active profile so the profile.json
        // matches cfg — same pattern SetModPriorityAsync uses for
        // the single-mod case.
        if (_activeProfile != null)
        {
            var dirty = false;
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.ModId)) continue;
                var pm = _activeProfile.Mods.FirstOrDefault(m =>
                    string.Equals(m.ModId, e.ModId,
                        StringComparison.OrdinalIgnoreCase));
                if (pm == null) continue;
                if (pm.Priority != newPriority) { pm.Priority = newPriority; dirty = true; }
            }
            if (dirty)
            {
                _activeProfile.UpdatedAt = DateTime.UtcNow;
                try { _activeProfile.SaveMetadataOnly(); } catch { }
            }
        }

        Rescan();
        PopulateModsGrid();
        var skipNote = skipped > 0 ? $" ({skipped} skipped — no mod_id)" : "";
        _modsLabel.Text = $"Set priority = {newPriority} on {changed} mod(s){skipNote}.";
    }

    /// <summary>Drag-drop payload: the source row's index in the
    /// _displayed list at drag-start time. Sent through
    /// DoDragDrop's IDataObject so the DragDrop handler can read
    /// it back and compute the move.</summary>
    private sealed record ModGridReorderPayload(int SourceRowIndex);

    /// <summary>Move the row at `sourceIdx` to land at `targetIdx`
    /// in the currently-displayed sort order. Sets the moved
    /// row's priority to fit between its new neighbours. If the
    /// neighbours leave no integer gap, shifts downstream rows up
    /// by 1 until there's room — touches O(cluster size) rows
    /// rather than the whole grid, so manual priorities on
    /// unrelated mods (e.g. -100 on a high-load pin) survive.</summary>
    private void DragReorderPriority(int sourceIdx, int targetIdx)
    {
        if (sourceIdx < 0 || sourceIdx >= _displayed.Count) return;
        if (targetIdx < 0) targetIdx = _displayed.Count - 1;
        if (targetIdx >= _displayed.Count) targetIdx = _displayed.Count - 1;
        if (sourceIdx == targetIdx) return;

        var moved = _displayed[sourceIdx];
        if (string.IsNullOrEmpty(moved.ModId))
        {
            _modsLabel.Text = "Can't reorder — mod has no mod_id.";
            return;
        }

        // Build the post-move visible order.
        var newOrder = new List<ModEntry>(_displayed);
        newOrder.RemoveAt(sourceIdx);
        // When dragging DOWN (source < target), removing the
        // source shifts every index above it up by 1, so the
        // index we want to insert AT becomes targetIdx (which
        // was the row's pre-move position, now identifies the
        // row that used to sit ABOVE it).
        var insertAt = sourceIdx < targetIdx ? targetIdx : targetIdx;
        insertAt = Math.Clamp(insertAt, 0, newOrder.Count);
        newOrder.Insert(insertAt, moved);

        // Pick a priority that fits between the neighbours.
        int? aboveP = insertAt > 0 ? newOrder[insertAt - 1].Priority : (int?)null;
        int? belowP = insertAt + 1 < newOrder.Count
            ? newOrder[insertAt + 1].Priority
            : (int?)null;

        int newP;
        if (aboveP == null && belowP == null)        newP = 0;
        else if (aboveP == null)                     newP = belowP!.Value - 1;
        else if (belowP == null)                     newP = aboveP.Value + 1;
        else if (aboveP.Value + 1 < belowP.Value)    newP = aboveP.Value + 1;
        else                                          newP = aboveP.Value + 1;

        // Write the moved row's priority + downstream shift if
        // needed. wantP increments as we walk so any row whose
        // current priority is below wantP gets bumped to it.
        _modConfig.SetPriority(moved.ModId, moved.Version, newP);
        moved.Priority = newP;
        int wantP = newP + 1;
        for (int i = insertAt + 1; i < newOrder.Count; i++)
        {
            var e = newOrder[i];
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (e.Priority >= wantP) break;
            _modConfig.SetPriority(e.ModId, e.Version, wantP);
            e.Priority = wantP;
            wantP++;
        }
        if (!SaveModConfigSafely()) return;

        // Mirror into the active profile so cfg + profile.json
        // agree (same convention as SetModPriorityAsync).
        if (_activeProfile != null)
        {
            var dirty = false;
            foreach (var e in newOrder)
            {
                if (string.IsNullOrEmpty(e.ModId)) continue;
                var pm = _activeProfile.Mods.FirstOrDefault(m =>
                    string.Equals(m.ModId, e.ModId,
                        StringComparison.OrdinalIgnoreCase));
                if (pm != null && pm.Priority != e.Priority)
                {
                    pm.Priority = e.Priority;
                    dirty = true;
                }
            }
            if (dirty)
            {
                _activeProfile.UpdatedAt = DateTime.UtcNow;
                try { _activeProfile.SaveMetadataOnly(); } catch { }
            }
        }

        Rescan();
        PopulateModsGrid();
        _modsLabel.Text =
            $"Reordered `{moved.DisplayName}` to priority {newP}.";
    }

    private void DeleteMods(List<ModEntry> entries)
    {
        if (entries.Count == 0) return;
        if (entries.Count == 1) { DeleteMod(entries[0]); return; }

        var hasActive = _activeProfile != null;
        const int previewMax = 10;
        var preview = string.Join("\n",
            entries.Take(previewMax).Select(e => $"  • {e.DisplayName}"));
        if (entries.Count > previewMax)
            preview += $"\n  …and {entries.Count - previewMax} more";

        var lines = new List<string>
        {
            $"Send the following {entries.Count} mods to the Recycle Bin?",
            "",
            preview,
            "",
            hasActive
                ? $"For each: drops the profile '{_activeProfile!.Name}' "
                  + "entry, recycles the file, clears its mod_config.cfg "
                  + "entry, removes its lock (if any). Library copies are "
                  + "KEPT — delete those individually if you want them gone."
                : "For each: recycles the file and clears its "
                  + "mod_config.cfg entry. Recoverable from the Recycle Bin.",
        };
        var dr = Ui.ThemedMessageBox.Show(this,
            string.Join("\n", lines),
            $"Delete {entries.Count} mods — confirm",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (dr != DialogResult.Yes) return;

        var failures = new List<(string Name, string Reason)>();
        var deleted  = 0;
        var profileDirty = false;
        var cfgDirty     = false;
        foreach (var e in entries)
        {
            try
            {
                if (File.Exists(e.Path))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        e.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else if (Directory.Exists(e.Path))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        e.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                // else: stale row, treat as already-gone — no error.

                if (!string.IsNullOrEmpty(e.ModId))
                {
                    _modConfig.RemoveEntry(e.ModId, e.Version);
                    cfgDirty = true;
                    if (_settings.LockedMods.Remove(e.ModId))
                    {
                        try { _settings.Save(); } catch { }
                    }
                    if (hasActive)
                    {
                        var removed = _activeProfile!.Mods.RemoveAll(m =>
                            string.Equals(m.ModId, e.ModId,
                                StringComparison.OrdinalIgnoreCase));
                        if (removed > 0) profileDirty = true;
                    }
                }
                deleted++;
            }
            catch (Exception ex)
            {
                failures.Add((e.DisplayName, ex.Message));
            }
        }
        if (cfgDirty) SaveModConfigSafely();
        if (profileDirty && _activeProfile != null)
        {
            _activeProfile.UpdatedAt = DateTime.UtcNow;
            try { _activeProfile.SaveMetadataOnly(); } catch { }
        }

        _modsLabel.Text = failures.Count == 0
            ? $"Deleted {deleted} mod{(deleted == 1 ? "" : "s")} (sent to Recycle Bin)."
            : $"Deleted {deleted} mod{(deleted == 1 ? "" : "s")}; {failures.Count} failed.";

        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);

        if (failures.Count > 0)
        {
            var failPreview = string.Join("\n",
                failures.Take(previewMax)
                    .Select(f => $"  • {f.Name}: {f.Reason}"));
            if (failures.Count > previewMax)
                failPreview += $"\n  …and {failures.Count - previewMax} more";
            Ui.ThemedMessageBox.Show(this,
                $"{deleted} deleted; {failures.Count} failed.\n\n{failPreview}",
                "Batch delete report",
                MessageBoxButtons.OK,
                deleted > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
        }
    }

    /// <summary>Sends the mod's .vmz / directory to the Recycle Bin
    /// (NOT hard-deleted — recoverable) and clears its
    /// mod-id@version entries from mod_config.cfg's active profile.
    /// Strong confirmation dialog with No as default. Lock state is
    /// also cleared from settings since the mod no longer exists.</summary>
    private void DeleteMod(ModEntry e)
    {
        var fileName = Path.GetFileName(e.Path);
        var sizeNote = "";
        try
        {
            if (e.IsArchive && File.Exists(e.Path))
            {
                var bytes = new FileInfo(e.Path).Length;
                sizeNote = $" ({bytes / 1024} KiB)";
            }
        }
        catch { /* size is decorative */ }

        // ── Stage 1: remove from the active profile + live folder.
        // Pre-migration (no active profile) the prompt copy collapses
        // to a single-stage "send to Recycle Bin" the way it did
        // before profiles existed. Post-migration the user gets a
        // two-stage flow: profile-remove now, optional library-
        // remove second.
        var hasActive = _activeProfile != null;
        var stage1Lines = new List<string>();
        if (hasActive)
            stage1Lines.Add($"  • Drops the entry from profile '{_activeProfile!.Name}'");
        stage1Lines.Add($"  • Sends `{fileName}` to the Recycle Bin");
        stage1Lines.Add("  • Clears its mod_config.cfg entry");
        if (IsLocked(e)) stage1Lines.Add("  • Removes its lock");
        var stage1Tail = hasActive
            ? "\nThe Library copy is KEPT (asked separately after) so "
              + "other profiles can still reference this version."
            : "\nRecoverable from the Recycle Bin if you change your mind. "
              + "The .bak files (if any) are NOT deleted.";

        var title = hasActive
            ? $"Remove `{e.DisplayName}` from profile '{_activeProfile!.Name}'?"
            : $"Delete `{e.DisplayName}`{sizeNote}?";
        var dr = Ui.ThemedMessageBox.Show(this,
            title + "\n\n"
            + string.Join("\n", stage1Lines)
            + stage1Tail,
            hasActive ? "Remove from active profile" : "Delete mod — confirm",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (dr != DialogResult.Yes) return;

        try
        {
            // Microsoft.VisualBasic ships in the .NET Windows Forms
            // SDK; FileSystem.DeleteFile/Directory with the
            // SendToRecycleBin option is the simplest reliable way
            // to recycle-bin from .NET. Avoids P/Invoke.
            if (File.Exists(e.Path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    e.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else if (Directory.Exists(e.Path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    e.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                Ui.ThemedMessageBox.Show(this,
                    $"Couldn't find `{e.Path}` on disk — nothing to "
                    + "recycle. Refreshing the registry to clean up "
                    + "the stale grid entry.",
                    "File missing",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            ShowError("Couldn't delete mod", ex);
            return;
        }

        // Clear cfg + lock state for this mod so we don't leave
        // orphaned entries behind.
        if (!string.IsNullOrEmpty(e.ModId))
        {
            _modConfig.RemoveEntry(e.ModId, e.Version);
            SaveModConfigSafely();
            if (_settings.LockedMods.Remove(e.ModId))
            {
                try { _settings.Save(); }
                catch { /* lock-list cleanup is best-effort */ }
            }
        }

        // Drop from the active profile's Mods list + persist. The
        // library is intentionally untouched at this stage.
        if (hasActive && !string.IsNullOrEmpty(e.ModId))
        {
            var removed = _activeProfile!.Mods.RemoveAll(m =>
                string.Equals(m.ModId, e.ModId,
                    StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                _activeProfile.UpdatedAt = DateTime.UtcNow;
                try { _activeProfile.SaveMetadataOnly(); }
                catch { /* best-effort */ }
            }
        }

        // ── Stage 2: library cleanup (post-migration only, and only
        // when the library actually has a copy of this version).
        if (hasActive && !string.IsNullOrEmpty(e.ModId))
        {
            var libPath = Domain.ModLibrary.Find(ModsDir, e.ModId, e.Version);
            if (!string.IsNullOrEmpty(libPath))
            {
                var dr2 = Ui.ThemedMessageBox.Show(this,
                    $"Also delete `{e.DisplayName}` v{e.Version} from the Library?\n\n"
                    + "  • Removes the canonical .vmz copy from "
                    + $"{Path.GetFileName(Domain.ModLibrary.LibraryDir(ModsDir))}/ "
                    + "(Recycle Bin)\n"
                    + "  • Other profiles referencing this version "
                    + "won't be able to add the mod back without "
                    + "re-downloading from ModWorkshop.\n\n"
                    + "Say NO if you might want this version back later.",
                    "Delete from Library?",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (dr2 == DialogResult.Yes)
                {
                    try
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            libPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    catch (Exception ex)
                    {
                        ShowError("Couldn't delete library copy", ex);
                    }
                }
            }
        }

        _modsLabel.Text = hasActive
            ? $"Removed `{e.DisplayName}` from profile '{_activeProfile!.Name}'."
            : $"Deleted `{e.DisplayName}` (sent to Recycle Bin).";
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
    }

    /// <summary>Opens the read-only dependencies dialog for a mod.
    /// Builds an id-keyed view of the registry so the dialog can
    /// classify each dep as enabled / disabled / not installed
    /// without re-querying.</summary>
    private void ShowDependenciesDialog(ModEntry e)
    {
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _registry.Entries)
        {
            if (string.IsNullOrEmpty(entry.ModId)) continue;
            // First-wins on duplicate IDs — registry shouldn't have
            // any, but if a user manually copied a .vmz to two
            // places we don't want a TryAdd-style throw here.
            byId.TryAdd(entry.ModId, entry);
        }
        using var dlg = new DependenciesDialog(e, byId);
        dlg.ShowDialog(this);
    }

    /// <summary>Whether this mod is currently in the user's lock list.
    /// Locked mods are skipped by BulkToggle but can still be toggled
    /// individually via the checkbox.</summary>
    private bool IsLocked(ModEntry e)
        => !string.IsNullOrEmpty(e.ModId)
            && _settings.LockedMods.Contains(e.ModId);

    /// <summary>Adds or removes the mod from the lock list and
    /// persists. Triggers a grid repopulate so the 🔒 indicator on
    /// the Mod column updates.</summary>
    private void ToggleLock(ModEntry e)
    {
        if (string.IsNullOrEmpty(e.ModId))
        {
            // No mod_id in mod.txt — we'd have nothing to key the
            // lock state on. Surface the issue rather than silently
            // doing nothing.
            Ui.ThemedMessageBox.Show(this,
                $"`{Path.GetFileName(e.Path)}` has no mod_id in its "
                + "mod.txt — can't lock without an ID to key off.",
                "Can't lock",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_settings.LockedMods.Contains(e.ModId))
            _settings.LockedMods.Remove(e.ModId);
        else
            _settings.LockedMods.Add(e.ModId);
        _settings.Save();
        PopulateModsGrid();
    }

    /// <summary>True when the mod is currently flagged "Testing Mod"
    /// — purely a visual marker (yellow row tint). Like IsLocked,
    /// keyed on mod_id so a no-id mod can never be in the set.</summary>
    private bool IsTesting(ModEntry e)
        => !string.IsNullOrEmpty(e.ModId)
            && _settings.TestingMods.Contains(e.ModId);

    /// <summary>Adds or removes the mod from the testing list and
    /// persists. Triggers a grid repopulate so the yellow tint
    /// updates immediately.</summary>
    private void ToggleTesting(ModEntry e)
    {
        if (string.IsNullOrEmpty(e.ModId))
        {
            Ui.ThemedMessageBox.Show(this,
                $"`{Path.GetFileName(e.Path)}` has no mod_id in its "
                + "mod.txt — can't flag without an ID to key off.",
                "Can't flag as testing",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_settings.TestingMods.Contains(e.ModId))
            _settings.TestingMods.Remove(e.ModId);
        else
            _settings.TestingMods.Add(e.ModId);
        _settings.Save();
        PopulateModsGrid();
    }


    private DataGridView BuildConflictsGrid()
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
            // MultiSelect = true so the cross-grid highlight (selecting
            // a row in Installed mods auto-selects every conflict row
            // that mod appears in) can select more than one row.
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
                SelectionForeColor = Color.FromArgb(220, 225, 235),
            },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font = new Font("Consolas", 12f),
                WrapMode = DataGridViewTriState.True,
            },
            GridColor = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 36,
            RowTemplate = { Height = 32 },
            // WrapMode = True alone doesn't grow rows — without
            // AutoSizeRowsMode the cells try to wrap inside the
            // 32px row template and clip. DisplayedCells lets each
            // visible row grow to fit its tallest wrapped cell;
            // it's bounded so very long Mods lists don't blow up
            // the grid height.
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells,
        };
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Resolve",
            HeaderText = "",
            Width = 80,
            // FlatStyle.System → Windows light-theme button chrome,
            // which against our dark grid renders as bright white
            // tiles on every row (including the inert non-Resolve
            // ones). Flat + dark cell colors blend in; empty rows
            // get suppressed entirely via CellPainting below.
            FlatStyle = FlatStyle.Flat,
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(45, 55, 70),
                ForeColor = Color.FromArgb(225, 230, 240),
                SelectionBackColor = Color.FromArgb(65, 80, 105),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            },
        });
        // "What" — plain-English description of the conflict.
        // Two-line cells: title (what + key) on line 1, type +
        // resolution hint on line 2. Replaces the raw `Type` + `Key`
        // columns of the previous design. Fill absorbs leftover width
        // so the Mods/Wins columns can sit at fixed sensible widths.
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "What",
            HeaderText = "What",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 100,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Mods",
            HeaderText = "Mods",
            Width = 220,
        });
        // "Wins" — which mod currently wins this conflict given the
        // load order. Last column on the right (per user preference).
        // Computed in PopulateConflictsList by DetermineWinner;
        // semantics depend on conflict type. Cell text shows the
        // mod's DisplayName (falling back to mod_id when the
        // manifest doesn't declare a name).
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Wins",
            HeaderText = "Wins",
            Width = 260,
        });
        // Combined painter:
        //   - Banner rows (row.Tag is Severity) → suppress per-cell
        //     rendering; the banner visual is drawn in RowPrePaint.
        //   - Resolve column → suppress button chrome on empty cells
        //     and paint the tier-color gutter stripe at the left edge
        //     of every data row.
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var row = grid.Rows[e.RowIndex];
            if (row.Tag is Severity)
            {
                e.Handled = true;
                return;
            }
            if (grid.Columns[e.ColumnIndex].Name != "Resolve") return;
            var s = e.Value as string;
            if (string.IsNullOrEmpty(s))
                e.PaintBackground(e.ClipBounds, true);
            else
                e.Paint(e.ClipBounds, e.PaintParts);
            if (row.Tag is ConflictDetector.Conflict cf)
            {
                var (gutter, _, _, _) = TierStyle(SeverityOf(cf.Type), 0);
                using var b = new SolidBrush(gutter);
                e.Graphics!.FillRectangle(b,
                    e.CellBounds.X, e.CellBounds.Y, 4, e.CellBounds.Height);
            }
            e.Handled = true;
        };
        // Banner row visual: solid tier-tinted background, 4px gutter
        // stripe, severity label centered vertically. Drawn before
        // cell painting; CellPainting then no-ops for these rows.
        grid.RowPrePaint += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var row = grid.Rows[e.RowIndex];
            if (row.Tag is not Severity sev) return;
            var label = row.Cells["What"].Value as string ?? "";
            var (gutter, bannerBg, bannerFg, _) = TierStyle(sev, 0);
            using (var bg = new SolidBrush(bannerBg))
                e.Graphics.FillRectangle(bg, e.RowBounds);
            using (var gut = new SolidBrush(gutter))
                e.Graphics.FillRectangle(gut,
                    e.RowBounds.X, e.RowBounds.Y, 4, e.RowBounds.Height);
            using var font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, label, font,
                new Rectangle(e.RowBounds.X + 14, e.RowBounds.Y,
                              e.RowBounds.Width - 14, e.RowBounds.Height),
                bannerFg,
                TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix);
        };
        // Banner rows aren't selectable — bounce selection off them
        // so a stray click on a banner doesn't leave it highlighted
        // (and so keyboard navigation skips over them naturally).
        grid.SelectionChanged += (_, _) =>
        {
            foreach (DataGridViewRow r in grid.SelectedRows)
                if (r.Tag is Severity) r.Selected = false;
        };
        grid.CellContentClick += async (_, e) => await OnConflictsCellClickedAsync(e);
        return grid;
    }

    /// <summary>Reads `_settings.ColumnWidths` and applies any saved
    /// width to a non-Fill column whose name matches. Fill columns
    /// are skipped — they auto-compute and pinning them would defeat
    /// the leftover-space behaviour. The lower bound (20px) blocks a
    /// corrupted settings file from rendering an unfindable 1px
    /// column.</summary>
    private void ApplyColumnWidths(DataGridView grid, string prefix)
    {
        foreach (DataGridViewColumn col in grid.Columns)
        {
            if (col.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill) continue;
            var key = $"{prefix}.{col.Name}";
            if (_settings.ColumnWidths.TryGetValue(key, out var w) && w >= 20)
                col.Width = w;
        }
    }

    /// <summary>Snapshots the current widths of every non-Fill column
    /// into `_settings.ColumnWidths`. Caller is responsible for
    /// _settings.Save() afterwards (FormClosing batches this with the
    /// rest of the persisted UI state).</summary>
    private void SaveColumnWidths(DataGridView grid, string prefix)
    {
        foreach (DataGridViewColumn col in grid.Columns)
        {
            if (col.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill) continue;
            _settings.ColumnWidths[$"{prefix}.{col.Name}"] = col.Width;
        }
    }

    /// <summary>Wraps a content control in a panel with a bold header
    /// label at the top and an optional toolbar between the header
    /// and the content. WinForms docks children in reverse order, so
    /// add: body (Fill) → toolbar (Top) → header (Top), and the
    /// header lands at the very top with the toolbar just below it.</summary>
    private static Panel WrapInPanel(string headerText, Control body, Control? toolbar = null)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var hdr = new Label
        {
            Text = headerText,
            Dock = DockStyle.Top,
            Height = 32,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
        };
        body.Dock = DockStyle.Fill;
        p.Controls.Add(body);
        if (toolbar != null)
        {
            toolbar.Dock = DockStyle.Top;
            p.Controls.Add(toolbar);
        }
        p.Controls.Add(hdr);
        // Military crate-corner brackets around the body. Drawn on
        // the wrapper Panel's surface so they sit just outside the
        // grid's edges. Repainted on resize automatically because
        // Panel.Paint fires after Dock-driven re-layouts.
        p.Paint += (s, e) =>
        {
            var bodyBounds = body.Bounds;
            // Stretch a few px outward so the brackets frame the
            // grid rather than overlap the grid's own border.
            var rect = new Rectangle(
                bodyBounds.Left - 4, bodyBounds.Top - 4,
                bodyBounds.Width + 8, bodyBounds.Height + 8);
            DrawCrateCorners(e.Graphics, rect,
                Color.FromArgb(180, 200, 50, 60),
                size: 22, thickness: 3);
        };
        return p;
    }

    /// <summary>Draws four "L"-shaped corner marks just inside the
    /// rectangle, like the rope-handle reinforcements on a military
    /// crate. Cheap visual frame without a full border.</summary>
    private static void DrawCrateCorners(
        Graphics g, Rectangle r, Color color, int size, float thickness)
    {
        using var pen = new Pen(color, thickness);
        var prev = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // Top-left
        g.DrawLine(pen, r.Left, r.Top, r.Left + size, r.Top);
        g.DrawLine(pen, r.Left, r.Top, r.Left, r.Top + size);
        // Top-right
        g.DrawLine(pen, r.Right, r.Top, r.Right - size, r.Top);
        g.DrawLine(pen, r.Right, r.Top, r.Right, r.Top + size);
        // Bottom-left
        g.DrawLine(pen, r.Left, r.Bottom, r.Left + size, r.Bottom);
        g.DrawLine(pen, r.Left, r.Bottom, r.Left, r.Bottom - size);
        // Bottom-right
        g.DrawLine(pen, r.Right, r.Bottom, r.Right - size, r.Bottom);
        g.DrawLine(pen, r.Right, r.Bottom, r.Right, r.Bottom - size);
        g.SmoothingMode = prev;
    }

    private Control BuildModsToolbar()
    {
        var bar = new TableLayoutPanel
        {
            // 48px = bumped-button Height (40) + top/bottom padding
            // (4 + 4). Used as the canonical toolbar height; the
            // conflicts grid gets a same-height empty spacer so its
            // column headers vertically line up with the mods grid's.
            Height = 48,
            Dock = DockStyle.Top,
            // 10 columns: [Filter label][textbox(fill)][× clear][Install]
            // [Import list][Enable all][Disable all][Refresh][Dependencies][Conflicts toggle].
            // The × button used to live inside a nested TableLayoutPanel
            // alongside the textbox — that nest's Dock=Fill + sub-column
            // sizing kept rendering the × invisible. It's promoted to a
            // first-class toolbar column here so it renders by the same
            // rules every other button does.
            ColumnCount = 10,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 4),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // Filter:
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));       // textbox (fills)
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,  32f));      // × clear
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // Install
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // Import list
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // Enable all
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // Disable all
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // Refresh
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // Dependencies
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // Conflicts toggle

        // Filter label. Anchor with no Top/Bottom = vertically centred
        // in the cell, so it sits on the same horizontal midline as the
        // textbox and the buttons regardless of their individual
        // natural heights.
        bar.Controls.Add(new Label
        {
            Text      = "Filter:",
            AutoSize  = true,
            Anchor    = AnchorStyles.Left,
            ForeColor = Color.FromArgb(220, 225, 235),
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", 12f, FontStyle.Bold),
            Margin    = new Padding(4, 0, 6, 0),
        }, 0, 0);

        // Filter textbox — natural height (TextBox can't stretch
        // vertically), centred in its cell via Anchor=Left|Right
        // (horizontal fill, no Top/Bottom = vertical centre).
        _filterBox = new TextBox
        {
            Anchor      = AnchorStyles.Left | AnchorStyles.Right,
            BackColor   = Color.FromArgb(30, 36, 48),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "filter by name, id, or filename",
            Margin      = new Padding(0),
        };
        bar.Controls.Add(_filterBox, 1, 0);

        // × clear chip. Explicit Size matching the textbox height
        // (~24px for our Segoe UI 12 + FixedSingle), centred in its
        // cell via the same anchorless-vertical trick. AutoSize=false
        // so the explicit Height isn't overwritten on layout.
        var fgActive = Color.FromArgb(235, 240, 250);
        var fgDim    = Color.FromArgb(120, 132, 156);
        var clearFilter = new Label
        {
            Text        = "×",
            Font        = new Font("Segoe UI", 12f, FontStyle.Bold),
            TextAlign   = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor   = Color.FromArgb(60, 72, 92),
            ForeColor   = fgDim,
            Anchor      = AnchorStyles.Left | AnchorStyles.Right,
            Margin      = new Padding(0, 0, 8, 0),
            Cursor      = Cursors.Hand,
            AutoSize    = false,
            Height      = _filterBox.PreferredHeight,
        };
        clearFilter.Click += (_, _) =>
        {
            _filterBox.Clear();
            _filterBox.Focus();
        };
        clearFilter.MouseEnter += (_, _) =>
            clearFilter.BackColor = Color.FromArgb(85, 100, 130);
        clearFilter.MouseLeave += (_, _) =>
            clearFilter.BackColor = Color.FromArgb(60, 72, 92);
        bar.Controls.Add(clearFilter, 2, 0);

        _filterBox.TextChanged += (_, _) =>
        {
            clearFilter.ForeColor = _filterBox.Text.Length > 0
                ? fgActive
                : fgDim;
            PopulateModsGrid();
        };
        // Esc inside the filter clears it (without bubbling — main form
        // doesn't trap Esc any more, but suppressing the key also
        // silences the system "ding").
        _filterBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            _filterBox.Clear();
            e.SuppressKeyPress = true;
            e.Handled = true;
        };

        var install = ThemedButton("Install mod…");
        install.Margin = new Padding(0, 2, 4, 2);
        install.Click += async (_, _) => await InstallModFromFilePickerAsync();
        bar.Controls.Add(install, 3, 0);

        // Batch-add from a JSON mod-list file (a "I want these mods
        // plus their deps" manifest the user dropped in). Adds to
        // the CURRENT active profile — never creates / replaces a
        // profile, never wipes anything. Each entry's
        // mod_workshop_id drives the download. Distinct enough from
        // Install mod… that it gets its own button rather than a
        // dropdown — the user shouldn't have to dig through a menu
        // to grab a 50-mod bundle a friend shared as JSON.
        var importList = ThemedButton("Import list…");
        importList.Margin = new Padding(0, 2, 4, 2);
        importList.Click += async (_, _) => await ImportModListFromFilePickerAsync();
        // Right-click on Import list… → save a hand-editable JSON
        // template. Keeps the template feature discoverable without
        // adding another toolbar column; the tooltip below tells
        // the user about both actions.
        var importMenu = new ContextMenuStrip
        {
            Font = new Font("Segoe UI", 11f),
        };
        var saveTemplateItem = new ToolStripMenuItem("📝 Save sample template…")
        {
            ToolTipText = "Write a starter JSON file you can edit by hand "
                        + "to list the mods you want bundled.",
        };
        saveTemplateItem.Click += (_, _) => SaveSampleModListTemplate();
        importMenu.Items.Add(saveTemplateItem);
        importList.ContextMenuStrip = importMenu;
        var importTip = new ToolTip();
        importTip.SetToolTip(importList,
            "Import a mod-pack JSON into the active profile.\n"
            + "Right-click for a starter template.");
        bar.Controls.Add(importList, 4, 0);

        var enableAll = ThemedButton("Enable all");
        enableAll.Margin = new Padding(0, 2, 4, 2);
        enableAll.Click += (_, _) => BulkToggle(enable: true);
        bar.Controls.Add(enableAll, 5, 0);

        var disableAll = ThemedButton("Disable all");
        disableAll.Margin = new Padding(0, 2, 4, 2);
        disableAll.Click += (_, _) => BulkToggle(enable: false);
        bar.Controls.Add(disableAll, 6, 0);

        var refresh = ThemedButton("Refresh");
        refresh.Margin = new Padding(0, 2, 4, 2);
        refresh.Click += async (_, _) => await RefreshAllAsync();
        bar.Controls.Add(refresh, 7, 0);

        // Dependencies rollup for the active profile. Opens a read-
        // only dialog listing every mod in the profile that declares
        // a [dependencies] section, with each declared dep paired
        // with its live state (✓ enabled / ⚠ disabled / ✗ missing).
        // Per-mod dependency dialog still lives in the row's right-
        // click menu — this button is the profile-wide rollup.
        var deps = ThemedButton("Dependencies");
        deps.Margin = new Padding(0, 2, 4, 2);
        deps.Click += (_, _) => ShowProfileDependenciesDialog();
        bar.Controls.Add(deps, 8, 0);

        // Sidebar toggle. Label flips between "Hide conflicts" and
        // "Show conflicts" via SyncConflictsToggleLabel so the text
        // always reflects the current Panel2Collapsed state. Saved to
        // Settings.ConflictsVisible so the choice persists across
        // sessions.
        _conflictsToggle = ThemedButton("Hide conflicts");
        _conflictsToggle.Margin = new Padding(0, 2, 0, 2);
        _conflictsToggle.Click += (_, _) =>
        {
            var nowVisible = _split.Panel2Collapsed; // about to flip
            _split.Panel2Collapsed = !nowVisible;
            _settings.ConflictsVisible = nowVisible;
            _settings.Save();
            // Re-snap the splitter to the saved ratio when re-showing —
            // SplitContainer keeps SplitterDistance across a collapse,
            // but the value may be stale if the panel was hidden at
            // startup (ApplySplit no-ops while collapsed).
            if (nowVisible) _applySplit();
            SyncConflictsToggleLabel();
        };
        bar.Controls.Add(_conflictsToggle, 9, 0);

        return bar;
    }

    /// <summary>True when `path` is a file extension we can act on
    /// from a drag-drop: .vmz (install) or .json (import list).
    /// Used by the form-level DragEnter so the cursor only lights
    /// up "drop ok" for payloads we'll actually handle.</summary>
    private static bool IsAcceptedDropPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.EndsWith(".vmz",  StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Picks (or accepts a pre-supplied path for) a .json
    /// mod-list file and hands it off to ImportModListDialog. The
    /// `preselectedPath` parameter lets the drag-drop handler skip
    /// the file picker — drop a .json on the form and it routes
    /// straight here. Bails early with a friendly message when
    /// there's no active profile (the dialog needs a target to
    /// merge into; we don't want to silently create one).</summary>
    private async Task ImportModListFromFilePickerAsync(string preselectedPath = "")
    {
        if (_activeProfile == null)
        {
            Ui.ThemedMessageBox.Show(this,
                "Import adds mods to the ACTIVE profile, but no profile "
                + "is active yet.\n\nOpen Profiles… to create or activate "
                + "a profile first, then try Import list… again.",
                "No active profile",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string filePath;
        if (!string.IsNullOrEmpty(preselectedPath) && File.Exists(preselectedPath))
        {
            filePath = preselectedPath;
        }
        else
        {
            using var dlg = new OpenFileDialog
            {
                Title       = "Import mod list (JSON)",
                Filter      = "Mod list (*.json)|*.json|All files (*.*)|*.*",
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
            filePath = dlg.FileName;
        }

        Domain.ModListImport import;
        try
        {
            import = Domain.ModListImport.LoadFromFile(filePath);
        }
        catch (Exception ex)
        {
            Ui.ThemedMessageBox.Show(this,
                $"Couldn't parse `{Path.GetFileName(filePath)}`:\n\n{ex.Message}\n\n"
                + "Expected shape:\n"
                + "{\n"
                + "  \"mods\": [\n"
                + "    { \"mod_id\": \"x\", \"mod_workshop_id\": 12345 }\n"
                + "  ]\n"
                + "}",
                "Import failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (import.Mods.Count == 0)
        {
            Ui.ThemedMessageBox.Show(this,
                "The JSON parsed cleanly but contained no entries under "
                + "`mods`. Nothing to import.",
                "Empty mod list",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool applied;
        List<string> importedIds;
        // Pass the JSON's directory as the companion dir so the
        // dialog scans alongside .vmz files for fallback MW ids
        // when the JSON itself omits them. Covers the common
        // "drop the JSON next to the .vmz files" flow.
        var companionDir = Path.GetDirectoryName(filePath) ?? "";
        using (var importDlg = new Ui.ImportModListDialog(
            import, _activeProfile, _registry, _mw, ModsDir, companionDir))
        {
            importDlg.ShowDialog(this);
            applied = importDlg.Applied;
            importedIds = importDlg.InstalledModIds.ToList();
        }

        // Always rescan + refresh — even when Applied is false the
        // user might have partially-imported via Cancel, and we want
        // the grid to reflect whatever DID land on disk.
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
        if (applied) await CheckUpdatesAsync();

        // Scan declared deps for every imported mod and prompt to
        // resolve any that aren't yet installed.
        if (importedIds.Count > 0)
            await CheckAndPromptMissingDepsAsync(importedIds);
    }

    /// <summary>Writes a hand-editable mod-pack JSON template to a
    /// path the user picks. The file is structured as a working
    /// example with 4 sample mods, comments explaining each field,
    /// and `mod_workshop_id` placeholders the user replaces with
    /// real numbers — exactly what you'd hand to a friend so they
    /// can drop the file on the manager and merge your pack into
    /// their active profile.
    ///
    /// Triggered from the Import list… button's right-click menu;
    /// see the toolbar wiring for that hookup.</summary>
    private void SaveSampleModListTemplate()
    {
        using var dlg = new SaveFileDialog
        {
            Title    = "Save mod pack template",
            Filter   = "Mod pack (*.json)|*.json",
            FileName = "my-mod-pack.json",
            OverwritePrompt = true,
        };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var docs = Path.Combine(home, "Documents");
            if (Directory.Exists(docs)) dlg.InitialDirectory = docs;
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // Plain text — System.Text.Json's serializer would strip
        // // comments, so the file is built as a string literal.
        // Indented with two spaces, JSON-conformant after the
        // /* … */ comment block at the top is left in (System.Text.
        // Json's reader ignores them by virtue of our
        // ReadCommentHandling = Skip option in ModListImport).
        var template =
@"{
  /* Mod pack manifest — drop this file on the mod manager (or use
     Import list…) to merge every listed mod into your ACTIVE profile.

     • mod_workshop_id (numeric, from the modworkshop.net/mod/<n> URL)
       drives the download. REQUIRED for any mod the recipient
       doesn't already have installed.
     • mod_id is the manifest mod_id (slug, from mod.txt). If
       you don't know it, leave it as a hint and we'll reconcile
       to the real id after the download.
     • display_name is just for the import-plan dialog header.
     • version, is_enabled, priority are optional.
     • The `dependencies` array nests the same shape; the
       importer flattens it so you can structure related mods
       hierarchically OR list them all top-level — either works. */
  ""name"": ""My mod pack"",
  ""description"": ""Description shown at the top of the import dialog."",
  ""mods"": [
    {
      ""mod_id"": ""my-first-mod"",
      ""display_name"": ""My First Mod"",
      ""mod_workshop_id"": 00000,
      ""version"": ""1.0.0"",
      ""is_enabled"": true,
      ""priority"": 0
    },
    {
      ""mod_id"": ""my-second-mod"",
      ""display_name"": ""My Second Mod"",
      ""mod_workshop_id"": 00000
    },
    {
      ""mod_id"": ""my-third-mod"",
      ""display_name"": ""My Third Mod"",
      ""mod_workshop_id"": 00000
    },
    {
      ""mod_id"": ""my-fourth-mod"",
      ""display_name"": ""My Fourth Mod"",
      ""mod_workshop_id"": 00000
    }
  ]
}
";
        try
        {
            File.WriteAllText(dlg.FileName, template);
            Ui.ThemedMessageBox.Show(this,
                $"Saved a starter template to:\n\n{dlg.FileName}\n\n"
                + "Edit the file in any text editor — replace the "
                + "00000 placeholders with real mod_workshop_id "
                + "numbers (the digits from each mod's ModWorkshop "
                + "URL: https://modworkshop.net/mod/<NUMBER>).\n\n"
                + "Then either drop the .json on this window or "
                + "use Import list… to bring those mods into your "
                + "active profile.",
                "Template saved",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Ui.ThemedMessageBox.Show(this,
                $"Couldn't write the template:\n\n{ex.Message}",
                "Save failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Opens the Mod Packager — passes the live registry
    /// so the dependency picker can offer ticks against currently-
    /// installed mods. Modal because the source/output paths are
    /// per-session intent; cancellation by closing is fine. Doesn't
    /// trigger a rescan on close — packing writes a NEW .vmz at the
    /// user's chosen output path, which by convention lives outside
    /// the live mods folder.</summary>
    private void OpenModPackagerDialog()
    {
        // Pass the active profile too — its ProfileMod entries
        // carry mod_workshop_id values that the live mod.txt may
        // be missing, so the dep grid + Export JSON can surface
        // MW ids for mods that were originally added via JSON
        // import / install-list flows.
        using var dlg = new Ui.ModPackagerDialog(_registry, _activeProfile);
        dlg.ShowDialog(this);
    }

    /// <summary>Opens the profile-wide dependency rollup. Passes the
    /// active ModProfile (may be null — the dialog handles that with
    /// a friendly empty state), the registry for live state, and the
    /// lock list so locked-but-not-in-profile mods are surfaced too
    /// (same bypass rule the main grid uses).
    ///
    /// Side-effect: while the dialog is open, the main grid is
    /// filtered to only the mods that participate in dependency
    /// relationships (dependents + their declared deps). Restored
    /// on close, including a failure path via try/finally so an
    /// exception inside the dialog can't leave the grid stuck in
    /// the narrower view.</summary>
    private void ShowProfileDependenciesDialog()
    {
        _dependencyFilter = BuildDependencyFilterSet();
        PopulateModsGrid();
        try
        {
            using var dlg = new Ui.ProfileDependenciesDialog(
                _activeProfile, _registry, _settings.LockedMods);
            dlg.ShowDialog(this);
        }
        finally
        {
            _dependencyFilter = null;
            PopulateModsGrid();
        }
    }

    /// <summary>Builds the mod-id set the grid narrows to while the
    /// Dependencies dialog is open: every mod that declares a
    /// `[dependencies]` section, plus every mod those entries
    /// reference. Profile + lock scopes are layered on top by
    /// PopulateModsGrid — this set is purely the "participates in a
    /// dep relationship" axis.</summary>
    private HashSet<string> BuildDependencyFilterSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profileIds = _activeProfile != null
            ? new HashSet<string>(
                _activeProfile.Mods.Select(m => m.ModId),
                StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var entry in _registry.Entries)
        {
            if (string.IsNullOrEmpty(entry.ModId)) continue;
            // Only consider mods the dialog would also consider —
            // those in the active profile (or all when there's no
            // active profile, matching the dialog's empty-profile
            // path which is "do nothing").
            if (profileIds != null && !profileIds.Contains(entry.ModId)) continue;
            var required = entry.RequiredDependencies;
            var optional = entry.OptionalDependencies;
            if (required.Count == 0 && optional.Count == 0) continue;
            // The dependent itself + each declared dep id. Deps may
            // or may not be installed — if they're not, they simply
            // don't appear in the grid (the dialog already surfaces
            // the missing-dep status, which is the right place for
            // that signal).
            set.Add(entry.ModId);
            foreach (var d in required) set.Add(d);
            foreach (var d in optional) set.Add(d);
        }
        return set;
    }

    /// <summary>Keeps the toolbar button label in sync with
    /// Panel2Collapsed. Called both at startup (after the initial
    /// collapse state is applied) and on every toggle click. Safe to
    /// call before the button is constructed — early returns if so,
    /// since InitializeWindow's first call lands before
    /// BuildModsToolbar runs.</summary>
    private void SyncConflictsToggleLabel()
    {
        if (_conflictsToggle is null) return;
        _conflictsToggle.Text = _split.Panel2Collapsed
            ? "Show conflicts"
            : "Hide conflicts";
    }

    private async Task RunStartupAsync()
    {
#if AI_RESOLVER
        // First-run convenience: try to auto-detect the Decomp/ folder
        // if the user hasn't set one yet. We only check known locations
        // (next to the game install, repo Desktop layout, Documents)
        // and only when the candidate looks like a real Decomp (Scripts/
        // Loader.gd + Interface.gd present).
        // Integrated edition skips this — the path is only used by the
        // AI resolver to give Claude the original game script as context.
        if (string.IsNullOrEmpty(_settings.GameSourcePath))
        {
            var detected = AutodetectDecomp();
            if (!string.IsNullOrEmpty(detected))
            {
                _settings.GameSourcePath = detected;
                _settings.Save();
                _resolver.GameSourcePath = detected;
            }
        }

        _claude.Detect();
        UpdateClaudeStatus();
#endif
        RefreshSetupBanner();

        _modsLabel.Text = $"Mods: scanning {ModsDir} ...";
        if (!Rescan())
        {
            _modsLabel.Text = $"Mods: cannot read {ModsDir}. " +
                "Is the game installed at the default Steam path?";
            return;
        }
        // Resolve the active profile (if any) and refresh the
        // title-row selector BEFORE populating the mods grid, so
        // the grid's profile filter sees the right value on its
        // first paint instead of flashing the full mod list and
        // then filtering down.
        ReloadProfiles();
        // Now that _activeProfile is resolved, run orphan adoption
        // explicitly. The first Rescan() above ran with no active
        // profile (it hadn't been loaded yet) so AutoAdopt
        // short-circuited; this catch-up pass picks up any .vmz
        // the user dropped manually in Explorer between sessions.
        AutoAdoptOrphansIntoActiveProfile();
        // Arm the live file-system watcher so future Explorer-side
        // drops + deletes auto-refresh the grid without the user
        // clicking Refresh. Wired here (after profile resolution)
        // so the first auto-triggered rescan has the right
        // active-profile context.
        InitModsFolderWatcher();
        UpdateModsStatus();
        UpdateCheckpointStatus();
        PopulateModsGrid();

        _conflictsLabel.Text = "Conflicts: detecting (deep analysis) ...";
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);

        // Phase-6 migration prompt — fires when:
        //   • No active profile name yet (new model hasn't been
        //     adopted on this install)
        //   • User hasn't already declined the prompt
        //   • There ARE installed archive mods worth migrating
        // Runs on the UI thread after the first scan paints, so
        // the user sees the populated grid behind the modal and
        // can correlate "found N mods" with their own setup.
        if (string.IsNullOrEmpty(_settings.ActiveProfileName)
            && !_settings.MigrationDeclined
            && _registry.Entries.Any(e => e.IsArchive))
        {
            OfferProfileMigration();
        }

        await CheckUpdatesAsync();
        await CheckMmlVersionAsync();
        // Force-fresh on launch — the cached value is fine for mid-
        // session refreshes (the 24h TTL keeps the toolbar from
        // hammering the API), but on a cold start the user expects
        // the status row to reflect what's actually published, not
        // what was on the server yesterday. Especially important
        // when a release ships and the user wants to know there's
        // an update available immediately.
        await CheckManagerUpdateAsync(forceFresh: true);

        // First-run welcome — runs AFTER scan + update checks so the
        // dialog appears on top of a fully-populated form (the user
        // gets context for what's about to be backed up), and the
        // backup itself reflects the disk state we just observed. We
        // Save() unconditionally afterwards so the next launch sees us
        // as non-first-run regardless of which button was picked
        // (including X-close → DialogResult.Cancel).
        if (_isFirstRun)
        {
            await OfferFirstRunBackupAsync();
            try { _settings.Save(); } catch { /* best-effort */ }
        }
    }

    /// <summary>Shows the welcome dialog and, if the user accepts,
    /// writes a .zip of the mods folder + mod_config.cfg to
    /// Documents\Road to Vostok Mod Manager Backups\. Surfaces both
    /// success (path + Open folder button) and any IO errors via
    /// themed message boxes. Best-effort: a missing mods folder or
    /// missing mod_config.cfg is logged in the success message rather
    /// than treated as a hard failure.</summary>
    private async Task OfferFirstRunBackupAsync()
    {
        var mcmDir = Path.Combine(
            Path.GetDirectoryName(ModConfig.DefaultPath) ?? "",
            "MCM");
        using var dlg = new Ui.WelcomeDialog(
            ModsDir, ModConfig.DefaultPath, mcmDir);
        if (dlg.ShowDialog(this) != DialogResult.Yes) return;

        var backupsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Road to Vostok Mod Manager Backups");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(backupsDir, $"backup-{stamp}.zip");

        var summary = new System.Text.StringBuilder();
        try
        {
            Directory.CreateDirectory(backupsDir);
            await Task.Run(() => WriteFirstRunBackup(zipPath, summary));
            Ui.ThemedMessageBox.Show(this,
                $"Backup written to:\n\n{zipPath}\n\n{summary}",
                "Backup complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            // Open the folder so the user can see / copy / move it.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = backupsDir,
                    UseShellExecute = true,
                });
            }
            catch { /* user can navigate manually */ }
        }
        catch (Exception ex)
        {
            ShowError("Couldn't write backup", ex);
        }
    }

    /// <summary>Writes the actual zip. Adds each .vmz (and any non-
    /// hidden file) under `mods/` plus the bare mod_config.cfg at
    /// the root. Forward slashes in entry paths so the archive opens
    /// cleanly on every extractor (Windows Explorer, 7-Zip, Linux
    /// unzip). Summary lines are appended for the success dialog so
    /// the user can verify what got captured.</summary>
    private void WriteFirstRunBackup(string zipPath, System.Text.StringBuilder summary)
    {
        using var fs = File.Create(zipPath);
        using var zip = new System.IO.Compression.ZipArchive(
            fs, System.IO.Compression.ZipArchiveMode.Create);

        // mods folder ------------------------------------------------
        if (Directory.Exists(ModsDir))
        {
            var files = Directory.GetFiles(ModsDir, "*", SearchOption.TopDirectoryOnly);
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                var entry = zip.CreateEntry(
                    "mods/" + name,
                    System.IO.Compression.CompressionLevel.Optimal);
                using var es = entry.Open();
                using var rs = File.OpenRead(f);
                rs.CopyTo(es);
            }
            summary.AppendLine($"• mods/ — {files.Length} file(s)");
        }
        else
        {
            summary.AppendLine($"• mods/ — skipped (folder not found at {ModsDir})");
        }

        // mod_config.cfg --------------------------------------------
        var cfg = ModConfig.DefaultPath;
        if (File.Exists(cfg))
        {
            var entry = zip.CreateEntry(
                "mod_config.cfg",
                System.IO.Compression.CompressionLevel.Optimal);
            using var es = entry.Open();
            using var rs = File.OpenRead(cfg);
            rs.CopyTo(es);
            summary.AppendLine("• mod_config.cfg");
        }
        else
        {
            summary.AppendLine("• mod_config.cfg — skipped (no file yet)");
        }

        // MCM per-mod settings -------------------------------------
        // Mod Configuration Menu writes to user://MCM/, which in
        // Godot 4 with the game's project name resolves to
        // %APPDATA%\Road to Vostok\MCM\. Mirror the whole tree so
        // every registered mod's saved config is captured, not just
        // the top-level files. Skipped silently if MCM isn't
        // installed (the directory doesn't exist).
        var mcmDir = Path.Combine(
            Path.GetDirectoryName(cfg) ?? "",
            "MCM");
        if (Directory.Exists(mcmDir))
        {
            var mcmFiles = Directory.GetFiles(
                mcmDir, "*", SearchOption.AllDirectories);
            foreach (var f in mcmFiles)
            {
                // Preserve sub-folder structure under MCM/. Replace
                // backslashes with forward slashes so the archive
                // opens cleanly in every extractor.
                var rel = Path.GetRelativePath(mcmDir, f).Replace('\\', '/');
                var entry = zip.CreateEntry(
                    "MCM/" + rel,
                    System.IO.Compression.CompressionLevel.Optimal);
                using var es = entry.Open();
                using var rs = File.OpenRead(f);
                rs.CopyTo(es);
            }
            summary.AppendLine($"• MCM/ — {mcmFiles.Length} file(s)");
        }
        else
        {
            summary.AppendLine("• MCM/ — skipped (MCM not installed)");
        }
    }

    /// <summary>Updates the MML status row: detects the installed
    /// MODLOADER_VERSION from `<gameDir>/modloader.gd`, polls GitHub
    /// for the latest release tag (cached for 24h), and renders the
    /// pair with a comparison glyph (✓ / ⬆ / ↑) using the same
    /// version-compare logic the mod grid uses. Network failures
    /// fall back to the cached latest; missing modloader.gd shows
    /// "not detected" with a hint.</summary>
    private async Task CheckMmlVersionAsync(bool forceFresh = false)
    {
        var installed = MmlInstall.DetectInstalledVersion(ModsDir);

        // Latest from GitHub — cache-first.
        string latest = "";
        bool fromCache = false;
        if (!forceFresh && _settings.IsMmlCacheFresh)
        {
            latest = _settings.MmlLatestTag;
            fromCache = true;
        }
        else
        {
            _mmlLabel.Text = "MML: checking for latest release …";
            var release = await _mml.FetchLatestAsync();
            if (release != null)
            {
                latest = release.TagName;
                _settings.MmlLatestTag = release.TagName;
                _settings.MmlLatestUrl = release.HtmlUrl;
                _settings.MmlCheckedAt = DateTime.UtcNow.ToString("o");
                try { _settings.Save(); } catch { /* best-effort */ }
            }
            else if (!string.IsNullOrEmpty(_settings.MmlLatestTag))
            {
                latest = _settings.MmlLatestTag;
                fromCache = true;
            }
        }
        RenderMmlLabel(installed, latest, fromCache);
    }

    private void RenderMmlLabel(string installed, string latest, bool fromCache)
    {
        var freshness = fromCache ? " (cached)" : "";
        var hasInstalled = !string.IsNullOrEmpty(installed);
        var hasLatest = !string.IsNullOrEmpty(latest);

        if (!hasInstalled && !hasLatest)
        {
            _mmlLabel.Text =
                "MML: not detected and latest-version check failed. "
                + "Click to open the releases page.";
            return;
        }
        if (!hasInstalled)
        {
            _mmlLabel.Text =
                $"MML: not detected at modloader.gd; latest release "
                + $"v{latest}{freshness} — click to open releases page.";
            return;
        }
        if (!hasLatest)
        {
            _mmlLabel.Text =
                $"MML: installed v{installed}; latest unknown — "
                + "click to open releases page.";
            return;
        }
        var cmp = CompareVersions(installed, latest);
        string state;
        if (cmp == 0) state = "✓ up to date";
        else if (cmp > 0) state = "↑ ahead of release";
        else state = "⬆ outdated";
        _mmlLabel.Text =
            $"MML: installed v{installed}, latest v{latest} {state}{freshness} — "
            + "click to open releases page.";
    }

    private void OpenMmlReleasesPage()
    {
        var url = !string.IsNullOrEmpty(_settings.MmlLatestUrl)
            ? _settings.MmlLatestUrl
            : "https://github.com/ametrocavich/vostok-mod-loader/releases";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowError("Couldn't open browser", ex);
        }
    }

    // --- mod-manager self-update check ----------------------------

    /// <summary>Cache-first check of the manager's own ModWorkshop
    /// listing. Reuses _mw.CheckVersionsAsync (the same endpoint we
    /// hit for every other mod) with a single-id batch. Updates
    /// _managerLabel + persists the freshly-fetched version into
    /// Settings.ManagerLatestVersion so a re-launch within 24h skips
    /// the network call entirely.</summary>
    private async Task CheckManagerUpdateAsync(bool forceFresh = false)
    {
        var installed = ManagerInstalledVersion();
        string latest = "";
        bool fromCache = false;

        if (!forceFresh && _settings.IsManagerCacheFresh)
        {
            latest = _settings.ManagerLatestVersion;
            fromCache = true;
        }
        else
        {
            _managerLabel.Text = "Manager: checking for update …";
            try
            {
                var versions = await _mw.CheckVersionsAsync(
                    new[] { ManagerModWorkshopId });
                if (versions.TryGetValue(ManagerModWorkshopId, out var v)
                    && !string.IsNullOrEmpty(v))
                {
                    latest = v;
                    _settings.ManagerLatestVersion = v;
                    _settings.ManagerCheckedAt = DateTime.UtcNow.ToString("o");
                    try { _settings.Save(); } catch { /* best-effort */ }
                }
                else if (!string.IsNullOrEmpty(_settings.ManagerLatestVersion))
                {
                    // ModWorkshop returned no row for our id (page
                    // missing / temporarily 404). Fall back to the
                    // last good value rather than blanking the row.
                    latest = _settings.ManagerLatestVersion;
                    fromCache = true;
                }
            }
            catch
            {
                if (!string.IsNullOrEmpty(_settings.ManagerLatestVersion))
                {
                    latest = _settings.ManagerLatestVersion;
                    fromCache = true;
                }
            }
        }
        RenderManagerLabel(installed, latest, fromCache);
    }

    /// <summary>Reads the running assembly's Version (Major.Minor.
    /// Build, matching the csproj &lt;Version&gt; tag) so the status
    /// row's "installed" side is the truth on disk, not whatever was
    /// last cached. Falls back to "0.0.0" if reflection fails for
    /// some reason — defensive against trimming/AOT edge cases.</summary>
    private static string ManagerInstalledVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private void RenderManagerLabel(string installed, string latest, bool fromCache)
    {
        // Reset visual emphasis each call — falls back to the
        // default status-row look unless the state below escalates
        // it (outdated → bold + amber).
        _managerLabel.Font = new Font("Segoe UI", 11f);
        _managerLabel.ForeColor = Color.FromArgb(180, 190, 210);

        var freshness = fromCache ? " (cached)" : "";
        if (string.IsNullOrEmpty(latest))
        {
            _managerLabel.Text =
                $"Manager: installed v{installed}; update check failed — "
                + "click to open ModWorkshop page.";
            return;
        }
        var cmp = CompareVersions(installed, latest);
        if (cmp == 0)
        {
            // Up-to-date: muted green, normal weight — confirms
            // the state without competing for attention.
            _managerLabel.ForeColor = Color.FromArgb(120, 180, 130);
            _managerLabel.Text =
                $"Manager: v{installed} ✓ up to date{freshness}";
            return;
        }
        if (cmp > 0)
        {
            // Ahead of release (running a local build newer than
            // what's published) — informational blue.
            _managerLabel.ForeColor = Color.FromArgb(150, 180, 230);
            _managerLabel.Text =
                $"Manager: v{installed}  ↑ ahead of published v{latest}{freshness}";
            return;
        }
        // Outdated — escalate. Bold + amber so the row stands out
        // against the other muted status lines. Glyph repeated +
        // version delta foregrounded so the user reads "you have
        // an update" before the surrounding text.
        _managerLabel.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        _managerLabel.ForeColor = Color.FromArgb(255, 200, 80);
        _managerLabel.Text =
            $"⬆ UPDATE AVAILABLE — Mod Manager v{installed} → v{latest}{freshness}  "
            + "·  click to auto-update";
    }

    private void OpenManagerModWorkshopPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"https://modworkshop.net/mod/{ManagerModWorkshopId}",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowError("Couldn't open browser", ex);
        }
    }

    /// <summary>Click handler for the Manager status row. When an
    /// update is available, prompts the user to either auto-
    /// install (downloads the new .exe and swaps it via a tiny
    /// batch script) or open the MW page. When up-to-date, just
    /// opens the page (so the user can still browse / report
    /// issues).</summary>
    private async Task HandleManagerLabelClickAsync()
    {
        var installed = ManagerInstalledVersion();
        var latest = _settings.ManagerLatestVersion;
        var outdated = !string.IsNullOrEmpty(latest)
                    && CompareVersions(installed, latest) < 0;
        if (!outdated)
        {
            OpenManagerModWorkshopPage();
            return;
        }
        var dr = Ui.ThemedMessageBox.Show(this,
            $"A newer version of the mod manager is available "
            + $"(installed v{installed}, latest v{latest}).\n\n"
            + "Yes → download the new build, swap the .exe in place, "
            + "relaunch automatically.\n"
            + "No → just open the ModWorkshop page in your browser.",
            "Update mod manager",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (dr == DialogResult.Cancel) return;
        if (dr == DialogResult.No) { OpenManagerModWorkshopPage(); return; }
        await AutoUpdateManagerAsync();
    }

    /// <summary>Download + replace + relaunch flow. Steps:
    ///   1. Download the latest from ModWorkshop into a temp file.
    ///   2. If it's a .zip / .vmz, extract; look for an .exe whose
    ///      name matches our edition (VostokModManagerAI.exe /
    ///      VostokModManagerIntegrated.exe). Fall back to the
    ///      first .exe found if no name match.
    ///   3. Write a small .bat that waits for our process to exit,
    ///      copies the new .exe over the current one (with retry
    ///      so a slow shutdown doesn't fail the copy), launches
    ///      the new .exe, then deletes itself.
    ///   4. Start the batch and Application.Exit. Windows handles
    ///      the rest.</summary>
    private async Task AutoUpdateManagerAsync()
    {
        var currentExe = System.Reflection.Assembly.GetExecutingAssembly()
            .Location;
        // GetExecutingAssembly().Location is empty for single-file
        // bundles — fall back to Process MainModule path which
        // does return the .exe even after self-extraction.
        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
        {
            try
            {
                currentExe = System.Diagnostics.Process
                    .GetCurrentProcess().MainModule?.FileName ?? "";
            }
            catch { }
        }
        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
        {
            Ui.ThemedMessageBox.Show(this,
                "Couldn't locate the running .exe path — auto-update "
                + "can't proceed. Open the ModWorkshop page and grab "
                + "the new build manually.",
                "Auto-update aborted",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(),
            "VostokModManagerUpdate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, "download.bin");

        _managerLabel.Text = $"Manager: downloading update …";
        try
        {
            await Task.Run(() =>
                _mw.DownloadLatestAsync(ManagerModWorkshopId, downloadPath));
        }
        catch (Exception ex)
        {
            Ui.ThemedMessageBox.Show(this,
                $"Couldn't download the update:\n\n{ex.Message}",
                "Download failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            try { Directory.Delete(tempDir, true); } catch { }
            return;
        }

        // Locate the new .exe inside whatever the upload looks like.
        var newExe = FindUpdateExe(downloadPath, tempDir, currentExe);
        if (string.IsNullOrEmpty(newExe))
        {
            Ui.ThemedMessageBox.Show(this,
                "Downloaded the update, but couldn't find a .exe inside "
                + "the package. Open the ModWorkshop page and install "
                + "manually.\n\nThe download is at:\n  " + downloadPath,
                "Update format not recognised",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Write a swap-and-relaunch batch script and run it.
        var batPath = Path.Combine(tempDir, "swap.bat");
        var script =
            "@echo off\r\n"
            + ":wait\r\n"
            + "timeout /t 1 /nobreak >nul\r\n"
            + $"copy /y \"{newExe}\" \"{currentExe}\" >nul\r\n"
            + "if errorlevel 1 goto wait\r\n"
            + $"start \"\" \"{currentExe}\"\r\n"
            // Self-delete trick: redirect the goto-error to nul,
            // then chain a del so cmd processes the del AFTER
            // the script's done parsing this line.
            + "(goto) 2>nul & del \"%~f0\"\r\n";
        try { File.WriteAllText(batPath, script); }
        catch (Exception ex)
        {
            ShowError("Couldn't write update script", ex);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/c \"\"{batPath}\"\"",
                UseShellExecute = true,
                WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden,
                CreateNoWindow  = true,
            });
        }
        catch (Exception ex)
        {
            ShowError("Couldn't launch update script", ex);
            return;
        }
        // Hand over to the script. Application.Exit unwires WinForms
        // cleanly so file handles on the .exe drop, letting the
        // copy in the script succeed.
        Application.Exit();
    }

    /// <summary>Find a .exe inside the downloaded payload that
    /// matches our edition's assembly name, falling back to any
    /// .exe when no name match is possible. Returns empty string
    /// when nothing usable was found.</summary>
    private static string FindUpdateExe(string downloadPath, string workDir, string currentExe)
    {
        // Heuristic 1: payload IS an .exe (author uploaded the
        // binary directly). Validate via a magic-byte sniff
        // (MZ header) since the file might just have a wrong
        // extension.
        try
        {
            if (LooksLikeExe(downloadPath))
            {
                var targetName = Path.GetFileName(currentExe);
                var renamed = Path.Combine(workDir, targetName);
                File.Copy(downloadPath, renamed, overwrite: true);
                return renamed;
            }
        }
        catch { /* fall through to zip handling */ }

        // Heuristic 2: zip-shaped payload. Try to extract.
        var extractDir = Path.Combine(workDir, "extracted");
        try
        {
            Directory.CreateDirectory(extractDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                downloadPath, extractDir, overwriteFiles: true);
        }
        catch { return ""; }

        var exes = Directory.GetFiles(
            extractDir, "*.exe", SearchOption.AllDirectories);
        if (exes.Length == 0) return "";

        // Prefer an exe whose filename matches our currently-
        // running .exe — that's how the user knows the build
        // matches the edition they're running.
        var preferred = Path.GetFileName(currentExe);
        var match = exes.FirstOrDefault(p =>
            string.Equals(Path.GetFileName(p), preferred,
                StringComparison.OrdinalIgnoreCase));
        return match ?? exes[0];
    }

    /// <summary>Quick "is this an MZ-headed .exe" probe so we
    /// don't try to ZIP-extract a single .exe upload.</summary>
    private static bool LooksLikeExe(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            int b0 = fs.ReadByte();
            int b1 = fs.ReadByte();
            return b0 == 'M' && b1 == 'Z';
        }
        catch { return false; }
    }

    /// <summary>Click on the MML status row: when MML is outdated,
    /// prompt to update; Yes runs the auto-update flow, No falls
    /// through to opening the releases page in the browser.
    /// Otherwise (up-to-date / unknown) just opens releases.</summary>
    private async Task HandleMmlClickAsync()
    {
        var installed = MmlInstall.DetectInstalledVersion(ModsDir);
        var latest = _settings.MmlLatestTag;
        if (string.IsNullOrEmpty(installed)
            || string.IsNullOrEmpty(latest)
            || CompareVersions(installed, latest) >= 0)
        {
            OpenMmlReleasesPage();
            return;
        }

        var dr = Ui.ThemedMessageBox.Show(this,
            $"MML v{installed} is installed; latest is v{latest}.\n\n"
            + $"Download v{latest}'s `modloader.gd` and `override.cfg` "
            + "and replace the files in your game folder?\n\n"
            + "Existing files will be backed up first as "
            + "<name>.<timestamp>.bak alongside the originals — "
            + "if anything looks wrong after launch, restore the "
            + ".bak files manually.\n\n"
            + "Yes  →  download + install\n"
            + "No   →  just open the releases page in the browser",
            "Update MML?",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (dr == DialogResult.Yes) await UpdateMmlAsync(latest);
        else if (dr == DialogResult.No) OpenMmlReleasesPage();
        // Cancel = no-op
    }

    /// <summary>Downloads `modloader.gd` and `override.cfg` from the
    /// given tag's release assets, backs up any existing copies in
    /// the game folder, and atomically replaces them. Both files
    /// are downloaded to .download temp paths first so a mid-
    /// transfer failure on the second file leaves the first one's
    /// original in place untouched.</summary>
    private async Task UpdateMmlAsync(string tag)
    {
        var gameDir = Path.GetDirectoryName(ModsDir);
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
        {
            ShowError("Game folder not found",
                new Exception(
                    $"Couldn't resolve the game folder from ModsDir=\n  {ModsDir}\n"
                    + "Set the Mods folder to <game>/mods/ via Settings."));
            return;
        }
        var modloaderPath = Path.Combine(gameDir, "modloader.gd");
        var overridePath = Path.Combine(gameDir, "override.cfg");
        var modloaderTmp = modloaderPath + ".download";
        var overrideTmp = overridePath + ".download";

        _busy = true;
        _mmlLabel.Text = $"MML: downloading v{tag} …";
        try
        {
            // Clean up any leftover .download files from a prior
            // failed attempt before we start.
            DeleteIfExists(modloaderTmp);
            DeleteIfExists(overrideTmp);

            await _mml.DownloadReleaseAssetAsync(tag, "modloader.gd", modloaderTmp);
            await _mml.DownloadReleaseAssetAsync(tag, "override.cfg", overrideTmp);

            // Sanity-check the downloads. modloader.gd should be a
            // GDScript file containing the version constant; if it
            // doesn't, something went wrong (404 page, partial
            // download, GitHub asset moved).
            if (!File.Exists(modloaderTmp) || new FileInfo(modloaderTmp).Length < 1000)
                throw new InvalidDataException(
                    "Downloaded modloader.gd looks empty / truncated.");
            var probe = File.ReadAllText(modloaderTmp).Substring(
                0, Math.Min(2000, (int)new FileInfo(modloaderTmp).Length));
            if (!probe.Contains("MODLOADER_VERSION"))
                throw new InvalidDataException(
                    "Downloaded modloader.gd doesn't look like a real "
                    + "MML build (no MODLOADER_VERSION constant). Aborting.");

            // Backup existing files (if any). Same .timestamp.bak
            // naming as the .vmz patch flow for consistency.
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var backups = new List<string>();
            if (File.Exists(modloaderPath))
            {
                var bak = $"{modloaderPath}.{stamp}.bak";
                File.Copy(modloaderPath, bak, overwrite: false);
                backups.Add(Path.GetFileName(bak));
            }
            if (File.Exists(overridePath))
            {
                var bak = $"{overridePath}.{stamp}.bak";
                File.Copy(overridePath, bak, overwrite: false);
                backups.Add(Path.GetFileName(bak));
            }

            // Move temp files into place. Both final paths use
            // overwrite: true since we already backed them up.
            File.Move(modloaderTmp, modloaderPath, overwrite: true);
            File.Move(overrideTmp, overridePath, overwrite: true);

            await CheckMmlVersionAsync(forceFresh: false);

            var bakNote = backups.Count > 0
                ? $"\n\nBackups: {string.Join(", ", backups)}"
                : "\n\n(No prior install — nothing to back up.)";
            Ui.ThemedMessageBox.Show(this,
                $"MML updated to v{tag}.\n\n"
                + "Restart Road to Vostok if it's running for the new "
                + "loader to take effect."
                + bakNote,
                "MML updated",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            DeleteIfExists(modloaderTmp);
            DeleteIfExists(overrideTmp);
            ShowError("MML update failed", ex);
        }
        finally
        {
            _busy = false;
            // Re-render in case CheckMmlVersionAsync didn't reach
            // (e.g. exception thrown before the download finished).
            await CheckMmlVersionAsync(forceFresh: false);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }

    // --- status / list rendering ----------------------------------

#if AI_RESOLVER
    private void UpdateClaudeStatus()
    {
        if (_claude.IsAvailable)
        {
            _claudeLabel.Text =
                $"Claude Code: ✓  {_claude.Version}   ({_claude.ResolvedPath})";
        }
        else if (ClaudeCodeRunner.HasMsixInstall())
        {
            _claudeLabel.Text =
                "Claude Code: ✗  Detected Claude Desktop (Microsoft Store), " +
                "but its CLI is sandboxed and unreachable from outside the " +
                "package. Install the standalone CLI: " +
                "`npm install -g @anthropic-ai/claude-code`.";
        }
        else
        {
            _claudeLabel.Text =
                "Claude Code: ✗ not found — AI conflict resolution disabled. " +
                "Install Node.js (nodejs.org), then run " +
                "`npm install -g @anthropic-ai/claude-code`.";
        }
    }
#endif

    private void UpdateModsStatus()
    {
        var enabled = _registry.Enabled().Count;
        var total = _registry.Entries.Count;
        _modsLabel.Text =
            $"Mods: {total} found  ({enabled} enabled, {total - enabled} disabled)";
        UpdateDriftStatus();
    }

    /// <summary>One specific drift between the active profile
    /// and the live registry. Surfaced to the user in the drift
    /// dialog before they commit a sync.</summary>
    private record DriftItem(
        string ModId,
        string DisplayName,
        string Description);

    /// <summary>Compute every drift point between the active
    /// profile and the live registry. Categories:
    ///   • toggle    — enabled/disabled flipped
    ///   • priority  — load-order number changed
    ///   • version   — file on disk is a different version than
    ///                 profile.json records
    ///   • missing   — profile lists a mod that isn't live (Apply
    ///                 would have to re-fetch it)
    ///   • adopted   — live mod that isn't in profile (orphan
    ///                 not yet absorbed)
    /// Empty list = profile and live state agree.</summary>
    private List<DriftItem> ComputeDrift()
    {
        var result = new List<DriftItem>();
        if (_activeProfile == null) return result;

        var liveById = new Dictionary<string, ModEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var e in _registry.Entries)
            if (!string.IsNullOrEmpty(e.ModId)) liveById[e.ModId] = e;

        foreach (var pm in _activeProfile.Mods)
        {
            if (string.IsNullOrEmpty(pm.ModId)) continue;
            var name = !string.IsNullOrEmpty(pm.DisplayName) ? pm.DisplayName : pm.ModId;
            if (!liveById.TryGetValue(pm.ModId, out var live))
            {
                result.Add(new DriftItem(pm.ModId, name,
                    "in profile but not currently live (Apply will re-fetch)"));
                continue;
            }
            if (pm.IsEnabled != live.IsEnabled)
                result.Add(new DriftItem(pm.ModId, name,
                    pm.IsEnabled ? "live: disabled (profile: enabled)"
                                 : "live: enabled (profile: disabled)"));
            if (pm.Priority != live.Priority)
                result.Add(new DriftItem(pm.ModId, name,
                    $"priority: profile {pm.Priority} → live {live.Priority}"));
            if (!string.IsNullOrEmpty(pm.Version)
                && !string.IsNullOrEmpty(live.Version)
                && !string.Equals(pm.Version, live.Version,
                       StringComparison.Ordinal))
                result.Add(new DriftItem(pm.ModId, name,
                    $"version: profile v{pm.Version} → live v{live.Version}"));
        }
        // Live mods not in profile — orphans. Rescan normally
        // adopts them, but the check surfaces the gap before
        // that runs.
        var profileIds = new HashSet<string>(
            _activeProfile.Mods.Select(m => m.ModId),
            StringComparer.OrdinalIgnoreCase);
        foreach (var e in _registry.Entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (profileIds.Contains(e.ModId)) continue;
            var name = !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName : e.ModId;
            result.Add(new DriftItem(e.ModId, name,
                "live mod not in profile (will be added on sync)"));
        }
        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName,
            StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Show or hide the drift status row + write a
    /// short summary line listing the first 2 specific changes
    /// so the user knows WHAT changed without opening the
    /// dialog. Clicking the row opens the full list dialog.
    /// </summary>
    private void UpdateDriftStatus()
    {
        if (_driftLabel == null) return;
        if (_activeProfile == null) { _driftLabel.Visible = false; return; }

        var drift = ComputeDrift();
        if (drift.Count == 0)
        {
            _driftLabel.Visible = false;
            return;
        }
        _driftLabel.Visible = true;
        _driftLabel.ForeColor = Color.FromArgb(255, 200, 80);
        // Inline summary: first 2 specific changes, then "+N more"
        // if there are more. Gives the user enough context to
        // decide whether to open the dialog without making the
        // status bar a wall of text.
        var preview = string.Join("; ",
            drift.Take(2).Select(d => $"{d.DisplayName} ({d.Description})"));
        if (drift.Count > 2)
            preview += $"; +{drift.Count - 2} more";
        _driftLabel.Text =
            $"⚠ Drift ({drift.Count}) vs '{_activeProfile.Name}': "
            + preview + "  —  click for details.";
    }

    /// <summary>Toggle the checkpoint status row's visibility +
    /// text based on which slots have snapshots on disk:
    ///   • Last-launch only — "🔁 Last-launch checkpoint (Xm ago)
    ///                        — click to undo your most recent
    ///                        cfg/profile change."
    ///   • Last-known-good only — "🔁 Last-known-good (Yh ago) —
    ///                        click to roll back to last clean
    ///                        session's state."
    ///   • Both — "🔁 Checkpoints: last-launch (Xm) + last-known-
    ///                        good (Yh) — click to choose."
    ///
    /// Click handler routes to either a direct restore (single
    /// slot) or a picker dialog (both).</summary>
    private void UpdateCheckpointStatus()
    {
        if (_checkpointLabel == null) return;
        var hasLaunch = Domain.CrashCheckpoint.Exists(
            Domain.CheckpointSlot.LastLaunch);
        var hasGood = Domain.CrashCheckpoint.Exists(
            Domain.CheckpointSlot.LastKnownGood);
        if (!hasLaunch && !hasGood)
        {
            _checkpointLabel.Visible = false;
            return;
        }
        _checkpointLabel.Visible = true;
        _checkpointLabel.ForeColor = Color.FromArgb(170, 200, 240);
        if (hasLaunch && hasGood)
        {
            _checkpointLabel.Text =
                $"🔁 Checkpoints: last-launch {FormatAge(Domain.CheckpointSlot.LastLaunch)} "
                + $"+ last-known-good {FormatAge(Domain.CheckpointSlot.LastKnownGood)} "
                + "— click to choose which to restore.";
        }
        else if (hasLaunch)
        {
            _checkpointLabel.Text =
                $"🔁 Last-launch checkpoint {FormatAge(Domain.CheckpointSlot.LastLaunch)} "
                + "— click to undo your most recent cfg / profile change.";
        }
        else
        {
            _checkpointLabel.Text =
                $"🔁 Last-known-good checkpoint {FormatAge(Domain.CheckpointSlot.LastKnownGood)} "
                + "— click to roll back to your last clean session's state.";
        }
    }

    private static string FormatAge(Domain.CheckpointSlot slot)
    {
        var marker = Domain.CrashCheckpoint.ReadMarker(slot);
        if (marker == null) return "";
        var age = DateTime.UtcNow - marker.CreatedAt;
        if (age.TotalMinutes < 60) return $"({(int)age.TotalMinutes}m ago)";
        if (age.TotalHours   < 48) return $"({(int)age.TotalHours}h ago)";
        return $"({(int)age.TotalDays}d ago)";
    }

    /// <summary>Click handler for the checkpoint status row.
    /// Picks a slot (auto when only one exists, picker dialog
    /// when both) and runs the restore.</summary>
    private void RestoreCheckpoint()
    {
        var hasLaunch = Domain.CrashCheckpoint.Exists(
            Domain.CheckpointSlot.LastLaunch);
        var hasGood = Domain.CrashCheckpoint.Exists(
            Domain.CheckpointSlot.LastKnownGood);
        if (!hasLaunch && !hasGood) return;

        Domain.CheckpointSlot slot;
        if (hasLaunch && hasGood)
        {
            using var picker = new Ui.CheckpointPickerDialog(
                Domain.CrashCheckpoint.ReadMarker(Domain.CheckpointSlot.LastLaunch),
                Domain.CrashCheckpoint.ReadMarker(Domain.CheckpointSlot.LastKnownGood));
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            slot = picker.Choice;
        }
        else if (hasLaunch) slot = Domain.CheckpointSlot.LastLaunch;
        else slot = Domain.CheckpointSlot.LastKnownGood;

        DoRestore(slot);
    }

    private void DoRestore(Domain.CheckpointSlot slot)
    {
        var marker = Domain.CrashCheckpoint.ReadMarker(slot);
        var slotName = slot == Domain.CheckpointSlot.LastLaunch
            ? "last-launch"
            : "last-known-good";
        var msg = marker != null
            ? $"Restore the {slotName} checkpoint taken "
              + $"{marker.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}?\n\n"
              + $"Active profile at snapshot time: '{marker.ActiveProfileName}'.\n\n"
            : $"Restore the {slotName} checkpoint?\n\n";
        msg += "Overwrites mod_config.cfg + the active profile's "
             + "profile.json with the snapshot. Mods themselves on "
             + "disk aren't touched — only the enabled/priority/"
             + "version metadata rolls back.";
        var dr = Ui.ThemedMessageBox.Show(this, msg,
            $"Restore {slotName} checkpoint",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);
        if (dr != DialogResult.Yes) return;

        var ok = Domain.CrashCheckpoint.Restore(slot,
            Domain.ModConfig.DefaultPath,
            _activeProfile?.FolderPath);
        if (!ok)
        {
            Ui.ThemedMessageBox.Show(this,
                "Couldn't restore the checkpoint — file write failed. "
                + "The snapshot is still on disk; check folder permissions.",
                "Restore failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Clear only the restored slot. The OTHER slot still
        // represents a valid anchor (e.g. restoring last-launch
        // doesn't invalidate last-known-good).
        Domain.CrashCheckpoint.Clear(slot);
        ReloadProfiles();
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
        UpdateCheckpointStatus();
        _modsLabel.Text = $"{slotName} checkpoint restored.";
    }

    /// <summary>Best-effort game-process watcher. Polls for a
    /// process whose name starts with "RoadToVostok" or contains
    /// "Vostok" (Godot exports vary by build target). If one
    /// appears within ~60s of launch and later exits with a
    /// non-zero code OR exits within 30s (likely-crash signal),
    /// prompt to restore the checkpoint. Silently no-ops when no
    /// candidate process is found — Steam launches under various
    /// names and we'd rather miss a crash than annoy the user
    /// with false positives.</summary>
    private async Task WatchForCrashAsync()
    {
        const int findTimeoutMs = 60_000;
        const int pollMs = 1_000;
        var launchTime = DateTime.UtcNow;
        System.Diagnostics.Process? game = null;
        var elapsed = 0;
        while (elapsed < findTimeoutMs && !IsDisposed)
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    var n = p.ProcessName ?? "";
                    if (n.StartsWith("RoadToVostok",
                            StringComparison.OrdinalIgnoreCase)
                        || (n.IndexOf("vostok",
                                StringComparison.OrdinalIgnoreCase) >= 0
                            && !n.Contains("Manager",
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        game = p;
                        break;
                    }
                }
            }
            catch { /* permission denied on some processes — keep polling */ }
            if (game != null) break;
            await Task.Delay(pollMs);
            elapsed += pollMs;
        }
        if (game == null) return;

        try
        {
            await Task.Run(() => game.WaitForExit());
            var sessionSec = (DateTime.UtcNow - launchTime).TotalSeconds;
            var exitCode = -1;
            try { exitCode = game.ExitCode; } catch { /* may throw if process gone */ }
            // Clean session: exit code 0 AND session was long
            // enough to be a real play (60s is the threshold —
            // quick quit-out under a minute is more likely "I
            // just tested launch / bounced out" than a real
            // session worth anchoring as last-known-good).
            var cleanExit = exitCode == 0 && sessionSec >= 60;
            var likelyCrash = exitCode != 0 || sessionSec < 60;
            if (cleanExit)
            {
                // Promote last-launch → last-known-good. The
                // current cfg state has been validated by a real
                // play session.
                Domain.CrashCheckpoint.PromoteToKnownGood();
                if (!IsDisposed)
                    BeginInvoke(new Action(UpdateCheckpointStatus));
                return;
            }
            if (!likelyCrash) return;
            if (IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                var hasGood = Domain.CrashCheckpoint.Exists(
                    Domain.CheckpointSlot.LastKnownGood);
                var msg =
                    $"Road to Vostok exited after {(int)sessionSec}s "
                    + $"(exit code {exitCode}).\n\n"
                    + "This looks like a crash. Restore a checkpoint?\n\n"
                    + "• Last-launch — undo just the change you made "
                    + "right before this launch.\n"
                    + (hasGood
                        ? "• Last-known-good — roll back to the state "
                          + "from the most recent session that ended "
                          + "cleanly.\n\n"
                        : "(no last-known-good available yet)\n\n")
                    + "Yes opens the restore picker; No leaves things "
                    + "as-is.";
                var dr = Ui.ThemedMessageBox.Show(this, msg,
                    "Possible crash detected",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (dr == DialogResult.Yes) RestoreCheckpoint();
            }));
        }
        catch { /* process gone before we could read state; nothing to do */ }
        finally
        {
            try { game.Dispose(); } catch { }
        }
    }

    /// <summary>Snapshot the live registry into the active
    /// profile — write each live mod's enabled/priority/version
    /// into the matching ProfileMod (creating new entries for
    /// adopted orphans). The reverse direction (profile → live)
    /// is what Apply Profile does; this method is the "I tweaked
    /// the live state, save it back to the profile" shortcut.
    /// </summary>
    private void SyncLiveStateIntoActiveProfile()
    {
        if (_activeProfile == null) return;

        // Show the user the EXACT list of drift items first so
        // they can review before committing the sync. A confused
        // user clicking "save live state" without knowing what
        // that means is how state-sync flows go wrong.
        var drift = ComputeDrift();
        if (drift.Count == 0)
        {
            _modsLabel.Text = "No drift detected — nothing to sync.";
            UpdateDriftStatus();
            return;
        }

        var rows = drift
            .Select(d => (Name: d.DisplayName, Description: d.Description))
            .ToList();
        using var dlg = new Ui.DriftDetailDialog(_activeProfile.Name, rows);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var changed = 0;
        var byId = _activeProfile.Mods.ToDictionary(
            m => m.ModId, StringComparer.OrdinalIgnoreCase);
        foreach (var e in _registry.Entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (byId.TryGetValue(e.ModId, out var pm))
            {
                if (pm.IsEnabled != e.IsEnabled) { pm.IsEnabled = e.IsEnabled; changed++; }
                if (pm.Priority  != e.Priority)  { pm.Priority  = e.Priority;  changed++; }
                if (!string.IsNullOrEmpty(e.Version)
                    && !string.Equals(pm.Version, e.Version, StringComparison.Ordinal))
                { pm.Version = e.Version; changed++; }
            }
            else
            {
                _activeProfile.Mods.Add(new Domain.ProfileMod
                {
                    ModId         = e.ModId,
                    DisplayName   = e.DisplayName,
                    Version       = e.Version,
                    IsEnabled     = e.IsEnabled,
                    Priority      = e.Priority,
                    ModWorkshopId = e.ModWorkshopId,
                });
                changed++;
            }
        }
        if (changed == 0)
        {
            _modsLabel.Text = "No drift detected — nothing to sync.";
            UpdateDriftStatus();
            return;
        }
        _activeProfile.UpdatedAt = DateTime.UtcNow;
        try { _activeProfile.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            ShowError("Couldn't save profile", ex);
            return;
        }
        _modsLabel.Text = $"Synced {changed} change(s) into '{_activeProfile.Name}'.";
        UpdateDriftStatus();
    }

    private void UpdateConflictsStatus(List<ConflictDetector.Conflict> conflicts)
    {
        if (conflicts.Count == 0)
        {
            _conflictsLabel.Text = "Conflicts: none detected ✓";
            return;
        }
        var byTier = conflicts
            .GroupBy(c => SeverityOf(c.Type))
            .ToDictionary(g => g.Key, g => g.Count());
        var parts = new List<string>();
        if (byTier.TryGetValue(Severity.Blocking, out var b) && b > 0)
            parts.Add($"{b} load-blocking");
        if (byTier.TryGetValue(Severity.Behavior, out var be) && be > 0)
            parts.Add($"{be} behavior");
        if (byTier.TryGetValue(Severity.Info, out var i) && i > 0)
            parts.Add($"{i} info");
        _conflictsLabel.Text =
            $"Conflicts: {conflicts.Count} ({string.Join(" · ", parts)})";
    }

    private void PopulateModsGrid()
    {
        // Sort: load order (priority asc) then filename. The same
        // ordering applies regardless of IsEnabled — toggling a mod
        // shouldn't shuffle its row, just flip its checkbox. The grid
        // itself is locked (every column has SortMode = NotSortable in
        // BuildModsGrid) so the user can't accidentally re-sort by
        // clicking a header either.
        // Filter: case-insensitive substring on display name, mod_id,
        // or filename. Empty filter = no filtering.
        var filter = (_filterBox?.Text ?? "").Trim().ToLowerInvariant();
        bool Matches(ModEntry e)
        {
            if (filter.Length == 0) return true;
            if (e.DisplayName.ToLowerInvariant().Contains(filter)) return true;
            if (e.ModId.ToLowerInvariant().Contains(filter)) return true;
            if (Path.GetFileName(e.Path).ToLowerInvariant().Contains(filter)) return true;
            return false;
        }

        // Profile scope: when a profile is active, only its mods
        // appear in the grid. Pre-migration (no active profile),
        // every installed mod still shows — the behaviour before
        // Phase 3 / the new model. `InActiveProfile` short-circuits
        // to "always true" in that case.
        // LOCKED mods are ALWAYS visible regardless of profile
        // membership — the lock semantic is "don't modify or hide
        // this mod"; filtering it out of the grid because the
        // active profile doesn't list it would surprise the user
        // (and make it impossible to unlock without switching
        // profiles first).
        var activeIds = _activeProfile != null
            ? new HashSet<string>(
                _activeProfile.Mods.Select(m => m.ModId),
                StringComparer.OrdinalIgnoreCase)
            : null;
        var lockedIds = new HashSet<string>(
            _settings.LockedMods,
            StringComparer.OrdinalIgnoreCase);
        bool InActiveProfile(ModEntry e)
        {
            if (activeIds == null) return true;
            if (string.IsNullOrEmpty(e.ModId)) return false;
            if (activeIds.Contains(e.ModId)) return true;
            // Locked mods bypass the profile filter so the user
            // always sees what's locked.
            return lockedIds.Contains(e.ModId);
        }

        // Dependency-rollup filter (set only while the Dependencies
        // dialog is open). Same locked-bypass rule as the profile
        // filter — locks always show regardless of the narrower
        // dependency scope.
        var depFilter = _dependencyFilter;
        bool InDependencyScope(ModEntry e)
        {
            if (depFilter == null) return true;
            if (string.IsNullOrEmpty(e.ModId)) return false;
            if (depFilter.Contains(e.ModId)) return true;
            return lockedIds.Contains(e.ModId);
        }

        _displayed = _registry.Entries
            .Where(InActiveProfile)
            .Where(InDependencyScope)
            .Where(Matches)
            .OrderBy(e => e.Priority)
            .ThenBy(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Build the row sequence: unpacked mods appear inline at
        // their natural priority; named packs are CONTIGUOUS
        // blocks anchored at the minimum priority of any mod in
        // the pack (so a pack containing the -100-priority MCM
        // would sit near the top, while a pack of late-loaders
        // sits near the bottom). The block emits a single header
        // row + its mods in priority order; collapse hides the
        // mods but keeps the header in place.
        var packsByMod = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        if (_activeProfile != null)
        {
            foreach (var pm in _activeProfile.Mods)
            {
                if (string.IsNullOrEmpty(pm.ModId)) continue;
                if (!string.IsNullOrEmpty(pm.PackName))
                    packsByMod[pm.ModId] = pm.PackName;
            }
        }

        // Bucket each displayed mod by pack name (empty → singleton).
        var packBuckets = new Dictionary<string, List<ModEntry>>(
            StringComparer.OrdinalIgnoreCase);
        var unpackedSingles = new List<ModEntry>();
        foreach (var e in _displayed)
        {
            var pack = !string.IsNullOrEmpty(e.ModId)
                && packsByMod.TryGetValue(e.ModId, out var p)
                && !string.IsNullOrEmpty(p) ? p : "";
            if (string.IsNullOrEmpty(pack))
            {
                unpackedSingles.Add(e);
            }
            else
            {
                if (!packBuckets.TryGetValue(pack, out var list))
                    packBuckets[pack] = list = new List<ModEntry>();
                list.Add(e);
            }
        }

        // Each "unit" is either an unpacked mod (sort key = its
        // priority) or a pack block (sort key = min priority of
        // its members). Render order is the unit sequence after
        // sorting by that key + a stable secondary tiebreak.
        var units = new List<(int sortKey, string secondary, ModEntry? single, string? packName, List<ModEntry>? packMods)>();
        foreach (var e in unpackedSingles)
            units.Add((e.Priority, Path.GetFileName(e.Path), e, null, null));
        foreach (var kvp in packBuckets)
        {
            var minPrio = kvp.Value.Min(m => m.Priority);
            units.Add((minPrio, kvp.Key, null, kvp.Key, kvp.Value));
        }
        units.Sort((a, b) =>
        {
            var k = a.sortKey.CompareTo(b.sortKey);
            if (k != 0) return k;
            return string.Compare(a.secondary, b.secondary,
                StringComparison.OrdinalIgnoreCase);
        });
        var collapsed = new HashSet<string>(
            _settings.CollapsedPacks, StringComparer.OrdinalIgnoreCase);

        // Capture scroll state before Rows.Clear() so toggling a
        // mod 30 rows down doesn't snap us back to row 0. Both the
        // first-displayed-row and the selected-row indexes
        // survive the rebuild since the load-order sort means
        // each mod stays at the same row index across rescans.
        var topRowBefore = _modsGrid.FirstDisplayedScrollingRowIndex;
        var selectedRowBefore = _modsGrid.CurrentRow?.Index ?? -1;

        // Guard the CellValueChanged handler — setting the checkbox
        // value programmatically would otherwise be indistinguishable
        // from a user click and would re-toggle the mod.
        _populatingMods = true;
        _modsGrid.SuspendLayout();
        // DataGridView retains a stale CurrentCell after Rows.Clear if
        // the cell was being edited (checkbox click). On the rebuilt
        // grid that stale CurrentCell snaps to the new row 0, and its
        // checkbox visually renders as unchecked even when Value=true.
        // Symptom the user sees: toggling row N flips row 0 visually
        // to unchecked until any other row is clicked. Detaching
        // CurrentCell first, then EndEdit-ing, clears the edit state
        // entirely so the new rows paint from scratch.
        try { _modsGrid.CurrentCell = null; } catch { /* no current cell */ }
        try { _modsGrid.EndEdit();        } catch { /* no active edit */ }
        try
        {
            _modsGrid.Rows.Clear();
            var pos = 0;

            void EmitModRow(ModEntry e, bool inPack = false)
            {
                var rowIdx = _modsGrid.Rows.Add();
                var row = _modsGrid.Rows[rowIdx];
                row.Tag = e;
                // Tint pack-member rows with a slightly-bluer
                // background than default so the eye reads them
                // as visually associated with the header above.
                // Subtle — doesn't override per-cell colours like
                // Update column's amber/green tag, but enough to
                // distinguish a packed row from an unpacked one
                // sitting just above or below it.
                if (inPack)
                {
                    row.DefaultCellStyle.BackColor = Color.FromArgb(24, 32, 46);
                    row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(50, 70, 100);
                }
                // 🧪 Testing flag — solid yellow tint that wins over
                // the pack-row blue. Dark text so it stays legible
                // against the bright background (the default light-
                // grey foreground would wash out). Applied last so
                // it overrides any earlier style above.
                var testing = IsTesting(e);
                if (testing)
                {
                    row.DefaultCellStyle.BackColor = Color.FromArgb(180, 150, 30);
                    row.DefaultCellStyle.ForeColor = Color.FromArgb(20, 20, 20);
                    row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(210, 175, 50);
                    row.DefaultCellStyle.SelectionForeColor = Color.FromArgb(20, 20, 20);
                }
                row.Cells["Enabled"].Value = e.IsEnabled;
                StyleUpdateCell(row.Cells["Update"], e);
                row.Cells["Pos"].Value = e.IsEnabled ? $"{++pos}" : "";
                var name = !string.IsNullOrEmpty(e.DisplayName)
                    ? e.DisplayName
                    : Path.GetFileName(e.Path);
                var locked = IsLocked(e);
                // 🔒 prefix on the cell text gives an at-a-glance
                // signal — same as the Lock/Unlock context-menu state,
                // but visible without right-clicking each row.
                // Personal note suffix: 📝 sigil after the name so
                // the user sees at a glance which rows have notes.
                // The actual note text lands in the tooltip below.
                // 🧪 prefix for testing mods — a second at-a-glance
                // cue in addition to the yellow row tint so the
                // flag is obvious in screenshots / shared captures
                // too, not only by colour.
                var hasNote = !string.IsNullOrEmpty(e.ModId)
                    && _settings.ModNotes.TryGetValue(e.ModId, out var noteRaw)
                    && !string.IsNullOrWhiteSpace(noteRaw);
                var noteSigil = hasNote ? "  📝" : "";
                var prefix = (testing ? "🧪 " : "") + (locked ? "🔒 " : "");
                row.Cells["Name"].Value = $"{prefix}{name}{noteSigil}";
                var tip = $"id: {e.ModId}\npath: {e.Path}";
                if (locked)
                    tip += "\n🔒 Locked — Enable all / Disable all skip this mod.";
                if (testing)
                    tip += "\n🧪 Flagged as Testing Mod (yellow highlight).";
                if (hasNote)
                    tip += $"\n\n📝 Note:\n{_settings.ModNotes[e.ModId!]}";
                row.Cells["Name"].ToolTipText = tip;
                row.Cells["Version"].Value = e.Version;
                row.Cells["Priority"].Value = e.Priority.ToString();
            }

            foreach (var unit in units)
            {
                if (unit.single != null)
                {
                    // Unpacked mod — emit as a normal row inline,
                    // no header.
                    EmitModRow(unit.single);
                    continue;
                }
                // Named pack block — header row + (if expanded)
                // its mods. Header sits at the pack's anchor
                // priority slot in the load order.
                var packName = unit.packName!;
                var mods = unit.packMods!;
                var isCollapsed = collapsed.Contains(packName);

                var hdrIdx = _modsGrid.Rows.Add();
                var hdr = _modsGrid.Rows[hdrIdx];
                hdr.Tag = $"pack:{packName}";
                var glyph = isCollapsed ? "▶" : "▼";
                // The Enabled column is a CheckBoxColumn for data
                // rows; for pack headers we swap in a TextBoxCell
                // showing the chevron, since the checkbox would
                // be meaningless here AND the column slot is
                // exactly where the eye expects an interactive
                // control. Click anywhere on the header row still
                // toggles collapse via the MouseDown handler.
                var chevronCell = new DataGridViewTextBoxCell { Value = glyph };
                chevronCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                chevronCell.Style.Font = new Font("Segoe UI Symbol", 14f, FontStyle.Bold);
                chevronCell.Style.ForeColor = Color.FromArgb(170, 200, 240);
                hdr.Cells["Enabled"] = chevronCell;
                hdr.Cells["Pos"].Value = "";
                hdr.Cells["Update"].Value = "";
                hdr.Cells["Update"].ToolTipText = "";
                hdr.Cells["Version"].Value = "";
                hdr.Cells["Priority"].Value = "";
                hdr.Cells["Name"].Value =
                    $"{packName}  ({mods.Count} mod{(mods.Count == 1 ? "" : "s")})";
                hdr.Cells["Name"].ToolTipText =
                    $"Pack '{packName}' — {mods.Count} mod(s). Click to "
                    + (isCollapsed ? "expand." : "collapse.");
                hdr.DefaultCellStyle.BackColor = Color.FromArgb(34, 42, 58);
                hdr.DefaultCellStyle.ForeColor = Color.FromArgb(170, 200, 240);
                hdr.DefaultCellStyle.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
                hdr.DefaultCellStyle.SelectionBackColor = Color.FromArgb(48, 58, 80);
                hdr.DefaultCellStyle.SelectionForeColor = Color.FromArgb(220, 235, 255);

                if (isCollapsed) continue;
                foreach (var e in mods) EmitModRow(e, inPack: true);
            }
        }
        finally
        {
            _modsGrid.ResumeLayout();
            _populatingMods = false;
        }

        // Restore scroll + selection. Wrapped in try/catch because
        // FirstDisplayedScrollingRowIndex throws if the grid hasn't
        // had its handle created yet, or if the index is out of
        // range after a filter that shrank the row count.
        if (topRowBefore >= 0 && topRowBefore < _modsGrid.RowCount)
        {
            try { _modsGrid.FirstDisplayedScrollingRowIndex = topRowBefore; }
            catch { /* benign — first paint hasn't happened yet */ }
        }
        if (selectedRowBefore >= 0 && selectedRowBefore < _modsGrid.RowCount)
        {
            _modsGrid.ClearSelection();
            _modsGrid.Rows[selectedRowBefore].Selected = true;
        }
        // Synchronous full repaint as the final step. Comes after the
        // scroll/selection restore so any paint events those triggered
        // can't overwrite freshly-set cell visuals. Refresh() (vs
        // Invalidate()) forces the paint NOW rather than queueing it,
        // which is what we need to defeat the "row 0 checkbox stays
        // visually wrong" quirk.
        _modsGrid.Refresh();
    }

    private bool IsOutdated(ModEntry e)
    {
        var mw = e.ModWorkshopId;
        if (mw <= 0) return false;
        if (!_latestVersions.TryGetValue(mw, out var latest)) return false;
        if (string.IsNullOrEmpty(latest)) return false;
        // Numeric-aware compare: only flag as outdated when LOCAL
        // is strictly older than REMOTE. String inequality fired
        // false positives when local was newer (e.g. local "1.14"
        // ≠ remote "1.13" but local is newer, not outdated).
        // Ambiguous-format versions (e.g. "0.0.420" vs "0.4.20_R")
        // can't be reliably ordered because the underlying compare
        // falls back to ordinal string compare for non-numeric
        // segments, which lies. Surface those as the ≠ "version
        // mismatch" state in StyleUpdateCell instead of "outdated"
        // so we don't keep re-downloading the same file each time
        // the user clicks ⬆.
        if (VersionsAreAmbiguous(e.Version, latest)) return false;
        return CompareVersions(e.Version, latest) < 0;
    }

    /// <summary>True when two version strings can't be reliably
    /// ordered because their formats disagree — at least one segment
    /// on either side isn't an integer. Pure-numeric vs pure-numeric
    /// is NEVER ambiguous (CompareVersions handles that correctly
    /// even when the strings differ). Used to surface a ≠ glyph in
    /// the Update column instead of a false ⬆ "outdated" when the
    /// author's mod.txt and the ModWorkshop listing format the same
    /// release differently (the headline example is `0.0.420`
    /// vs `0.4.20_R` — same release, two encodings).</summary>
    internal static bool VersionsAreAmbiguous(string? a, string? b)
    {
        a = (a ?? "").Trim().TrimStart('v', 'V');
        b = (b ?? "").Trim().TrimStart('v', 'V');
        if (a == b) return false;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        static bool AllNumeric(string[] parts)
        {
            foreach (var p in parts)
                if (!int.TryParse(p, out _)) return false;
            return true;
        }
        return !(AllNumeric(aParts) && AllNumeric(bParts));
    }

    /// <summary>Numeric-aware version comparison.
    /// Returns &lt;0 when a is older, 0 when equal, &gt;0 when a is
    /// newer. Strips a leading "v", splits on dots, compares each
    /// component as int when both are numeric (so "1.14" &gt; "1.13"),
    /// fallback to ordinal string compare otherwise. Missing
    /// components are treated as 0 (so "1.2" == "1.2.0").</summary>
    internal static int CompareVersions(string? a, string? b)
    {
        a = (a ?? "").Trim().TrimStart('v', 'V');
        b = (b ?? "").Trim().TrimStart('v', 'V');
        if (a == b) return 0;
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        var n = Math.Max(aParts.Length, bParts.Length);
        for (var i = 0; i < n; i++)
        {
            var ap = i < aParts.Length ? aParts[i] : "0";
            var bp = i < bParts.Length ? bParts[i] : "0";
            var aIsInt = int.TryParse(ap, out var ai);
            var bIsInt = int.TryParse(bp, out var bi);
            if (aIsInt && bIsInt)
            {
                var cmp = ai.CompareTo(bi);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = string.CompareOrdinal(ap, bp);
                if (cmp != 0) return cmp;
            }
        }
        return 0;
    }

    /// <summary>Writes the Update column's text + per-cell style for a
    /// row. The DataGridViewLinkCell renders link-blue by default; we
    /// override LinkColor per-cell to get green (current) / orange
    /// (outdated) / muted-grey (no info) variants. Only outdated rows
    /// keep an underline-on-hover and the hand cursor — the others are
    /// styled to look static even though they're still link cells.</summary>
    private void StyleUpdateCell(DataGridViewCell cell, ModEntry e)
    {
        var mw = e.ModWorkshopId;
        var muted = Color.FromArgb(120, 130, 150);
        var orange = Color.FromArgb(255, 200, 80);
        var green = Color.FromArgb(120, 220, 140);

        string text;
        Color color;
        string tip;

        if (mw <= 0)
        {
            text = "—";
            color = muted;
            tip = "No ModWorkshop link in mod.txt — can't check for updates.";
        }
        else if (!_latestVersions.TryGetValue(mw, out var latest)
                 || string.IsNullOrEmpty(latest))
        {
            text = "—";
            color = muted;
            tip = "Update status unknown (cache miss). Click Refresh to check.";
        }
        else
        {
            var cmp = CompareVersions(e.Version, latest);
            if (cmp == 0)
            {
                text = "✓";
                color = green;
                tip = $"Up to date (v{latest}).";
            }
            else if (VersionsAreAmbiguous(e.Version, latest))
            {
                // mod.txt and ModWorkshop list the version in
                // formats that can't be reliably ordered (e.g.
                // "0.0.420" vs "0.4.20_R"). The two strings are
                // almost certainly the same release written two
                // ways; we surface a ≠ glyph so the user knows
                // the file IS the latest one MW serves, the
                // author's mod.txt just doesn't agree with the
                // listing's version string.
                text = "≠";
                color = Color.FromArgb(200, 160, 220); // soft lavender
                tip = $"Version mismatch: mod.txt v{e.Version}, "
                    + $"ModWorkshop reports v{latest}.\n\n"
                    + "The two strings format differently and can't "
                    + "be reliably ordered — likely the same release "
                    + "written two ways. The local file matches "
                    + "what ModWorkshop currently serves; the author "
                    + "just hasn't matched mod.txt to the listing.";
            }
            else if (cmp > 0)
            {
                // Local is newer than ModWorkshop's reported version
                // — dev build, manually-bumped mod.txt ahead of the
                // latest release, or the API cache lagging. Distinct
                // glyph + colour so it doesn't get conflated with
                // "up to date" or with "outdated".
                text = "↑";
                color = Color.FromArgb(110, 190, 240); // cool blue
                tip = $"Local v{e.Version} is newer than ModWorkshop's "
                    + $"reported v{latest}. Likely a dev build, a "
                    + "manual bump, or the API cache catching up.";
            }
            else
            {
                text = "⬆";
                color = orange;
                tip = $"Update available: v{e.Version} → v{latest}. Click to install.";
            }
        }

        cell.Value = text;
        cell.ToolTipText = tip;
        cell.Style.ForeColor = color;
        cell.Style.SelectionForeColor = color;
        if (cell is DataGridViewLinkCell link)
        {
            link.LinkColor = color;
            link.ActiveLinkColor = color;
            link.VisitedLinkColor = color;
        }
    }

    private void PopulateConflictsList(List<ConflictDetector.Conflict> conflicts)
    {
        // Sort conflicts by severity tier first (Blocking → Behavior
        // → Info), then by load-order priority of their earliest-
        // loading involved mod (so within-tier rows surface early-
        // loading mods first), then by type/key for stable ordering.
        // Mods we can't resolve to a registry entry (e.g.
        // duplicate_mod_id ModIds are filenames, not mod IDs) sort to
        // the end via int.MaxValue.
        int PrioOf(string modIdOrFile)
        {
            var hit = _registry.Entries.FirstOrDefault(
                m => string.Equals(m.ModId, modIdOrFile,
                    StringComparison.OrdinalIgnoreCase));
            return hit?.Priority ?? int.MaxValue;
        }
        int EarliestPrio(ConflictDetector.Conflict c)
            => c.ModIds.Count == 0 ? int.MaxValue : c.ModIds.Min(PrioOf);

        var ordered = conflicts
            .OrderBy(c => (int)SeverityOf(c.Type))
            .ThenBy(EarliestPrio)
            .ThenBy(c => c.Type, StringComparer.Ordinal)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

        _conflictsGrid.SuspendLayout();
        _conflictsGrid.Rows.Clear();
        // Walk groups in tier order; emit a banner row before each
        // new tier, then the data rows for that tier.
        foreach (var tierGroup in ordered.GroupBy(c => SeverityOf(c.Type))
                                          .OrderBy(g => (int)g.Key))
        {
            var sev = tierGroup.Key;
            var bucket = tierGroup.ToList();
            // Banner row: row.Tag = Severity (marks it as banner);
            // What cell holds the rendered label so RowPrePaint can
            // read it.
            var bannerIdx = _conflictsGrid.Rows.Add();
            var bannerRow = _conflictsGrid.Rows[bannerIdx];
            bannerRow.Tag = sev;
            bannerRow.ReadOnly = true;
            bannerRow.Cells["What"].Value = TierStyle(sev, bucket.Count).label;
            // Banner-row hover tip. CellPainting suppresses the cells'
            // own rendering for these rows, but ToolTipText is honoured
            // independently — DataGridView fires the tooltip from cell
            // hover regardless of paint state. Set it on every cell so
            // the tip appears wherever the user hovers along the row.
            var tip = TierTooltip(sev);
            foreach (DataGridViewCell cell in bannerRow.Cells)
                cell.ToolTipText = tip;
            foreach (var c in bucket)
            {
                var rowIdx = _conflictsGrid.Rows.Add();
                var row = _conflictsGrid.Rows[rowIdx];
                row.Tag = c;     // click handler resolves conflict via row.Tag
                row.Cells["Resolve"].Value = IsButtonRow(c) ? "Resolve" : "";
                row.Cells["Resolve"].ToolTipText = ResolveButtonTooltip(c);
                var (title, subtitle) = DescribeConflict(c);
                row.Cells["What"].Value = title + "\n" + subtitle;
                row.Cells["What"].ToolTipText = $"[{c.Type}]  key: {c.Key}";
                // One mod name per line. The grid's DefaultCellStyle
                // has WrapMode=True and AutoSizeRowsMode=DisplayedCells,
                // so the row height grows to fit the wrapped names —
                // no extra layout wiring needed.
                row.Cells["Mods"].Value = string.Join("\n", OrderedModNames(c.ModIds));
                var (winnerText, winnerColor, winnerTip) = DetermineWinner(c);
                row.Cells["Wins"].Value = winnerText;
                row.Cells["Wins"].ToolTipText = winnerTip;
                row.Cells["Wins"].Style.ForeColor = winnerColor;
                row.Cells["Wins"].Style.SelectionForeColor = winnerColor;
            }
        }
        _conflictsGrid.ResumeLayout();
    }

    // --- conflict severity tiering -----------------------------------

    /// <summary>Three tiers used to visually group the conflicts grid.
    /// Blocking = game refuses to load (class_name) or a mod is inert
    /// until the user acts (missing_dependency); Behavior = a winner
    /// silently overrides others (file_overlap, autoload, hook,
    /// script_extend, take_over, duplicate_mod_id); Info = advisory
    /// load-order constraints that aren't necessarily wrong
    /// (dependency_order, super_chain_constraint).</summary>
    private enum Severity { Blocking, Behavior, Info }

    private static Severity SeverityOf(string type) => type switch
    {
        ConflictDetector.TYPE_CLASS_NAME_COLLISION   => Severity.Blocking,
        ConflictDetector.TYPE_MISSING_DEPENDENCY     => Severity.Blocking,
        ConflictDetector.TYPE_DEPENDENCY_ORDER       => Severity.Info,
        ConflictDetector.TYPE_SUPER_CHAIN_CONSTRAINT => Severity.Info,
        _ => Severity.Behavior,
    };

    /// <summary>Plain-English description shown in the "What" cell —
    /// title goes on line 1, the technical type + resolution hint on
    /// line 2. The raw type/key remain accessible via the cell
    /// tooltip for power users.</summary>
    private static (string title, string subtitle) DescribeConflict(
        ConflictDetector.Conflict c)
    {
        var key = c.Key;
        return c.Type switch
        {
            ConflictDetector.TYPE_CLASS_NAME_COLLISION
                => ($"Two mods declare class `{key}`",
                    "class_name collision · project will not load"),
            ConflictDetector.TYPE_FILE_OVERLAP
                => ($"Both modify `{key}`",
                    "file overlap · mergeable"),
            ConflictDetector.TYPE_AUTOLOAD_COLLISION
                => ($"Both register autoload `{key}`",
                    "autoload collision · last to load wins"),
            ConflictDetector.TYPE_HOOK_COLLISION
                => ($"Two mods hook `{key}`",
                    "hook collision · last to load wins the chain"),
            ConflictDetector.TYPE_SCRIPT_EXTEND_COLLISION
                => ($"Both extend `{key}`",
                    "script_extend collision · last applies"),
            ConflictDetector.TYPE_TAKE_OVER_COLLISION
                => ($"Two mods take_over `{key}`",
                    "take-over collision · only one applies"),
            ConflictDetector.TYPE_DUPLICATE_MOD_ID
                => ($"Two .vmz files share mod_id `{key}`",
                    "duplicate mod_id · loader picks one, ignores the rest"),
            ConflictDetector.TYPE_MISSING_DEPENDENCY
                => (c.Details.TryGetValue("required", out var dep)
                        ? $"Missing dependency: `{dep}`"
                        : "Missing dependency",
                    "missing dependency · dependent mod won't function"),
            ConflictDetector.TYPE_DEPENDENCY_ORDER
                => ($"Load-order constraint on `{key}`",
                    "dependency order · raise dependent's priority above its dependency"),
            ConflictDetector.TYPE_SUPER_CHAIN_CONSTRAINT
                => ($"Super-chain constraint on `{key}`",
                    "super_chain · replacer must load before chainer"),
            _ => (c.Type, c.Key),
        };
    }

    /// <summary>Per-tier visual palette: gutter colour for data rows
    /// + banner backgrounds/foregrounds + the banner header label.
    /// Centralised so RowPrePaint and CellPainting agree on colours
    /// and the SVG mockup's three-tier accent (red / amber / blue) is
    /// the single source of truth.</summary>
    private static (Color gutter, Color bannerBg, Color bannerFg, string label)
        TierStyle(Severity s, int count) => s switch
    {
        Severity.Blocking => (
            Color.FromArgb(220,  67,  80),
            Color.FromArgb( 40,  22,  26),
            Color.FromArgb(232, 144, 152),
            $"●  LOAD-BLOCKING ({count})"),
        Severity.Behavior => (
            Color.FromArgb(224, 176,  64),
            Color.FromArgb( 37,  31,  16),
            Color.FromArgb(232, 204, 128),
            $"●  BEHAVIOR-AFFECTING ({count})"),
        Severity.Info => (
            Color.FromArgb( 78, 132, 208),
            Color.FromArgb( 16,  26,  36),
            Color.FromArgb(136, 180, 232),
            $"●  INFO ({count})"),
        _ => (Color.Gray, Color.Black, Color.White, ""),
    };

    /// <summary>Plain-English explanation of what each severity tier
    /// means in practice — shown as the hover tip on banner rows so
    /// the colored ●  LOAD-BLOCKING / BEHAVIOR-AFFECTING / INFO
    /// labels are self-documenting on first encounter.</summary>
    private static string TierTooltip(Severity s) => s switch
    {
        Severity.Blocking =>
            "Load-blocking — game refuses to start until you fix it.",
        Severity.Behavior =>
            "Behavior-affecting — game runs, but one mod overrides "
            + "another in a way you might not notice.",
        Severity.Info =>
            "Info — advisory only; ordering hint, not really wrong.",
        _ => "",
    };

    /// <summary>Resolves which mod currently "wins" a conflict given
    /// the live load-order priorities. Semantics vary per conflict
    /// type:
    ///   - file_overlap / autoload / hook / script_extend /
    ///     take_over → highest priority value among the involved
    ///     mods (last to load → final write wins / hook chain
    ///     terminator);
    ///   - class_name → no winner; the project refuses to load,
    ///     so the cell renders red with a "hard fail" hint;
    ///   - super_chain_constraint / dependency_order → the late-
    ///     loading side of the constraint (its behaviour is what
    ///     the player ultimately sees);
    ///   - missing_dependency → "(needs &lt;dep&gt;)" — neither
    ///     mod meaningfully loads until the dep is enabled;
    ///   - duplicate_mod_id → loader picks the alphabetically-
    ///     first filename it discovers, so we surface that too.
    /// Returns (display text, foreground colour, tooltip).</summary>
    private (string text, Color color, string tooltip) DetermineWinner(
        ConflictDetector.Conflict c)
    {
        var defaultColor = Color.FromArgb(220, 225, 235);
        var mutedColor = Color.FromArgb(160, 170, 190);
        var redColor = Color.FromArgb(245, 130, 120);
        var greenColor = Color.FromArgb(120, 220, 140);

        if (c.ModIds.Count == 0)
            return ("—", mutedColor, "No involved mods recorded.");

        switch (c.Type)
        {
            case ConflictDetector.TYPE_CLASS_NAME_COLLISION:
                return ("(hard fail — game won't load)",
                    redColor,
                    "Two mods declare the same class_name. Godot "
                    + "refuses to load the project. Disable one of "
                    + "the involved mods.");

            case ConflictDetector.TYPE_MISSING_DEPENDENCY:
                // Details["required"] holds the missing dep id.
                var required = c.Details.TryGetValue("required", out var r)
                    ? r?.ToString() ?? "?"
                    : "?";
                return ($"(needs `{required}`)",
                    redColor,
                    "Dependent mod won't function until the required "
                    + "mod is installed and enabled.");

            case ConflictDetector.TYPE_DUPLICATE_MOD_ID:
                // ModIds for this type are FILENAMES, not mod_ids.
                // The loader picks the first one it discovers,
                // which is typically alphabetical.
                var picked = c.ModIds
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .First();
                return ($"{picked} (loader picks first)",
                    mutedColor,
                    "Two .vmz files share the same mod_id; the in-game "
                    + "loader picks the alphabetically-first filename "
                    + "and silently ignores the rest.");

            case ConflictDetector.TYPE_DEPENDENCY_ORDER:
            case ConflictDetector.TYPE_SUPER_CHAIN_CONSTRAINT:
                // These are ordering constraints, not winner-takes-
                // all conflicts. The mod that loads LAST is the one
                // whose behaviour the player ultimately sees.
                var lateMod = HighestPriorityMod(c.ModIds);
                return (lateMod == null ? "—" : DisplayLabel(lateMod),
                    defaultColor,
                    "Late-loading side of the order constraint — its "
                    + "overrides land last in the chain.");

            default:
                // file_overlap, autoload, hook, script_extend,
                // take_over — last write wins.
                var winner = HighestPriorityMod(c.ModIds);
                if (winner == null)
                    return ("—", mutedColor, "Couldn't resolve any "
                        + "involved mod against the registry.");
                return (DisplayLabel(winner), greenColor,
                    "Highest priority value (loads last) → its "
                    + "version is what the game actually sees.");
        }
    }

    /// <summary>Among the given mod_ids, returns the ModEntry with
    /// the highest Priority value (= loads last). Falls back to
    /// alphabetical mod_id ordering on ties; returns null when
    /// none of the ids resolve against the registry.</summary>
    private ModEntry? HighestPriorityMod(IEnumerable<string> modIds)
    {
        return modIds
            .Select(id => _registry.FindById(id))
            .Where(e => e != null)
            .Select(e => e!)
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.ModId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>Human-readable label for a mod — DisplayName when
    /// declared in mod.txt, falling back to mod_id (and then the
    /// archive filename if that's also empty).</summary>
    private static string DisplayLabel(ModEntry e)
        => !string.IsNullOrEmpty(e.DisplayName)
            ? e.DisplayName
            : !string.IsNullOrEmpty(e.ModId)
                ? e.ModId
                : Path.GetFileName(e.Path);

    /// <summary>Maps a single conflict-row id (mod_id for most
    /// types, or filename for duplicate_mod_id) to the user-
    /// facing string that goes into the Mods cell. Resolves
    /// against the registry to swap in DisplayName when we have
    /// it; falls through to the raw id when the mod isn't
    /// installed (e.g. the missing-side of a missing_dependency)
    /// or when the id is actually a filename (duplicate_mod_id).</summary>
    private string ModNameForCell(string idOrFilename)
    {
        var entry = _registry.FindById(idOrFilename);
        return entry != null ? DisplayLabel(entry) : idOrFilename;
    }

    /// <summary>Maps + ORDERS conflict-row ids by current
    /// load-order priority (ascending — earliest-loading first,
    /// latest-loading last). The last name in the result is
    /// therefore the same mod as the one in the Wins column for
    /// "last write wins" conflict types. Unresolvable ids
    /// (missing mods, filenames in duplicate_mod_id) sort after
    /// the resolvable ones, ordered alphabetically among
    /// themselves.</summary>
    private IEnumerable<string> OrderedModNames(IEnumerable<string> ids)
    {
        var idList = ids.ToList();
        var resolved = new List<(string label, int priority)>();
        var unresolved = new List<string>();
        foreach (var id in idList)
        {
            var entry = _registry.FindById(id);
            if (entry != null)
                resolved.Add((DisplayLabel(entry), entry.Priority));
            else
                unresolved.Add(id);
        }
        return resolved
            .OrderBy(t => t.priority)
            .ThenBy(t => t.label, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.label)
            .Concat(unresolved.OrderBy(
                s => s, StringComparer.OrdinalIgnoreCase));
    }

    private string ResolveButtonTooltip(ConflictDetector.Conflict c)
    {
        if (c.Type == ConflictDetector.TYPE_DUPLICATE_MOD_ID)
            return "Two installed mods declare the same mod_id. The Mods "
                + "column lists the colliding files — delete or move one "
                + "out of the mods folder (and its Disabled subfolder) so "
                + "only one remains. Often this is a leftover .vmz from a "
                + "previous version sitting alongside the current copy.";
        if (c.Type == ConflictDetector.TYPE_MISSING_DEPENDENCY)
            return "An enabled mod requires another mod that's either "
                + "disabled or not installed. The Mods column lists the "
                + "dependent and the missing one — enable / install the "
                + "required mod, or disable the dependent.";
        if (c.Type == ConflictDetector.TYPE_DEPENDENCY_ORDER)
        {
            var min = c.Details.TryGetValue("suggested_dependent_min", out var v)
                ? v?.ToString() ?? "?" : "?";
            return $"A mod loads before its declared dependency. Raise the "
                + $"dependent's priority to at least {min} (right-click → "
                + "Set priority…) so it loads after the dependency.";
        }
        if (c.Type != ConflictDetector.TYPE_FILE_OVERLAP)
            return "AI resolve only handles file_overlap conflicts in v1.";
#if AI_RESOLVER
        if (!_claude.IsAvailable)
            return "Claude Code not detected — install it to enable AI resolve.";
        return "Send this conflict to Claude Code for analysis.";
#else
        return "AI conflict resolution is not available in the Integrated edition.";
#endif
    }

#if AI_RESOLVER
    /// <summary>True if the conflict can be sent to Claude. v1 covers
    /// every file_overlap (Claude can merge any text file — README,
    /// INSTRUCTIONS, .gd, etc. — though .gd is where real merges
    /// happen). Other conflict types (autoload / hook / script_extend
    /// / class_name / take_over / super_chain) require either custom
    /// resolution prompts or aren't really merge candidates; we'll
    /// add them iteratively.</summary>
    private bool IsResolvable(ConflictDetector.Conflict c)
    {
        if (c.Type != ConflictDetector.TYPE_FILE_OVERLAP) return false;
        return _claude.IsAvailable;
    }
#endif

    /// <summary>True for any file_overlap regardless of Claude state —
    /// drives whether the button cell shows "Resolve" or stays blank.
    /// Click handler still re-checks IsResolvable() and surfaces a
    /// clear hint if Claude isn't available. Always false in the
    /// Integrated edition — the Resolve column stays empty.</summary>
    private static bool IsButtonRow(ConflictDetector.Conflict c)
#if AI_RESOLVER
        => c.Type == ConflictDetector.TYPE_FILE_OVERLAP;
#else
        => false;
#endif

    // --- async update check ---------------------------------------

    private async Task CheckUpdatesAsync(bool forceFresh = false)
    {
        // Use the persisted cache if it's still inside the 1h TTL —
        // saves a round-trip to ModWorkshop on every launch and
        // respects their no-spam policy. The Refresh toolbar button
        // forces a re-fetch.
        if (!forceFresh && _settings.IsCacheFresh)
        {
            _latestVersions.Clear();
            foreach (var (k, v) in _settings.CachedVersions)
                if (int.TryParse(k, out var id))
                    _latestVersions[id] = v;
            var ageMin = (int)Math.Round(_settings.CacheAge.TotalMinutes);
            _updatesLabel.Text =
                $"Updates: {SummarizeUpdates()}  (cached ~{ageMin} min ago — Refresh to recheck)";
            PopulateModsGrid();
            return;
        }

        var ids = _registry.Enabled()
            .Select(e => e.ModWorkshopId)
            .Where(i => i > 0)
            .ToList();
        if (ids.Count == 0)
        {
            _updatesLabel.Text = "Updates: no enabled mods have a ModWorkshop link";
            return;
        }
        _updatesLabel.Text = $"Updates: checking {ids.Count} mods on ModWorkshop ...";
        try
        {
            var versions = await _mw.CheckVersionsAsync(ids);
            _latestVersions.Clear();
            foreach (var (k, v) in versions) _latestVersions[k] = v;
            // Persist for the next launch.
            _settings.CachedVersions = _latestVersions
                .ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
            _settings.CacheTimestamp = DateTime.UtcNow.ToString("o");
            _settings.Save();
            _updatesLabel.Text = $"Updates: {SummarizeUpdates()}";
            PopulateModsGrid();
        }
        catch (Exception ex)
        {
            _updatesLabel.Text = $"Updates: check failed — {ex.Message}";
        }
    }

    private string SummarizeUpdates()
    {
        int outdated = 0, current = 0, unknown = 0, mismatch = 0;
        foreach (var e in _registry.Enabled())
        {
            var mw = e.ModWorkshopId;
            if (mw <= 0) continue;
            if (!_latestVersions.TryGetValue(mw, out var latest))
            { unknown++; continue; }
            if (latest == e.Version) { current++; continue; }
            // Ambiguous-format pair (e.g. mod.txt 0.0.420 vs MW
            // 0.4.20_R) — same release, two encodings — is a
            // separate category from "outdated".
            if (VersionsAreAmbiguous(e.Version, latest)) { mismatch++; continue; }
            outdated++;
        }
        return $"{outdated} outdated, {current} current"
            + (mismatch > 0 ? $", {mismatch} mismatch" : "")
            + $", {unknown} unknown";
    }

    // --- toolbar actions ------------------------------------------

    /// <summary>Bulk enable/disable a specific subset of mods —
    /// the right-click-on-multi-selection equivalent of the
    /// toolbar's all-mods BulkToggle. Locked mods in the
    /// selection are skipped; other already-in-target-state mods
    /// are no-op. One cfg save + one full refresh per batch
    /// instead of N partial writes.</summary>
    private void BulkToggleSelected(List<ModEntry> entries, bool enable)
    {
        if (entries.Count == 0) return;
        var candidates = entries.Where(e => e.IsEnabled != enable).ToList();
        var targets = candidates.Where(e => !IsLocked(e)).ToList();
        var skipped = candidates.Where(e => IsLocked(e)).ToList();
        if (targets.Count == 0)
        {
            _modsLabel.Text = enable
                ? $"Selection: all already enabled"
                  + (skipped.Count > 0 ? $" ({skipped.Count} locked, skipped)." : ".")
                : $"Selection: all already disabled"
                  + (skipped.Count > 0 ? $" ({skipped.Count} locked, skipped)." : ".");
            return;
        }
        var failed = 0;
        foreach (var e in targets)
            if (!ToggleModFiles(e)) failed++;
        SaveModConfigSafely();
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);

        var verb = enable ? "Enabled" : "Disabled";
        var skipNote = skipped.Count > 0
            ? $" ({skipped.Count} locked, skipped)"
            : "";
        var failNote = failed > 0 ? $" ({failed} failed)" : "";
        _modsLabel.Text = $"{verb} {targets.Count - failed} mod(s){skipNote}{failNote}.";
    }

    private void BulkToggle(bool enable)
    {
        // Mods that need toggling, partitioned into "free" (will be
        // toggled) and "locked" (skipped). Locked mods are surfaced
        // in the confirmation prompt so the user knows the bulk
        // action isn't fully comprehensive.
        var candidates = _registry.Entries.Where(e => e.IsEnabled != enable).ToList();
        var targets = candidates.Where(e => !IsLocked(e)).ToList();
        var skipped = candidates.Where(e => IsLocked(e)).ToList();
        if (targets.Count == 0)
        {
            if (skipped.Count > 0)
                _modsLabel.Text = enable
                    ? $"All non-locked mods are already enabled "
                      + $"({skipped.Count} locked, skipped)."
                    : $"All non-locked mods are already disabled "
                      + $"({skipped.Count} locked, skipped).";
            else
                _modsLabel.Text = enable
                    ? "All mods are already enabled."
                    : "All mods are already disabled.";
            return;
        }
        var verb = enable ? "Enable" : "Disable";
        // Show up to N mod names in the prompt so the user can spot
        // anything they didn't mean to touch. Bigger N = more noise;
        // 8 fits comfortably in the default MessageBox width.
        const int previewCount = 8;
        var preview = string.Join("\n",
            targets.Take(previewCount)
                   .Select(e => "  • " + (string.IsNullOrEmpty(e.DisplayName)
                       ? Path.GetFileName(e.Path)
                       : e.DisplayName)));
        if (targets.Count > previewCount)
            preview += $"\n  …and {targets.Count - previewCount} more";
        var skippedNote = skipped.Count > 0
            ? $"\n\n🔒 Skipping {skipped.Count} locked mod"
              + (skipped.Count == 1 ? "" : "s")
              + " — they'll keep their current state."
            : "";
        var dr = Ui.ThemedMessageBox.Show(this,
            $"{verb} all {targets.Count} {(enable ? "disabled" : "enabled")} mods?\n\n"
            + preview + skippedNote + "\n\n"
            + "Each mod's .vmz will be moved between <mods>/ and <mods>/Disabled/. "
            + "You can undo with the opposite bulk action.",
            $"{verb} all — confirm",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            // Default to No so a stray Enter cancels instead of
            // bulk-toggling the entire mod list.
            MessageBoxDefaultButton.Button2);
        if (dr != DialogResult.Yes) return;

        var failed = 0;
        foreach (var e in targets)
        {
            if (!ToggleModFiles(e)) failed++;
        }
        // Single cfg save for the whole batch — one .bak rotation,
        // one disk hit even for 50+ toggles.
        SaveModConfigSafely();
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
        if (failed > 0)
        {
            Ui.ThemedMessageBox.Show(this,
                $"{failed} of {targets.Count} mods couldn't be toggled. "
                + "(Likely because the .vmz file in mods/Disabled/ is "
                + "locked, or a mod has no mod_id in its mod.txt.)",
                "Bulk toggle finished with errors",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RefreshAllAsync()
    {
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
        await CheckUpdatesAsync(forceFresh: true);
    }

    /// <summary>Opens an OpenFileDialog seeded at the user's Downloads
    /// folder (where browsers drop ModWorkshop downloads by default)
    /// and installs every selected .vmz via InstallModFilesAsync.</summary>
    private async Task InstallModFromFilePickerAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Install mod from .vmz",
            Filter = "Vostok mod (*.vmz)|*.vmz|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var downloads = Path.Combine(home, "Downloads");
            if (Directory.Exists(downloads)) dlg.InitialDirectory = downloads;
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await InstallModFilesAsync(dlg.FileNames);
    }

    /// <summary>Copies one or more .vmz files into the mods folder,
    /// rejecting anything that doesn't look like a real Vostok mod
    /// archive. Skip-or-overwrite prompt for filename collisions.
    /// Single registry rescan + conflict redetect at the end so a
    /// drop of 20 files doesn't trigger 20 full refreshes.</summary>
    private async Task InstallModFilesAsync(IEnumerable<string> sources)
    {
        var modsDir = ModsDir;
        if (!Directory.Exists(modsDir))
        {
            Ui.ThemedMessageBox.Show(this,
                $"Mods folder `{modsDir}` doesn't exist. Set a valid "
                + "Mods folder above before installing.",
                "Mods folder not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var installed = new List<string>();
        var skipped = new List<(string path, string reason)>();
        // Manifest mod_ids of the freshly-installed mods — captured
        // here so the post-batch dependency scan can look them up
        // in the rescanned registry and grab their declared deps.
        var installedModIds = new List<string>();
        // Whether any install touched the active profile — controls
        // whether we save profile.json at the end of the batch.
        var profileDirty = false;
        // Whether any install wrote enable/priority entries into
        // mod_config.cfg. One save at the end of the batch instead
        // of N partial writes.
        var cfgDirty = false;
        // Compute full path of mods dir (case-insensitive comparable)
        // so we can detect "drop a file already in the mods folder."
        var modsDirFull = Path.GetFullPath(modsDir);

        foreach (var src in sources)
        {
            try
            {
                if (!File.Exists(src))
                    throw new FileNotFoundException("File not found.", src);
                if (!src.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Not a .vmz file.");

                // Validate: opens as a zip and contains mod.txt.
                // We need the manifest values (mod_id, version, etc.)
                // both for the library copy AND for the profile-add
                // step further down, so capture them here once.
                string  manifestModId, manifestVersion, manifestDisplayName;
                int     manifestPriority, manifestMwId;
                using (var arch = new ModArchive())
                {
                    if (!arch.Open(src))
                        throw new InvalidDataException(
                            "Couldn't open as a zip archive — probably "
                            + "corrupt or wrong file type.");
                    if (!arch.HasFile("mod.txt"))
                        throw new InvalidDataException(
                            "No mod.txt inside the archive — doesn't "
                            + "look like a Vostok mod.");
                    manifestModId       = arch.ModId;
                    manifestVersion     = arch.ModVersion;
                    manifestDisplayName = string.IsNullOrEmpty(arch.ModName)
                                            ? arch.ModId : arch.ModName;
                    manifestPriority    = arch.ModPriority;
                    manifestMwId        = arch.ModWorkshopId;
                }

                var dst = Path.Combine(modsDir, Path.GetFileName(src));
                var srcInsideMods = string.Equals(
                    Path.GetFullPath(src),
                    Path.GetFullPath(dst),
                    StringComparison.OrdinalIgnoreCase);

                if (srcInsideMods)
                {
                    // Source already lives in `<mods>/`. We don't need
                    // to copy — but we DO still want to capture it in
                    // the library and add it to the active profile so
                    // this file participates in the new model.
                    installed.Add(Path.GetFileName(src));
                }
                else
                {
                    if (File.Exists(dst))
                    {
                        var dr = Ui.ThemedMessageBox.Show(this,
                            $"`{Path.GetFileName(src)}` already exists in the "
                            + "mods folder. Overwrite the existing file?",
                            "File exists",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2);
                        if (dr != DialogResult.Yes)
                        {
                            skipped.Add((src, "exists, user declined overwrite"));
                            continue;
                        }
                    }
                    // File.Copy is fast for typical mod sizes (<10MB) but
                    // run on a thread pool so a slow disk doesn't freeze
                    // the UI mid-batch.
                    await Task.Run(() => File.Copy(src, dst, overwrite: true));
                    installed.Add(Path.GetFileName(src));
                }

                // Track manifest mod_id so the post-install dep
                // scan can find this mod in the rescanned registry.
                if (!string.IsNullOrEmpty(manifestModId))
                    installedModIds.Add(manifestModId);

                // Library capture — idempotent on (mod_id, version).
                // Failure here doesn't block the install; the live
                // file is already in place and the mod will still
                // load. Library will catch up on the next profile
                // switch's ensure-in-lib pass.
                try { Domain.ModLibrary.Add(modsDir, dst); }
                catch { /* best-effort; non-fatal */ }

                // Active-profile auto-add. New mods land enabled with
                // their manifest's declared priority. Existing entries
                // (same mod_id) get their Version updated so the
                // profile points at the freshly-installed copy.
                if (_activeProfile != null
                    && !string.IsNullOrEmpty(manifestModId))
                {
                    var existingPm = _activeProfile.Mods.FirstOrDefault(m =>
                        string.Equals(m.ModId, manifestModId,
                            StringComparison.OrdinalIgnoreCase));
                    if (existingPm == null)
                    {
                        _activeProfile.Mods.Add(new Domain.ProfileMod
                        {
                            ModId         = manifestModId,
                            DisplayName   = manifestDisplayName,
                            Version       = manifestVersion,
                            IsEnabled     = true,
                            Priority      = manifestPriority,
                            ModWorkshopId = manifestMwId,
                        });
                    }
                    else
                    {
                        existingPm.Version     = manifestVersion;
                        existingPm.DisplayName = manifestDisplayName;
                        if (manifestMwId > 0) existingPm.ModWorkshopId = manifestMwId;
                    }
                    profileDirty = true;
                }

                // mod_config.cfg WRITE — must be explicit. The
                // manager's grid uses `fallback=true` when cfg
                // has no entry for a mod (so a fresh install
                // displays as enabled), but the in-game loader
                // does NOT apply that fallback — without a real
                // [profile.<active>.enabled] mod_id@ver = true
                // line, MML leaves the mod OFF at runtime.
                // Result before this fix: the grid shows the
                // freshly-installed mod as enabled while it
                // silently stays disabled in-game. Set BOTH
                // enabled and priority cells so a re-launch
                // picks the mod up correctly.
                if (!string.IsNullOrEmpty(manifestModId))
                {
                    _modConfig.SetEnabled(manifestModId, manifestVersion, true);
                    _modConfig.SetPriority(manifestModId, manifestVersion, manifestPriority);
                    cfgDirty = true;
                }
            }
            catch (Exception ex)
            {
                skipped.Add((src, ex.Message));
            }
        }

        // Persist the active profile once for the whole batch — a
        // drop of 20 mods is a single profile.json write rather
        // than 20 partial ones.
        if (profileDirty && _activeProfile != null)
        {
            _activeProfile.UpdatedAt = DateTime.UtcNow;
            try { _activeProfile.SaveMetadataOnly(); }
            catch { /* best-effort; in-memory state still reflects the change */ }
        }
        // Persist cfg once for the batch. Without this the
        // freshly-installed mods never appear in mod_config.cfg
        // and the in-game loader can't see them as enabled.
        if (cfgDirty) SaveModConfigSafely();

        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);

        if (installed.Count > 0)
            _modsLabel.Text =
                $"Installed {installed.Count} mod"
                + (installed.Count == 1 ? "" : "s")
                + (skipped.Count > 0 ? $" ({skipped.Count} skipped)" : "")
                + ".";
        else if (skipped.Count > 0)
            _modsLabel.Text =
                $"No mods installed ({skipped.Count} skipped).";

        if (skipped.Count > 0)
        {
            var preview = string.Join("\n",
                skipped.Take(8).Select(s => $"  • {Path.GetFileName(s.path)}: {s.reason}"));
            if (skipped.Count > 8)
                preview += $"\n  …and {skipped.Count - 8} more";
            Ui.ThemedMessageBox.Show(this,
                $"{installed.Count} installed; {skipped.Count} skipped.\n\n"
                + preview,
                "Install report",
                MessageBoxButtons.OK,
                installed.Count > 0
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning);
        }

        // Dependency follow-up — scan the just-installed mods for
        // declared [dependencies] entries, auto-copy from library
        // where possible, prompt for genuinely-missing required/
        // optional deps.
        if (installedModIds.Count > 0)
            await CheckAndPromptMissingDepsAsync(installedModIds);
    }

    /// <summary>For each freshly-installed mod_id, scan its
    /// [dependencies] required/optional lists and reconcile the
    /// gap against the live registry + local library. Library-
    /// available deps are copied into <mods>/ automatically; the
    /// remaining missing ones surface via MissingDependenciesDialog
    /// so the user can paste MW URLs / ids and trigger downloads.
    ///
    /// Called from BOTH the install-mod flow (drop / picker) and
    /// the import-list flow — they're the two entry points where
    /// new mods land and may carry unsatisfied deps. Profile apply
    /// has its own dep handling baked into the plan.</summary>
    public async Task CheckAndPromptMissingDepsAsync(IEnumerable<string> newlyInstalledModIds)
    {
        var ids = newlyInstalledModIds
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0) return;

        // Index live entries by mod_id for the "is this dep already
        // installed?" check.
        var liveById = new Dictionary<string, ModEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var e in _registry.Entries)
            if (!string.IsNullOrEmpty(e.ModId)) liveById[e.ModId] = e;

        // Library snapshot keyed by mod_id (newest version per id).
        var libByMod = new Dictionary<string, Domain.ModLibrary.Entry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var le in Domain.ModLibrary.List(ModsDir))
        {
            if (string.IsNullOrEmpty(le.ModId)) continue;
            if (!libByMod.TryGetValue(le.ModId, out var cur)
                || Domain.ModRegistry.CompareVersions(le.Version, cur.Version) > 0)
                libByMod[le.ModId] = le;
        }

        // Collect every (parent, dep_id, required?) tuple from the
        // newly-installed set, deduped on dep_id (case-insensitive)
        // — the first parent that named a dep wins the "needed by"
        // label, which is fine because we just need ONE attribution
        // for the user to recognise the chain.
        //
        // ALSO collect parents' [dependency_sources] mappings into
        // a single dep_id → MW id dictionary. This is the bridge
        // that lets us auto-download a missing dep without round-
        // tripping to the user for a URL — when a mod was packed
        // with the Mod Packager, its mod.txt carries the MW ids
        // of every declared dep. Later parents overwrite earlier
        // ones (rare collision; last-wins is arbitrary but stable).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(string DepId, string ParentName, bool Required)>();
        var knownMwIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var modId in ids)
        {
            if (!liveById.TryGetValue(modId, out var entry)) continue;
            var parentLabel = string.IsNullOrEmpty(entry.DisplayName)
                ? entry.ModId : entry.DisplayName;
            foreach (var dep in entry.RequiredDependencies)
            {
                if (!seen.Add(dep)) continue;
                pending.Add((dep, parentLabel, true));
            }
            foreach (var dep in entry.OptionalDependencies)
            {
                if (!seen.Add(dep)) continue;
                pending.Add((dep, parentLabel, false));
            }
            foreach (var kvp in entry.DependencySources)
                knownMwIds[kvp.Key] = kvp.Value;
        }
        if (pending.Count == 0) return;

        // First pass: auto-resolve from library. Skip any dep that
        // already has a live entry (a chained dep declared by two
        // separate parents where one parent already installed it).
        var libraryResolved = 0;
        var missing = new List<MissingDependenciesDialog.MissingDep>();
        foreach (var (depId, parent, required) in pending)
        {
            if (liveById.ContainsKey(depId)) continue;
            if (libByMod.TryGetValue(depId, out var libEntry))
            {
                try
                {
                    var liveName = $"{Domain.ModProfile.SafeFileName(depId)}.vmz";
                    var dst      = Path.Combine(ModsDir, liveName);
                    if (!File.Exists(dst))
                    {
                        File.Copy(libEntry.Path, dst, overwrite: false);
                        libraryResolved++;
                    }
                    continue;
                }
                catch { /* fall through into "missing" — let user retry */ }
            }
            missing.Add(new MissingDependenciesDialog.MissingDep
            {
                ModId      = depId,
                ParentName = parent,
                Required   = required,
            });
        }

        // Refresh the registry if we copied anything from library
        // so the UI reflects the new files before the dialog opens.
        if (libraryResolved > 0)
        {
            Rescan();
            UpdateModsStatus();
            PopulateModsGrid();
            // Re-check missing: a freshly-library-copied dep might
            // ALSO satisfy another dep entry that was previously
            // listed as missing. Rebuild liveById and re-filter.
            liveById = new Dictionary<string, ModEntry>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var e in _registry.Entries)
                if (!string.IsNullOrEmpty(e.ModId)) liveById[e.ModId] = e;
            missing = missing.Where(m => !liveById.ContainsKey(m.ModId)).ToList();
        }

        if (missing.Count == 0)
        {
            if (libraryResolved > 0)
                _modsLabel.Text =
                    $"Auto-resolved {libraryResolved} dependency mod"
                    + (libraryResolved == 1 ? "" : "s")
                    + " from your library.";
            return;
        }

        // Ask first — don't pop a busy dialog at the user without
        // warning. Lets them keep working if they'd rather sort
        // deps later. Required-vs-optional shown for triage.
        int reqCount = missing.Count(m => m.Required);
        int optCount = missing.Count - reqCount;
        var prompt = $"The freshly-installed mod"
                   + (ids.Count == 1 ? "" : "s")
                   + " declared "
                   + (reqCount > 0 ? $"{reqCount} required" : "")
                   + (reqCount > 0 && optCount > 0 ? " + " : "")
                   + (optCount > 0 ? $"{optCount} optional" : "")
                   + " dependency mod"
                   + ((reqCount + optCount) == 1 ? "" : "s")
                   + " that aren't installed"
                   + (libraryResolved > 0
                       ? $" ({libraryResolved} other dep(s) auto-copied from your library)."
                       : ".")
                   + "\n\nOpen the Resolve dialog to paste ModWorkshop URLs and download them?";
        var answer = Ui.ThemedMessageBox.Show(this, prompt,
            "Missing dependencies",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (answer != DialogResult.Yes) return;

        bool downloadedAny;
        using (var dlg = new Ui.MissingDependenciesDialog(
            _mw, ModsDir, missing, knownMwIds))
        {
            dlg.ShowDialog(this);
            downloadedAny = dlg.AnyDownloaded;
        }

        if (downloadedAny)
        {
            Rescan();
            UpdateModsStatus();
            PopulateModsGrid();
            _lastConflicts = DetectConflictsForActive();
            UpdateConflictsStatus(_lastConflicts);
            PopulateConflictsList(_lastConflicts);
            // Recurse — the newly-downloaded deps may themselves
            // declare deps. One extra pass keeps the chain
            // resolving without an infinite loop (the recursion
            // terminates when ALL declared deps are present).
            var newIds = _registry.Entries
                .Where(e => missing.Any(m => string.Equals(
                    m.ModId, e.ModId, StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.ModId)
                .ToList();
            if (newIds.Count > 0)
                await CheckAndPromptMissingDepsAsync(newIds);
        }
    }

    // --- decomp auto-detect ---------------------------------------

    private static string AutodetectDecomp()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(home))
        {
            candidates.Add(Path.Combine(home, "Desktop", "RoadToVostok Dev", "Decomp"));
            candidates.Add(Path.Combine(home, "Desktop", "Decomp"));
        }
        if (!string.IsNullOrEmpty(docs))
            candidates.Add(Path.Combine(docs, "RoadToVostok_Decomp"));
        foreach (var c in candidates)
            if (LooksLikeDecomp(c)) return c;
        return "";
    }

    /// <summary>True if `path` contains the landmark files we expect
    /// in a real Decomp/ — Scripts/Loader.gd and Scripts/Interface.gd
    /// are both shipped by Road to Vostok and not by anything else.</summary>
    private static bool LooksLikeDecomp(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
        return File.Exists(Path.Combine(path, "Scripts", "Loader.gd"))
            && File.Exists(Path.Combine(path, "Scripts", "Interface.gd"));
    }

    // --- per-row actions -------------------------------------------

    private async Task OnGridCellClickedAsync(DataGridViewCellEventArgs e)
    {
        var entry = ModAtRow(e.RowIndex);
        if (entry == null) return; // header row or out-of-range
        if (_busy) return;
        var col = _modsGrid.Columns[e.ColumnIndex].Name;
        switch (col)
        {
            case "Update":
                if (IsOutdated(entry))
                    await UpdateModAsync(entry);
                // The muted "—" state means we have no ModWorkshop ID
                // for this mod. Repurpose the click to "tell me the
                // ID" — most discoverable place to fix the missing
                // link.
                else if (entry.ModWorkshopId <= 0)
                    await SetModWorkshopIdAsync(entry);
                // The ≠ state: the local mod.txt and ModWorkshop's
                // listing format the version differently. Clicking
                // is informational — show the explainer in the
                // status row instead of silently doing nothing
                // (re-downloading the same file would be wasteful).
                else if (entry.ModWorkshopId > 0
                    && _latestVersions.TryGetValue(entry.ModWorkshopId, out var latest)
                    && !string.IsNullOrEmpty(latest)
                    && VersionsAreAmbiguous(entry.Version, latest))
                {
                    _updatesLabel.Text =
                        $"≠ {entry.DisplayName}: mod.txt v{entry.Version} "
                        + $"vs ModWorkshop v{latest} — same release written "
                        + "two ways. Local file matches what MW serves; no "
                        + "download needed. (Hover the ≠ icon for the long "
                        + "explanation.)";
                }
                break;
        }
    }

    /// <summary>Fires when the user edits a cell in-place. Two
    /// columns are editable: the Enabled checkbox (toggle-mod flow,
    /// writes mod_config.cfg) and the Priority text cell (writes
    /// the cfg's [profile.&lt;active&gt;.priority] block).</summary>
    private void OnGridCellValueChanged(DataGridViewCellEventArgs e)
    {
        if (_populatingMods) return;
        var entry = ModAtRow(e.RowIndex);
        if (entry == null) return; // pack-header row — skip
        if (e.ColumnIndex < 0 || e.ColumnIndex >= _modsGrid.Columns.Count) return;
        if (_busy) return;
        var col = _modsGrid.Columns[e.ColumnIndex].Name;

        if (col == "Enabled")
        {
            var cellValue = _modsGrid.Rows[e.RowIndex].Cells["Enabled"].Value;
            var nowChecked = cellValue is bool b && b;
            if (nowChecked == entry.IsEnabled) return;
            // Defer the registry rescan + grid rebuild so the
            // CellValueChanged callback can return first. Tearing
            // down rows synchronously inside this handler leaves
            // DataGridView's edit-state machine half-committed —
            // the checkbox's visual tick doesn't redraw until the
            // user clicks somewhere else. BeginInvoke posts the
            // work back to the message loop, after WinForms has
            // finished its internal commit cycle.
            BeginInvoke(() => ToggleMod(entry));
            return;
        }

        if (col == "Priority")
        {
            var raw = _modsGrid.Rows[e.RowIndex].Cells["Priority"]
                .Value?.ToString()?.Trim() ?? "";
            if (!int.TryParse(raw, out var newPriority))
            {
                Ui.ThemedMessageBox.Show(this,
                    $"`{raw}` isn't a valid priority. Must be a whole "
                    + "integer (negative is fine). Reverting.",
                    "Invalid priority",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _populatingMods = true;
                try { _modsGrid.Rows[e.RowIndex].Cells["Priority"].Value
                    = entry.Priority.ToString(); }
                finally { _populatingMods = false; }
                return;
            }
            if (newPriority == entry.Priority) return;
            // Priority lives in mod_config.cfg now — the in-game
            // loader's [profile.<active>.priority] section overrides
            // any [mod] priority in mod.txt. Editing mod.txt would
            // be a no-op against the running game.
            if (string.IsNullOrEmpty(entry.ModId))
            {
                Ui.ThemedMessageBox.Show(this,
                    $"`{Path.GetFileName(entry.Path)}` has no mod_id in "
                    + "its mod.txt — can't write a priority entry without "
                    + "an ID to key off.",
                    "Can't set priority",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _populatingMods = true;
                try { _modsGrid.Rows[e.RowIndex].Cells["Priority"].Value
                    = entry.Priority.ToString(); }
                finally { _populatingMods = false; }
                return;
            }
            var oldPriority = entry.Priority;
            _modConfig.SetPriority(entry.ModId, entry.Version, newPriority);
            if (!SaveModConfigSafely())
            {
                _populatingMods = true;
                try { _modsGrid.Rows[e.RowIndex].Cells["Priority"].Value
                    = oldPriority.ToString(); }
                finally { _populatingMods = false; }
                return;
            }
            _modsLabel.Text =
                $"`{entry.DisplayName}` priority {oldPriority} → {newPriority}.";
            Rescan();
            // Push the new priority into the active profile's JSON
            // so it sticks across profile switches.
            SyncActiveProfileFromEntry(entry.ModId);
            UpdateModsStatus();
            PopulateModsGrid();
            return;
        }
    }

    private void ToggleMod(ModEntry e)
    {
        // Capture the direction BEFORE toggling — ToggleModFiles
        // flips state based on the current e.IsEnabled, which is
        // still pre-toggle here.
        var enabling = !e.IsEnabled;
        if (!ToggleModFiles(e))
        {
            ShowError("Toggle failed",
                new Exception($"Couldn't toggle `{e.DisplayName}` — "
                    + "the file is locked or the mod has no mod_id. "
                    + "Quit Road to Vostok and any antivirus scan, "
                    + "then try again."));
            return;
        }
        // Enabling a mod with dependencies: pull any disabled
        // required deps on too (transitively), so the user never
        // ends up with an enabled mod whose prerequisites are off.
        var autoEnabled = new List<ModEntry>();
        if (enabling)
            autoEnabled = EnableRequiredDependencies(e);
        if (!SaveModConfigSafely()) return;
        if (autoEnabled.Count > 0)
        {
            var names = string.Join(", ", autoEnabled.Select(d =>
                string.IsNullOrEmpty(d.DisplayName) ? d.ModId : d.DisplayName));
            _modsLabel.Text =
                $"Enabled `{e.DisplayName}` + {autoEnabled.Count} "
                + $"required dependenc{(autoEnabled.Count == 1 ? "y" : "ies")}: {names}.";
        }
        // Rescan + redetect conflicts (different enabled set may have
        // a different conflict set).
        Rescan();
        // Mirror the new enabled state into the active profile's
        // JSON so a profile switch later doesn't lose this toggle.
        SyncActiveProfileFromEntry(e.ModId);
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
    }

    /// <summary>When a mod is being enabled, walk its declared
    /// REQUIRED dependencies and flip any that are currently
    /// installed-but-disabled to enabled too — transitively, so a
    /// dep-of-a-dep also comes on. Returns the entries that were
    /// actually flipped (for the caller's status message).
    ///
    /// Rules:
    ///   • Only REQUIRED deps are auto-enabled. Optional deps are
    ///     "nice to have" by definition — forcing them on would
    ///     overstep.
    ///   • Deps that aren't installed locally are skipped silently
    ///     here; the existing missing-dependency flow (post-install
    ///     scan) handles fetching those.
    ///   • LOCKED deps are left untouched — a lock means "don't
    ///     change this mod's state", which outranks the convenience
    ///     of auto-enabling.
    ///   • Doesn't save the cfg — the caller batches that into one
    ///     SaveModConfigSafely with the root toggle.</summary>
    private List<ModEntry> EnableRequiredDependencies(ModEntry root)
    {
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _registry.Entries)
            if (!string.IsNullOrEmpty(m.ModId) && !byId.ContainsKey(m.ModId))
                byId[m.ModId] = m;

        var enabled = new List<ModEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(root.ModId)) visited.Add(root.ModId);

        var queue = new Queue<ModEntry>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var depId in cur.RequiredDependencies)
            {
                if (string.IsNullOrEmpty(depId)) continue;
                if (!visited.Add(depId)) continue;      // already handled
                if (!byId.TryGetValue(depId, out var dep)) continue; // not installed
                // Recurse regardless of current enabled state so a
                // chain like A→B(on)→C(off) still reaches C.
                queue.Enqueue(dep);
                if (dep.IsEnabled) continue;            // already on
                if (IsLocked(dep)) continue;            // lock wins
                if (ToggleModFiles(dep))
                    enabled.Add(dep);
            }
        }
        return enabled;
    }

    /// <summary>Toggles a mod's enabled state via mod_config.cfg
    /// (the in-game loader's source of truth) — and, when enabling
    /// a mod that's currently sitting in &lt;mods&gt;/Disabled/, also
    /// moves the file back to &lt;mods&gt;/ so the loader can discover
    /// it. Disabling never moves files anymore: cfg=false is enough,
    /// and leaving the file in &lt;mods&gt;/ matches what the in-game
    /// loader UI does. mods/Disabled/ shrinks naturally as the
    /// user re-enables legacy disables.
    ///
    /// Doesn't save the cfg here — caller batches that via
    /// SaveModConfigSafely so a 50-file BulkToggle is one rotation,
    /// not 50. Returns false when the move fails so the caller can
    /// count errors.</summary>
    private bool ToggleModFiles(ModEntry e)
    {
        if (string.IsNullOrEmpty(e.ModId))
        {
            // No mod_id → can't key cfg state. Surface the issue
            // instead of silently doing nothing.
            return false;
        }
        var enabling = !e.IsEnabled;
        if (enabling)
        {
            // Move out of Disabled/ if necessary so the loader can
            // discover it. Files already in mods/ stay put.
            if (IsUnderDisabledFolder(e.Path))
            {
                var fileName = Path.GetFileName(e.Path);
                var dst = Path.Combine(ModsDir, fileName);
                try
                {
                    if (e.IsArchive) File.Move(e.Path, dst);
                    else Directory.Move(e.Path, dst);
                    e.Path = dst;
                }
                catch
                {
                    return false;
                }
            }
            _modConfig.SetEnabled(e.ModId, e.Version, true);
            // Refresh priority entry too if missing — declared
            // priority becomes the cfg priority on first enable so
            // future cfg-only flows see a stable value.
            if (!_modConfig.HasEntry(e.ModId, e.Version))
                _modConfig.SetPriority(e.ModId, e.Version, e.DeclaredPriority);
        }
        else
        {
            _modConfig.SetEnabled(e.ModId, e.Version, false);
        }
        return true;
    }

    private bool IsUnderDisabledFolder(string entryPath)
    {
        var disabled = Path.Combine(ModsDir, "Disabled");
        try
        {
            var entryFull = Path.GetFullPath(entryPath);
            var disabledFull = Path.GetFullPath(disabled);
            return entryFull.StartsWith(
                disabledFull + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Open the mod's ModWorkshop page in the user's default
    /// browser. No-op (with a status message) if the mod has no MW ID
    /// linked — the context menu's Open item is greyed in that case,
    /// so this only triggers if something else called it.</summary>
    private void OpenModPage(ModEntry e)
    {
        if (e.ModWorkshopId <= 0)
        {
            _modsLabel.Text = $"`{e.DisplayName}` has no ModWorkshop ID linked.";
            return;
        }
        var url = $"https://modworkshop.net/mod/{e.ModWorkshopId}";
        try
        {
            // UseShellExecute = true so the OS resolves the default
            // browser. ProcessStartInfo with a bare URL would fail on
            // .NET Core without this flag.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowError("Couldn't open browser", ex);
        }
    }

    /// <summary>Reveal the mod's file (or directory) in Windows
    /// Explorer. For a .vmz / .zip we open Explorer with the file
    /// pre-selected (`/select,`); for a mod directory we open the
    /// folder itself. Falls back to opening the parent directory if
    /// the exact path no longer exists (e.g. it was deleted out from
    /// under us between scan and click).</summary>
    private void ShowModInFolder(ModEntry e)
    {
        var path = e.Path;
        if (string.IsNullOrEmpty(path))
        {
            _modsLabel.Text = $"`{e.DisplayName}` has no on-disk path.";
            return;
        }
        try
        {
            if (File.Exists(path))
            {
                // /select, highlights the file inside its folder. The
                // path MUST be quoted — spaces in the mods dir (common
                // under "Program Files") otherwise truncate the arg.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName  = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
            else if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName  = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                // Path's gone — open the parent so the user lands
                // somewhere useful rather than getting an error.
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName  = "explorer.exe",
                        Arguments = $"\"{parent}\"",
                        UseShellExecute = true,
                    });
                }
                else
                {
                    _modsLabel.Text = $"`{e.DisplayName}` is no longer on disk.";
                }
            }
        }
        catch (Exception ex)
        {
            ShowError("Couldn't open Explorer", ex);
        }
    }

    /// <summary>Opens the description dialog for a mod. Pre-fills
    /// from `_settings.CachedDescriptions` so subsequent opens are
    /// instant; the dialog kicks off a fresh fetch when there's
    /// nothing cached. The cache callback persists fresh fetches
    /// back into settings.json so the next launch sees them too.</summary>
    private void ShowDescription(ModEntry e)
    {
        var key = e.ModWorkshopId.ToString();
        _settings.CachedDescriptions.TryGetValue(key, out var cached);
        using var dlg = new DescriptionDialog(
            e, _mw, cached ?? "",
            onCached: text =>
            {
                _settings.CachedDescriptions[key] = text;
                try { _settings.Save(); }
                catch { /* description cache is best-effort */ }
            });
        dlg.ShowDialog(this);
    }

    /// <summary>Prompts for a ModWorkshop ID (or URL — we parse either)
    /// and rewrites the mod's mod.txt to set [updates] modworkshop = N.
    /// Backs up archive mods to a .bak first; for directory mods we
    /// just overwrite mod.txt in place. Refreshes the registry on
    /// success so the Update column re-renders with the new state.</summary>
    /// <summary>Reads the mod's mod.txt, applies `transform` to the
    /// content, and writes it back — handling the archive vs
    /// directory-mod split, .bak backups, and error reporting in one
    /// place. Returns the success message line on success (so the
    /// caller can decide what to do with it) or null on failure.</summary>
    private async Task<string?> EditModTxtAsync(ModEntry e, Func<string, string> transform)
    {
        try
        {
            if (e.IsArchive)
            {
                string oldText;
                using (var arch = new ModArchive())
                {
                    if (!arch.Open(e.Path))
                        throw new IOException("Failed to open the .vmz archive for reading.");
                    oldText = arch.ReadText("mod.txt");
                    if (string.IsNullOrEmpty(oldText))
                        throw new InvalidDataException("mod.txt is missing or empty inside the archive.");
                }
                var newText = transform(oldText);
                var backup = ZipPatcher.CreateBackup(e.Path);
                ZipPatcher.ReplaceEntry(e.Path, "mod.txt", newText);
                return $"Backup: {Path.GetFileName(backup)}";
            }
            else
            {
                var modTxtPath = Path.Combine(e.Path, "mod.txt");
                if (!File.Exists(modTxtPath))
                    throw new FileNotFoundException("mod.txt not found in the mod folder.", modTxtPath);
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var bakPath = $"{modTxtPath}.{stamp}.bak";
                File.Copy(modTxtPath, bakPath, overwrite: false);
                var newText = transform(await File.ReadAllTextAsync(modTxtPath));
                await File.WriteAllTextAsync(modTxtPath, newText);
                return $"Backup: {Path.GetFileName(bakPath)}";
            }
        }
        catch (Exception ex)
        {
            ShowError("Couldn't update mod.txt", ex);
            return null;
        }
    }

    private async Task SetModWorkshopIdAsync(ModEntry e)
    {
        var current = e.ModWorkshopId > 0 ? e.ModWorkshopId.ToString() : "";
        var prompt =
            $"Enter the ModWorkshop ID for `{e.DisplayName}`.\n\n"
            + "Accepts either a numeric ID (e.g. 56398) or the full mod URL "
            + "(e.g. https://modworkshop.net/mod/56398/optional-slug).\n\n"
            + "This rewrites the mod's mod.txt to add or update its "
            + "[updates] modworkshop = N field.";
        var input = TextInputDialog.Prompt(this, "Set ModWorkshop ID", prompt, current);
        if (input == null) return;
        var newId = ManifestEditor.ParseModWorkshopIdInput(input);
        if (newId <= 0)
        {
            Ui.ThemedMessageBox.Show(this,
                $"Couldn't parse a ModWorkshop ID from:\n  {input}\n\n"
                + "Expected a positive integer or a URL like "
                + "https://modworkshop.net/mod/56398.",
                "Invalid input",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (newId == e.ModWorkshopId)
        {
            _modsLabel.Text = $"`{e.DisplayName}` already linked to ModWorkshop {newId}.";
            return;
        }
        var bak = await EditModTxtAsync(e, txt => ManifestEditor.SetUpdatesModworkshop(txt, newId));
        if (bak == null) return;
        _modsLabel.Text = $"Linked `{e.DisplayName}` → ModWorkshop {newId}. {bak}";

        // Re-scan + re-check updates so the new link kicks in.
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        await CheckUpdatesAsync(forceFresh: true);
    }

    /// <summary>Prompts for a new load-order priority and rewrites
    /// `[mod] priority = N` in mod.txt. Lower numbers load earlier.
    /// Negative values are allowed — useful for pinning a mod above
    /// the default-0 priority of stock mods.</summary>
    /// <summary>Writes a new load-order priority for the mod into
    /// mod_config.cfg's [profile.&lt;active&gt;.priority] section. We
    /// do NOT write to mod.txt here — the in-game loader's cfg
    /// overrides any [mod] priority in mod.txt, so a mod.txt edit
    /// would be silent against the running game. The mod-author's
    /// declared priority remains as a fallback when the cfg has
    /// no entry.</summary>
    private Task SetModPriorityAsync(ModEntry e)
    {
        var current = e.Priority.ToString();
        var prompt =
            $"Enter the load-order priority for `{e.DisplayName}`.\n\n"
            + "Lower numbers load earlier; default is 0. Negative "
            + "values are fine (e.g. -100 to pin above everything "
            + "else).\n\n"
            + "Writes to mod_config.cfg's "
            + $"[profile.{_modConfig.ActiveProfile}.priority] block — "
            + "the same key the in-game loader UI edits.";
        var input = TextInputDialog.Prompt(this, "Set priority", prompt, current);
        if (input == null) return Task.CompletedTask;
        if (!int.TryParse(input.Trim(), out var newPriority))
        {
            Ui.ThemedMessageBox.Show(this,
                $"Couldn't parse `{input}` as an integer. Priority must "
                + "be a whole number (positive, negative, or zero).",
                "Invalid input",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Task.CompletedTask;
        }
        if (newPriority == e.Priority)
        {
            _modsLabel.Text = $"`{e.DisplayName}` priority is already {newPriority}.";
            return Task.CompletedTask;
        }
        if (string.IsNullOrEmpty(e.ModId))
        {
            Ui.ThemedMessageBox.Show(this,
                $"`{Path.GetFileName(e.Path)}` has no mod_id in mod.txt — "
                + "can't write a priority entry without an ID to key off.",
                "Can't set priority",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Task.CompletedTask;
        }
        var oldPriority = e.Priority;
        _modConfig.SetPriority(e.ModId, e.Version, newPriority);
        if (!SaveModConfigSafely()) return Task.CompletedTask;
        _modsLabel.Text =
            $"`{e.DisplayName}` priority {oldPriority} → {newPriority}.";

        Rescan();
        // Mirror to the active profile so the new priority is
        // captured before any later profile switch wipes the cfg.
        SyncActiveProfileFromEntry(e.ModId);
        UpdateModsStatus();
        PopulateModsGrid();
        // Re-detect — priority changes can resolve dependency_order
        // conflicts.
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
        return Task.CompletedTask;
    }

    /// <summary>Picks required dependencies from a checklist of
    /// installed mods (with a manual-add box for not-yet-installed
    /// mod IDs), then rewrites `[dependencies] required = ...` in
    /// mod.txt. An empty selection clears the field. Optional deps
    /// aren't editable here yet — the "user wants to declare a hard
    /// dependency" use case is much more common.</summary>
    private async Task SetModDependenciesAsync(ModEntry e)
    {
        var newDeps = DependencyPickerDialog.Pick(
            this, e, _registry.Entries, e.RequiredDependencies);
        if (newDeps == null) return;
        // Set comparison — the picker returns ticks in click order,
        // not the order they were declared in mod.txt, so positional
        // diff would falsely report a change after every dialog
        // close. Order doesn't matter semantically for deps anyway.
        var existing = new HashSet<string>(
            e.RequiredDependencies, StringComparer.OrdinalIgnoreCase);
        var picked = new HashSet<string>(
            newDeps, StringComparer.OrdinalIgnoreCase);
        if (existing.SetEquals(picked))
        {
            _modsLabel.Text = $"`{e.DisplayName}` dependencies unchanged.";
            return;
        }

        // Order check — among the deps the user JUST added, find any
        // installed dep whose priority is >= the subject's. If found,
        // offer to bump the subject above the highest-priority new
        // dep so the load order will satisfy the new declaration.
        // Only newly-added deps matter here: pre-existing violations
        // would already be flagged in the conflicts panel.
        int? bumpTo = null;
        var added = picked.Except(existing, StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count > 0)
        {
            var registryById = new Dictionary<string, ModEntry>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _registry.Entries)
            {
                if (string.IsNullOrEmpty(entry.ModId)) continue;
                registryById.TryAdd(entry.ModId, entry);
            }
            var blockers = added
                .Where(id => registryById.ContainsKey(id))
                .Select(id => registryById[id])
                .Where(d => d.Priority >= e.Priority)
                .ToList();
            if (blockers.Count > 0)
            {
                var maxBlocker = blockers.OrderByDescending(d => d.Priority).First();
                var newPrio = maxBlocker.Priority + 1;
                var blockerNames = string.Join(", ",
                    blockers
                        .OrderByDescending(d => d.Priority)
                        .Select(d => $"{d.ModId} (prio {d.Priority})"));
                var dr = Ui.ThemedMessageBox.Show(this,
                    $"`{e.DisplayName}` is at priority {e.Priority}, but "
                    + $"now requires:\n  {blockerNames}\n\n"
                    + "Dependents must load AFTER their dependencies, so "
                    + $"`{e.DisplayName}`'s priority needs to be > "
                    + $"{maxBlocker.Priority}.\n\n"
                    + $"Bump `{e.DisplayName}`'s priority "
                    + $"{e.Priority} → {newPrio} as part of this edit?",
                    "Bump priority?",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (dr == DialogResult.Cancel) return;
                if (dr == DialogResult.Yes) bumpTo = newPrio;
                // No → write deps only; the conflicts panel will
                // surface the dependency_order violation so the user
                // can fix it later.
            }
        }

        // Deps still live in mod.txt (no cfg equivalent), but
        // priority moved to mod_config.cfg. So we do TWO writes:
        // mod.txt for the new dep list, cfg for the optional bump.
        var bak = await EditModTxtAsync(e, txt =>
            ManifestEditor.SetDependencyList(txt, "required", newDeps));
        if (bak == null) return;
        if (bumpTo.HasValue && !string.IsNullOrEmpty(e.ModId))
        {
            _modConfig.SetPriority(e.ModId, e.Version, bumpTo.Value);
            SaveModConfigSafely();
        }
        var depsMsg = newDeps.Count == 0
            ? $"Cleared `{e.DisplayName}` required dependencies."
            : $"Set `{e.DisplayName}` required dependencies "
              + $"({newDeps.Count}).";
        var prioMsg = bumpTo.HasValue
            ? $" Priority {e.Priority} → {bumpTo.Value}."
            : "";
        _modsLabel.Text = $"{depsMsg}{prioMsg} {bak}";

        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        // Re-detect since dependency edits can satisfy or break
        // missing_dependency / dependency_order conflicts elsewhere.
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);
    }

    /// <summary>Opens the revert dialog for `e`, and if the user
    /// picks a backup, snapshots the CURRENT .vmz first (so the
    /// revert is reversible), then copies the chosen backup over
    /// the live file. Migrates cfg state when the reverted .vmz
    /// declares a different version string in mod.txt — same
    /// concern that UpdateModAsync handles in the opposite direction.</summary>
    private async Task RevertModFromBackupAsync(
        ModEntry e,
        List<ModBackup.BackupEntry> backups)
    {
        if (IsLocked(e))
        {
            _updatesLabel.Text =
                $"🔒 {e.DisplayName} is locked — revert skipped. "
                + "Unlock it via right-click → Unlock to revert.";
            return;
        }
        if (backups.Count == 0) return;
        using var dlg = new Ui.RevertModDialog(e, backups, ModsDir);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var src = dlg.SelectedBackupPath;
        if (string.IsNullOrEmpty(src) || !File.Exists(src)) return;

        var modId = e.ModId;
        var oldVersion = e.Version;
        var oldEnabled = _modConfig.IsEnabled(modId, oldVersion, fallback: true);
        var hadCfgPriority = _modConfig.HasEntry(modId, oldVersion);
        var oldCfgPriority = _modConfig.Priority(modId, oldVersion, e.DeclaredPriority);

        _busy = true;
        _updatesLabel.Text = $"Reverting {e.DisplayName} …";
        PopulateModsGrid();
        try
        {
            // Snapshot the current version BEFORE overwriting, so
            // the rollback is itself reversible. Same best-effort
            // policy as UpdateModAsync — a failure here is logged
            // but doesn't block the revert.
            try { ModBackup.MakeBackup(ModsDir, e); } catch { }

            // Replace the live .vmz with the chosen backup.
            if (File.Exists(e.Path))
            {
                try { File.Delete(e.Path); }
                catch (Exception ex)
                {
                    _updatesLabel.Text =
                        $"Couldn't replace `{Path.GetFileName(e.Path)}` "
                        + $"({ex.Message}). Quit the game and try again.";
                    return;
                }
            }
            File.Copy(src, e.Path, overwrite: false);

            // Rescan + cfg migration mirrors UpdateModAsync's
            // post-download flow, except the version is going
            // DOWN (or sideways) rather than up.
            Rescan();
            var newEntry = _registry.FindById(modId);
            var newVersion = newEntry?.Version ?? oldVersion;
            if (!string.IsNullOrEmpty(modId)
                && !string.Equals(newVersion, oldVersion, StringComparison.Ordinal))
            {
                _modConfig.SetEnabled(modId, newVersion, oldEnabled);
                if (hadCfgPriority)
                    _modConfig.SetPriority(modId, newVersion, oldCfgPriority);
                _modConfig.RemoveEntry(modId, oldVersion);
                SaveModConfigSafely();
                Rescan();
            }

            // Library + active-profile sync, same shape as
            // UpdateModAsync: the reverted version belongs in the
            // library (might already be there via the backup, but
            // ModLibrary.Add is idempotent on duplicates) and the
            // active profile's recorded version needs to reflect
            // what's actually live.
            try { Domain.ModLibrary.Add(ModsDir, e.Path); }
            catch { /* best-effort */ }
            SyncActiveProfileFromEntry(modId);

            UpdateModsStatus();
            PopulateModsGrid();
            _lastConflicts = DetectConflictsForActive();
            UpdateConflictsStatus(_lastConflicts);
            PopulateConflictsList(_lastConflicts);

            _updatesLabel.Text =
                $"Reverted {e.DisplayName}: v{oldVersion} → v{newVersion}.";
        }
        catch (Exception ex)
        {
            ShowError($"Revert failed for {e.DisplayName}", ex);
        }
        finally
        {
            _busy = false;
            PopulateModsGrid();
        }
        await Task.CompletedTask;
    }

    private async Task UpdateModAsync(ModEntry e)
    {
        var mw = e.ModWorkshopId;
        if (mw <= 0) return;
        var label = string.IsNullOrEmpty(e.DisplayName)
            ? Path.GetFileName(e.Path)
            : e.DisplayName;
        // Locked mods are explicitly protected from being overwritten —
        // surface the lock in the status line so the user knows why the
        // download didn't happen (rather than failing silently or
        // worse, clobbering the locked archive).
        if (IsLocked(e))
        {
            _updatesLabel.Text =
                $"🔒 {label} is locked — update skipped. "
                + "Unlock it via right-click → Unlock to update.";
            return;
        }
        var modId = e.ModId;
        var oldVersion = e.Version;
        // Capture pre-update cfg state so we can carry the user's
        // enable + priority customisations forward when the new mod
        // version's mod.txt declares a different version string —
        // cfg is keyed by mod-id@version, so the new version starts
        // with no cfg entries unless we migrate.
        var oldEnabled = _modConfig.IsEnabled(modId, oldVersion, fallback: true);
        var hadCfgPriority = _modConfig.HasEntry(modId, oldVersion);
        var oldCfgPriority = _modConfig.Priority(modId, oldVersion, e.DeclaredPriority);

        var finalPath = e.Path;
        var tempPath = finalPath + ".download";

        _busy = true;
        _updatesLabel.Text = $"Downloading {label} ...";
        PopulateModsGrid();  // re-render to disable buttons

        try
        {
            // Clean up any leftover from a prior failed attempt.
            if (File.Exists(tempPath)) File.Delete(tempPath);

            // Snapshot the current .vmz BEFORE we touch it. The
            // backup lives in %APPDATA%\VostokModManager\backups\
            // and is reachable from the per-mod context menu's
            // "Revert to previous version…" entry. Best-effort: a
            // failure here doesn't block the update (the .download
            // path is unaffected and we still want the new version
            // to land), it just leaves the rollback unavailable.
            try { ModBackup.MakeBackup(ModsDir, e); }
            catch { /* best-effort; revert still works via any
                       earlier backups for this mod */ }

            await _mw.DownloadLatestAsync(mw, tempPath);

            // Swap the file. If the original is locked (game running
            // and reading it), File.Delete throws — we leave the temp
            // file in place and tell the user to rename manually.
            if (File.Exists(finalPath))
            {
                try { File.Delete(finalPath); }
                catch (Exception ex)
                {
                    _updatesLabel.Text =
                        $"Downloaded {label} to {Path.GetFileName(tempPath)}, but " +
                        $"couldn't replace existing file ({ex.Message}). " +
                        "Quit the game and rename the .download file manually.";
                    return;
                }
            }
            File.Move(tempPath, finalPath);

            // First rescan so we can see the new mod.txt's version.
            Rescan();

            // Migrate cfg state if the mod.txt-declared version
            // changed. Without this, the user's previous priority
            // override would silently revert to the declared default
            // and the new key would default to enabled — close to
            // right but losing customisations.
            var migrated = false;
            var newEntry = _registry.FindById(modId);
            var newVersion = newEntry?.Version ?? oldVersion;
            if (!string.IsNullOrEmpty(modId)
                && !string.Equals(newVersion, oldVersion, StringComparison.Ordinal))
            {
                _modConfig.SetEnabled(modId, newVersion, oldEnabled);
                if (hadCfgPriority)
                    _modConfig.SetPriority(modId, newVersion, oldCfgPriority);
                _modConfig.RemoveEntry(modId, oldVersion);
                SaveModConfigSafely();
                Rescan();
                migrated = true;
            }

            // Seed the library with the new version so a later
            // profile switch can find it via ModLibrary.Find. Without
            // this step the library still only has the OLD version
            // of this mod (or nothing if it was first installed
            // pre-migration), and a switch would either fall through
            // to ProfileSwitcher's "newest version" fallback or
            // report missing.
            try { Domain.ModLibrary.Add(ModsDir, finalPath); }
            catch { /* best-effort; live file is still in place */ }

            // Mirror the new version into the active profile's
            // JSON so the next profile switch's lookup uses the
            // post-update version, not the stale pre-update one.
            SyncActiveProfileFromEntry(modId);

            // Force-refresh the ModWorkshop /mods/versions cache —
            // we just downloaded a new file, and the cached API
            // value on _our_ side is likely stale (the cached
            // "outdated" check is what told us to update in the
            // first place). Without this refresh, the comparison
            // below would always look like a version mismatch even
            // when the new mod.txt + the latest API value agree.
            // Best-effort: a network failure here just leaves the
            // cache as-is and the message falls through to a
            // neutral "versions disagree" wording.
            try { await CheckUpdatesAsync(forceFresh: true); }
            catch { /* keep going with the stale cache */ }

            UpdateModsStatus();
            PopulateModsGrid();

            var apiVersion = _latestVersions.TryGetValue(mw, out var av) ? av : "";
            if (string.IsNullOrEmpty(apiVersion))
            {
                _updatesLabel.Text = $"Updated {label} → v{newVersion}"
                    + (migrated ? " (cfg state carried over)." : ".");
            }
            else if (string.Equals(newVersion, apiVersion, StringComparison.Ordinal))
            {
                _updatesLabel.Text = $"Updated {label} → v{newVersion}"
                    + (migrated ? " (cfg state carried over)." : ".");
            }
            else
            {
                // Could be either side lagging — author hasn't
                // bumped mod.txt yet, OR ModWorkshop's CDN hasn't
                // caught up to a brand-new release. Don't take
                // sides; just describe what we see.
                _updatesLabel.Text =
                    $"Updated {label}: file's mod.txt is v{newVersion}, "
                    + $"ModWorkshop API reports v{apiVersion}. The Update "
                    + "column may still show ⬆ until both sides agree — "
                    + "click Refresh in a couple of minutes.";
            }
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
            { try { File.Delete(tempPath); } catch { } }
            ShowError($"Update failed for {label}", ex);
        }
        finally
        {
            _busy = false;
            PopulateModsGrid();
        }
    }

    private void ShowError(string title, Exception ex)
    {
        Ui.ThemedMessageBox.Show(this, ex.Message, title,
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // --- settings + setup banner ----------------------------------

    /// <summary>Launches Road to Vostok via Steam. Auto-detects the
    /// app id from the steamapps appmanifest matching the parent of
    /// our ModsDir, then fires steam://rungameid/&lt;id&gt;. Steam
    /// integration matters for achievements, playtime, and the
    /// in-game overlay — we don't try to launch the .exe directly.
    /// Falls back to a clear error message when detection fails.</summary>
    private void LaunchVostok()
    {
        // Pre-launch checkpoint: snapshot mod_config.cfg + the
        // active profile's profile.json so any "I launched the
        // game and it broke" outcome has a one-click rollback
        // path. Non-fatal if the snapshot fails — better to let
        // the user launch than block them on a backup write.
        var captured = Domain.CrashCheckpoint.Capture(
            Domain.ModConfig.DefaultPath,
            _activeProfile?.Name ?? "",
            _activeProfile?.FolderPath ?? "");
        if (captured) UpdateCheckpointStatus();

        // Watch for crashes by polling for a RoadToVostok-named
        // process that appears after launch. If one is found and
        // later exits with a non-zero code, prompt the user to
        // restore the checkpoint. Steam-launch is fire-and-
        // forget so we don't get a Process handle directly — the
        // poll-by-name is best-effort and silently no-ops on a
        // Steam install that runs under a different name.
        _ = WatchForCrashAsync();

        var appId = SteamLauncher.FindAppId(ModsDir);
        if (appId <= 0)
        {
            Ui.ThemedMessageBox.Show(this,
                "Couldn't find Road to Vostok's Steam app id.\n\n"
                + $"Expected an appmanifest_*.acf in:\n  "
                + $"{Path.GetDirectoryName(Path.GetDirectoryName(ModsDir)) ?? "(unknown)"}\n"
                + "with installdir matching the game folder.\n\n"
                + "Either the Mods folder isn't under a Steam library, "
                + "or you've moved the game outside of Steam. Launch "
                + "from your Steam library directly.",
                "Can't launch via Steam",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!SteamLauncher.LaunchAppId(appId))
        {
            Ui.ThemedMessageBox.Show(this,
                $"Steam protocol launch failed for app id {appId}. "
                + "Is Steam installed and running?",
                "Launch failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _modsLabel.Text = $"Launching Vostok via Steam (app id {appId})…";
    }

    /// <summary>Five-pointed star polygon, used for the title-row
    /// ornament + the watermark separators. Filled solid.</summary>
    internal static void DrawStar(Graphics g, float cx, float cy, float r, Color color)
    {
        var pts = new PointF[10];
        for (var i = 0; i < 10; i++)
        {
            var rr = (i % 2 == 0) ? r : r * 0.4f;
            var angle = (i * 36 - 90) * Math.PI / 180.0;
            pts[i] = new PointF(
                cx + (float)(Math.Cos(angle) * rr),
                cy + (float)(Math.Sin(angle) * rr));
        }
        var prev = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, pts);
        g.SmoothingMode = prev;
    }

    /// <summary>Opens the Settings modal. On Save the dialog has
    /// already updated the Settings instance fields; we persist
    /// + re-apply the runtime-affecting paths (Claude detect,
    /// ConflictResolver decomp, mods folder rescan) and refresh
    /// the setup banner. Cancel = no-op.</summary>
    private void OpenSettingsDialog()
    {
        // Snapshot the values so we can detect what changed and
        // skip unnecessary work (e.g. don't rescan if mods folder
        // didn't move).
        var oldMods = _settings.ModsDir;
        var oldClaude = _settings.ClaudePath;
        var oldDecomp = _settings.GameSourcePath;

        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // Validate mods folder before persisting; if it's invalid,
        // revert that one field so we don't try to scan a missing
        // folder. Other paths can be empty (auto-detect) or
        // optional (decomp), so they don't need this check.
        if (!string.IsNullOrEmpty(_settings.ModsDir)
            && !Directory.Exists(_settings.ModsDir))
        {
            Ui.ThemedMessageBox.Show(this,
                $"`{_settings.ModsDir}` doesn't exist. Reverting Mods "
                + "folder to the previous value. Pick the `mods/` "
                + "folder inside your Road to Vostok install.",
                "Mods folder not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _settings.ModsDir = oldMods;
        }

        try { _settings.Save(); }
        catch (Exception ex) { ShowError("Couldn't save settings", ex); return; }

#if AI_RESOLVER
        if (_settings.ClaudePath != oldClaude)
        {
            _claude.OverridePath = _settings.ClaudePath;
            _claude.Detect();
            UpdateClaudeStatus();
            PopulateConflictsList(_lastConflicts);
        }
        if (_settings.GameSourcePath != oldDecomp)
        {
            _resolver.GameSourcePath = _settings.GameSourcePath;
        }
#endif
        if (_settings.ModsDir != oldMods)
        {
            Rescan();
            UpdateModsStatus();
            PopulateModsGrid();
            _lastConflicts = DetectConflictsForActive();
            UpdateConflictsStatus(_lastConflicts);
            PopulateConflictsList(_lastConflicts);
            // Repoint the live file-system watcher at the new
            // folder. Without this it'd keep watching the old
            // location and miss every change in the new one.
            InitModsFolderWatcher();
        }
        RefreshSetupBanner();
    }

    /// <summary>Opens the About dialog (version + edition + credits +
    /// links). Reached from the in-form title label.</summary>
    private void OpenAboutDialog()
    {
        using var dlg = new Ui.AboutDialog();
        dlg.ShowDialog(this);
    }

    /// <summary>Opens the profile manager dialog. On close we
    /// ALWAYS reload the profiles snapshot + refresh the grid /
    /// conflict view, even when no profile was applied — Delete,
    /// Clone, Import, Empty-Slate, and metadata edits all change
    /// which profiles exist on disk OR which one is active, and
    /// the title-row selector + filtered grid need to see those
    /// changes immediately. The full mods-folder Rescan +
    /// CheckUpdatesAsync only fire when the dialog APPLIED a
    /// profile (NeedsRescan), since that's the only case where
    /// the live mods folder + cfg could have changed.</summary>
    private async Task OpenProfilesDialogAsync()
    {
        using var dlg = new Ui.ProfileManagerDialog(
            _registry, _mw, _modConfig, ModsDir, _settings.LockedMods);
        dlg.ShowDialog(this);

        // The dialog can change which profile is active in two ways:
        //   • a direct Switch button (already updates _settings)
        //   • Apply Profile on a non-active profile (writes only to
        //     _modConfig.ActiveProfile)
        // Mirror cfg → settings so the title-row selector picks up
        // the new active name and ReloadProfiles re-resolves
        // _activeProfile against the correct entry.
        if (!string.Equals(_settings.ActiveProfileName,
                _modConfig.ActiveProfile,
                StringComparison.OrdinalIgnoreCase))
        {
            _settings.ActiveProfileName = _modConfig.ActiveProfile;
            try { _settings.Save(); } catch { /* best-effort */ }
        }

        ReloadProfiles();
        // Re-filter the grid + recompute conflicts against the new
        // active-profile mod set. Cheap — operates on the existing
        // _registry.Entries, no disk read.
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);

        if (dlg.NeedsRescan)
        {
            Rescan();
            UpdateModsStatus();
            // Rescan changed the registry; re-filter again now that
            // _registry.Entries is fresh.
            PopulateModsGrid();
            _lastConflicts = DetectConflictsForActive();
            UpdateConflictsStatus(_lastConflicts);
            PopulateConflictsList(_lastConflicts);
            await CheckUpdatesAsync();
        }
    }

    // ── Active-profile glue ───────────────────────────────────────

    /// <summary>Reloads every profile from disk and re-resolves the
    /// active one against Settings.ActiveProfileName. Called once
    /// at startup and after every profile-manager round trip.
    /// Always refreshes the title-row selector label.</summary>
    private void ReloadProfiles()
    {
        _allProfiles  = Domain.ModProfile.LoadAll();
        _activeProfile = _allProfiles.FirstOrDefault(
            p => string.Equals(p.Name, _settings.ActiveProfileName,
                StringComparison.OrdinalIgnoreCase));
        SyncProfileSelectorLabel();
    }

    /// <summary>Keeps the title-row button text in sync with the
    /// active profile. "(no profile)" when pre-migration so the
    /// button reads identically to its initial label.</summary>
    private void SyncProfileSelectorLabel()
    {
        if (_profileSelector is null) return;
        var label = _activeProfile?.Name ?? "(no profile)";
        // Crop long names so the title row's selector doesn't
        // overflow into Launch/Profiles/Settings.
        if (label.Length > 22) label = label[..21] + "…";
        _profileSelector.Text = $"📋 Active: {label}  ▾";
    }

    /// <summary>Rebuilds the dropdown menu from the live
    /// _allProfiles snapshot. Checked = currently active. The menu
    /// is shared across opens so .Items needs a Clear each time.</summary>
    private void RebuildProfileSelectorMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        if (_allProfiles.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem(
                "(no profiles yet — open Profiles… to create one)")
            {
                Enabled = false,
            });
            return;
        }
        foreach (var p in _allProfiles)
        {
            var isActive = string.Equals(p.Name,
                _activeProfile?.Name, StringComparison.OrdinalIgnoreCase);
            var item = new ToolStripMenuItem(p.Name)
            {
                Checked = isActive,
            };
            // Capture by value — p is the loop variable.
            var captured = p;
            item.Click += (_, _) => SwitchActiveProfile(captured);
            menu.Items.Add(item);
        }
    }

    /// <summary>Activates `target` via ProfileSwitcher and refreshes
    /// the whole UI. No-op when the target is already active. On
    /// switcher failure (locked files, cfg save error), the result
    /// is surfaced to the conflicts status row but the partial
    /// state is left in place — ProfileSwitcher's stages are
    /// individually best-effort.</summary>
    private void SwitchActiveProfile(Domain.ModProfile target)
    {
        if (target == null) return;
        if (string.Equals(target.Name, _activeProfile?.Name,
                StringComparison.OrdinalIgnoreCase))
            return;
        Cursor = Cursors.WaitCursor;
        Domain.ProfileSwitcher.SwitchResult result;
        try
        {
            result = Domain.ProfileSwitcher.SetActive(
                ModsDir, target, _modConfig, _settings.LockedMods);
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        if (!result.Success)
        {
            ShowError("Profile switch failed",
                new Exception(string.Join("\n", result.Errors)));
            return;
        }

        _settings.ActiveProfileName = target.Name;
        try { _settings.Save(); } catch { /* best-effort */ }

        ReloadProfiles();
        Rescan();
        UpdateModsStatus();
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);

        var missingNote = result.MissingFromLibrary.Count > 0
            ? $"  ·  {result.MissingFromLibrary.Count} mod(s) missing from library (skipped)"
            : "";
        _conflictsLabel.Text =
            $"Switched to '{target.Name}'  ·  "
            + $"{result.CopiedFromLibrary} copied  ·  "
            + $"{result.KeptLocked} locked kept{missingNote}";
    }

    /// <summary>Runs ConflictDetector against either every entry
    /// (pre-migration: no active profile) OR just the entries that
    /// match the active profile's Mods list (post-migration). Keeps
    /// conflict scope aligned with what's visible in the grid.</summary>
    /// <summary>Pushes the live state of `modId` (post-Rescan) into
    /// the corresponding ProfileMod entry of the active profile,
    /// then persists. No-op when:
    ///   • there's no active profile (pre-migration);
    ///   • the mod isn't tracked by the active profile (e.g. it's
    ///     locked but not in this profile);
    ///   • the registry doesn't know about the mod_id any more
    ///     (deleted between rescan + sync).
    /// Cheap and best-effort — used after toggle / priority-change
    /// flows so a switch-then-switch-back round trip preserves
    /// changes the user made on the main grid.</summary>
    private void SyncActiveProfileFromEntry(string modId)
    {
        if (_activeProfile == null) return;
        if (string.IsNullOrEmpty(modId)) return;
        var entry = _registry.FindById(modId);
        if (entry == null) return;
        var pm = _activeProfile.Mods.FirstOrDefault(m =>
            string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase));
        if (pm == null) return;
        if (pm.IsEnabled == entry.IsEnabled
            && pm.Priority == entry.Priority
            && string.Equals(pm.Version, entry.Version, StringComparison.Ordinal))
            return;
        pm.IsEnabled = entry.IsEnabled;
        pm.Priority  = entry.Priority;
        pm.Version   = entry.Version;
        _activeProfile.UpdatedAt = DateTime.UtcNow;
        try { _activeProfile.SaveMetadataOnly(); }
        catch { /* best-effort — in-memory state still reflects the change */ }
    }

    /// <summary>Re-runs whenever the mods-grid selection changes:
    /// finds the mod_id of the currently-selected mods grid row,
    /// then walks every data row in the conflicts grid and selects
    /// it iff the row's Conflict references that mod_id. Banner
    /// rows (row.Tag is Severity) are skipped automatically because
    /// they have no Conflict to match against.
    /// Guarded by `_syncingConflicts` so the conflicts grid's own
    /// SelectionChanged handler (which deselects banner rows)
    /// doesn't bounce back into this method mid-loop.</summary>
    private bool _syncingConflicts;
    private void SyncConflictsHighlight()
    {
        if (_syncingConflicts) return;
        if (_modsGrid == null || _conflictsGrid == null) return;
        if (_modsGrid.SelectedRows.Count == 0) return;
        var rowIdx = _modsGrid.SelectedRows[0].Index;
        var selectedMod = ModAtRow(rowIdx);
        if (selectedMod == null) return; // header row selected
        var modId = selectedMod.ModId;
        if (string.IsNullOrEmpty(modId)) return;

        _syncingConflicts = true;
        try
        {
            _conflictsGrid.ClearSelection();
            DataGridViewRow? firstMatch = null;
            foreach (DataGridViewRow row in _conflictsGrid.Rows)
            {
                if (row.Tag is not ConflictDetector.Conflict c) continue;
                var hit = c.ModIds.Any(id =>
                    string.Equals(id, modId, StringComparison.OrdinalIgnoreCase));
                if (!hit) continue;
                row.Selected = true;
                firstMatch ??= row;
            }
            // Scroll the first matching row into view so the user
            // doesn't have to find it in a long conflict list.
            if (firstMatch != null)
            {
                try { _conflictsGrid.FirstDisplayedScrollingRowIndex = firstMatch.Index; }
                catch { /* benign if the grid hasn't laid out yet */ }
            }
        }
        finally
        {
            _syncingConflicts = false;
        }
    }

    private List<ConflictDetector.Conflict> DetectConflictsForActive()
    {
        if (_activeProfile == null)
            return ConflictDetector.DetectAll(_registry.Entries);
        // Conflict scope matches what's VISIBLE in the grid: active
        // profile mods plus any locked mods that aren't in the
        // profile. Without including locked mods, a locked-mod-vs-
        // profile-mod conflict would silently vanish on profile
        // switch, which is exactly the kind of surprise the lock
        // is supposed to prevent.
        var allowed = new HashSet<string>(
            _activeProfile.Mods.Select(m => m.ModId),
            StringComparer.OrdinalIgnoreCase);
        foreach (var lockId in _settings.LockedMods)
            allowed.Add(lockId);
        var scoped = _registry.Entries
            .Where(e => !string.IsNullOrEmpty(e.ModId)
                     && allowed.Contains(e.ModId));
        return ConflictDetector.DetectAll(scoped);
    }

    // ── First-launch profile-model migration ─────────────────────

    /// <summary>Shows the migration dialog and, on confirm, ingests
    /// every live .vmz into the Library + builds a profile from the
    /// current registry state + activates it. Non-destructive: the
    /// live mods folder, mod_config.cfg, and lock list are all left
    /// alone — only the Library + a new profile.json appear on
    /// disk. If the user picks Skip, Settings.MigrationDeclined
    /// flips so the prompt doesn't return on every launch.</summary>
    private void OfferProfileMigration()
    {
        var modCount = _registry.Entries.Count(
            e => e.IsArchive && !string.IsNullOrEmpty(e.ModId));
        // Default the name to whatever mod_config.cfg already calls
        // the active profile — keeps cfg and profile.json aligned
        // and matches what the user sees in the in-game loader.
        var defaultName = string.IsNullOrWhiteSpace(_modConfig.ActiveProfile)
            ? "Default"
            : _modConfig.ActiveProfile;

        using var dlg = new Ui.MigrationDialog(modCount, defaultName);
        var result = dlg.ShowDialog(this);
        if (result != DialogResult.OK || string.IsNullOrEmpty(dlg.ChosenName))
        {
            // Skip / X-close / Esc — remember so we don't re-prompt.
            _settings.MigrationDeclined = true;
            try { _settings.Save(); } catch { /* best-effort */ }
            _modsLabel.Text =
                "Profile model: migration skipped. You can adopt it "
                + "later by deleting Settings.json or via a future "
                + "menu option.";
            return;
        }

        var name = dlg.ChosenName!;
        Cursor = Cursors.WaitCursor;
        int libAdded = 0;
        int libSkipped = 0;
        try
        {
            // 1. Ingest every live archive mod into the Library.
            foreach (var entry in _registry.Entries)
            {
                if (!entry.IsArchive) continue;
                if (string.IsNullOrEmpty(entry.Path) || !File.Exists(entry.Path))
                    continue;
                var added = Domain.ModLibrary.Add(ModsDir, entry.Path);
                if (added != null) libAdded++;
                else               libSkipped++;
            }

            // 2. Snapshot current state into a new profile.
            var profile = Domain.ModProfile.FromRegistry(
                name,
                $"Migrated from existing mods folder — {DateTime.Now:yyyy-MM-dd HH:mm}",
                _registry.Entries);
            try { profile.SaveMetadataOnly(); }
            catch (Exception ex)
            {
                ShowError("Couldn't write profile metadata", ex);
                return;
            }

            // 3. Align cfg's active_profile with the new profile
            // name (preserves cfg state — we don't ClearProfileEntries
            // here; that's a deliberate destructive op reserved for
            // ProfileSwitcher).
            if (!string.Equals(_modConfig.ActiveProfile, name,
                    StringComparison.OrdinalIgnoreCase))
            {
                _modConfig.ActiveProfile = name;
                SaveModConfigSafely();
            }

            // 4. Record adoption in settings.
            _settings.ActiveProfileName = name;
            _settings.MigrationDeclined = false;
            try { _settings.Save(); } catch { /* best-effort */ }
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        // Refresh state machine — re-resolve _activeProfile, repopulate
        // the title-row selector, grid, conflicts.
        ReloadProfiles();
        PopulateModsGrid();
        _lastConflicts = DetectConflictsForActive();
        UpdateConflictsStatus(_lastConflicts);
        PopulateConflictsList(_lastConflicts);

        var libNote = libSkipped > 0
            ? $", {libSkipped} skipped (no mod_id)"
            : "";
        _modsLabel.Text =
            $"Migrated to profile '{name}'  ·  "
            + $"{libAdded} mod(s) added to Library{libNote}.";
    }

    private void RefreshSetupBanner()
    {
        var msgs = new List<string>();
#if AI_RESOLVER
        if (!_claude.IsAvailable)
        {
            if (ClaudeCodeRunner.HasMsixInstall())
            {
                msgs.Add(
                    "• Claude Desktop (Microsoft Store) detected — that " +
                    "install is sandboxed and unreachable from outside the " +
                    "package. Install the standalone CLI: install Node.js " +
                    "from nodejs.org, then in a NEW terminal run " +
                    "`npm install -g @anthropic-ai/claude-code`. Restart " +
                    "the Manager when done.");
            }
            else
            {
                msgs.Add(
                    "• Claude Code not detected. Install Node.js from " +
                    "nodejs.org, then in a NEW terminal run " +
                    "`npm install -g @anthropic-ai/claude-code`. Or paste " +
                    "a known claude.exe path into the input below and " +
                    "click Save.");
            }
        }
#endif
        if (!Directory.Exists(ModsDir))
        {
            msgs.Add(
                $"• Mods folder `{ModsDir}` doesn't exist. The default " +
                "Steam install path doesn't apply on your machine — set " +
                "the Mods folder below to wherever Road to Vostok lives.");
        }
#if AI_RESOLVER
        if (string.IsNullOrEmpty(_settings.GameSourcePath)
            || !Directory.Exists(_settings.GameSourcePath))
        {
            msgs.Add(
                "• Game source (Decomp/) not configured. AI conflict " +
                "resolution still works without it, but produces better " +
                "merges when given the original game script as context. " +
                "Paste your Decomp/ path into the input below or click " +
                "Browse... to pick the folder.");
        }
#endif
        if (msgs.Count == 0)
        {
            _setupBanner.Visible = false;
            return;
        }
        _setupBannerLabel.Text = "⚙ Setup needed:\n\n" + string.Join("\n\n", msgs);
        _setupBanner.Visible = true;
    }


    // --- conflict resolve ----------------------------------------

    private async Task OnConflictsCellClickedAsync(DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _conflictsGrid.Rows.Count) return;
        var row = _conflictsGrid.Rows[e.RowIndex];
        // Banner rows (row.Tag is Severity) carry no conflict — clicks
        // on them are inert. Data rows store the Conflict in Tag.
        if (row.Tag is not ConflictDetector.Conflict conflict) return;
        if (_busy)
        {
            _conflictsLabel.Text = "(Busy — wait for the current operation to finish.)";
            return;
        }
        var col = _conflictsGrid.Columns[e.ColumnIndex].Name;
        if (col != "Resolve") return;
#if AI_RESOLVER
        if (!IsButtonRow(conflict))
        {
            _conflictsLabel.Text =
                $"Resolve doesn't handle [{conflict.Type}] conflicts in v1 — "
                + "only file_overlap.";
            return;
        }
        if (!_claude.IsAvailable)
        {
            _conflictsLabel.Text =
                "Resolve needs Claude Code. "
                + "Install Node.js then `npm install -g @anthropic-ai/claude-code`, "
                + "then restart the manager.";
            return;
        }
        await ResolveAsync(conflict);
#else
        // Integrated edition: clicking the (empty) Resolve cell is a no-op,
        // but surface a hint so users know why the column is unused.
        _conflictsLabel.Text =
            "AI conflict resolution is not available in the Integrated edition. "
            + "Download the full edition to enable it.";
        await Task.CompletedTask;
#endif
    }

#if AI_RESOLVER
    private async Task ResolveAsync(ConflictDetector.Conflict conflict)
    {
        _busy = true;
        var prevConflictsLabel = _conflictsLabel.Text;
        _conflictsLabel.Text =
            $"Resolving {conflict.Key} with Claude Code ...";
        // Re-render grids to disable buttons during the call.
        PopulateModsGrid();

        ConflictResolver.Verdict verdict;
        try
        {
            verdict = await _resolver.ResolveFileOverlapAsync(conflict);
        }
        catch (Exception ex)
        {
            verdict = new ConflictResolver.Verdict
            {
                Ok = false,
                ConflictKey = conflict.Key,
                Error = ex.Message,
            };
        }
        finally
        {
            _busy = false;
            _conflictsLabel.Text = prevConflictsLabel;
            PopulateModsGrid();
        }

        using var dlg = new ResolutionDialog(verdict);
        dlg.ShowDialog(this);
        // If the user applied the merge inside the dialog, the .vmz
        // contents changed — rescan + redetect so the conflict (and
        // any related ones) drop off the list cleanly.
        if (dlg.Applied)
        {
            Rescan();
            UpdateModsStatus();
            PopulateModsGrid();
            _lastConflicts = DetectConflictsForActive();
            UpdateConflictsStatus(_lastConflicts);
            PopulateConflictsList(_lastConflicts);
        }
    }
#endif
}
