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

    // ── Plan rows ─────────────────────────────────────────────────────

    private enum RowKind { Match, ReplaceFromBundle, InstallFromBundle, UpdateDownload, MissingDownload, NoSource }

    private record PlanRow(
        RowKind    Kind,
        ProfileMod ProfileMod,
        ModEntry?  Installed,       // null when not installed
        string     BundlePath,      // non-empty when a bundled archive is available
        string     StatusIcon,
        string     StatusText,
        bool       OptIn);          // initial checkbox value for downloadable rows

    private List<PlanRow> _plan = new();

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
        string modsDir)
    {
        _profile   = profile;
        _registry  = registry;
        _mw        = mw;
        _modConfig = modConfig;
        _modsDir   = modsDir;

        BuildPlan();
        InitUi();
    }

    private void BuildPlan()
    {
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _registry.Entries)
            if (!string.IsNullOrEmpty(e.ModId)) byId[e.ModId] = e;

        _plan = new List<PlanRow>();
        foreach (var pm in _profile.Mods)
        {
            byId.TryGetValue(pm.ModId, out var installed);
            var bundle = _profile.ResolveBundledArchive(pm);
            var hasBundle = !string.IsNullOrEmpty(bundle);
            var hasMw     = pm.ModWorkshopId > 0;

            PlanRow row;
            if (installed != null)
            {
                var cmp = ModRegistry.CompareVersions(installed.Version, pm.Version);
                if (cmp == 0)
                {
                    row = new PlanRow(RowKind.Match, pm, installed, "",
                        "✓", "Installed (match)", false);
                }
                else if (hasBundle)
                {
                    var direction = cmp > 0 ? "downgrade" : "upgrade";
                    row = new PlanRow(RowKind.ReplaceFromBundle, pm, installed, bundle,
                        "↔", $"Will {direction} from bundle (installed {installed.Version} → {pm.Version})", true);
                }
                else if (cmp < 0 && hasMw)
                {
                    row = new PlanRow(RowKind.UpdateDownload, pm, installed, "",
                        "⬆", "Outdated — update available from ModWorkshop", true);
                }
                else
                {
                    // Installed at the wrong version but no way to switch
                    // — surface it but it'll just apply settings to whatever's there.
                    row = new PlanRow(RowKind.Match, pm, installed, "",
                        "✓", $"Installed (version mismatch: {installed.Version} vs {pm.Version})", false);
                }
            }
            else if (hasBundle)
            {
                row = new PlanRow(RowKind.InstallFromBundle, pm, null, bundle,
                    "⬇", "Will install from bundle", true);
            }
            else if (hasMw)
            {
                row = new PlanRow(RowKind.MissingDownload, pm, null, "",
                    "⬇", "Not installed — will download from ModWorkshop", true);
            }
            else
            {
                row = new PlanRow(RowKind.NoSource, pm, null, "",
                    "✗", "Not installed (no bundle and no ModWorkshop ID)", false);
            }
            _plan.Add(row);
        }
    }

    private bool IsCheckable(RowKind k) =>
        k is RowKind.ReplaceFromBundle
            or RowKind.InstallFromBundle
            or RowKind.UpdateDownload
            or RowKind.MissingDownload;

    private void InitUi()
    {
        Text = $"Apply Profile — {_profile.Name}";
        StartPosition  = FormStartPosition.CenterParent;
        BackColor      = Color.FromArgb(26, 30, 40);
        ForeColor      = Color.FromArgb(220, 225, 235);
        Font           = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize    = new Size(900, 580);
        Width  = 1000;
        Height = 660;
        ShowInTaskbar  = false;

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

        Controls.Add(btnRow);
        Controls.Add(content);

        CancelButton = _cancelBtn;
    }

    private string BuildSummaryText()
    {
        int matches    = _plan.Count(r => r.Kind == RowKind.Match);
        int replaces   = _plan.Count(r => r.Kind == RowKind.ReplaceFromBundle);
        int installs   = _plan.Count(r => r.Kind == RowKind.InstallFromBundle);
        int updates    = _plan.Count(r => r.Kind == RowKind.UpdateDownload);
        int downloads  = _plan.Count(r => r.Kind == RowKind.MissingDownload);
        int noSrc      = _plan.Count(r => r.Kind == RowKind.NoSource);

        var parts = new List<string>();
        if (matches  > 0) parts.Add($"{matches} matching");
        if (replaces > 0) parts.Add($"{replaces} from bundle (replace)");
        if (installs > 0) parts.Add($"{installs} from bundle (install)");
        if (updates  > 0) parts.Add($"{updates} update via MW");
        if (downloads > 0) parts.Add($"{downloads} download via MW");
        if (noSrc    > 0) parts.Add($"{noSrc} unavailable");

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
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name         = "ModName",
            HeaderText   = "Mod",
            ReadOnly     = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 160,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "ProfileVer",
            HeaderText = "Profile ver.",
            Width      = 100,
            ReadOnly   = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "InstalledVer",
            HeaderText = "Installed",
            Width      = 100,
            ReadOnly   = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "Status",
            HeaderText = "Status",
            Width      = 320,
            ReadOnly   = true,
        });

        foreach (DataGridViewColumn col in grid.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;

        foreach (var row in _plan)
        {
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

            var fg = row.Kind switch
            {
                RowKind.Match              => Color.FromArgb(120, 200, 130),
                RowKind.ReplaceFromBundle  => Color.FromArgb(200, 180, 255),
                RowKind.InstallFromBundle  => Color.FromArgb(150, 200, 255),
                RowKind.UpdateDownload     => Color.FromArgb(255, 200, 80),
                RowKind.MissingDownload    => Color.FromArgb(120, 170, 255),
                RowKind.NoSource           => Color.FromArgb(140, 140, 160),
                _ => Color.FromArgb(220, 225, 235),
            };
            r.Cells["Status"].Style.ForeColor = fg;
            r.Cells["Icon"].Style.ForeColor   = fg;
            if (row.Kind == RowKind.NoSource)
                r.DefaultCellStyle.ForeColor = Color.FromArgb(140, 140, 160);
        }

        // Live checkbox commit (don't wait for focus loss)
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        return grid;
    }

    private Panel BuildProgressPanel()
    {
        var p = new Panel
        {
            Dock      = DockStyle.Fill,
            Height    = 180,
            BackColor = Color.Transparent,
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

        // Snapshot user choices
        var actions = new List<(PlanRow row, bool optIn)>();
        for (int i = 0; i < _plan.Count; i++)
        {
            var row = _plan[i];
            var optIn = IsCheckable(row.Kind)
                && _grid.Rows[i].Cells["Apply"].Value is true;
            actions.Add((row, optIn));
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var totalSteps =
            actions.Count(a => a.optIn) * 2  // file op + cfg apply per acted row
            + actions.Count(a => a.row.Kind == RowKind.Match) // cfg apply only
            + 2; // rescan + save
        var step = 0;

        void Log(string m)
        {
            _log.AppendText(m + Environment.NewLine);
            _log.ScrollToCaret();
        }
        void Advance(string m)
        {
            step = Math.Min(step + 1, totalSteps);
            _progressBar.Value = (int)((double)step / Math.Max(1, totalSteps) * 100);
            Log(m);
        }

        // ── Phase 1: File operations ─────────────────────────────────
        // Bundle copies first (cheap, local), then MW downloads.

        // Local-bundle ops
        foreach (var (row, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            if (!optIn) continue;
            if (row.Kind != RowKind.InstallFromBundle && row.Kind != RowKind.ReplaceFromBundle)
                continue;

            try
            {
                if (row.Kind == RowKind.ReplaceFromBundle && row.Installed != null
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

        // MW downloads
        var downloads = actions
            .Where(a => a.optIn && (a.row.Kind == RowKind.UpdateDownload
                                  || a.row.Kind == RowKind.MissingDownload))
            .ToList();
        foreach (var (row, _) in downloads)
        {
            if (ct.IsCancellationRequested) break;
            var pm = row.ProfileMod;
            var fileName = $"{ModProfile.SafeFileName(pm.ModId)}.vmz";
            var dest = Path.Combine(_modsDir, fileName);
            Advance($"⬇ {pm.DisplayName} (MW {pm.ModWorkshopId}) …");
            try
            {
                await Task.Run(() => _mw.DownloadLatestAsync(pm.ModWorkshopId, dest, ct), ct);
                Log($"  ✓ → {fileName}");
            }
            catch (OperationCanceledException) { Log("  ✗ Cancelled."); break; }
            catch (Exception ex) { Log($"  ✗ Download failed: {ex.Message}"); }
        }

        if (ct.IsCancellationRequested) goto Done;

        // ── Phase 2: Rescan ──────────────────────────────────────────
        Advance("↻ Rescanning mods …");
        _registry.Scan(_modsDir, _modConfig);

        // ── Phase 3: Apply enabled/priority state ─────────────────────
        foreach (var (row, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            // Skip rows we couldn't act on (NoSource, or downloadable rows
            // the user unchecked).
            if (row.Kind == RowKind.NoSource) continue;
            if (IsCheckable(row.Kind) && !optIn) continue;

            var pm = row.ProfileMod;
            var entry = _registry.Entries.FirstOrDefault(
                e => string.Equals(e.ModId, pm.ModId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                Advance($"  — {pm.DisplayName}: not found after scan, skipping.");
                continue;
            }
            _modConfig.SetEnabled(pm.ModId, entry.Version, pm.IsEnabled);
            _modConfig.SetPriority(pm.ModId, entry.Version, pm.Priority);
            Advance($"  ✓ {pm.DisplayName}: {(pm.IsEnabled ? "enabled" : "disabled")}, prio {pm.Priority}");
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
