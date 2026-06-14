// Log Analysis dialog.
//
// Parses the latest godot.log (via GodotLogAnalyzer) and shows:
//   • a log picker (newest first) + header stats,
//   • the hook / override OVERWRITE MAP — which mods overrode each
//     vanilla script and which one won (deterministic; both editions),
//   • the issue list (errors / warnings, with mod hints).
//
// In the AI edition (#if AI_RESOLVER) it additionally offers an
// "Analyze with Claude" action that sends a compact digest to the local
// Claude CLI and renders a summary, per-issue fixes, and a PROPOSED LOAD
// ORDER the user can apply with one click (writes via ModConfig).
//
// Mirrors ProfileApplyDialog / ResolutionDialog for theming + progress.

using VostokModManager.Domain;
#if AI_RESOLVER
using VostokModManager.Ai;
#endif

namespace VostokModManager.Ui;

public class LogAnalysisDialog : Form
{
    /// <summary>True when the user applied a proposed load order (AI
    /// edition). MainForm rescans on close when set — same contract as
    /// ResolutionDialog.Applied.</summary>
    public bool Applied { get; private set; }

    private readonly ModRegistry _registry;
    private readonly ModConfig   _modConfig;
    private readonly string      _modsDir;
#if AI_RESOLVER
    private readonly LogDiagnoser? _diagnoser;
    private readonly bool          _claudeAvailable;
#endif

    private ComboBox      _logPicker = null!;
    private Label         _headerLabel = null!;
    private DataGridView  _hookGrid = null!;
    private DataGridView  _clashGrid = null!;
    private DataGridView  _issueGrid = null!;
    private GodotLogAnalyzer.LogAnalysis _analysis = new();

#if AI_RESOLVER
    private Button       _aiBtn = null!;
    private ProgressBar  _aiProgress = null!;
    private TextBox      _aiLog = null!;
    private DataGridView _proposedGrid = null!;
    private Label        _aiSummary = null!;
    private Button       _applyOrderBtn = null!;
    private LogDiagnoser.LogVerdict? _verdict;
    private bool         _busy;
#endif

    // Integrated-edition constructor.
    public LogAnalysisDialog(ModRegistry registry, ModConfig modConfig, string modsDir)
    {
        _registry  = registry;
        _modConfig = modConfig;
        _modsDir   = modsDir;
        InitUi();
    }

#if AI_RESOLVER
    // AI-edition constructor (adds the Claude diagnoser).
    public LogAnalysisDialog(
        ModRegistry registry, ModConfig modConfig, string modsDir,
        LogDiagnoser diagnoser, bool claudeAvailable)
    {
        _registry        = registry;
        _modConfig       = modConfig;
        _modsDir         = modsDir;
        _diagnoser       = diagnoser;
        _claudeAvailable = claudeAvailable;
        InitUi();
    }
#endif

    private void InitUi()
    {
        Text            = "Analyze Log";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize     = new Size(960, 640);
        Width           = 1180;
        Height          = 760;
        ShowInTaskbar   = false;
        DialogSizing.ClampToWorkingArea(this);

        var title = new Label
        {
            Text      = "🩺 Log Analysis",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 4),
        };

        // Log picker row.
        var pickerRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6),
        };
        pickerRow.Controls.Add(new Label
        {
            Text = "Log:", AutoSize = true,
            ForeColor = Color.FromArgb(190, 200, 215),
            Margin = new Padding(0, 8, 6, 0),
        });
        _logPicker = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 520,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            FlatStyle = FlatStyle.Flat,
        };
        foreach (var p in GodotLogAnalyzer.AvailableLogs())
        {
            string label;
            try
            {
                var fi = new FileInfo(p);
                label = $"{fi.Name}   ({fi.LastWriteTime:yyyy-MM-dd HH:mm}, {fi.Length / 1024} KB)";
            }
            catch { label = Path.GetFileName(p); }
            _logPicker.Items.Add(new LogItem { Path = p, Label = label });
        }
        _logPicker.DisplayMember = nameof(LogItem.Label);
        if (_logPicker.Items.Count > 0) _logPicker.SelectedIndex = 0;
        _logPicker.SelectedIndexChanged += (_, _) => ReloadAnalysis();
        pickerRow.Controls.Add(_logPicker);

        _headerLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.FromArgb(170, 185, 210),
            Margin = new Padding(0, 0, 0, 10),
        };

        var hookHeader = SectionHeader("Hook clashes — functions hooked by more than one mod (these chain; load order decides precedence)");
        _hookGrid = BuildGrid();
        _hookGrid.Columns.Add(MakeCol("Target", "Hooked function (script :: method)", 420));
        _hookGrid.Columns.Add(MakeCol("Count", "Mods", 70));
        _hookGrid.Columns.Add(MakeCol("HookMods", "Mods hooking it (declaration order)", 500));

        var clashHeader = SectionHeader("Whole-script override clashes — same vanilla script replaced by 2+ mods (winner = last applied)");
        _clashGrid = BuildGrid();
        _clashGrid.Columns.Add(MakeCol("Vanilla", "Vanilla script", 320));
        _clashGrid.Columns.Add(MakeCol("Mods", "Mods (apply order)", 460));
        _clashGrid.Columns.Add(MakeCol("Winner", "Winner", 220));

        var issueHeader = SectionHeader("Issues from the log");
        _issueGrid = BuildGrid();
        _issueGrid.Columns.Add(MakeCol("Sev", "Severity", 110));
        _issueGrid.Columns.Add(MakeCol("Mod", "Mod", 160));
        _issueGrid.Columns.Add(MakeCol("Msg", "Message", 720));

        var content = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            Padding     = new Padding(16, 12, 16, 12),
            BackColor   = Color.Transparent,
            AutoScroll  = true,
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // title
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // picker
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // header stats
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // hook header
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 220f)); // hook grid
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // clash header
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 160f)); // clash grid
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // issue header
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 220f)); // issue grid

        int r = 0;
        content.Controls.Add(title,        0, r++);
        content.Controls.Add(pickerRow,    0, r++);
        content.Controls.Add(_headerLabel, 0, r++);
        content.Controls.Add(hookHeader,   0, r++);
        content.Controls.Add(_hookGrid,    0, r++);
        content.Controls.Add(clashHeader,  0, r++);
        content.Controls.Add(_clashGrid,   0, r++);
        content.Controls.Add(issueHeader,  0, r++);
        content.Controls.Add(_issueGrid,   0, r++);

#if AI_RESOLVER
        BuildAiSection(content, ref r);
#endif

        Controls.Add(content);
        Controls.Add(BuildButtonRow());

        ReloadAnalysis();
    }

    private void ReloadAnalysis()
    {
        var path = (_logPicker.SelectedItem as LogItem)?.Path ?? GodotLogAnalyzer.LatestLog();
        _analysis = GodotLogAnalyzer.Analyze(path);

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _headerLabel.Text = $"No Godot log found in {SupportPackage.GodotLogsDir}.";
        }
        else
        {
            _headerLabel.Text =
                $"Engine {_analysis.EngineVersion}   ·   "
                + $"{_analysis.LoadOrder.Count} mods loaded   ·   "
                + $"{_analysis.Hooks.Count} hooks   ·   "
                + $"{_analysis.HookClashes.Count} hook clash(es)   ·   "
                + $"{_analysis.Clashes.Count} script-override clash(es)   ·   "
                + $"{_analysis.Issues.Count} issue(s)";
        }

        _hookGrid.Rows.Clear();
        foreach (var c in _analysis.HookClashes)
        {
            int idx = _hookGrid.Rows.Add(c.Target, c.Mods.Count, string.Join("  →  ", c.Mods));
            // 3+ mods on one function is where ordering bugs cluster — flag amber.
            if (c.Mods.Count >= 3)
                _hookGrid.Rows[idx].Cells["Count"].Style.ForeColor = Color.FromArgb(255, 200, 80);
        }
        if (_analysis.HookClashes.Count == 0)
            _hookGrid.Rows.Add("(none)", "", "No function was hooked by more than one mod in this session.");

        _clashGrid.Rows.Clear();
        foreach (var c in _analysis.Clashes)
            _clashGrid.Rows.Add(c.VanillaPath, string.Join("  →  ", c.Mods), c.Winner);
        if (_analysis.Clashes.Count == 0)
            _clashGrid.Rows.Add("(none)", "No whole vanilla script was replaced by more than one mod.", "");

        _issueGrid.Rows.Clear();
        foreach (var iss in _analysis.Issues)
        {
            int idx = _issueGrid.Rows.Add(iss.Severity, iss.ModHint, iss.Message);
            var row = _issueGrid.Rows[idx];
            row.Cells[0].Style.ForeColor = iss.Severity switch
            {
                "ERROR"        => Color.FromArgb(245, 130, 120),
                "SCRIPT_ERROR" => Color.FromArgb(245, 130, 120),
                "WARNING"      => Color.FromArgb(255, 200, 80),
                _              => Color.FromArgb(200, 210, 225),
            };
        }
        if (_analysis.Issues.Count == 0)
            _issueGrid.Rows.Add("", "", "No errors or warnings found in this log.");
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static Label SectionHeader(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        AutoSize = true,
        Font = new Font("Segoe UI", 13f, FontStyle.Bold),
        ForeColor = Color.FromArgb(205, 215, 230),
        Margin = new Padding(0, 10, 0, 4),
    };

    private static DataGridView BuildGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.FromArgb(18, 22, 30),
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(36, 42, 54);
        g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(210, 220, 235);
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.DefaultCellStyle.BackColor = Color.FromArgb(18, 22, 30);
        g.DefaultCellStyle.ForeColor = Color.FromArgb(210, 218, 230);
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(40, 60, 90);
        g.DefaultCellStyle.SelectionForeColor = Color.FromArgb(235, 240, 248);
        g.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        return g;
    }

    private static DataGridViewTextBoxColumn MakeCol(string name, string header, int width) => new()
    {
        Name = name,
        HeaderText = header,
        Width = width,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    private Panel BuildButtonRow()
    {
        var p = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Color.FromArgb(30, 34, 44),
        };
        var close = MainForm.ThemedButton("Close");
        close.Width = 100;
        close.Height = 40;
        close.AutoSize = false;
        close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        close.Click += (_, _) => Close();
        p.Resize += (_, _) =>
        {
            close.Top = 8;
            close.Left = p.Width - close.Width - 16;
        };
        p.Controls.Add(close);
        AcceptButton = close;
        return p;
    }

    private class LogItem
    {
        public string Path  { get; set; } = "";
        public string Label { get; set; } = "";
    }

#if AI_RESOLVER
    private void BuildAiSection(TableLayoutPanel content, ref int r)
    {
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // ai header
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // ai button
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // ai summary
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 180f)); // proposed grid
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // apply row
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 90f));  // ai log

        content.Controls.Add(SectionHeader("AI diagnosis (Claude) — root causes + proposed load order"), 0, r++);

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent,
        };
        _aiBtn = MainForm.ThemedButton("🩺 Analyze with Claude");
        _aiBtn.Width = 220; _aiBtn.Height = 40; _aiBtn.AutoSize = false;
        _aiBtn.BackColor = Color.FromArgb(45, 90, 55);
        _aiBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _aiBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _aiBtn.Enabled = _claudeAvailable;
        _aiBtn.Click += async (_, _) => await RunDiagnoseAsync();
        btnRow.Controls.Add(_aiBtn);
        _aiProgress = new ProgressBar
        {
            Width = 260, Height = 24, Style = ProgressBarStyle.Marquee,
            Visible = false, Margin = new Padding(12, 8, 0, 0),
        };
        btnRow.Controls.Add(_aiProgress);
        if (!_claudeAvailable)
            btnRow.Controls.Add(new Label
            {
                Text = "Claude Code not detected — install it to enable AI diagnosis.",
                AutoSize = true, ForeColor = Color.FromArgb(255, 200, 80),
                Margin = new Padding(12, 10, 0, 0),
            });
        content.Controls.Add(btnRow, 0, r++);

        _aiSummary = new Label
        {
            Dock = DockStyle.Top, AutoSize = true,
            MaximumSize = new Size(1100, 0),
            ForeColor = Color.FromArgb(200, 212, 228),
            Margin = new Padding(0, 6, 0, 6),
        };
        content.Controls.Add(_aiSummary, 0, r++);

        _proposedGrid = BuildGrid();
        _proposedGrid.Columns.Add(MakeCol("Mod", "Mod", 360));
        _proposedGrid.Columns.Add(MakeCol("Cur", "Current priority", 160));
        _proposedGrid.Columns.Add(MakeCol("New", "Proposed priority", 180));
        content.Controls.Add(_proposedGrid, 0, r++);

        var applyRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent,
        };
        _applyOrderBtn = MainForm.ThemedButton("✔ Apply proposed load order");
        _applyOrderBtn.Width = 260; _applyOrderBtn.Height = 38; _applyOrderBtn.AutoSize = false;
        _applyOrderBtn.Enabled = false;
        _applyOrderBtn.Click += (_, _) => ApplyProposedOrder();
        applyRow.Controls.Add(_applyOrderBtn);
        content.Controls.Add(applyRow, 0, r++);

        _aiLog = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(180, 220, 160),
            Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.None,
        };
        content.Controls.Add(_aiLog, 0, r++);
    }

    private async Task RunDiagnoseAsync()
    {
        if (_busy || _diagnoser == null) return;
        _busy = true;
        _aiBtn.Enabled = false;
        _aiProgress.Visible = true;
        _aiLog.AppendText("Sending log digest to Claude…" + Environment.NewLine);

        try
        {
            var verdict = await _diagnoser.DiagnoseAsync(_analysis);
            _verdict = verdict;
            if (!verdict.Ok)
            {
                _aiSummary.Text = "Analysis failed.";
                _aiLog.AppendText("FAILED: " + verdict.Error + Environment.NewLine);
                ThemedMessageBox.Show(this,
                    "Claude analysis failed:\n" + verdict.Error,
                    "Analysis error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _aiSummary.Text = verdict.Summary;
            _aiLog.AppendText($"Done. Cost: ${verdict.CostUsd:F4}" + Environment.NewLine);
            if (verdict.Issues.Count > 0)
            {
                _aiLog.AppendText(Environment.NewLine + "Issues:" + Environment.NewLine);
                foreach (var iss in verdict.Issues)
                {
                    _aiLog.AppendText($"• {iss.Title}" + Environment.NewLine);
                    if (!string.IsNullOrEmpty(iss.Cause)) _aiLog.AppendText($"    cause: {iss.Cause}" + Environment.NewLine);
                    if (!string.IsNullOrEmpty(iss.Fix))   _aiLog.AppendText($"    fix:   {iss.Fix}" + Environment.NewLine);
                }
            }

            // Populate the proposed-order grid (current → proposed).
            _proposedGrid.Rows.Clear();
            foreach (var pp in verdict.ProposedOrder)
            {
                var entry = _registry.FindById(pp.ModId);
                var name = entry != null
                    ? (string.IsNullOrEmpty(entry.DisplayName) ? entry.ModId : entry.DisplayName)
                    : pp.ModId;
                var cur = entry?.Priority.ToString() ?? "?";
                int idx = _proposedGrid.Rows.Add(name, cur, pp.Priority.ToString());
                if (entry != null && entry.Priority != pp.Priority)
                    _proposedGrid.Rows[idx].Cells["New"].Style.ForeColor = Color.FromArgb(120, 220, 140);
            }
            if (!string.IsNullOrEmpty(verdict.OrderRationale))
                _proposedGrid.Rows.Add("(rationale)", "", verdict.OrderRationale);

            _applyOrderBtn.Enabled = verdict.ProposedOrder.Count > 0;
            if (verdict.ProposedOrder.Count == 0)
                _aiLog.AppendText("No load-order change proposed." + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _aiLog.AppendText("FAILED: " + ex.Message + Environment.NewLine);
            ThemedMessageBox.Show(this,
                "Claude analysis failed:\n" + ex.Message,
                "Analysis error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            _aiBtn.Enabled = _claudeAvailable;
            _aiProgress.Visible = false;
        }
    }

    private void ApplyProposedOrder()
    {
        if (_verdict == null || _verdict.ProposedOrder.Count == 0) return;

        var lines = new List<string>();
        foreach (var pp in _verdict.ProposedOrder)
        {
            var entry = _registry.FindById(pp.ModId);
            if (entry == null) continue;
            lines.Add($"  {(string.IsNullOrEmpty(entry.DisplayName) ? entry.ModId : entry.DisplayName)}: "
                + $"{entry.Priority} → {pp.Priority}");
        }
        if (lines.Count == 0)
        {
            ThemedMessageBox.Show(this,
                "None of the proposed mods are currently installed, so there's "
                + "nothing to apply.",
                "Nothing to apply", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var dr = ThemedMessageBox.Show(this,
            "Apply these load-order (priority) changes to mod_config.cfg?\n\n"
            + string.Join("\n", lines)
            + "\n\nLower priority loads earlier. You can revert via the "
            + "last-launch checkpoint if needed.",
            "Apply proposed load order",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (dr != DialogResult.Yes) return;

        int applied = 0;
        foreach (var pp in _verdict.ProposedOrder)
        {
            var entry = _registry.FindById(pp.ModId);
            if (entry == null) continue;
            _modConfig.SetPriority(entry.ModId, entry.Version, pp.Priority);
            applied++;
        }
        try { _modConfig.Save(); }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this,
                "Priorities staged but saving mod_config.cfg failed:\n" + ex.Message,
                "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Applied = true;
        _applyOrderBtn.Enabled = false;
        ThemedMessageBox.Show(this,
            $"Applied {applied} priority change(s). The mod list will refresh "
            + "when you close this dialog.",
            "Load order applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
#endif
}
