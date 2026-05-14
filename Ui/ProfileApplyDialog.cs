// Profile-import plan and execution dialog.
//
// Given a ModProfile and the currently-installed ModRegistry, it
// builds a plan table showing what will happen for each profile mod:
//
//   ✓  Match    — mod installed, version OK; will apply enabled/priority
//   ⬆  Outdated — mod installed but older; can download update from MW
//   ⬇  Missing  — mod not installed;        can download from MW
//   ✗  No source— not installed, no MW ID;  will be skipped
//
// The user can check/uncheck the download rows. Clicking [Apply]
// executes in order: downloads → apply enabled/priority states →
// rescan. Progress is streamed into an inline log TextBox.

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ProfileApplyDialog : Form
{
    // ── Public outcome ────────────────────────────────────────────────

    /// <summary>True when the user successfully applied the profile
    /// (at least partially). The caller should rescan after Close.</summary>
    public bool Applied { get; private set; }

    // ── Dependencies ──────────────────────────────────────────────────

    private readonly ModProfile      _profile;
    private readonly ModRegistry     _registry;
    private readonly ModWorkshopClient _mw;
    private readonly ModConfig       _modConfig;
    private readonly string          _modsDir;

    // ── Plan rows ─────────────────────────────────────────────────────

    private enum RowKind { Match, Outdated, Missing, NoSource }

    private record PlanRow(
        RowKind    Kind,
        ProfileMod ProfileMod,
        ModEntry?  Installed,   // null for Missing / NoSource
        string     StatusIcon,
        string     StatusText,
        bool       CanDownload);

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
            if (!string.IsNullOrEmpty(e.ModId))
                byId[e.ModId] = e;

        _plan = new List<PlanRow>();
        foreach (var pm in _profile.Mods)
        {
            byId.TryGetValue(pm.ModId, out var installed);
            PlanRow row;
            if (installed != null)
            {
                // Compare versions: 0 = same, >0 = installed newer, <0 = installed older
                var cmp = ModRegistry.CompareVersions(installed.Version, pm.Version);
                if (cmp >= 0)
                {
                    row = new PlanRow(RowKind.Match, pm, installed,
                        "✓", "Installed (match)", false);
                }
                else
                {
                    row = new PlanRow(RowKind.Outdated, pm, installed,
                        "⬆", "Outdated — update available", pm.ModWorkshopId > 0);
                }
            }
            else if (pm.ModWorkshopId > 0)
            {
                row = new PlanRow(RowKind.Missing, pm, null,
                    "⬇", "Not installed — will download", true);
            }
            else
            {
                row = new PlanRow(RowKind.NoSource, pm, null,
                    "✗", "Not installed (no ModWorkshop ID)", false);
            }
            _plan.Add(row);
        }
    }

    private void InitUi()
    {
        Text = $"Apply Profile — {_profile.Name}";
        StartPosition  = FormStartPosition.CenterParent;
        BackColor      = Color.FromArgb(26, 30, 40);
        ForeColor      = Color.FromArgb(220, 225, 235);
        Font           = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize    = new Size(860, 560);
        Width  = 960;
        Height = 640;
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

        // Plan grid
        _grid = BuildGrid();

        // Progress panel (hidden until Apply starts)
        _progressPanel = BuildProgressPanel();
        _progressPanel.Visible = false;

        // Button row
        var btnRow = BuildButtonRow();

        // Layout: bottom-up so Dock.Top stacks correctly
        var content = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 4,
            Padding     = new Padding(16, 12, 16, 12),
            BackColor   = Color.Transparent,
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // title
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // summary
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // grid
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // progress
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
        var matches  = _plan.Count(r => r.Kind == RowKind.Match);
        var outdated = _plan.Count(r => r.Kind == RowKind.Outdated);
        var missing  = _plan.Count(r => r.Kind == RowKind.Missing);
        var noSrc    = _plan.Count(r => r.Kind == RowKind.NoSource);

        var parts = new List<string>();
        if (matches  > 0) parts.Add($"{matches} matching");
        if (outdated > 0) parts.Add($"{outdated} outdated");
        if (missing  > 0) parts.Add($"{missing} to download");
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
            AutoGenerateColumns = false,
            AllowUserToAddRows  = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly            = false,
            SelectionMode       = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect         = false,
            RowHeadersVisible   = false,
            BackgroundColor     = Color.FromArgb(18, 22, 30),
            BorderStyle         = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle =
            {
                BackColor       = Color.FromArgb(36, 42, 54),
                ForeColor       = Color.FromArgb(220, 225, 235),
                Font            = new Font("Segoe UI", 12f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
            },
            DefaultCellStyle =
            {
                BackColor       = Color.FromArgb(18, 22, 30),
                ForeColor       = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font            = new Font("Consolas", 11f),
            },
            GridColor        = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 36,
            RowTemplate      = { Height = 30 },
        };

        // Download checkbox — visible only for downloadable rows
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name       = "Download",
            HeaderText = "DL",
            Width      = 42,
            ReadOnly   = false,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "Icon",
            HeaderText = "",
            Width      = 32,
            ReadOnly   = true,
            DefaultCellStyle =
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI Symbol", 14f),
            },
        });

        // Mod name (fill)
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
            Width      = 240,
            ReadOnly   = true,
        });

        foreach (DataGridViewColumn col in grid.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;

        // Populate
        foreach (var row in _plan)
        {
            var i = grid.Rows.Add();
            var r = grid.Rows[i];
            var chk = row.CanDownload;
            r.Cells["Download"].Value     = chk;
            r.Cells["Download"].ReadOnly  = !row.CanDownload;
            r.Cells["Icon"].Value         = row.StatusIcon;
            r.Cells["ModName"].Value      = row.ProfileMod.DisplayName;
            r.Cells["ProfileVer"].Value   = row.ProfileMod.Version;
            r.Cells["InstalledVer"].Value = row.Installed?.Version ?? "—";
            r.Cells["Status"].Value       = row.StatusText;

            // Colour-code by kind
            var fg = row.Kind switch
            {
                RowKind.Match    => Color.FromArgb(120, 200, 130),
                RowKind.Outdated => Color.FromArgb(255, 200, 80),
                RowKind.Missing  => Color.FromArgb(120, 170, 255),
                RowKind.NoSource => Color.FromArgb(140, 140, 160),
                _ => Color.FromArgb(220, 225, 235),
            };
            r.Cells["Status"].Style.ForeColor = fg;
            r.Cells["Icon"].Style.ForeColor   = fg;
            if (row.Kind == RowKind.NoSource)
                r.DefaultCellStyle.ForeColor = Color.FromArgb(140, 140, 160);
        }

        return grid;
    }

    private Panel BuildProgressPanel()
    {
        var p = new Panel
        {
            Dock        = DockStyle.Fill,
            Height      = 180,
            BackColor   = Color.Transparent,
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
            Dock        = DockStyle.Fill,
            Multiline   = true,
            ReadOnly    = true,
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

        // ── Collect plan on UI thread ─────────────────────────────────
        var toDownload = new List<PlanRow>();
        var toApply    = new List<PlanRow>();

        for (int i = 0; i < _plan.Count; i++)
        {
            var row = _plan[i];
            var dl  = _grid.Rows[i].Cells["Download"].Value is true;

            if (row.Kind == RowKind.Match)
                toApply.Add(row);
            else if ((row.Kind == RowKind.Outdated || row.Kind == RowKind.Missing) && dl)
                toDownload.Add(row);
            // Outdated/Missing rows not checked → skip
            // NoSource rows are always skipped
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Progress helpers — always called from UI thread
        var step      = 0;
        var totalSteps = toDownload.Count + toApply.Count + toDownload.Count + 2;

        void Log(string msg)
        {
            _log.AppendText(msg + Environment.NewLine);
            _log.ScrollToCaret();
        }
        void Advance(string msg)
        {
            step = Math.Min(step + 1, totalSteps);
            _progressBar.Value = (int)((double)step / totalSteps * 100);
            Log(msg);
        }

        // ── Phase 1: Downloads (background, progress marshalled to UI) ─
        if (toDownload.Count > 0)
        {
            Log($"→ Downloading {toDownload.Count} mod(s) …");
        }
        foreach (var row in toDownload)
        {
            if (ct.IsCancellationRequested) break;
            var pm = row.ProfileMod;
            var fileName = $"{ModProfile.SafeFileName(pm.ModId)}.vmz";
            var dest = System.IO.Path.Combine(_modsDir, fileName);
            Advance($"⬇ {pm.DisplayName} …");
            try
            {
                await Task.Run(() => _mw.DownloadLatestAsync(pm.ModWorkshopId, dest, ct), ct);
                Log($"  ✓ → {fileName}");
            }
            catch (OperationCanceledException)
            {
                Log("  ✗ Cancelled.");
                break;
            }
            catch (Exception ex)
            {
                Log($"  ✗ Download failed: {ex.Message}");
            }
        }

        if (ct.IsCancellationRequested) goto Done;

        // ── Phase 2: Rescan (UI thread — ModRegistry is not thread-safe) ─
        if (toDownload.Count > 0)
        {
            Advance("↻ Rescanning mods …");
            _registry.Scan(_modsDir, _modConfig);
        }

        // ── Phase 3: Apply enabled / priority states ──────────────────
        //  Match rows use the profile's IsEnabled; freshly downloaded
        //  mods default to enabled (they weren't in the game before).
        var allToApply = toApply
            .Concat(toDownload.Select(r => r))
            .ToList();

        foreach (var row in allToApply)
        {
            if (ct.IsCancellationRequested) break;
            var pm = row.ProfileMod;
            var entry = _registry.Entries.FirstOrDefault(
                e => string.Equals(e.ModId, pm.ModId,
                    StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                Advance($"  — {pm.DisplayName}: not found after scan, skipping.");
                continue;
            }
            var enable = (row.Kind == RowKind.Match)
                ? pm.IsEnabled
                : true;  // freshly-downloaded → default on
            _modConfig.SetEnabled(pm.ModId, entry.Version, enable);
            _modConfig.SetPriority(pm.ModId, entry.Version, pm.Priority);
            Advance($"  ✓ {pm.DisplayName}: {(enable ? "enabled" : "disabled")}, prio {pm.Priority}");
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosing(e);
    }
}
